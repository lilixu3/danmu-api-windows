using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DanmuApi.App.Services;
using DanmuApi.Platform;
using Microsoft.Win32.SafeHandles;

namespace DanmuApi.Tests;

public sealed class InstallerUpdateLeaseTests
{
    private static readonly TimeSpan ParentTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FinishTimeout = TimeSpan.FromSeconds(4);

    [Fact]
    public void RequiresExactlySixArgumentsAndDistinctPositivePids()
    {
        var request = InstallerUpdateLease.ParseArguments(ValidArguments());
        Assert.Equal(123, request.ParentPid);
        Assert.Equal(456, request.InstallerPid);
        foreach (var args in new[]
        {
            Array.Empty<string>(), ValidArguments()[..5], ValidArguments().Append("extra").ToArray(),
            ChangedArgument(0, "--installer-instance-probe"), ChangedArgument(1, "0"), ChangedArgument(1, "-1"),
            ChangedArgument(1, " 123"), ChangedArgument(2, "123"), ChangedArgument(3, @"relative\app"),
            ChangedArgument(4, @"\\server\share\installer.ready"), ChangedArgument(5, @"C:\relay\update-lease:stream")
        })
            Assert.Throws<InstallerUpdateLease.LeaseException>(() => InstallerUpdateLease.ParseArguments(args));
    }

    [Fact]
    public void HandshakeAndPreparedFollowVerificationParentExitAndBothLocks()
    {
        var operations = new FakeOperations { ParentExitAt = TimeSpan.FromSeconds(1), FinishAt = TimeSpan.FromSeconds(3) };
        Assert.Equal(0, Run(operations));
        Assert.Equal(new[] { "verify", "probe", "ready", "parent-exited", "acquire", "prepared", "finish", "release", "released", "dispose" }, operations.Events);
        Assert.True(operations.PreparedWhileHeld);
        Assert.True(operations.HeldWhenFinishObserved);
        Assert.False(operations.Held);
        Assert.Empty(operations.Diagnostics);
    }

    [Theory]
    [InlineData("parentidentity")]
    [InlineData("tokenmissing")]
    [InlineData("tokenidentity")]
    [InlineData("readyinvalid")]
    [InlineData("jobinvalid")]
    [InlineData("leaseinvalid")]
    public void VerificationFailureCannotWriteReadyOrLockProof(string reason)
    {
        var operations = new FakeOperations { VerifyError = new InstallerUpdateLease.LeaseException(reason) };
        Assert.Equal(1, Run(operations));
        Assert.Equal(new[] { "verify", "error", "dispose" }, operations.Events);
        AssertFailure(operations, "verify", reason);
    }

    [Fact]
    public void ParentHeldPreflightIsAllowedButNotAnExitProof()
    {
        var operations = new FakeOperations { ParentExitAt = TimeSpan.FromSeconds(1), ProbeResult = new(false, "held", AlreadyOwned: true) };
        Assert.Equal(0, Run(operations));
        Assert.True(operations.Events.IndexOf("parent-exited") < operations.Events.IndexOf("acquire"));
        Assert.True(operations.PreparedWhileHeld);
    }

    [Fact]
    public void ModernSharingConflictCannotHideLegacyPermissionFailureBeforeHandshake()
    {
        var legacyChecked = false;
        var error = Assert.Throws<UnauthorizedAccessException>(() => InstallerUpdateLease.ProbeLocks(
            () => throw new SharingIOException(),
            () => { legacyChecked = true; throw new UnauthorizedAccessException("legacy denied"); }));
        Assert.True(legacyChecked);
        var operations = new FakeOperations { ProbeError = error };
        Assert.Equal(1, Run(operations));
        AssertFailure(operations, "probe", "operationfailed");
        Assert.DoesNotContain("ready", operations.Events);
    }

    [Fact]
    public void BothSharingConflictsAreValidPreflightButAreNotSuccessfulAcquisition()
    {
        var calls = 0;
        var result = InstallerUpdateLease.ProbeLocks(
            () => { calls++; throw new SharingIOException(); },
            () => { calls++; throw new SharingIOException(); });
        Assert.Equal(2, calls);
        Assert.False(result.Succeeded);
        Assert.True(result.AlreadyOwned);
    }

