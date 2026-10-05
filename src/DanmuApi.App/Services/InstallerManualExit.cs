using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using DanmuApi.Platform;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using LeaseNative = DanmuApi.App.Services.InstallerUpdateLease.Native;

namespace DanmuApi.App.Services;

/// <summary>
/// Elevated, signed Setup's manual-install helper. It never launches an endpoint executable,
/// selects a linked token, or substitutes an IPC acknowledgement for actual process exit.
/// </summary>
internal static class InstallerManualExit
{
    public const string Argument = "--installer-manual-exit";
    public const string HelperExecutableName = "DanmuApi.InstallExit.exe";
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan FinishTimeout = TimeSpan.FromMinutes(15);

    public static int Run(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) throw Failure("platformunsupported");
            var request = ParseArguments(args);
            return RunCore(new WindowsOperations(request), ExitTimeout, FinishTimeout);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(Diagnostic.From("arguments", error).Format());
            return 1;
        }
    }

    internal sealed record Request(int SetupPid, string TargetDirectory);
    internal sealed record ShutdownResult(bool Succeeded, string? FailureCode = null, string? FailureType = null, int? FailureHResult = null)
    {
        internal string Reason => FailureCode switch
        {
            "commandrejected" => "exitrejected",
            "endpointowner" => "endpointowner",
            _ => "operationfailed"
        };
    }

    internal sealed record Diagnostic(string Phase, string Reason, string Type, int HResult)
    {
        internal static Diagnostic From(string phase, Exception error)
        {
            var hresult = error.HResult;
            for (Exception? current = error; current is not null; current = current.InnerException)
            {
                if (current is not Win32Exception native) continue;
                if (native.NativeErrorCode is >= 0 and <= 65535)
                    hresult = native.NativeErrorCode == 0 ? 0 : unchecked((int)(0x80070000u | (uint)native.NativeErrorCode));
                break;
            }
            var reason = error is ManualExitException failure ? failure.Reason :
                error is InstallerUpdateLease.LeaseException lease ? lease.Reason switch
                {
                    "pathinvalid" => "pathinvalid",
                    "knownfolderinvalid" => "profileinvalid",
                    "installidentity" => "installidentity",
                    "parentidentity" => "targetidentity",
                    "leaseinvalid" => "securityinvalid",
                    _ => "operationfailed"
                } : error is OperationCanceledException ? "cancelled" : "operationfailed";
            var type = error is ResultException shutdown ? SafeFailureType(shutdown.Result.FailureType) : error.GetType().Name;
            if (error is ResultException ipc && ipc.Result.FailureHResult is { } nativeHResult) hresult = nativeHResult;
            return new(phase, reason, type, hresult);
        }
        internal string Format() => $"installer-manual-exit phase={Phase} reason={Reason} type={Type} HRESULT=0x{HResult:X8}; {ReasonText(Reason)}";
    }

    private static string SafeFailureType(string? type) => type is "Win32Exception" or "IOException" or "SocketException" or
        "ObjectDisposedException" or "OperationCanceledException" or "UnauthorizedAccessException" or "FormatException" or
        "FileNotFoundException" or "DirectoryNotFoundException" or "EndOfStreamException" or "DecoderFallbackException" or
        "InvalidOperationException" or "SecurityException" or "ArgumentException" or "ArgumentOutOfRangeException" or
        "PlatformNotSupportedException" ? type : nameof(ManualExitException);

    private sealed class ResultException(ShutdownResult result, string? reason = null) : ManualExitException(reason ?? result.Reason)
    {
        internal ShutdownResult Result { get; } = result;
    }

    internal class ManualExitException : Exception
    {
        internal string Reason { get; }
        internal ManualExitException(string reason, Exception? inner = null) : base(ReasonText(reason), inner)
        {
            Reason = reason;
            if (inner is not null) HResult = inner.HResult;
        }
    }

    internal interface ILease
    {
        AppInstanceLockResult Release();
    }

    internal interface IOperations : IDisposable
    {
        TimeSpan Elapsed { get; }
        bool HasTarget { get; }
        void Verify();
        bool SetupExited();
        bool TargetExited();
        void WriteRunning();
        bool Requested();
        bool Finished();
        ShutdownResult RequestExit();
        ILease Acquire();
        void WritePrepared();
        void EnsureNoNewTargets();
        void WriteReleased();
        void Delay();
        void ReportFailure(IReadOnlyList<Diagnostic> diagnostics);
    }

    // The fake seam tests ordering without using Setup, application entry points, or real user processes.
    internal static int RunCore(IOperations operations, TimeSpan exitTimeout, TimeSpan finishTimeout,
        CancellationToken cancellationToken = default)
    {
        var phase = "verify";
        var diagnostics = new List<Diagnostic>();
        ILease? lease = null;
        var protectionEstablished = false;
        var approvalDenied = false;
        try
        {
            operations.Verify();
            CheckSetup(operations, cancellationToken);
            if (operations.HasTarget)
            {
                phase = "running";
                operations.WriteRunning();
                phase = "confirm-wait";
                while (true)
                {
                    CheckSetup(operations, cancellationToken);
                    // Refusal wins even when request and finish appear together. No dispatch before approval.
                    if (operations.Finished())
                    {
                        approvalDenied = true;
                        throw Failure("cancelled");
                    }
                    if (operations.Requested()) break;
                    operations.Delay();
                }
                phase = "request-exit";
                CheckSetup(operations, cancellationToken);
                if (operations.Finished()) throw Failure("cancelled");
                if (!operations.TargetExited())
                {
                    try
                    {
                        var result = operations.RequestExit();
                        if (!result.Succeeded) throw new ResultException(result);
                    }
                    catch (Exception error)
                    {
                        // A raced endpoint failure is not an exit proof. Only the pinned handle's wait-zero is.
                        if (!operations.TargetExited()) throw;
                        Console.Error.WriteLine(Diagnostic.From(phase, error).Format());
                    }
                }
                phase = "target-wait";
                var deadline = operations.Elapsed + exitTimeout;
                while (true)
                {
                    CheckSetup(operations, cancellationToken);
                    if (operations.Finished()) throw Failure("cancelled");
                    if (operations.TargetExited()) break;
                    if (operations.Elapsed >= deadline) throw Failure("targettimeout");
                    operations.Delay();
                }
                phase = "acquire";
                CheckSetup(operations, cancellationToken);
                if (operations.Finished()) throw Failure("cancelled");
                // No preflight/open-file test can authorize acquisition while the original process is alive.
                if (!operations.TargetExited()) throw Failure("targetalive");
                lease = operations.Acquire() ?? throw Failure("acquirefailed");
            }
            // With no exact installed executable running, retain the protected target-directory pins only.
            // There is no original token and we make no claim about any other profile's instance locks.
            protectionEstablished = true;
            phase = "prepared";
            CheckSetup(operations, cancellationToken);
            operations.WritePrepared();
            phase = "finish-wait";
            var finishDeadline = operations.Elapsed + finishTimeout;
            while (true)
            {
                CheckSetup(operations, cancellationToken);
                operations.EnsureNoNewTargets();
                if (operations.Finished()) break;
                if (operations.Elapsed >= finishDeadline) throw Failure("finishtimeout");
                operations.Delay();
            }
            phase = "release";
            if (lease is not null)
            {
                var release = lease.Release();
                lease = null;
                if (!release.Succeeded) throw Failure("lockreleasefail");
            }
            protectionEstablished = false;
            phase = "released";
            operations.WriteReleased();
        }
        catch (Exception error)
        {
            diagnostics.Add(Diagnostic.From(phase, error));
        }
        finally
        {
            if (diagnostics.Count != 0) Report(operations, diagnostics);
            if (lease is not null || protectionEstablished)
            {
                // Publish failure first; neither timeout nor cancellation is permission to release protections.
                WaitForSafeRelease(operations, diagnostics);
                if (lease is not null)
                {
                    try
                    {
                        if (!lease.Release().Succeeded) AddDiagnostic(diagnostics, Diagnostic.From("cleanup", Failure("lockreleasefail")));
                    }
                    catch (Exception error) { AddDiagnostic(diagnostics, Diagnostic.From("cleanup", new ManualExitException("lockreleasefail", error))); }
                }
            }
            if (approvalDenied)
            {
                try { operations.WriteReleased(); }
                catch (Exception error) { AddDiagnostic(diagnostics, Diagnostic.From("released", error)); }
            }
            try { operations.Dispose(); }
            catch (Exception error) { AddDiagnostic(diagnostics, Diagnostic.From("cleanup", new ManualExitException("cleanupfailed", error))); }
        }
        return diagnostics.Count == 0 ? 0 : approvalDenied && diagnostics.Count == 1 ? 2 : 1;
    }

    private static void CheckSetup(IOperations operations, CancellationToken cancellationToken)
    {
        if (operations.SetupExited()) throw Failure("setupdead");
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void WaitForSafeRelease(IOperations operations, List<Diagnostic> diagnostics)
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            // Only the original Setup handle or its fixed protected finish file authorizes release.
            try { if (operations.SetupExited()) return; }
            catch (Exception error) { Record("setup-monitor", error); }
            try { if (operations.Finished()) return; }
            catch (Exception error) { Record("finish-monitor", error); }
            try { operations.Delay(); }
            catch (Exception error) { Record("delay-monitor", error); }
        }
        void Record(string phase, Exception error)
        {
            var diagnostic = Diagnostic.From(phase, error);
            if (reported.Add($"{phase}:{diagnostic.Reason}:{diagnostic.Type}:{diagnostic.HResult}")) AddDiagnostic(diagnostics, diagnostic);
        }
    }

    private static void AddDiagnostic(List<Diagnostic> diagnostics, Diagnostic diagnostic)
    {
        diagnostics.Add(diagnostic);
        Console.Error.WriteLine(diagnostic.Format());
    }

    private static void Report(IOperations operations, List<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics) Console.Error.WriteLine(diagnostic.Format());
        try { operations.ReportFailure(diagnostics); }
        catch (Exception error) { AddDiagnostic(diagnostics, Diagnostic.From("diagnostic", new ManualExitException("diagnosticwritefail", error))); }
    }

    internal static Request ParseArguments(string[] args)
    {
        if (args.Length != 3 || args[0] != Argument ||
            !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var setup) || setup <= 0 || setup == Environment.ProcessId)
            throw Failure("argumentsinvalid");
        _ = InstallerUpdateLease.FullPath(args[2]); // Apply the existing local/ADS/fully-qualified validation only.
        return new(setup, Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[2])));
    }

    internal static void ValidateOriginalToken(InstallerUpdateLease.TokenIdentity original, InstallerUpdateLease.TokenIdentity duplicate)
    {
        // Full / unlinked Administrator tokens are valid. Do not de-elevate, select a linked token, or replace SID/session.
        if (original != duplicate) throw Failure("tokenidentity");
    }

    internal static ShutdownResult SendAfterEndpointValidation(Func<IDisposable> validateEndpoint, Func<ShutdownResult> sendCommand)
    {
        // A READ data handle without FILE_SHARE_DELETE would obstruct the original owner's normal cleanup.
        // Validation is complete before IPC; SendCommand re-reads under the original token and binds the connected PID.
        using (validateEndpoint() ?? throw Failure("endpointinvalid")) { }
        return sendCommand();
    }

    internal static void ValidateSignal(string name, byte[] bytes)
    {
        if (name is not ("request" or "finish") || bytes.Length > 256) throw Failure("stateinvalid");
        try
        {
            if (!string.Equals(new UTF8Encoding(false, true).GetString(bytes), name, StringComparison.Ordinal)) throw Failure("stateinvalid");
        }
        catch (DecoderFallbackException error) { throw new ManualExitException("stateinvalid", error); }
    }

    internal static void ValidateProfileOwner(RawSecurityDescriptor descriptor, string originalSid)
    {
        if (descriptor.Owner is null || !TrustedProfileSid(descriptor.Owner.Value, originalSid)) throw Failure("profileinvalid");
    }

    private static bool TrustedProfileSid(string sid, string originalSid) => sid == originalSid || sid is "S-1-5-18" or "S-1-5-32-544";

    internal static void ValidateProfileSecurity(RawSecurityDescriptor descriptor, string originalSid)
    {
        ValidateProfileOwner(descriptor, originalSid);
        if (descriptor.DiscretionaryAcl is null) throw Failure("profileinvalid");
        foreach (GenericAce ace in descriptor.DiscretionaryAcl)
        {
            if (ace is not CommonAce common || common.IsCallback) throw Failure("profileinvalid");
            if (common.AceQualifier != AceQualifier.AccessAllowed || (common.AceFlags & AceFlags.InheritOnly) != 0) continue;
            const uint mutations = 0x0002 | 0x0004 | 0x0010 | 0x0040 | 0x0100 | 0x00010000 | 0x00040000 | 0x00080000 | 0x10000000 | 0x40000000;
            if ((unchecked((uint)common.AccessMask) & mutations) != 0 && !TrustedProfileSid(common.SecurityIdentifier.Value, originalSid)) throw Failure("profileinvalid");
        }
    }

    internal static void PublishMarker(string directory, string name, string value, Action? beforePublish = null)
    {
        if (name is not ("running" or "prepared" or "error.txt" or "released")) throw Failure("stateinvalid");
        var destination = Path.Combine(directory, name);
        var temporary = Path.Combine(directory, name + ".tmp");
        if (InstallerUpdateLease.PlainExists(destination) || InstallerUpdateLease.PlainExists(temporary)) throw Failure("stateinvalid");
        var created = false;
        try
        {
            InstallerUpdateLease.RejectReparse(temporary, optional: true);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                stream.Write(new UTF8Encoding(false, true).GetBytes(value));
                stream.Flush(flushToDisk: true);
            }
            beforePublish?.Invoke();
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
                    throw new ManualExitException("markercleanupfailed", new AggregateException(error, cleanup));
                }
            }
            throw;
        }
    }

    private static ManualExitException Failure(string reason) => new(reason);
    private static string ReasonText(string reason) => reason switch
    {
        "argumentsinvalid" => "Expected Setup PID and a fully qualified local installation directory.",
        "platformunsupported" => "Windows process, token and security APIs are required.",
        "installidentity" => "Setup must be the actual pinned creator, elevated administrator, and in the helper session.",
        "installertrust" => "The pinned Setup image must have the trusted publisher signature in a protected plain directory.",
        "helpertrust" => "The signed helper must run from the protected plain extraction directory.",
        "stateinvalid" => "The fixed manual-exit state directory and its files must be protected, plain, and fresh.",
        "securityinvalid" => "Protected files and directories must have trusted ownership and no ordinary-principal mutation rights.",
        "targetinvalid" => "The target must be the registered HKLM installation with a trusted signed plain executable.",
        "targetidentity" => "The pinned application image does not match the exact installed executable.",
        "targetmultiple" => "Multiple exact target instances are running; exit them manually before retrying Setup.",
        "targetsession" => "The exact target belongs to another session; exit it manually before retrying Setup.",
        "targetalive" => "The original pinned application is still alive; free lock files are not an exit proof.",
        "targetrace" => "An exact target instance started after verification; no prepared proof was published.",
        "newinstance" => "A new exact target instance appeared after preparation; Setup must stop copying and protections remain held.",
        "enumerationfailed" => "The exact target process set could not be established safely.",
        "tokenidentity" => "The impersonation token must retain the original instance SID, session, elevation, and elevation type.",
        "tokenmissing" => "The original instance token could not be captured or duplicated for impersonation.",
        "profileinvalid" => "Explicit-original-token profile paths, ownership or access permissions are not trustworthy.",
        "endpointinvalid" => "The original-user IPC endpoint must be plain, protected, bounded, and identify the installed executable.",
        "endpointowner" => "The connected IPC owner must be the pinned original process; no command was sent to another owner.",
        "exitrejected" => "The original instance rejected REQUEST_EXIT; older versions such as 0.5.13 require manual safe exit. No process was killed.",
        "targettimeout" => "The original pinned process did not actually exit within 120 seconds; no prepared proof was published.",
        "instanceheld" => "Another instance holds a modern or legacy lock after the original process actually exited.",
        "acquirefailed" => "Both original-user instance locks could not be acquired.",
        "setupdead" => "The pinned Setup process exited before normal finish was received.",
        "cancelled" => "The request was cancelled; cancellation is not permission to release prepared protections.",
        "finishtimeout" => "Setup finish was not received in time; protections remain held until finish or pinned Setup exit.",
        "lockreleasefail" => "An instance lock could not be released cleanly.",
        "cleanupfailed" => "Helper resources could not be disposed cleanly.",
        "diagnosticwritefail" => "The protected failure diagnostic could not be published; stderr retains the failure.",
        "markercleanupfailed" => "A failed marker publication could not remove its temporary file.",
        _ => "The operation failed explicitly; no token text, forced exit, or substitute success proof was used."
    };

    private sealed class WindowsOperations(Request request) : IOperations
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<SafeFileHandle> _adminPins = [];
        private readonly List<SafeFileHandle> _userPins = [];
        private readonly List<FileStream> _adminImages = [];
        private readonly string _stateDirectory = Path.Combine(AppContext.BaseDirectory, "manual-exit");
        private SafeProcessHandle? _setup;
        private SafeProcessHandle? _target;
        private SafeAccessTokenHandle? _originalToken;
        private InstallerUpdateLease.TokenIdentity? _targetIdentity;
        private AppPaths? _paths;
        private Native.FileIdentity _targetFileIdentity;
        private string? _nativeTargetPath;
        private string? _targetImagePath;
        private string? _setupSid;
        private string? _targetPinDirectory;
        private SafeFileHandle? _targetDirectoryPin;
        private bool _hasTargetImage;
        private int _targetPid;
        private bool _trustedState;
        private bool _verified;

        public TimeSpan Elapsed => _clock.Elapsed;
        public bool HasTarget => _target is not null;

        public void Verify()
        {
            _setup = LeaseNative.OpenPinnedProcess(request.SetupPid);
            using var setupToken = LeaseNative.OpenToken(_setup);
            var setupIdentity = LeaseNative.DescribeToken(setupToken);
            using var helperProcess = LeaseNative.OpenPinnedProcess(Environment.ProcessId);
            using var helperToken = LeaseNative.OpenToken(helperProcess);
            if (LeaseNative.CreatorPid() != request.SetupPid || LeaseNative.StartTicks(_setup) > LeaseNative.StartTicks(helperProcess) ||
                !setupIdentity.Elevated || !LeaseNative.IsAdministrator(setupToken) ||
                setupIdentity.Session != LeaseNative.DescribeToken(helperToken).Session || LeaseNative.Exited(_setup))
                throw Failure("installidentity");
            _setupSid = setupIdentity.Sid;

            Guard("helpertrust", () =>
            {
                PinDirectoryChain(AppContext.BaseDirectory, _adminPins);
                LeaseNative.EnsureAdminDirectory(_adminPins[^1], setupIdentity.Sid);
                var helperImage = LeaseNative.ImagePath(helperProcess);
                if (!string.Equals(Path.GetFileName(helperImage), HelperExecutableName, StringComparison.OrdinalIgnoreCase)) throw Failure("helpertrust");
                var image = InstallerUpdateLease.OpenPlainRead(Path.Combine(AppContext.BaseDirectory, HelperExecutableName));
                _adminImages.Add(image);
                if (!string.Equals(Native.FinalNativePath(image.SafeFileHandle), Native.NativeImagePath(helperProcess), StringComparison.OrdinalIgnoreCase))
                    throw Failure("helpertrust");
                LeaseNative.EnsureAdminDirectory(image.SafeFileHandle, setupIdentity.Sid);
                AppUpdateTrust.VerifyExecutable(helperImage);
            });
            Guard("stateinvalid", () =>
            {
                // No external path argument, alias guessing, or profile directory is involved.
                PinDirectoryChain(_stateDirectory, _adminPins);
                LeaseNative.EnsureAdminDirectory(_adminPins[^1], setupIdentity.Sid);
                foreach (var name in new[] { "running", "prepared", "error.txt", "released", "request", "finish", "running.tmp", "prepared.tmp", "error.txt.tmp", "released.tmp" })
                    if (InstallerUpdateLease.PlainExists(Path.Combine(_stateDirectory, name))) throw Failure("stateinvalid");
                _trustedState = true;
            });
            Guard("installertrust", () =>
            {
                var setupImage = LeaseNative.ImagePath(_setup);
                PinDirectoryChain(Path.GetDirectoryName(setupImage) ?? throw Failure("installertrust"), _adminPins);
                LeaseNative.EnsureAdminDirectory(_adminPins[^1], setupIdentity.Sid);
                var image = InstallerUpdateLease.OpenPlainRead(setupImage);
                _adminImages.Add(image);
                LeaseNative.EnsureAdminDirectory(image.SafeFileHandle, setupIdentity.Sid);
                AppUpdateTrust.VerifyExecutable(setupImage);
            });
            Guard("targetinvalid", VerifyTargetLayout);
            var candidates = FindExactTargets();
            try
            {
                if (candidates.Count > 1) throw Failure("targetmultiple");
                if (candidates.Count == 1)
                {
                    _target = candidates[0].Handle;
                    _targetPid = candidates[0].Pid;
                    _targetImagePath = candidates[0].Path;
                    candidates.Clear();
                    Guard("targetinvalid", VerifyInstalledTarget);
                    Guard("tokenmissing", () => _originalToken = Native.CaptureOriginalToken(_target));
                    _targetIdentity = LeaseNative.DescribeToken(_originalToken!);
                    if (_targetIdentity.Session != setupIdentity.Session) throw Failure("targetsession");
                    Guard("profileinvalid", VerifyProfile);
                }
            }
            finally { foreach (var candidate in candidates) candidate.Handle.Dispose(); }
            if (LeaseNative.Exited(_setup)) throw Failure("setupdead");
            _verified = true;
        }

        private void VerifyTargetLayout()
        {
            RefreshTargetDirectoryPins();
            using var targetImage = OpenTargetImageIfPresent();
            if (targetImage is null) return; // Only an actual not-found result permits a new installation.
            _hasTargetImage = true;
            _targetFileIdentity = Native.Identity(targetImage.SafeFileHandle);
            _nativeTargetPath = Native.FinalNativePath(targetImage.SafeFileHandle);
            AppUpdateTrust.VerifyExecutable(Path.Combine(request.TargetDirectory, "DanmuApi.App.exe"));
        }

        private FileStream? OpenTargetImageIfPresent()
        {
            try { return InstallerUpdateLease.OpenPlainRead(Path.Combine(request.TargetDirectory, "DanmuApi.App.exe")); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        private void RefreshTargetDirectoryPins()
        {
            if (_targetPinDirectory is null)
            {
                var root = Path.GetPathRoot(request.TargetDirectory) ?? throw Failure("pathinvalid");
                var pin = Native.OpenPinnedDirectory(root);
                try { LeaseNative.EnsurePlainHandle(pin, directory: true); _adminPins.Add(pin); }
                catch { pin.Dispose(); throw; }
                _targetPinDirectory = root;
                _targetDirectoryPin = pin;
            }
            var remaining = Path.GetRelativePath(_targetPinDirectory, request.TargetDirectory);
            if (remaining != ".")
            {
                foreach (var segment in remaining.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var next = Path.Combine(_targetPinDirectory, segment);
                    SafeFileHandle pin;
                    try { pin = Native.OpenPinnedDirectory(next); }
                    catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { break; }
                    try { LeaseNative.EnsurePlainHandle(pin, directory: true); _adminPins.Add(pin); }
                    catch { pin.Dispose(); throw; }
                    _targetPinDirectory = next;
                    _targetDirectoryPin = pin;
                }
            }
            // Real existing ancestor name from its handle; nonexistent suffixes have no short/long aliases to guess.
            remaining = Path.GetRelativePath(_targetPinDirectory, request.TargetDirectory);
            var nativeDirectory = Native.FinalNativePath(_targetDirectoryPin!).TrimEnd('\\');
            _nativeTargetPath = nativeDirectory + (remaining == "." ? string.Empty : "\\" + remaining) + "\\DanmuApi.App.exe";
        }

        private void VerifyInstalledTarget()
        {
            if (!_hasTargetImage) throw Failure("targetinvalid");
            // Registry is required only for an existing exact process; a new/no-process installation may be unregistered.
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, ApplicationUpdateHelper.InstallationRegistryView);
            using var key = hklm.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{D6F1E4DB-9C73-4DE2-B985-9DAF96B8E9B4}_is1");
            var installedDirectory = key?.GetValue("InstallLocation") as string ?? throw Failure("targetinvalid");
            var registryPins = new List<SafeFileHandle>();
            try
            {
                PinDirectoryChain(InstallerUpdateLease.FullPath(installedDirectory), registryPins);
                if (Native.Identity(registryPins[^1]) != Native.Identity(_targetDirectoryPin!)) throw Failure("targetinvalid");
                using var installedImage = InstallerUpdateLease.OpenPlainRead(Path.Combine(installedDirectory, "DanmuApi.App.exe"));
                if (Native.Identity(installedImage.SafeFileHandle) != _targetFileIdentity) throw Failure("targetinvalid");
            }
            finally { foreach (var pin in registryPins) pin.Dispose(); }
        }

        private void VerifyProfile() => AsUser(() =>
        {
            var local = LeaseNative.KnownFolder(new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091"), _originalToken!);
            var roaming = LeaseNative.KnownFolder(new Guid("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D"), _originalToken!);
            foreach (var folder in new[] { local, roaming })
            {
                PinDirectoryChain(folder, _userPins);
                Native.EnsureProfileSecurity(_userPins[^1], _targetIdentity!.Sid, ownershipOnly: true);
            }
            _paths = new AppPaths(Path.Combine(local, "DanmuApi"), Path.Combine(roaming, "DanmuApi"));
            PinDirectoryChain(_paths.SettingsDirectory, _userPins);
            Native.EnsureProfileSecurity(_userPins[^1], _targetIdentity!.Sid);
            // The matching process's actual image is only opened under its own token.
            using (var image = InstallerUpdateLease.OpenPlainRead(_targetImagePath!))
                if (Native.Identity(image.SafeFileHandle) != _targetFileIdentity) throw Failure("targetidentity");
            foreach (var path in new[] { _paths.InstanceLockFile, LegacyLockPath }) InstallerUpdateLease.RejectReparse(path, optional: true);
            using var endpoint = OpenTrustedEndpoint();
        });

        public bool SetupExited() => LeaseNative.Exited(_setup ?? throw Failure("installidentity"));
        public bool TargetExited() => LeaseNative.Exited(_target ?? throw Failure("targetidentity"));
        public void WriteRunning() { EnsureVerified(); WriteTrusted("running", "running"); }
        public bool Requested() => ReadSignal("request");
        public bool Finished() => ReadSignal("finish");

        public ShutdownResult RequestExit() => AsVerifiedUser(() =>
        {
            if (TargetExited()) throw Failure("targetidentity");
            return SendAfterEndpointValidation(OpenTrustedEndpoint, () =>
            {
                // The platform overload binds the connected socket owner and dispatches REQUEST_EXIT only once.
                // No server is started; Release on this sender cannot delete somebody else's endpoint (token is null).
                var sender = new AppInstanceLock(_paths!, _ => Console.Error.WriteLine(Diagnostic.From("ipc-detail", Failure("operationfailed")).Format()));
                var result = sender.SendCommand(InstanceCommand.REQUEST_EXIT, expectedProcessId: _targetPid);
                return new ShutdownResult(result.Succeeded, result.FailureCode, result.FailureType, result.FailureHResult);
            });
        });

        public ILease Acquire() => AsVerifiedUser<ILease>(() =>
        {
            if (!TargetExited()) throw Failure("targetalive");
            var instance = new AppInstanceLock(_paths!, _ => Console.Error.WriteLine(Diagnostic.From("lock-detail", Failure("acquirefailed")).Format()));
            var pins = new List<SafeFileHandle>();
            var acquired = false;
            try
            {
                foreach (var path in new[] { _paths!.InstanceLockFile, LegacyLockPath })
                {
                    InstallerUpdateLease.RejectReparse(path, optional: true);
                    var pin = Native.PinLockMetadata(path);
                    try
                    {
                        LeaseNative.EnsurePlainHandle(pin, directory: false);
                        Native.EnsureProfileSecurity(pin, _targetIdentity!.Sid);
                        pins.Add(pin);
                    }
                    catch { pin.Dispose(); throw; }
                }
                var result = instance.TryAcquireDetailed();
                if (!result.Succeeded) throw new ResultException(
                    new ShutdownResult(false, result.FailureCode, result.FailureType, result.FailureHResult),
                    result.AlreadyOwned ? "instanceheld" : "acquirefailed");
                acquired = true;
                return new UserLease(instance, _originalToken!, pins);
            }
            finally
            {
                if (!acquired)
                {
                    try { if (!instance.Release().Succeeded) throw Failure("lockreleasefail"); }
                    finally { foreach (var pin in pins) pin.Dispose(); }
                }
            }
        });

        public void WritePrepared()
        {
            EnsureVerified();
            if (HasTarget && !TargetExited()) throw Failure("targetalive");
            RefreshTargetDirectoryPins();
            var targets = FindExactTargets();
            try { if (targets.Count != 0) throw Failure("targetrace"); }
            finally { foreach (var target in targets) target.Handle.Dispose(); }
            WriteTrusted("prepared", "prepared");
        }
        public void EnsureNoNewTargets()
        {
            EnsureVerified();
            RefreshTargetDirectoryPins();
            var targets = FindExactTargets();
            try { if (targets.Count != 0) throw Failure("newinstance"); }
            finally { foreach (var target in targets) target.Handle.Dispose(); }
        }
        public void WriteReleased() { EnsureVerified(); WriteTrusted("released", "released"); }
        public void Delay() => Thread.Sleep(100);
        public void ReportFailure(IReadOnlyList<Diagnostic> diagnostics)
        {
            if (_trustedState) WriteTrusted("error.txt", string.Join(Environment.NewLine, diagnostics.Select(item => item.Format())));
        }

        private bool ReadSignal(string name)
        {
            if (!_trustedState) throw Failure("stateinvalid");
            var path = Path.Combine(_stateDirectory, name);
            if (!InstallerUpdateLease.PlainExists(path)) return false;
            using var stream = InstallerUpdateLease.OpenPlainRead(path);
            LeaseNative.EnsureAdminDirectory(stream.SafeFileHandle, _setupSid ?? throw Failure("installidentity"));
            if (stream.Length > 256) throw Failure("stateinvalid");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            ValidateSignal(name, bytes);
            return true;
        }

        private FileStream OpenTrustedEndpoint()
        {
            var endpoint = InstallerUpdateLease.OpenPlainRead(_paths?.InstanceEndpointFile ?? throw Failure("profileinvalid"));
            try
            {
                Native.EnsureProfileSecurity(endpoint.SafeFileHandle, _targetIdentity!.Sid);
                if (endpoint.Length > 16 * 1024) throw Failure("endpointinvalid");
                using var reader = new StreamReader(endpoint, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                while (reader.ReadLine() is { } line)
                {
                    var separator = line.IndexOf('=');
                    if (separator <= 0 || separator == line.Length - 1 || !fields.TryAdd(line[..separator], line[(separator + 1)..])) throw Failure("endpointinvalid");
                }
                if (!fields.TryGetValue("exe", out var executable)) throw Failure("endpointinvalid");
                var imagePins = new List<SafeFileHandle>();
                try
                {
                    var imagePath = InstallerUpdateLease.FullPath(executable);
                    PinDirectoryChain(Path.GetDirectoryName(imagePath)!, imagePins);
                    using var image = InstallerUpdateLease.OpenPlainRead(imagePath);
                    if (Native.Identity(image.SafeFileHandle) != _targetFileIdentity) throw Failure("endpointinvalid");
                }
                finally { foreach (var pin in imagePins) pin.Dispose(); }
                return endpoint;
            }
            catch (Exception error)
            {
                endpoint.Dispose();
                throw new ManualExitException("endpointinvalid", error);
            }
        }

        private List<(int Pid, SafeProcessHandle Handle, string Path)> FindExactTargets()
        {
            var matches = new List<(int Pid, SafeProcessHandle Handle, string Path)>();
            try
            {
                foreach (var pid in Native.ApplicationPids())
                {
                    var process = LeaseNative.OpenPinnedProcess(pid);
                    var retained = false;
                    try
                    {
                        if (LeaseNative.Exited(process)) continue;
                        // Query the process section's real native path, never an endpoint's executable field.
                        if (!string.Equals(Native.NativeImagePath(process), _nativeTargetPath, StringComparison.OrdinalIgnoreCase)) continue;
                        matches.Add((pid, process, LeaseNative.ImagePath(process)));
                        retained = true;
                    }
                    finally { if (!retained) process.Dispose(); }
                }
                return matches;
            }
            catch (Exception error)
            {
                foreach (var match in matches) match.Handle.Dispose();
                throw new ManualExitException("enumerationfailed", error);
            }
        }

        private string LegacyLockPath => Path.Combine(_paths?.SettingsDirectory ?? throw Failure("profileinvalid"), "app.lock");
        private void WriteTrusted(string name, string value)
        {
            if (!_trustedState) throw Failure("stateinvalid");
            PublishMarker(_stateDirectory, name, value);
        }
        private void EnsureVerified() { if (!_verified) throw Failure("installidentity"); }
        private void AsUser(Action action) => WindowsIdentity.RunImpersonated(_originalToken ?? throw Failure("tokenmissing"), action);
        private T AsVerifiedUser<T>(Func<T> action)
        {
            EnsureVerified();
            return WindowsIdentity.RunImpersonated(_originalToken ?? throw Failure("tokenmissing"), action);
        }
        public void Dispose()
        {
            try
            {
                if (_originalToken is not null && !_originalToken.IsInvalid)
                    AsUser(() => { foreach (var pin in _userPins) pin.Dispose(); });
            }
            finally
            {
                try { _originalToken?.Dispose(); }
                finally
                {
                    try { _target?.Dispose(); }
                    finally
                    {
                        try { _setup?.Dispose(); }
                        finally { try { foreach (var image in _adminImages) image.Dispose(); } finally { foreach (var pin in _adminPins) pin.Dispose(); } }
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

    private static void Guard(string reason, Action action)
    {
        try { action(); }
        catch (ManualExitException) { throw; }
        catch (Exception error) { throw new ManualExitException(reason, error); }
    }

    private static void PinDirectoryChain(string path, List<SafeFileHandle> pins)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(full) ?? throw Failure("pathinvalid");
        var chain = new Stack<string>();
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            chain.Push(current);
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
        }
        while (chain.TryPop(out var directory)) pins.Add(Native.OpenPinnedDirectory(directory));
    }

    // Native test hooks query the testhost and explicitly test-owned temporary files only.
    // They never enter the real controller or visit real user-profile/runtime settings.
    internal static SafeFileHandle OpenPinnedDirectoryForTest(AppPaths testPaths) => Native.OpenPinnedDirectory(testPaths.SettingsDirectory);

    internal static void WithCurrentOriginalTokenLockPinsForTest(AppPaths testPaths, Action action)
    {
        using var process = LeaseNative.OpenPinnedProcess(Environment.ProcessId);
        using var token = Native.CaptureOriginalToken(process);
        var sid = LeaseNative.DescribeToken(token).Sid;
        WindowsIdentity.RunImpersonated(token, () =>
        {
            using var directoryPin = Native.OpenPinnedDirectory(testPaths.SettingsDirectory);
            var pins = new List<SafeFileHandle>();
            try
            {
                foreach (var path in new[] { testPaths.InstanceLockFile, Path.Combine(testPaths.SettingsDirectory, "app.lock") })
                {
                    var pin = Native.PinLockMetadata(path);
                    try
                    {
                        LeaseNative.EnsurePlainHandle(pin, directory: false);
                        Native.EnsureProfileSecurity(pin, sid);
                        pins.Add(pin);
                    }
                    catch { pin.Dispose(); throw; }
                }
                action();
            }
            finally { foreach (var pin in pins) pin.Dispose(); }
        });
    }

    internal static InstallerUpdateLease.TokenIdentity CaptureCurrentOriginalTokenForTest()
    {
        using var process = LeaseNative.OpenPinnedProcess(Environment.ProcessId);
        using var token = Native.CaptureOriginalToken(process);
        return LeaseNative.DescribeToken(token);
    }
    internal static (InstallerUpdateLease.TokenIdentity Identity, TokenImpersonationLevel Level) ImpersonateCurrentOriginalTokenForTest()
    {
        using var process = LeaseNative.OpenPinnedProcess(Environment.ProcessId);
        using var token = Native.CaptureOriginalToken(process);
        return WindowsIdentity.RunImpersonated(token, () =>
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            return (LeaseNative.DescribeToken(identity.AccessToken), identity.ImpersonationLevel);
        });
    }

    internal static bool CurrentProcessNativeImageMatchesForTest()
    {
        using var process = LeaseNative.OpenPinnedProcess(Environment.ProcessId);
        using var image = InstallerUpdateLease.OpenPlainRead(LeaseNative.ImagePath(process));
        return string.Equals(Native.NativeImagePath(process), Native.FinalNativePath(image.SafeFileHandle), StringComparison.OrdinalIgnoreCase);
    }

    private static class Native
    {
        internal readonly record struct FileIdentity(uint Volume, ulong Index);
        [StructLayout(LayoutKind.Sequential)] private struct FileTimes { public uint Low; public uint High; }
        [StructLayout(LayoutKind.Sequential)] private struct FileInformation
        {
            public uint Attributes;
            public FileTimes Creation;
            public FileTimes Access;
            public FileTimes Write;
            public uint Volume;
            public uint SizeHigh;
            public uint SizeLow;
            public uint Links;
            public uint IndexHigh;
            public uint IndexLow;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ProcessEntry
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public UIntPtr DefaultHeap;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int BasePriority;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
        }
        internal static SafeAccessTokenHandle CaptureOriginalToken(SafeProcessHandle process)
        {
            using var original = LeaseNative.OpenToken(process);
            var identity = LeaseNative.DescribeToken(original);
            if (!DuplicateTokenEx(original, 0x02000000 /* MAXIMUM_ALLOWED */, IntPtr.Zero, 2 /* SecurityImpersonation */, 2 /* TokenImpersonation */, out var token))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try { ValidateOriginalToken(identity, LeaseNative.DescribeToken(token)); return token; }
            catch { token.Dispose(); throw; }
        }
        internal static string NativeImagePath(SafeProcessHandle process)
        {
            var text = new StringBuilder(32768);
            var length = text.Capacity;
            if (!QueryFullProcessImageName(process, 1 /* PROCESS_NAME_NATIVE */, text, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return text.ToString();
        }
        internal static string FinalNativePath(SafeFileHandle file)
        {
            var text = new StringBuilder(32768);
            var length = GetFinalPathNameByHandle(file, text, text.Capacity, 2 /* FILE_NAME_NORMALIZED | VOLUME_NAME_NT */);
            if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (length >= text.Capacity) throw Failure("pathinvalid");
            return text.ToString();
        }
        internal static FileIdentity Identity(SafeFileHandle file)
        {
            if (!GetFileInformationByHandle(file, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new(info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow);
        }
        internal static List<int> ApplicationPids()
        {
            using var snapshot = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
            if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Executable = string.Empty };
            if (!Process32First(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var pids = new List<int>();
            do
            {
                if (string.Equals(entry.Executable, "DanmuApi.App.exe", StringComparison.OrdinalIgnoreCase)) pids.Add(checked((int)entry.ProcessId));
            }
            while (Process32Next(snapshot, ref entry));
            var error = Marshal.GetLastWin32Error();
            if (error != 18 /* ERROR_NO_MORE_FILES */) throw new Win32Exception(error);
            return pids;
        }
        internal static SafeFileHandle OpenPinnedDirectory(string path)
        {
            // FILE_LIST_DIRECTORY participates in sharing checks; metadata-only access cannot pin a name.
            // Without FILE_SHARE_DELETE the directory cannot be renamed/replaced, but its children remain writable.
            var pin = CreateFile(path, 0x1 | 0x00020000 | 0x80 /* LIST_DIRECTORY | READ_CONTROL | READ_ATTRIBUTES */, 3,
                IntPtr.Zero, 3 /* OPEN_EXISTING */, 0x02000000 | 0x00200000 /* BACKUP_SEMANTICS | OPEN_REPARSE_POINT */, IntPtr.Zero);
            if (pin.IsInvalid) { var error = Marshal.GetLastWin32Error(); pin.Dispose(); throw new Win32Exception(error); }
            try { LeaseNative.EnsurePlainHandle(pin, directory: true); return pin; }
            catch { pin.Dispose(); throw; }
        }

        internal static SafeFileHandle PinLockMetadata(string path)
        {
            // No data read/write access: metadata pins remain compatible with the modern exclusive data handle.
            var pin = CreateFile(path, 0x00020000 | 0x80 /* READ_CONTROL | FILE_READ_ATTRIBUTES */, 3,
                IntPtr.Zero, 4 /* OPEN_ALWAYS */, 0x00200000 /* OPEN_REPARSE_POINT */, IntPtr.Zero);
            if (pin.IsInvalid) { var error = Marshal.GetLastWin32Error(); pin.Dispose(); throw new Win32Exception(error); }
            return pin;
        }
        internal static void EnsureProfileSecurity(SafeFileHandle file, string sid, bool ownershipOnly = false)
        {
            var result = GetSecurityInfo(file, 1, 0x1 | 0x2 | 0x4, out _, out _, out _, out _, out var descriptor);
            if (result != 0) throw new Win32Exception(unchecked((int)result));
            try
            {
                var bytes = new byte[checked((int)GetSecurityDescriptorLength(descriptor))];
                Marshal.Copy(descriptor, bytes, 0, bytes.Length);
                var security = new RawSecurityDescriptor(bytes, 0);
                if (ownershipOnly) ValidateProfileOwner(security, sid);
                else ValidateProfileSecurity(security, sid);
            }
            finally { LocalFree(descriptor); }
        }

        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, IntPtr attributes, int level, int type, out SafeAccessTokenHandle duplicate);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder text, ref int length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder text, int capacity, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr attributes, uint disposition, uint flags, IntPtr template);
        [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(SafeFileHandle file, int objectType, uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
        [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    }
}
