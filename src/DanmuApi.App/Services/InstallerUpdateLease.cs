using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DanmuApi.Core.ApplicationUpdates;
using DanmuApi.Platform;
using Microsoft.Win32.SafeHandles;

namespace DanmuApi.App.Services;

/// <summary>Signed, elevated Setup relay. User files are accessed only while impersonating the pinned parent.</summary>
internal static class InstallerUpdateLease
{
    public const string Argument = "--installer-update-lease";
    private static readonly TimeSpan ParentTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan FinishTimeout = TimeSpan.FromMinutes(15);

    public static int Run(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) throw Failure("platformunsupported");
            var request = ParseArguments(args);
            return RunCore(new WindowsOperations(request), ParentTimeout, FinishTimeout);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(Diagnostic.From("arguments", error).Format());
            return 1;
        }
    }

    internal sealed record Request(int ParentPid, int InstallerPid, string TargetDirectory, string ReadyPath, string LeaseDirectory);
    internal sealed record TokenIdentity(string Sid, int Session, bool Elevated, int ElevationType);
    internal sealed record Diagnostic(string Phase, string Reason, string Type, int HResult)
    {
        internal static Diagnostic From(string phase, Exception error)
        {
            var hresult = error.HResult;
            for (Exception? current = error; current is not null; current = current.InnerException)
            {
                if (current is not Win32Exception native) continue;
                // Win32Exception.HResult can be E_FAIL; retain the actual native code without exposing its message.
                if (native.NativeErrorCode is >= 0 and <= 65535)
                    hresult = native.NativeErrorCode == 0 ? 0 : unchecked((int)(0x80070000u | (uint)native.NativeErrorCode));
                break;
            }
            return new(phase, error is LeaseException failure ? failure.Reason : "operationfailed", error.GetType().Name, hresult);
        }
        internal string Format() => $"installer-update-lease phase={Phase} reason={Reason} type={Type} HRESULT=0x{HResult:X8}; {ReasonText(Reason)}";
    }

    internal interface ILease
    {
        AppInstanceLockResult Release();
    }

    internal interface IOperations : IDisposable
    {
        TimeSpan Elapsed { get; }
        void Verify();
        AppInstanceLockResult Probe();
        bool Cancelled();
        bool ParentExited();
        bool InstallerExited();
        void WriteReady();
        ILease Acquire();
        void WritePrepared();
        bool Finished();
        void WriteReleased();
        void Delay();
        void ReportFailure(IReadOnlyList<Diagnostic> diagnostics);
    }

    // No marker is a substitute for owning both locks. This synchronous seam also tests failure ordering.
    internal static int RunCore(IOperations operations, TimeSpan parentTimeout, TimeSpan finishTimeout)
    {
        var phase = "verify";
        ILease? lease = null;
        var diagnostics = new List<Diagnostic>();
        try
        {
            operations.Verify();
            phase = "probe";
            var probe = operations.Probe();
            if (!probe.Succeeded && !probe.AlreadyOwned) throw Failure("probefailed");
            phase = "ready";
            CheckOwnerAndCancellation(operations);
            operations.WriteReady();
            phase = "parent-wait";
            var deadline = operations.Elapsed + parentTimeout;
            while (true)
            {
                CheckOwnerAndCancellation(operations);
                if (operations.ParentExited()) break;
                if (operations.Elapsed >= deadline) throw Failure("parenttimeout");
                operations.Delay();
            }
            phase = "acquire";
            CheckOwnerAndCancellation(operations);
            lease = operations.Acquire() ?? throw Failure("acquirefailed");
            phase = "prepared";
            CheckOwnerAndCancellation(operations);
            operations.WritePrepared();
            phase = "finish-wait";
            deadline = operations.Elapsed + finishTimeout;
            while (true)
            {
                CheckOwnerAndCancellation(operations);
                if (operations.Finished()) break;
                if (operations.Elapsed >= deadline) throw Failure("finishtimeout");
                operations.Delay();
            }
            phase = "release";
            var release = lease.Release();
            lease = null;
            if (!release.Succeeded) throw Failure("lockreleasefail");
            phase = "released";
            operations.WriteReleased();
        }
        catch (Exception error)
        {
            diagnostics.Add(Diagnostic.From(phase, error));
        }
        finally
        {
            // Setup may still be copying files after cancel/timeout/read failure. Publish the failure first,
            // then retain the real locks until the pinned owner exits or explicitly authorizes release.
            if (diagnostics.Count != 0) Report(operations, diagnostics);
            if (lease is not null)
            {
                WaitForSafeFailureRelease(operations, diagnostics);
                try
                {
                    if (!lease.Release().Succeeded) AddDiagnostic(diagnostics, Diagnostic.From("cleanup", Failure("lockreleasefail")));
                }
                catch (Exception error) { AddDiagnostic(diagnostics, Diagnostic.From("cleanup", new LeaseException("lockreleasefail", error))); }
            }
            try { operations.Dispose(); }
            catch (Exception error) { AddDiagnostic(diagnostics, Diagnostic.From("cleanup", new LeaseException("cleanupfailed", error))); }
        }
        return diagnostics.Count == 0 ? 0 : 1;
    }

    private static void WaitForSafeFailureRelease(IOperations operations, List<Diagnostic> diagnostics)
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            // User cancel/job paths are never revisited here. A monitoring error is not release authority.
            try { if (operations.InstallerExited()) return; }
            catch (Exception error) { Record("owner-monitor", error); }
            try { if (operations.Finished()) return; }
            catch (Exception error) { Record("finish-monitor", error); }
            try { operations.Delay(); }
            catch (Exception error) { Record("delay-monitor", error); }
        }
        void Record(string phase, Exception error)
        {
            var diagnostic = Diagnostic.From(phase, error);
            var key = $"{phase}:{diagnostic.Reason}:{diagnostic.Type}:{diagnostic.HResult}";
            if (reported.Add(key)) AddDiagnostic(diagnostics, diagnostic);
        }
    }

    private static void AddDiagnostic(List<Diagnostic> diagnostics, Diagnostic diagnostic)
    {
        diagnostics.Add(diagnostic);
        Console.Error.WriteLine(diagnostic.Format());
    }

    private static void CheckOwnerAndCancellation(IOperations operations)
    {
        if (operations.InstallerExited()) throw Failure("installdead");
        if (operations.Cancelled()) throw Failure("cancelled");
    }

    private static void Report(IOperations operations, IReadOnlyList<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics) Console.Error.WriteLine(diagnostic.Format());
        try { operations.ReportFailure(diagnostics); }
        catch (Exception error) { Console.Error.WriteLine(Diagnostic.From("diagnostic", new LeaseException("diagnosticwritefail", error)).Format()); }
    }

    internal static Request ParseArguments(string[] args)
    {
        if (args.Length != 6 || args[0] != Argument ||
            !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent) || parent <= 0 ||
            !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var installer) || installer <= 0 || parent == installer)
            throw Failure("argumentsinvalid");
        return new Request(parent, installer, FullPath(args[3]), FullPath(args[4]), FullPath(args[5]));
    }

    internal static void ValidateTokenSelection(TokenIdentity parent, TokenIdentity selected)
    {
        if (!string.Equals(parent.Sid, selected.Sid, StringComparison.Ordinal) || parent.Session != selected.Session || selected.Elevated ||
            (parent.Elevated && (parent.ElevationType != 2 || selected.ElevationType != 3)))
            throw Failure("tokenidentity");
    }

    internal static string ValidateReadyPath(string localFolder, string readyPath)
    {
        var jobDirectory = Path.GetDirectoryName(FullPath(readyPath)) ?? throw Failure("readyinvalid");
        if (!string.Equals(Path.GetFileName(readyPath), "installer.ready", StringComparison.Ordinal) ||
            !Guid.TryParseExact(Path.GetFileName(jobDirectory), "N", out _) ||
            !SamePath(Path.GetDirectoryName(jobDirectory) ?? string.Empty, Path.Combine(localFolder, "DanmuApi", "app-updates")))
            throw Failure("readyinvalid");
        return jobDirectory;
    }

    internal static void ValidateJob(byte[] bytes, Request request, long parentStartTicks, string parentVersion)
    {
        if (bytes.Length > 64 * 1024) throw Failure("joboversize");
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Failure("jobinvalid");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!names.Add(property.Name)) throw Failure("jobinvalid");
            foreach (var required in new[] { "ParentPid", "ParentStartTicks", "TargetDirectory", "Kind", "ExpectedVersion" })
                if (!names.Contains(required)) throw Failure("jobinvalid");
            var job = JsonSerializer.Deserialize<ApplicationUpdateJob>(bytes) ?? throw Failure("jobinvalid");
            if (job.ParentPid != request.ParentPid || job.ParentStartTicks != parentStartTicks || job.Kind != "installer" ||
                string.IsNullOrEmpty(job.TargetDirectory) || !SamePath(FullPath(job.TargetDirectory), request.TargetDirectory) ||
                string.IsNullOrEmpty(job.ExpectedVersion) || SemanticVersion.Parse(job.ExpectedVersion).CompareTo(SemanticVersion.Parse(parentVersion)) <= 0)
                throw Failure("jobinvalid");
        }
        catch (LeaseException) { throw; }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException or InvalidOperationException)
        { throw new LeaseException("jobinvalid", error); }
    }

    internal static string FullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.IndexOf(':', 2) >= 0) throw Failure("pathinvalid");
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool SamePath(string first, string second) => string.Equals(first.TrimEnd('\\', '/'), second.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private static LeaseException Failure(string reason) => new(reason);
    internal sealed class LeaseException : Exception
    {
        internal string Reason { get; }
        internal LeaseException(string reason, Exception? inner = null) : base(ReasonText(reason), inner)
        {
            Reason = reason;
            if (inner is not null) HResult = inner.HResult;
        }
    }

    private static string ReasonText(string reason) => reason switch
    {
        "argumentsinvalid" => "Expected six relay arguments and distinct positive process IDs.",
        "platformunsupported" => "Windows native process and token APIs are required.",
        "parentidentity" => "Pinned parent process identity does not match the installed application.",
        "parentdead" => "The original parent exited before identity capture completed.",
        "installidentity" => "Pinned Setup must be the relay's actual creator and an elevated administrator.",
        "installertrust" => "The actual pinned Setup self-copy must have the trusted application publisher signature.",
        "targetinvalid" => "Target must match the actual HKLM installation and a trusted signed application.",
        "tokenmissing" => "The parent has no usable original limited user token.",
        "tokenidentity" => "The selected token must be non-elevated with the parent SID and session.",
        "knownfolderinvalid" => "Explicit-token KnownFolder lookup failed or returned an invalid directory.",
        "pathinvalid" => "A fully qualified local non-reparse path is required.",
        "leaseinvalid" => "The existing fixed lease directory must be protected by administrator ownership and permissions.",
        "readyinvalid" => "Ready must be the exact original-user GUID job installer.ready path.",
        "jobinvalid" => "The installer job must match the pinned parent, target and a newer version.",
        "joboversize" => "The installer job exceeds the 64 KiB limit.",
        "probefailed" => "The original-user lock preflight failed; a sharing conflict alone is allowed before parent exit.",
        "instanceheld" => "Another instance holds a modern or legacy lock after the original parent exited.",
        "acquirefailed" => "Both original-user instance locks could not be acquired.",
        "parenttimeout" => "The original parent did not exit within 120 seconds.",
        "installdead" => "The pinned Setup process exited before finish was received.",
        "cancelled" => "The original-user job has a cancellation marker.",
        "finishtimeout" => "Setup did not provide finish within 15 minutes.",
        "lockreleasefail" => "An instance lock could not be released cleanly.",
        "cleanupfailed" => "Native relay resources could not be disposed cleanly.",
        "diagnosticwritefail" => "The trusted lease failure diagnostic could not be written.",
        "markercleanupfailed" => "A failed marker publication could not remove its temporary file.",
        _ => "The operation failed explicitly; no success marker was substituted."
    };

    private sealed class WindowsOperations : IOperations
    {
        private readonly Request _request;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<SafeFileHandle> _adminPins = [];
        private readonly List<SafeFileHandle> _userPins = [];
        private SafeProcessHandle? _parent;
        private SafeProcessHandle? _installer;
        private SafeAccessTokenHandle? _userToken;
        private SafeFileHandle? _relayDirectoryPin;
        private SafeFileHandle? _leaseDirectoryPin;
        private SafeFileHandle? _setupDirectoryPin;
        private FileStream? _setupImagePin;
        private FileStream? _jobPin;
        private AppPaths? _paths;
        private string? _jobDirectory;
        private bool _trustedLease;
        private bool _verified;

        internal WindowsOperations(Request request) { _request = request; }
        public TimeSpan Elapsed => _clock.Elapsed;

        public void Verify()
        {
            // No user-profile path is visited here with the elevated token.
            _installer = Native.OpenPinnedProcess(_request.InstallerPid);
            using var installerToken = Native.OpenToken(_installer);
            var setupIdentity = Native.DescribeToken(installerToken);
            using var currentRelay = Native.OpenPinnedProcess(Environment.ProcessId);
            if (!setupIdentity.Elevated || !Native.IsAdministrator(installerToken) || Native.CreatorPid() != _request.InstallerPid ||
                Native.StartTicks(_installer) > Native.StartTicks(currentRelay))
                throw Failure("installidentity");
            ValidateLease(setupIdentity.Sid);
            var setupImage = Native.ImagePath(_installer);
            var setupDirectory = Path.GetDirectoryName(setupImage) ?? throw Failure("installidentity");
            // Inno's signed temporary self-copy can reside in a different protected directory from {tmp}.
            PinDirectoryChain(setupDirectory, _adminPins);
            _setupDirectoryPin = _adminPins[^1];
            Native.EnsureAdminDirectory(_setupDirectoryPin, setupIdentity.Sid);
            _setupImagePin = OpenPlainRead(setupImage);
            try { AppUpdateTrust.VerifyExecutable(setupImage); }
            catch (Exception error) { throw new LeaseException("installertrust", error); }
            if (Native.Exited(_installer)) throw Failure("installdead");
            _parent = Native.OpenPinnedProcess(_request.ParentPid);
            if (Native.Exited(_parent)) throw Failure("parentdead");
            var targetExe = Path.Combine(_request.TargetDirectory, "DanmuApi.App.exe");
            if (!SamePath(Native.ImagePath(_parent), targetExe)) throw Failure("parentidentity");
            if (!ApplicationUpdateHelper.IsInstalled(targetExe)) throw Failure("targetinvalid");
            PinDirectoryChain(_request.TargetDirectory, _adminPins);
            string parentVersion;
            using (var targetPin = new FileStream(targetExe, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                RejectReparse(targetExe);
                AppUpdateTrust.VerifyExecutable(targetExe);
                parentVersion = FileVersionInfo.GetVersionInfo(targetExe).ProductVersion ?? throw Failure("targetinvalid");
            }
            var startTicks = Native.StartTicks(_parent);
            _userToken = Native.CaptureUserToken(_parent);
            // DACL validation also rejects direct writes granted to the captured ordinary identity.
            Native.EnsureNoUserWrite(_relayDirectoryPin!, _userToken);
            Native.EnsureNoUserWrite(_leaseDirectoryPin!, _userToken);
            Native.EnsureNoUserWrite(_setupDirectoryPin!, _userToken);
            AsUser(() =>
            {
                var local = Native.KnownFolder(new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091"), _userToken);
                var roaming = Native.KnownFolder(new Guid("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D"), _userToken);
                PinDirectoryChain(local, _userPins);
                PinDirectoryChain(roaming, _userPins);
                _paths = new AppPaths(Path.Combine(local, "DanmuApi"), Path.Combine(roaming, "DanmuApi"));
                _jobDirectory = ValidateReadyPath(local, _request.ReadyPath);
                PinDirectoryChain(_jobDirectory, _userPins);
                PinDirectoryChain(_paths.SettingsDirectory, _userPins);
                foreach (var path in new[] { _request.ReadyPath, CancelPath, _paths.InstanceLockFile, LegacyLockPath }) RejectReparse(path, optional: true);
                _jobPin = OpenPlainRead(Path.Combine(_jobDirectory, "job.json"));
                if (_jobPin.Length > 64 * 1024) throw Failure("joboversize");
                var bytes = new byte[checked((int)_jobPin.Length)];
                _jobPin.ReadExactly(bytes);
                ValidateJob(bytes, _request, startTicks, parentVersion);
            });
            if (Native.Exited(_parent)) throw Failure("parentdead");
            _verified = true;
        }

        private void ValidateLease(string setupSid)
        {
            if (!SamePath(_request.LeaseDirectory, Path.Combine(AppContext.BaseDirectory, "update-lease"))) throw Failure("leaseinvalid");
            PinDirectoryChain(AppContext.BaseDirectory, _adminPins);
            _relayDirectoryPin = _adminPins[^1];
            Native.EnsureAdminDirectory(_relayDirectoryPin, setupSid);
            PinDirectoryChain(_request.LeaseDirectory, _adminPins);
            _leaseDirectoryPin = _adminPins[^1];
            Native.EnsureAdminDirectory(_leaseDirectoryPin, setupSid);
            foreach (var name in new[] { "prepared", "error.txt", "released", "finish", "prepared.tmp", "error.txt.tmp", "released.tmp" })
                if (PlainExists(Path.Combine(_request.LeaseDirectory, name))) throw Failure("leaseinvalid");
            _trustedLease = true;
        }

        public AppInstanceLockResult Probe() => AsVerifiedUser(() => ProbeLocks(
            () => OpenExistingLock(_paths!.InstanceLockFile, legacy: false),
            () => OpenExistingLock(LegacyLockPath, legacy: true)));

        public bool Cancelled() => AsVerifiedUser(() => PlainExists(CancelPath));
        public bool ParentExited() => Native.Exited(_parent ?? throw Failure("parentidentity"));
        public bool InstallerExited() => Native.Exited(_installer ?? throw Failure("installidentity"));
        public void WriteReady() => AsVerifiedUser(() =>
        {
            if (PlainExists(_request.ReadyPath)) throw Failure("readyinvalid");
            WriteNew(_request.ReadyPath, "ready");
        });

        public ILease Acquire() => AsVerifiedUser<ILease>(() =>
        {
            if (!ParentExited()) throw Failure("parentidentity");
            RejectReparse(_paths!.InstanceLockFile, optional: true);
            RejectReparse(LegacyLockPath, optional: true);
            var lockPins = new List<SafeFileHandle>();
            var instance = new AppInstanceLock(_paths, _ => { });
            var acquired = false;
            try
            {
                // Zero-access handles pin both names without conflicting with modern FileShare.None.
                foreach (var path in new[] { _paths.InstanceLockFile, LegacyLockPath })
                {
                    var pin = Native.PinLockName(path);
                    try { Native.EnsurePlainHandle(pin, directory: false); lockPins.Add(pin); }
                    catch { pin.Dispose(); throw; }
                }
                var result = instance.TryAcquireDetailed();
                if (!result.Succeeded) throw Failure(result.AlreadyOwned ? "instanceheld" : "acquirefailed");
                acquired = true;
                return new UserLease(instance, _userToken!, lockPins);
            }
            finally
            {
                if (!acquired)
                {
                    try { if (!instance.Release().Succeeded) throw Failure("lockreleasefail"); }
                    finally { foreach (var pin in lockPins) pin.Dispose(); }
                }
            }
        });

        public void WritePrepared() => WriteTrusted("prepared", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        public bool Finished()
        {
            if (!_trustedLease) throw Failure("leaseinvalid");
            // Setup writes this only inside the pinned administrator directory; no profile access here.
            var path = Path.Combine(_request.LeaseDirectory, "finish");
            if (!PlainExists(path)) return false;
            using var stream = OpenPlainRead(path);
            if (stream.Length > 256) throw Failure("leaseinvalid");
            return true;
        }
        public void WriteReleased() => WriteTrusted("released", "released");
        public void Delay() => Thread.Sleep(100);
        public void ReportFailure(IReadOnlyList<Diagnostic> diagnostics)
        {
            if (_trustedLease) WriteTrusted("error.txt", string.Join(Environment.NewLine, diagnostics.Select(item => item.Format())));
        }
        private void WriteTrusted(string name, string value)
        {
            if (!_trustedLease) throw Failure("leaseinvalid");
            PublishMarker(_request.LeaseDirectory, name, value);
        }
        private string CancelPath => Path.Combine(_jobDirectory ?? throw Failure("readyinvalid"), "cancel");
        private string LegacyLockPath => Path.Combine(_paths?.SettingsDirectory ?? throw Failure("knownfolderinvalid"), "app.lock");
        private T AsUser<T>(Func<T> action) => WindowsIdentity.RunImpersonated(_userToken ?? throw Failure("tokenmissing"), action);
        private void AsUser(Action action) => WindowsIdentity.RunImpersonated(_userToken ?? throw Failure("tokenmissing"), action);
        private T AsVerifiedUser<T>(Func<T> action)
        {
            if (!_verified) throw Failure("parentidentity");
            return AsUser(action);
        }
        private void AsVerifiedUser(Action action)
        {
            if (!_verified) throw Failure("parentidentity");
            AsUser(action);
        }
        public void Dispose()
        {
            // Handles for user files are also closed in that user's scope.
            try
            {
                if (_userToken is not null && !_userToken.IsInvalid)
                    AsUser(() => { try { _jobPin?.Dispose(); } finally { foreach (var pin in _userPins) pin.Dispose(); } });
            }
            finally
            {
                try { _userToken?.Dispose(); }
                finally
                {
                    try { _parent?.Dispose(); }
                    finally
                    {
                        try { _installer?.Dispose(); }
                        finally { try { _setupImagePin?.Dispose(); } finally { foreach (var pin in _adminPins) pin.Dispose(); } }
                    }
                }
            }
        }
    }

    private sealed class UserLease(AppInstanceLock instance, SafeAccessTokenHandle token, List<SafeFileHandle> pins) : ILease
    {
        public AppInstanceLockResult Release() => WindowsIdentity.RunImpersonated(token, () =>
        {
            try { return instance.Release(); }
            finally { foreach (var pin in pins) pin.Dispose(); }
        });
    }

    internal static AppInstanceLockResult ProbeLocks(Func<IDisposable?> openModern, Func<IDisposable?> openLegacy)
    {
        IDisposable? modern = null;
        IDisposable? legacy = null;
        var held = false;
        try
        {
            // A conflict on one lock never conceals an I/O or permission failure on the other.
            try { modern = openModern(); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { held = true; }
            try { legacy = openLegacy(); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { held = true; }
            return held ? new AppInstanceLockResult(false, "instanceheld", AlreadyOwned: true) : AppInstanceLockResult.Success();
        }
        finally
        {
            try { legacy?.Dispose(); }
            finally { modern?.Dispose(); }
        }
    }

    private static FileStream? OpenExistingLock(string path, bool legacy)
    {
        // Existing files only: preflight does not create locks or touch an endpoint.
        if (!PlainExists(path)) return null;
        var stream = new FileStream(path, FileMode.Open, legacy ? FileAccess.ReadWrite : FileAccess.Read, legacy ? FileShare.ReadWrite : FileShare.None);
        try
        {
            Native.EnsurePlainHandle(stream.SafeFileHandle, directory: false);
            if (legacy) stream.Lock(0, long.MaxValue);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    internal static void PublishMarker(string directory, string name, string value, Action? beforePublish = null)
    {
        if (name is not ("prepared" or "error.txt" or "released")) throw Failure("leaseinvalid");
        var destination = Path.Combine(directory, name);
        var temporary = Path.Combine(directory, name + ".tmp");
        if (PlainExists(destination) || PlainExists(temporary)) throw Failure("leaseinvalid");
        var created = false;
        try
        {
            RejectReparse(temporary, optional: true);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                stream.Write(new UTF8Encoding(false, true).GetBytes(value));
                stream.Flush(flushToDisk: true);
            }
            beforePublish?.Invoke();
            // Same-directory rename publishes the complete closed marker; never replace an existing file.
            File.Move(temporary, destination, overwrite: false);
        }
        catch (Exception error)
        {
            if (created)
            {
                try { File.Delete(temporary); }
                catch (Exception cleanup)
                {
                    Console.Error.WriteLine(Diagnostic.From("marker-cleanup", cleanup).Format());
                    throw new LeaseException("markercleanupfailed", new AggregateException(error, cleanup));
                }
            }
            throw;
        }
    }

    private static void WriteNew(string path, string value)
    {
        RejectReparse(path, optional: true);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var bytes = new UTF8Encoding(false, true).GetBytes(value);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }

    internal static FileStream OpenPlainRead(string path)
    {
        RejectReparse(path);
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try { Native.EnsurePlainHandle(file.SafeFileHandle, directory: false); return file; }
        catch { file.Dispose(); throw; }
    }

    internal static bool PlainExists(string path)
    {
        try { RejectReparse(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static void RejectReparse(string path, bool optional = false)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw Failure("pathinvalid");
        }
        catch (FileNotFoundException) when (optional) { }
        catch (DirectoryNotFoundException) when (optional) { }
    }

    private static void PinDirectoryChain(string path, List<SafeFileHandle> pins)
    {
        var chain = new Stack<string>();
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current.TrimEnd('\\', '/')))
        {
            chain.Push(current);
            if (SamePath(current, Path.GetPathRoot(current)!)) break;
        }
        while (chain.TryPop(out var directory))
        {
            var handle = Native.OpenDirectory(directory);
            try { Native.EnsurePlainHandle(handle, directory: true); pins.Add(handle); }
            catch { handle.Dispose(); throw; }
        }
    }

    // Native test hooks query only the test host, without loading settings, UI or user-profile files.
    internal static bool MatchesCurrentCreatorForTest(int installerPid)
    {
        if (Native.CreatorPid() != installerPid) return false;
        using var parent = Native.OpenPinnedProcess(installerPid);
        using var current = Native.OpenPinnedProcess(Environment.ProcessId);
        return Native.StartTicks(parent) <= Native.StartTicks(current);
    }

    internal static TokenIdentity CaptureCurrentProcessUserForTest()
    {
        using var process = Native.OpenPinnedProcess(Environment.ProcessId);
        using var token = Native.CaptureUserToken(process);
        return Native.DescribeToken(token);
    }

    internal static void VerifyCurrentTokenCannotWriteDirectoryForTest(string directory)
    {
        using var process = Native.OpenPinnedProcess(Environment.ProcessId);
        using var original = Native.OpenToken(process);
        using var token = Native.DuplicateQueryTokenForTest(original);
        using var pin = Native.OpenDirectory(directory);
        Native.EnsurePlainHandle(pin, directory: true);
        Native.EnsureNoUserWrite(pin, token);
    }

    internal static class Native
    {
        private const uint TokenQueryDuplicate = 0x0008 | 0x0002;
        private const uint DirectoryMutation = 0x0002 | 0x0004 | 0x0010 | 0x0040 | 0x0100;
        [StructLayout(LayoutKind.Sequential)] private struct FileTimes { public uint Low; public uint High; }
        [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { public uint Attributes; public uint Tag; }
        [StructLayout(LayoutKind.Sequential)] private struct GenericMapping { public uint Read; public uint Write; public uint Execute; public uint All; }
        [StructLayout(LayoutKind.Sequential)] private struct BasicProcessInformation
        {
            public IntPtr ExitStatus;
            public IntPtr PebBaseAddress;
            public IntPtr AffinityMask;
            public IntPtr BasePriority;
            public UIntPtr ProcessId;
            public UIntPtr InheritedFromProcessId;
        }
        internal static int CreatorPid()
        {
            using var current = OpenPinnedProcess(Environment.ProcessId);
            var size = Marshal.SizeOf<BasicProcessInformation>();
            var status = NtQueryInformationProcess(current, 0, out var information, size, out var actual);
            if (status != 0) throw new LeaseException("installidentity", new Win32Exception(unchecked((int)RtlNtStatusToDosError(status))));
            var pid = information.InheritedFromProcessId.ToUInt64();
            if (actual != size || information.ProcessId.ToUInt64() != (ulong)Environment.ProcessId || pid == 0 || pid > int.MaxValue)
                throw Failure("installidentity");
            return (int)pid;
        }

        internal static SafeProcessHandle OpenPinnedProcess(int pid)
        {
            var handle = OpenProcess(0x1000 | 0x00100000 /* QUERY_LIMITED_INFORMATION | SYNCHRONIZE */, false, pid);
            if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            if (GetProcessId(handle) != pid) { handle.Dispose(); throw Failure("parentidentity"); }
            return handle;
        }
        internal static string ImagePath(SafeProcessHandle process)
        {
            var value = new StringBuilder(32768);
            var size = value.Capacity;
            if (!QueryFullProcessImageName(process, 0, value, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return FullPath(value.ToString());
        }
        internal static long StartTicks(SafeProcessHandle process)
        {
            if (!GetProcessTimes(process, out var creation, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return DateTime.FromFileTimeUtc(unchecked((long)(((ulong)creation.High << 32) | creation.Low))).Ticks;
        }
        internal static bool Exited(SafeProcessHandle process) => WaitForSingleObject(process, 0) switch
        {
            0 => true,
            258 => false,
            _ => throw new Win32Exception(Marshal.GetLastWin32Error())
        };
        internal static SafeAccessTokenHandle OpenToken(SafeProcessHandle process)
        {
            if (!OpenProcessToken(process, TokenQueryDuplicate, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return token;
        }
        internal static TokenIdentity DescribeToken(SafeAccessTokenHandle token)
        {
            var user = TokenBuffer(token, 1);
            string sid;
            try { sid = new SecurityIdentifier(Marshal.ReadIntPtr(user)).Value; }
            finally { Marshal.FreeHGlobal(user); }
            return new TokenIdentity(sid, TokenInt(token, 12), TokenInt(token, 20) != 0, TokenInt(token, 18));
        }
        internal static SafeAccessTokenHandle CaptureUserToken(SafeProcessHandle parent)
        {
            using var original = OpenToken(parent);
            var identity = DescribeToken(original);
            SafeAccessTokenHandle? linked = null;
            try
            {
                var selected = original;
                if (identity.Elevated)
                {
                    if (identity.ElevationType != 2) throw Failure("tokenmissing");
                    IntPtr information;
                    try { information = TokenFixedBuffer(original, 19, IntPtr.Size); }
                    catch (Win32Exception error) { throw new LeaseException("tokenmissing", error); }
                    try { linked = new SafeAccessTokenHandle(Marshal.ReadIntPtr(information)); }
                    finally { Marshal.FreeHGlobal(information); }
                    if (linked.IsInvalid) throw Failure("tokenmissing");
                    selected = linked;
                }
                ValidateTokenSelection(identity, DescribeToken(selected));
                // MAXIMUM_ALLOWED, SecurityImpersonation, TokenImpersonation.
                if (!DuplicateTokenEx(selected, 0x02000000, IntPtr.Zero, 2, 2, out var duplicate)) throw new Win32Exception(Marshal.GetLastWin32Error());
                try { ValidateTokenSelection(identity, DescribeToken(duplicate)); return duplicate; }
                catch { duplicate.Dispose(); throw; }
            }
            finally { linked?.Dispose(); }
        }
        internal static SafeAccessTokenHandle DuplicateQueryTokenForTest(SafeAccessTokenHandle source)
        {
            if (!DuplicateTokenEx(source, 0x0008, IntPtr.Zero, 2, 2, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return token;
        }
        internal static bool IsAdministrator(SafeAccessTokenHandle primary)
        {
            if (!DuplicateTokenEx(primary, 0x0008, IntPtr.Zero, 2, 2, out var impersonation)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using (impersonation)
            {
                var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                var sid = new byte[administrators.BinaryLength];
                administrators.GetBinaryForm(sid, 0);
                if (!CheckTokenMembership(impersonation, sid, out var member)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return member;
            }
        }
        internal static string KnownFolder(Guid id, SafeAccessTokenHandle token)
        {
            var result = SHGetKnownFolderPath(ref id, 0, token, out var pointer);
            try
            {
                if (result < 0) throw new LeaseException("knownfolderinvalid", Marshal.GetExceptionForHR(result));
                return FullPath(Marshal.PtrToStringUni(pointer) ?? throw Failure("knownfolderinvalid"));
            }
            finally { if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer); }
        }
        private static IntPtr TokenBuffer(SafeAccessTokenHandle token, int kind)
        {
            if (GetTokenInformation(token, kind, IntPtr.Zero, 0, out var length) || Marshal.GetLastWin32Error() != 122 || length <= 0 || length > 65536)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetTokenInformation(token, kind, buffer, length, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return buffer;
            }
            catch { Marshal.FreeHGlobal(buffer); throw; }
        }
        private static IntPtr TokenFixedBuffer(SafeAccessTokenHandle token, int kind, int size)
        {
            // TokenElevation and other fixed-size classes can reject a null size query with ERROR_BAD_LENGTH.
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, kind, buffer, size, out var actual)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (actual != size) throw Failure("tokenidentity");
                return buffer;
            }
            catch { Marshal.FreeHGlobal(buffer); throw; }
        }
        private static int TokenInt(SafeAccessTokenHandle token, int kind)
        {
            var buffer = TokenFixedBuffer(token, kind, sizeof(int));
            try { return Marshal.ReadInt32(buffer); }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        internal static SafeFileHandle OpenDirectory(string path)
        {
            var handle = CreateFile(path, 0x00020000 | 0x80 /* READ_CONTROL | FILE_READ_ATTRIBUTES */, 3 /* no FILE_SHARE_DELETE */,
                IntPtr.Zero, 3, 0x02000000 | 0x00200000 /* BACKUP_SEMANTICS | OPEN_REPARSE_POINT */, IntPtr.Zero);
            if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            return handle;
        }
        internal static SafeFileHandle PinLockName(string path)
        {
            var handle = CreateFile(path, 0, 3, IntPtr.Zero, 4 /* OPEN_ALWAYS */, 0x00200000 /* OPEN_REPARSE_POINT */, IntPtr.Zero);
            if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            return handle;
        }
        internal static void EnsurePlainHandle(SafeFileHandle handle, bool directory)
        {
            if (!GetFileInformationByHandleEx(handle, 9 /* FileAttributeTagInfo */, out var info, Marshal.SizeOf<AttributeTag>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory) throw Failure("pathinvalid");
        }
        private static IntPtr SecurityDescriptor(SafeFileHandle directory)
        {
            // AccessCheck requires both owner and group, in addition to the discretionary ACL.
            var result = GetSecurityInfo(directory, 1, 0x1 | 0x2 | 0x4, out _, out _, out _, out _, out var descriptor);
            if (result != 0) throw new Win32Exception(unchecked((int)result));
            return descriptor;
        }
        internal static void EnsureAdminDirectory(SafeFileHandle directory, string administratorOwner)
        {
            var pointer = SecurityDescriptor(directory);
            try
            {
                var size = GetSecurityDescriptorLength(pointer);
                var bytes = new byte[checked((int)size)];
                Marshal.Copy(pointer, bytes, 0, bytes.Length);
                var descriptor = new RawSecurityDescriptor(bytes, 0);
                if (descriptor.Owner is null || (descriptor.Owner.Value != "S-1-5-18" && descriptor.Owner.Value != "S-1-5-32-544" && descriptor.Owner.Value != administratorOwner) || descriptor.DiscretionaryAcl is null)
                    throw Failure("leaseinvalid");
                foreach (GenericAce ace in descriptor.DiscretionaryAcl)
                {
                    if (ace is not QualifiedAce qualified || qualified.IsCallback) throw Failure("leaseinvalid");
                    if (qualified.AceQualifier != AceQualifier.AccessAllowed || (qualified.AceFlags & AceFlags.InheritOnly) != 0) continue;
                    // Generic write/all and ACL/owner changes are also unsafe for ordinary principals.
                    const uint unsafeRights = DirectoryMutation | 0x00010000 | 0x00040000 | 0x00080000 | 0x10000000 | 0x40000000;
                    if ((unchecked((uint)qualified.AccessMask) & unsafeRights) != 0 && qualified.SecurityIdentifier.Value is not ("S-1-5-18" or "S-1-5-32-544"))
                        throw Failure("leaseinvalid");
                }
            }
            finally { LocalFree(pointer); }
        }
        internal static void EnsureNoUserWrite(SafeFileHandle directory, SafeAccessTokenHandle token)
        {
            var descriptor = SecurityDescriptor(directory);
            var privileges = Marshal.AllocHGlobal(4096);
            try
            {
                var mapping = new GenericMapping { Read = 0x00120089, Write = 0x00120116, Execute = 0x001200A0, All = 0x001F01FF };
                foreach (var right in new uint[] { 0x0002, 0x0004, 0x0010, 0x0040, 0x0100, 0x00010000, 0x00040000, 0x00080000 })
                {
                    var length = 4096;
                    if (!AccessCheck(descriptor, token, right, ref mapping, privileges, ref length, out _, out var allowed))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (allowed) throw Failure("leaseinvalid");
                }
            }
            finally { Marshal.FreeHGlobal(privileges); LocalFree(descriptor); }
        }

        [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass, out BasicProcessInformation information, int size, out int returned);
        [DllImport("ntdll.dll")] private static extern uint RtlNtStatusToDosError(int status);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern int GetProcessId(SafeProcessHandle process);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder path, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process, out FileTimes creation, out FileTimes exit, out FileTimes kernel, out FileTimes user);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, IntPtr buffer, int length, out int needed);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, IntPtr attributes, int impersonation, int type, out SafeAccessTokenHandle duplicate);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CheckTokenMembership(SafeAccessTokenHandle token, byte[] sid, [MarshalAs(UnmanagedType.Bool)] out bool member);
        [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, SafeAccessTokenHandle token, out IntPtr path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr attributes, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out AttributeTag information, int size);
        [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(SafeFileHandle handle, int objectType, uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
        [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AccessCheck(IntPtr descriptor, SafeAccessTokenHandle token, uint access, ref GenericMapping mapping, IntPtr privileges, ref int length, out uint granted, [MarshalAs(UnmanagedType.Bool)] out bool allowed);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    }
}