    [Fact]
    public void LegacySharingConflictReleasesSuccessfulModernPreflight()
    {
        var modern = new MemoryStream();
        var result = InstallerUpdateLease.ProbeLocks(() => modern, () => throw new SharingIOException());
        Assert.True(result.AlreadyOwned);
        Assert.False(modern.CanRead);
    }

    [Fact]
    public void PreflightReadFailureCannotProduceHandshake()
    {
        var operations = new FakeOperations { ProbeResult = new(false, "denied") };
        Assert.Equal(1, Run(operations));
        AssertFailure(operations, "probe", "probefailed");
        Assert.DoesNotContain("ready", operations.Events);
        Assert.DoesNotContain("acquire", operations.Events);
    }

    [Fact]
    public void ParentExitTimeoutCannotProduceLockProof()
    {
        var operations = new FakeOperations { ParentExitAt = TimeSpan.MaxValue };
        Assert.Equal(1, Run(operations));
        AssertFailure(operations, "parent-wait", "parenttimeout");
        Assert.Contains("ready", operations.Events);
        Assert.DoesNotContain("acquire", operations.Events);
        Assert.DoesNotContain("prepared", operations.Events);
    }

    [Fact]
    public void ReadyWriteFailureIsExplicitAndCannotAcquire()
    {
        var operations = new FakeOperations { ReadyError = new UnauthorizedAccessException("TOKEN=secret-user-value") };
        Assert.Equal(1, Run(operations));
        AssertFailure(operations, "ready", "operationfailed");
        Assert.DoesNotContain("acquire", operations.Events);
        Assert.All(operations.Diagnostics, diagnostic => Assert.DoesNotContain("secret-user-value", diagnostic.Format()));
    }

    [Theory]
    [InlineData(0, "ready", false)]
    [InlineData(1, "parent-wait", false)]
    [InlineData(2, "finish-wait", true)]
    public void CancellationRemainsExplicitAndReleasesOnlyOwnedLocks(int cancellationSecond, string phase, bool expectedRelease)
    {
        var operations = new FakeOperations
        {
            ParentExitAt = TimeSpan.FromSeconds(1), FinishAt = TimeSpan.FromSeconds(3), CancelAt = TimeSpan.FromSeconds(cancellationSecond)
        };
        Assert.Equal(1, Run(operations));
        AssertFailure(operations, phase, "cancelled");
        Assert.Equal(expectedRelease, operations.Events.Contains("release"));
        Assert.DoesNotContain("released", operations.Events);
        Assert.False(operations.Held);
        if (expectedRelease) AssertFailureHeldUntilFinish(operations);
    }

    [Theory]
    [InlineData(0, "ready", false)]
    [InlineData(1, "parent-wait", false)]
    [InlineData(2, "finish-wait", true)]
    public void SetupDeathRemainsExplicitEvenIfFinishIsAlsoPresent(int deathSecond, string phase, bool expectedRelease)
    {
        var operations = new FakeOperations
        {
            ParentExitAt = TimeSpan.FromSeconds(1), FinishAt = TimeSpan.FromSeconds(deathSecond), InstallerExitAt = TimeSpan.FromSeconds(deathSecond)
        };
        Assert.Equal(1, Run(operations));
        AssertFailure(operations, phase, "installdead");
        Assert.Equal(expectedRelease, operations.Events.Contains("release"));
        Assert.DoesNotContain("released", operations.Events);
    }

    [Fact]
    public void RestartedInstanceHoldingLocksCannotProducePrepared()
    {
        var operations = new FakeOperations { AcquireError = new InstallerUpdateLease.LeaseException("instanceheld") };
        Assert.Equal(1, Run(operations));
        AssertFailure(operations, "acquire", "instanceheld");
        Assert.DoesNotContain("prepared", operations.Events);
        Assert.DoesNotContain("released", operations.Events);
    }

    [Fact]
    public void FinishTimeoutPublishesFailureAndKeepsLocksUntilDelayedFinish()
    {
        var operations = new FakeOperations { FinishAt = TimeSpan.FromSeconds(6) };
        Assert.Equal(1, Run(operations));
        AssertFailure(operations, "finish-wait", "finishtimeout");
        Assert.Contains("release", operations.Events);
        Assert.DoesNotContain("released", operations.Events);
        Assert.False(operations.Held);
        AssertFailureHeldUntilFinish(operations);
    }

    [Fact]
    public void CancellationReadFailureAfterPreparedIsVisibleAndReleases()
    {
        var operations = new FakeOperations { CancelReadErrorAt = TimeSpan.FromSeconds(1), FinishAt = TimeSpan.FromSeconds(3) };
        Assert.Equal(1, Run(operations));
        AssertFailure(operations, "finish-wait", "operationfailed");
        Assert.Contains("prepared", operations.Events);
        Assert.Contains("release", operations.Events);
        Assert.DoesNotContain("released", operations.Events);
        AssertFailureHeldUntilFinish(operations);
    }

    [Fact]
    public void FinishReadFailureIsVisibleAndReleases()
    {
        var operations = new FakeOperations { FinishError = new IOException("COOKIE=do-not-print"), FinishAt = TimeSpan.FromSeconds(3) };
        Assert.Equal(1, Run(operations));
        AssertFailure(operations, "finish-wait", "operationfailed");
        Assert.Contains("release", operations.Events);
        Assert.DoesNotContain("released", operations.Events);
        Assert.DoesNotContain("do-not-print", operations.Diagnostics.Single().Format());
        AssertFailureHeldUntilFinish(operations);
    }

    [Fact]
    public void PreparedWriteFailureReleasesLocksWithoutSuccessMarker()
    {
        var operations = new FakeOperations { PreparedError = new IOException("cannot write"), FinishAt = TimeSpan.FromSeconds(3) };
        Assert.Equal(1, Run(operations));
        AssertFailure(operations, "prepared", "operationfailed");
        Assert.Contains("release", operations.Events);
        Assert.DoesNotContain("released", operations.Events);
        AssertFailureHeldUntilFinish(operations);
    }

    [Fact]
    public void LockReleaseFailureIsNotReportedAsSuccess()
    {
        var operations = new FakeOperations { ReleaseResult = new(false, "release failed") };
        Assert.Equal(1, Run(operations));
        AssertFailure(operations, "release", "lockreleasefail");
        Assert.DoesNotContain("released", operations.Events);
    }

    [Fact]
    public void CleanupFailurePreservesPrimaryFailureAndHasItsOwnDiagnostic()
    {
        var operations = new FakeOperations { FinishError = new IOException("read failed"), ReleaseResult = new(false, "release failed") };
        Assert.Equal(1, Run(operations));
        Assert.Equal(new[] { "operationfailed", "lockreleasefail" }, operations.Diagnostics.Select(item => item.Reason));
        Assert.Equal(new[] { "finish-wait", "cleanup" }, operations.Diagnostics.Select(item => item.Phase));
        Assert.DoesNotContain("released", operations.Events);
    }

    [Fact]
    public void DiagnosticWriteFailureStillReturnsFailureAndDisposes()
    {
        var operations = new FakeOperations { VerifyError = new IOException("bad job"), ReportError = new IOException("bad diagnostic") };
        Assert.Equal(1, Run(operations));
        Assert.Contains("dispose", operations.Events);
    }

    [Fact]
    public void FailedErrorPublicationStillHoldsLocksUntilDelayedFinish()
    {
        var operations = new FakeOperations
        {
            PreparedError = new IOException("prepared failed"), ReportError = new IOException("error publication failed"),
            FinishAt = TimeSpan.FromSeconds(3)
        };
        Assert.Equal(1, Run(operations));
        AssertFailureHeldUntilFinish(operations);
        Assert.DoesNotContain("released", operations.Events);
    }

    [Fact]
    public void PersistentFinishReadFailureKeepsLocksUntilPinnedSetupExit()
    {
        var operations = new FakeOperations
        {
            FinishError = new IOException("finish unreadable"), PersistentFinishError = true,
            InstallerExitAt = TimeSpan.FromSeconds(3), FinishAt = TimeSpan.MaxValue
        };
        Assert.Equal(1, Run(operations));
        Assert.True(operations.HeldWhenFailureReported);
        Assert.Equal(TimeSpan.FromSeconds(3), operations.Elapsed);
        Assert.DoesNotContain("finish", operations.Events);
        Assert.DoesNotContain("released", operations.Events);
        Assert.Equal(new[] { "finish-wait", "finish-monitor" }, operations.Diagnostics.Select(item => item.Phase));
        Assert.True(operations.Events.IndexOf("error") < operations.Events.IndexOf("release"));
    }

    [Fact]
    public void TrustedMarkersPublishCompleteClosedContentWithoutOverwriting()
    {
        var directory = Path.Combine(Path.GetTempPath(), "danmu-lease-marker-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var name in new[] { "prepared", "error.txt", "released" })
            {
                var value = name == "prepared" ? "123456" : name;
                InstallerUpdateLease.PublishMarker(directory, name, value, () =>
                {
                    Assert.False(File.Exists(Path.Combine(directory, name)));
                    var temporary = Path.Combine(directory, name + ".tmp");
                    using var closed = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    using var reader = new StreamReader(closed, leaveOpen: true);
                    Assert.Equal(value, reader.ReadToEnd());
                });
                Assert.Equal(value, File.ReadAllText(Path.Combine(directory, name)));
                Assert.False(File.Exists(Path.Combine(directory, name + ".tmp")));
                Assert.Throws<InstallerUpdateLease.LeaseException>(() => InstallerUpdateLease.PublishMarker(directory, name, "overwrite"));
                Assert.Equal(value, File.ReadAllText(Path.Combine(directory, name)));
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void MarkerFailureAndPreexistingTemporaryCannotPublishOrOverwrite()
    {
        var directory = Path.Combine(Path.GetTempPath(), "danmu-lease-marker-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.Throws<IOException>(() => InstallerUpdateLease.PublishMarker(directory, "prepared", "987", () => throw new IOException("publication failed")));
            Assert.False(File.Exists(Path.Combine(directory, "prepared")));
            Assert.False(File.Exists(Path.Combine(directory, "prepared.tmp")));
            File.WriteAllText(Path.Combine(directory, "prepared.tmp"), "existing");
            Assert.Throws<InstallerUpdateLease.LeaseException>(() => InstallerUpdateLease.PublishMarker(directory, "prepared", "987"));
            Assert.Equal("existing", File.ReadAllText(Path.Combine(directory, "prepared.tmp")));
            Assert.False(File.Exists(Path.Combine(directory, "prepared")));
            Assert.Throws<InstallerUpdateLease.LeaseException>(() => InstallerUpdateLease.PublishMarker(directory, "../outside", "invalid"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void OrdinaryParentUsesOnlySameSidSessionAndNonElevatedToken()
    {
        var parent = Identity("A", 2, false, 1);
        InstallerUpdateLease.ValidateTokenSelection(parent, parent);
        foreach (var selected in new[] { Identity("B", 2, false, 1), Identity("A", 3, false, 1), Identity("A", 2, true, 2) })
            AssertTokenFailure(parent, selected);
    }

    [Fact]
    public void ElevatedParentRequiresLinkedLimitedSameSidAndSession()
    {
        var parent = Identity("A", 2, true, 2);
        InstallerUpdateLease.ValidateTokenSelection(parent, Identity("A", 2, false, 3));
        foreach (var selected in new[]
        {
            Identity("B", 2, false, 3), Identity("A", 3, false, 3), Identity("A", 2, false, 1), Identity("A", 2, true, 2)
        }) AssertTokenFailure(parent, selected);
        AssertTokenFailure(Identity("A", 2, true, 1), Identity("A", 2, false, 3));
    }

    [Fact]
    public void ReadyPathUsesExplicitOriginalUserLocalFolderAndGuidOnly()
    {
        var local = @"C:\Users\original\AppData\Local";
        var directory = Path.Combine(local, "DanmuApi", "app-updates", Guid.NewGuid().ToString("N"));
        Assert.Equal(directory, InstallerUpdateLease.ValidateReadyPath(local, Path.Combine(directory, "installer.ready")));
        foreach (var path in new[]
        {
            Path.Combine(directory, "helper.ready"), Path.Combine(local, "DanmuApi", "app-updates", "not-a-guid", "installer.ready"),
            Path.Combine(@"C:\Users\elevation-account\AppData\Local", "DanmuApi", "app-updates", Guid.NewGuid().ToString("N"), "installer.ready"),
            Path.Combine(directory, "nested", "installer.ready")
        })
            Assert.Equal("readyinvalid", Assert.Throws<InstallerUpdateLease.LeaseException>(() => InstallerUpdateLease.ValidateReadyPath(local, path)).Reason);
    }

    [Fact]
    public void JobMustMatchPinnedParentStartTargetInstallerAndHigherVersion()
    {
        var request = InstallerUpdateLease.ParseArguments(ValidArguments());
        var valid = Job(request);
        InstallerUpdateLease.ValidateJob(JsonSerializer.SerializeToUtf8Bytes(valid), request, 100, "0.5.23");
        foreach (var invalid in new[]
        {
            valid with { ParentPid = 124 }, valid with { ParentStartTicks = 101 }, valid with { TargetDirectory = @"C:\OtherApp" },
            valid with { Kind = "portable" }, valid with { ExpectedVersion = "0.5.23" }, valid with { ExpectedVersion = "0.5.22" },
            valid with { ExpectedVersion = "not-version" }
        })
            Assert.Equal("jobinvalid", Assert.Throws<InstallerUpdateLease.LeaseException>(() =>
                InstallerUpdateLease.ValidateJob(JsonSerializer.SerializeToUtf8Bytes(invalid), request, 100, "0.5.23")).Reason);
    }

    [Fact]
    public void MissingDuplicateMalformedAndOversizedJobsFailExplicitly()
    {
        var request = InstallerUpdateLease.ParseArguments(ValidArguments());
        foreach (var bytes in new[] { Encoding.UTF8.GetBytes("{}"), Encoding.UTF8.GetBytes("[]"), Encoding.UTF8.GetBytes("{bad"), Encoding.UTF8.GetBytes("{\"ParentPid\":123,\"ParentPid\":123}") })
            Assert.Equal("jobinvalid", Assert.Throws<InstallerUpdateLease.LeaseException>(() => InstallerUpdateLease.ValidateJob(bytes, request, 100, "0.5.23")).Reason);
        Assert.Equal("joboversize", Assert.Throws<InstallerUpdateLease.LeaseException>(() => InstallerUpdateLease.ValidateJob(new byte[65537], request, 100, "0.5.23")).Reason);
    }

    [Theory]
    [InlineData(1338, false)]
    [InlineData(1338, true)]
    [InlineData(5, false)]
    [InlineData(5, true)]
    public void DiagnosticPreservesActualWin32CodeWithoutChangingTextContract(int nativeCode, bool wrapped)
    {
        Exception error = new Win32Exception(nativeCode, "TOKEN=do-not-print-native-message");
        if (wrapped) error = new InstallerUpdateLease.LeaseException("leaseinvalid", new IOException("COOKIE=do-not-print-wrapper", error));
        var diagnostic = InstallerUpdateLease.Diagnostic.From("verify", error);
        var expected = unchecked((int)(0x80070000u | (uint)nativeCode));
        Assert.Equal(expected, diagnostic.HResult);
        Assert.Contains($"HRESULT=0x{expected:X8}", diagnostic.Format());
        Assert.DoesNotContain("do-not-print", diagnostic.Format());
        Assert.Equal(wrapped ? "leaseinvalid" : "operationfailed", diagnostic.Reason);
        Assert.Equal(error.GetType().Name, diagnostic.Type);
    }

    [Fact]
    public void DiagnosticPreservesOriginalHResultForNativeCodeOutsideWin32Range()
    {
        var error = new Win32Exception(-1, "SECRET=do-not-print");
        var diagnostic = InstallerUpdateLease.Diagnostic.From("verify", error);
        Assert.Equal(error.HResult, diagnostic.HResult);
        Assert.DoesNotContain("do-not-print", diagnostic.Format());
    }

    [SkippableFact]
    public void NativeAccessCheckUsesCompleteDescriptorAndRejectsWritableOwnDirectory()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var directory = Path.Combine(Path.GetTempPath(), "danmu-lease-access-check-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // The unchanged inherited ACL permits this test host to write. AccessCheck must return that
            // decision, not ERROR_INVALID_SECURITY_DESCR (1338) from a missing descriptor group.
            var error = Assert.Throws<InstallerUpdateLease.LeaseException>(() =>
                InstallerUpdateLease.VerifyCurrentTokenCannotWriteDirectoryForTest(directory));
            Assert.Equal("leaseinvalid", error.Reason);
            Assert.Null(error.InnerException);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [SkippableFact]
    public void NativeCreatorMatchesActualParentFromIndependentProcessSnapshot()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var parentPid = ActualCurrentParentPid();
        Assert.True(parentPid > 0);
        Assert.NotEqual(Environment.ProcessId, parentPid);
        using var parent = Process.GetProcessById(parentPid);
        Assert.Equal(parentPid, parent.Id);
        Assert.False(parent.HasExited);
        Assert.True(InstallerUpdateLease.MatchesCurrentCreatorForTest(parentPid));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeCreatorRejectsZeroAndUnrelatedCurrentProcessPid(bool useCurrentPid)
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var suppliedPid = useCurrentPid ? Environment.ProcessId : 0;
        Assert.NotEqual(ActualCurrentParentPid(), suppliedPid);
        Assert.False(InstallerUpdateLease.MatchesCurrentCreatorForTest(suppliedPid));
    }

    [SkippableFact]
    public void NativeCaptureUsesActualCurrentProcessIdentityOrExplicitlyRejectsNoLimitedToken()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value;
        using var current = Process.GetCurrentProcess();
        var session = current.SessionId;
        try
        {
            var captured = InstallerUpdateLease.CaptureCurrentProcessUserForTest();
            Assert.Equal(sid, captured.Sid);
            Assert.Equal(session, captured.Session);
            Assert.False(captured.Elevated);
        }
        catch (InstallerUpdateLease.LeaseException error)
        {
            // Built-in Administrator/UAC-disabled tokens have no linked limited token. This is a required refusal.
            Assert.Equal("tokenmissing", error.Reason);
            Assert.True(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
        }
    }

    private static int ActualCurrentParentPid()
    {
        // Toolhelp is independent of the production NtQueryInformationProcess implementation and ABI.
        using var snapshot = CreateToolhelp32Snapshot(0x00000002 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Executable = string.Empty };
        if (!Process32First(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
        do
        {
            if (entry.ProcessId == (uint)Environment.ProcessId) return checked((int)entry.ParentProcessId);
        }
        while (Process32Next(snapshot, ref entry));
        var error = Marshal.GetLastWin32Error();
        if (error != 18 /* ERROR_NO_MORE_FILES */) throw new Win32Exception(error);
        throw new InvalidOperationException("The current test host was missing from the native process snapshot.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);

    private static void AssertFailureHeldUntilFinish(FakeOperations operations)
    {
        Assert.True(operations.HeldWhenFailureReported);
        Assert.True(operations.HeldWhenFinishObserved);
        Assert.True(operations.Events.IndexOf("error") < operations.Events.IndexOf("finish"));
        Assert.True(operations.Events.IndexOf("finish") < operations.Events.IndexOf("release"));
    }
    private static int Run(FakeOperations operations) => InstallerUpdateLease.RunCore(operations, ParentTimeout, FinishTimeout);
    private static InstallerUpdateLease.TokenIdentity Identity(string sid, int session, bool elevated, int type) => new(sid, session, elevated, type);
    private static void AssertTokenFailure(InstallerUpdateLease.TokenIdentity parent, InstallerUpdateLease.TokenIdentity selected) =>
        Assert.Equal("tokenidentity", Assert.Throws<InstallerUpdateLease.LeaseException>(() => InstallerUpdateLease.ValidateTokenSelection(parent, selected)).Reason);
    private static void AssertFailure(FakeOperations operations, string phase, string reason)
    {
        var diagnostic = Assert.Single(operations.Diagnostics);
        Assert.Equal(phase, diagnostic.Phase);
        Assert.Equal(reason, diagnostic.Reason);
        Assert.False(string.IsNullOrEmpty(diagnostic.Type));
        Assert.Contains($"phase={phase}", diagnostic.Format());
        Assert.Contains($"reason={reason}", diagnostic.Format());
        Assert.Contains("HRESULT=0x", diagnostic.Format());
    }
    private static string[] ValidArguments() =>
    [
        InstallerUpdateLease.Argument, "123", "456", @"C:\Program Files\DanmuApi",
        @"C:\Users\original\AppData\Local\DanmuApi\app-updates\0123456789abcdef0123456789abcdef\installer.ready",
        @"C:\Windows\Temp\protected-setup\update-lease"
    ];
    private static string[] ChangedArgument(int index, string value)
    {
        var args = ValidArguments();
        args[index] = value;
        return args;
    }
    private static ApplicationUpdateJob Job(InstallerUpdateLease.Request request) => new(request.ParentPid, 100, request.TargetDirectory, "setup.exe", "installer", "0.5.24");

    private sealed class SharingIOException : IOException
    {
        internal SharingIOException() { HResult = unchecked((int)0x80070020); }
    }

    private sealed class FakeOperations : InstallerUpdateLease.IOperations, InstallerUpdateLease.ILease
    {
        internal readonly List<string> Events = [];
        internal IReadOnlyList<InstallerUpdateLease.Diagnostic> Diagnostics = [];
        internal TimeSpan ParentExitAt = TimeSpan.Zero;
        internal TimeSpan FinishAt = TimeSpan.Zero;
        internal TimeSpan CancelAt = TimeSpan.MaxValue;
        internal TimeSpan InstallerExitAt = TimeSpan.MaxValue;
        internal TimeSpan CancelReadErrorAt = TimeSpan.MaxValue;
        internal Exception? VerifyError;
        internal Exception? ProbeError;
        internal Exception? ReadyError;
        internal Exception? AcquireError;
        internal Exception? PreparedError;
        internal Exception? FinishError;
        internal Exception? ReportError;
        internal AppInstanceLockResult ProbeResult = AppInstanceLockResult.Success();
        internal AppInstanceLockResult ReleaseResult = AppInstanceLockResult.Success();
        internal bool Held;
        internal bool PreparedWhileHeld;
        internal bool HeldWhenFinishObserved;
        internal bool HeldWhenFailureReported;
        internal bool PersistentFinishError;
        private bool _verified;
        private bool _parentObserved;
        public TimeSpan Elapsed { get; private set; }
        public void Verify()
        {
            Events.Add("verify");
            if (VerifyError is not null) throw VerifyError;
            _verified = true;
        }
        public AppInstanceLockResult Probe()
        {
            Assert.True(_verified);
            Events.Add("probe");
            if (ProbeError is not null) throw ProbeError;
            return ProbeResult;
        }
        public bool Cancelled()
        {
            if (Elapsed >= CancelReadErrorAt) throw new IOException("TOKEN=read-error-secret");
            return Elapsed >= CancelAt;
        }
        public bool ParentExited()
        {
            if (Elapsed < ParentExitAt) return false;
            if (!_parentObserved) { Events.Add("parent-exited"); _parentObserved = true; }
            return true;
        }
        public bool InstallerExited() => Elapsed >= InstallerExitAt;
        public void WriteReady()
        {
            Assert.True(_verified);
            Events.Add("ready");
            if (ReadyError is not null) throw ReadyError;
        }
        public InstallerUpdateLease.ILease Acquire()
        {
            Assert.True(_parentObserved);
            Events.Add("acquire");
            if (AcquireError is not null) throw AcquireError;
            Held = true;
            return this;
        }
        public void WritePrepared()
        {
            Assert.True(Held);
            PreparedWhileHeld = Held;
            Events.Add("prepared");
            if (PreparedError is not null) throw PreparedError;
        }
        public bool Finished()
        {
            Assert.True(Held);
            if (FinishError is not null)
            {
                var error = FinishError;
                if (!PersistentFinishError) FinishError = null;
                throw error;
            }
            if (Elapsed < FinishAt) return false;
            HeldWhenFinishObserved = Held;
            Events.Add("finish");
            return true;
        }
        public AppInstanceLockResult Release() { Assert.True(Held); Events.Add("release"); Held = false; return ReleaseResult; }
        public void WriteReleased() { Assert.False(Held); Events.Add("released"); }
        public void Delay() { Elapsed += TimeSpan.FromSeconds(1); }
        public void ReportFailure(IReadOnlyList<InstallerUpdateLease.Diagnostic> diagnostics)
        {
            Events.Add("error");
            Diagnostics = diagnostics;
            HeldWhenFailureReported = Held;
            if (ReportError is not null) throw ReportError;
        }
        public void Dispose() { Events.Add("dispose"); Assert.False(Held); }
    }
}
