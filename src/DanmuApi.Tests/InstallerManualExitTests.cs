using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using DanmuApi.App.Services;
using DanmuApi.Platform;

namespace DanmuApi.Tests;

[Trait("InstallerManualExit", "manual-exit-unit-20261005")]
public sealed class InstallerManualExitTests
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FinishTimeout = TimeSpan.FromSeconds(4);

    [Fact]
    public void RequiresExactlySetupPidAndAbsoluteLocalTargetWithoutCallerStatePath()
    {
        var request = InstallerManualExit.ParseArguments(Arguments());
        Assert.Equal(123, request.SetupPid);
        Assert.Equal(@"D:\自定义安装目录\DanmuApi", request.TargetDirectory);
        foreach (var args in new[]
        {
            Array.Empty<string>(), Arguments()[..2], Arguments().Append(@"C:\untrusted\manual-exit").ToArray(),
            Changed(0, "--installer-update-lease"), Changed(1, "0"), Changed(1, "-1"), Changed(1, " 123"),
            Changed(1, Environment.ProcessId.ToString()), Changed(2, @"relative\DanmuApi"),
            Changed(2, @"\\server\share\DanmuApi"), Changed(2, @"C:\DanmuApi:stream")
        })
            Assert.ThrowsAny<Exception>(() => InstallerManualExit.ParseArguments(args));
    }

    [Theory]
    [InlineData(@"C:\\")]
    [InlineData(@"D:\custom\DanmuApi\\")]
    public void ValidatedPathsPreserveDriveRootsAndTrimOnlyDirectoryEndings(string path)
    {
        var request = InstallerManualExit.ParseArguments(Changed(2, path));
        Assert.True(Path.IsPathFullyQualified(request.TargetDirectory));
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), request.TargetDirectory);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("finish")]
    public void ProtectedSignalsRequireExactContentRatherThanBareExistence(string name)
    {
        InstallerManualExit.ValidateSignal(name, Encoding.UTF8.GetBytes(name));
        foreach (var invalid in new[] { "", name + "\n", name + "\0", "\uFEFF" + name, name.ToUpperInvariant(), name == "finish" ? "request" : "finish" })
            Assert.Equal("stateinvalid", Assert.Throws<InstallerManualExit.ManualExitException>(() => InstallerManualExit.ValidateSignal(name, Encoding.UTF8.GetBytes(invalid))).Reason);
        Assert.Throws<InstallerManualExit.ManualExitException>(() => InstallerManualExit.ValidateSignal(name, [0xff]));
        Assert.Throws<InstallerManualExit.ManualExitException>(() => InstallerManualExit.ValidateSignal(name, new byte[257]));
        Assert.Throws<InstallerManualExit.ManualExitException>(() => InstallerManualExit.ValidateSignal("cancel", Encoding.UTF8.GetBytes("cancel")));
    }

    [Fact]
    public void RunningRequiresExplicitApprovalBeforeDispatchActualExitAndBothLocksBeforePrepared()
    {
        var operations = new FakeOperations { RequestAt = Seconds(2), TargetExitAt = Seconds(3), FinishAt = Seconds(5) };
        Assert.Equal(0, Run(operations));
        Assert.Equal(new[] { "verify", "running", "request", "dispatch", "target-exited", "acquire", "prepared", "finish", "release", "released", "dispose" }, operations.Events);
        Assert.Equal(1, operations.Dispatches);
        Assert.Equal(Seconds(2), operations.DispatchAt);
        Assert.True(operations.PreparedWhileHeld);
        Assert.True(operations.HeldWhenFinishObserved);
        Assert.False(operations.Held);
        Assert.Empty(operations.Diagnostics);
    }

    [Fact]
    public void FinishWhileAwaitingApprovalIsExplicitCancellationExit2WithReleasedAndNoDispatch()
    {
        var operations = new FakeOperations { RequestAt = TimeSpan.MaxValue, FinishAt = Seconds(3) };
        Assert.Equal(2, Run(operations));
        AssertFailure(operations, "confirm-wait", "cancelled");
        Assert.Equal(new[] { "verify", "running", "finish", "error", "released", "dispose" }, operations.Events);
        Assert.Equal(0, operations.Dispatches);
        Assert.DoesNotContain("acquire", operations.Events);
        Assert.DoesNotContain("prepared", operations.Events);
    }

    [Fact]
    public void SimultaneousFinishAndRequestCancelsWithoutDispatch()
    {
        var operations = new FakeOperations { RequestAt = Seconds(1), FinishAt = Seconds(1) };
        Assert.Equal(2, Run(operations));
        Assert.DoesNotContain("request", operations.Events);
        Assert.DoesNotContain("dispatch", operations.Events);
        Assert.Contains("released", operations.Events);
    }

    [Fact]
    public void ConsentWaitHasNoAutomaticConfirmationOrTimeout()
    {
        var operations = new FakeOperations { RequestAt = Seconds(100), TargetExitAt = Seconds(101), FinishAt = Seconds(102) };
        Assert.Equal(0, Run(operations));
        Assert.Equal(Seconds(100), operations.DispatchAt);
        Assert.Equal(1, operations.Dispatches);
    }

    [Theory]
    [InlineData("fresh-install-missing-directory")]
    [InlineData("fresh-install-missing-exe")]
    [InlineData("registered-target-stopped")]
    [InlineData("unregistered-target-stopped")]
    public void NoExactTargetPreparesWithoutAnyProfileTokenShutdownOrInstanceLockClaim(string scenario)
    {
        var operations = new FakeOperations { HasTarget = false, Scenario = scenario, FinishAt = Seconds(3) };
        Assert.Equal(0, Run(operations));
        Assert.Equal(new[] { "verify", "prepared", "finish", "released", "dispose" }, operations.Events);
        Assert.Equal(0, operations.Dispatches);
        Assert.DoesNotContain("running", operations.Events);
        Assert.DoesNotContain("acquire", operations.Events);
        Assert.True(operations.TargetDirectoryPinnedWhenPrepared);
        Assert.True(operations.PinHeldWhenFinishObserved);
        Assert.True(operations.TargetMonitorCalls > 1);
        Assert.False(operations.PinHeld);
    }

    [Theory]
    [InlineData("installidentity")]
    [InlineData("installertrust")]
    [InlineData("helpertrust")]
    [InlineData("stateinvalid")]
    [InlineData("targetinvalid")]
    [InlineData("targetmultiple")]
    [InlineData("targetsession")]
    [InlineData("tokenmissing")]
    [InlineData("profileinvalid")]
    [InlineData("endpointinvalid")]
    [InlineData("enumerationfailed")]
    public void VerificationFailuresCannotPublishRunningPreparedOrDispatch(string reason)
    {
        var operations = new FakeOperations { VerifyError = new InstallerManualExit.ManualExitException(reason) };
        Assert.Equal(1, Run(operations));
        AssertFailure(operations, "verify", reason);
        Assert.Equal(new[] { "verify", "error", "dispose" }, operations.Events);
        Assert.Equal(0, operations.Dispatches);
    }

    [Fact]
    public void RunningMarkerWriteFailureCannotDispatch()
    {
        var operations = new FakeOperations { RunningError = new UnauthorizedAccessException("TOKEN=never-print") };
        Assert.Equal(1, Run(operations));
        AssertFailure(operations, "running", "operationfailed");
        Assert.Equal(0, operations.Dispatches);
        Assert.DoesNotContain("prepared", operations.Events);
    }

    [Theory]
    [InlineData("commandrejected", "exitrejected")]
    [InlineData("endpointowner", "endpointowner")]
    [InlineData("unexpected", "operationfailed")]
    public void RefusedOrUntrustedEndpointCannotPrepareOrStrongKill(string code, string reason)
    {
        var operations = new FakeOperations { Shutdown = new(false, code), TargetExitAt = TimeSpan.MaxValue };
        Assert.Equal(1, Run(operations));
        AssertFailure(operations, "request-exit", reason);
        Assert.Equal(1, operations.Dispatches);
        Assert.DoesNotContain("acquire", operations.Events);
        Assert.DoesNotContain("prepared", operations.Events);
        Assert.DoesNotContain("released", operations.Events);
        if (code == "commandrejected")
        {
            Assert.Contains("0.5.13", operations.Diagnostics.Single().Format());
            Assert.Contains("manual safe exit", operations.Diagnostics.Single().Format());
        }
    }

    [Fact]
    public void StructuredIpcTypeAndNativeHResultSurviveWithoutRawErrorText()
    {
        var operations = new FakeOperations
        {
            Shutdown = new(false, "endpointowner", "Win32Exception", unchecked((int)0x80070005)), TargetExitAt = TimeSpan.MaxValue
        };
        Assert.Equal(1, Run(operations));
        var diagnostic = Assert.Single(operations.Diagnostics);
        Assert.Equal("Win32Exception", diagnostic.Type);
        Assert.Equal(unchecked((int)0x80070005), diagnostic.HResult);
    }

    [Theory]
    [Trait("InstallerManualExitNativeProof", "manual-exit-native-proof-20261005")]
    [InlineData("FileNotFoundException", 2)]
    [InlineData("DirectoryNotFoundException", 3)]
    [InlineData("EndOfStreamException", 38)]
    [InlineData("DecoderFallbackException", 1113)]
    public void CommonSafeIpcReadFailureTypesRetainTheirNativeMetadata(string type, int nativeCode)
    {
        var hresult = unchecked((int)(0x80070000u | (uint)nativeCode));
        var operations = new FakeOperations { Shutdown = new(false, "operationfailed", type, hresult), TargetExitAt = TimeSpan.MaxValue };
        Assert.Equal(1, Run(operations));
        var diagnostic = Assert.Single(operations.Diagnostics);
        Assert.Equal(type, diagnostic.Type);
        Assert.Equal(hresult, diagnostic.HResult);
        Assert.Equal("operationfailed", diagnostic.Reason);
    }

    [Fact]
    public void UntrustedFailureTypeCannotInjectTextIntoStructuredDiagnostic()
    {
        var operations = new FakeOperations { Shutdown = new(false, "commandrejected", "TOKEN=unsafe\ntext"), TargetExitAt = TimeSpan.MaxValue };
        Assert.Equal(1, Run(operations));
        Assert.Equal("ManualExitException", operations.Diagnostics.Single().Type);
        Assert.DoesNotContain("unsafe", operations.Diagnostics.Single().Format());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedSendIsAcceptedOnlyWhenRealPinnedProcessAlreadyExited(bool throwException)
    {
        var operations = new FakeOperations
        {
            Shutdown = new(false, "commandrejected"), SendError = throwException ? new IOException("COOKIE=hidden") : null,
            ExitDuringDispatch = true, TargetExitAt = TimeSpan.MaxValue, FinishAt = Seconds(2)
        };
        Assert.Equal(0, Run(operations));
        Assert.Equal(1, operations.Dispatches);
        Assert.True(operations.Events.IndexOf("target-exited") < operations.Events.IndexOf("acquire"));
        Assert.Contains("prepared", operations.Events);
        Assert.Empty(operations.Diagnostics);
    }

    [Fact]
    public void ExistingPinnedProcessExitsDuringConsentWaitSoNoIpcIsNeededAfterApproval()
    {
        var operations = new FakeOperations { RequestAt = Seconds(2), TargetExitAt = Seconds(1), FinishAt = Seconds(3) };
        Assert.Equal(0, Run(operations));
        Assert.Equal(0, operations.Dispatches);
        Assert.Contains("target-exited", operations.Events);
        Assert.Contains("acquire", operations.Events);
        Assert.True(operations.PreparedWhileHeld);
    }

    [Fact]
    public void IpcOkAndFreeLockFilesCannotSubstituteForOriginalProcessExit()
    {
        var operations = new FakeOperations { TargetExitAt = TimeSpan.MaxValue, LockFilesWouldBeFree = true };
        Assert.Equal(1, Run(operations));
        AssertFailure(operations, "target-wait", "targettimeout");
        Assert.Equal(1, operations.Dispatches);
        Assert.DoesNotContain("acquire", operations.Events);
        Assert.DoesNotContain("prepared", operations.Events);
    }

    [Theory]
    [InlineData("instanceheld")]
    [InlineData("acquirefailed")]
    public void RealExitCannotSubstituteForSuccessfulModernAndLegacyLockAcquisition(string reason)
    {
        var operations = new FakeOperations { AcquireError = new InstallerManualExit.ManualExitException(reason) };
        Assert.Equal(1, Run(operations));
        AssertFailure(operations, "acquire", reason);
        Assert.Contains("target-exited", operations.Events);
        Assert.DoesNotContain("prepared", operations.Events);
        Assert.False(operations.Held);
    }

    [Fact]
    public void FinishTimeoutReportsFailureWhileHoldingLocksThenWaitsForDelayedFinish()
    {
        var operations = new FakeOperations { FinishAt = Seconds(8) };
        Assert.Equal(1, Run(operations));
        AssertFailure(operations, "finish-wait", "finishtimeout");
        AssertFailureHeldUntilFinish(operations);
        Assert.Equal(Seconds(8), operations.Elapsed);
        Assert.DoesNotContain("released", operations.Events);
    }

    [Fact]
    public void NoTargetFinishTimeoutKeepsTargetDirectoryPinsUntilFinish()
    {
        var operations = new FakeOperations { HasTarget = false, FinishAt = Seconds(8) };
        Assert.Equal(1, Run(operations));
        AssertFailure(operations, "finish-wait", "finishtimeout");
        Assert.True(operations.PinHeldWhenFailureReported);
        Assert.True(operations.PinHeldWhenFinishObserved);
        Assert.Equal(Seconds(8), operations.Elapsed);
        Assert.DoesNotContain("release", operations.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewExactTargetAfterPreparedFailsExplicitlyAndKeepsProtectionsUntilFinish(bool hasOriginal)
    {
        var operations = new FakeOperations { HasTarget = hasOriginal, NewInstanceAt = Seconds(2), FinishAt = Seconds(5) };
        Assert.Equal(1, Run(operations));
        AssertFailure(operations, "finish-wait", "newinstance");
        Assert.True(operations.PinHeldWhenFailureReported);
        Assert.True(operations.PinHeldWhenFinishObserved);
        if (hasOriginal) AssertFailureHeldUntilFinish(operations);
        Assert.DoesNotContain("released", operations.Events);
    }

    [Fact]
    public void EnumerationFailureWhilePreparedNeverBecomesAnEmptyProcessSet()
    {
        var operations = new FakeOperations { MonitorErrorAt = Seconds(2), FinishAt = Seconds(5) };
        Assert.Equal(1, Run(operations));
        AssertFailure(operations, "finish-wait", "enumerationfailed");
        AssertFailureHeldUntilFinish(operations);
    }

    [Fact]
    public void CancellationBeforeConsentDispatchIsExplicitAndHasNoShutdownSideEffects()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var operations = new FakeOperations { RequestAt = Seconds(2) };
        Assert.Equal(1, Run(operations, cancellation.Token));
        AssertFailure(operations, "verify", "cancelled");
        Assert.Equal(0, operations.Dispatches);
        Assert.DoesNotContain("acquire", operations.Events);
    }

    [Fact]
    public void CancellationAfterPreparedReportsThenHoldsLocksUntilFinish()
    {
        using var cancellation = new CancellationTokenSource();
        var operations = new FakeOperations { FinishAt = Seconds(5), AfterPrepared = cancellation.Cancel };
        Assert.Equal(1, Run(operations, cancellation.Token));
        AssertFailure(operations, "finish-wait", "cancelled");
        AssertFailureHeldUntilFinish(operations);
        Assert.DoesNotContain("released", operations.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PinnedSetupDeathAfterPreparedAuthorizesSafeReleaseButNeverNormalSuccess(bool finishAlsoExists)
    {
        var operations = new FakeOperations { SetupExitAt = Seconds(3), FinishAt = finishAlsoExists ? Seconds(3) : TimeSpan.MaxValue };
        Assert.Equal(1, Run(operations));
        AssertFailure(operations, "finish-wait", "setupdead");
        Assert.True(operations.HeldWhenFailureReported);
        Assert.Equal(Seconds(3), operations.Elapsed);
        Assert.True(operations.Events.IndexOf("error") < operations.Events.IndexOf("release"));
        Assert.DoesNotContain("released", operations.Events);
    }

    [Fact]
    public void SetupDeathDuringConsentWaitCannotDispatch()
    {
        var operations = new FakeOperations { SetupExitAt = Seconds(2), RequestAt = Seconds(3) };
        Assert.Equal(1, Run(operations));
        AssertFailure(operations, "confirm-wait", "setupdead");
        Assert.Equal(0, operations.Dispatches);
    }

    [Fact]
    public void PreparedPublicationFailureRetainsAcquiredLocksUntilFinish()
    {
        var operations = new FakeOperations { PreparedError = new IOException("TOKEN=marker-secret"), FinishAt = Seconds(4) };
        Assert.Equal(1, Run(operations));
        AssertFailure(operations, "prepared", "operationfailed");
        AssertFailureHeldUntilFinish(operations);
        Assert.DoesNotContain("released", operations.Events);
    }

    [Fact]
    public void UnreadableFinishCannotReleaseLocksBeforePinnedSetupReallyExits()
    {
        var operations = new FakeOperations { PersistentFinishError = true, FinishErrorAfterPrepared = new IOException("TOKEN=finish-secret"), SetupExitAt = Seconds(4) };
        Assert.Equal(1, Run(operations));
        Assert.Equal(new[] { "finish-wait", "finish-monitor" }, operations.Diagnostics.Select(item => item.Phase));
        Assert.True(operations.HeldWhenFailureReported);
        Assert.Equal(Seconds(4), operations.Elapsed);
        Assert.DoesNotContain("finish", operations.Events);
        Assert.DoesNotContain("released", operations.Events);
    }

    [Fact]
    public void MarkerDiagnosticWriteFailureCannotConcealTheInitialErrorOrReleaseLocksEarly()
    {
        var operations = new FakeOperations
        {
            PreparedError = new IOException("SECRET=prepared"), ReportError = new IOException("SECRET=error-marker"), FinishAt = Seconds(4)
        };
        Assert.Equal(1, Run(operations));
        Assert.Equal(new[] { "operationfailed", "diagnosticwritefail" }, operations.Diagnostics.Select(item => item.Reason));
        AssertFailureHeldUntilFinish(operations);
    }

    [Fact]
    public void ReleaseFailureCannotPublishReleasedOrReturnZero()
    {
        var operations = new FakeOperations { ReleaseResult = new(false, "release error") };
        Assert.Equal(1, Run(operations));
        AssertFailure(operations, "release", "lockreleasefail");
        Assert.DoesNotContain("released", operations.Events);
    }

    [Fact]
    public void CleanupFailuresRetainInitialPhaseAndAreStructured()
    {
        var operations = new FakeOperations
        {
            PreparedError = new IOException("SECRET=prepared"), ReleaseResult = new(false, "release error"),
            DisposeError = new IOException("SECRET=dispose"), FinishAt = Seconds(4)
        };
        Assert.Equal(1, Run(operations));
        Assert.Equal(new[] { "operationfailed", "lockreleasefail", "cleanupfailed" }, operations.Diagnostics.Select(item => item.Reason));
        Assert.All(operations.Diagnostics, item => Assert.DoesNotContain("SECRET", item.Format()));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    public void OriginalFullUnlinkedAndLimitedTokensRemainUnchangedAndAreAllAllowed(bool elevated, int elevationType)
    {
        var original = new InstallerUpdateLease.TokenIdentity("S-1-5-21-1-2-3-1001", 7, elevated, elevationType);
        InstallerManualExit.ValidateOriginalToken(original, original with { });
        foreach (var changed in new[]
        {
            original with { Sid = "S-1-5-21-1-2-3-1002" }, original with { Session = 8 },
            original with { Elevated = !elevated }, original with { ElevationType = elevationType == 1 ? 2 : 1 }
        })
            Assert.Equal("tokenidentity", Assert.Throws<InstallerManualExit.ManualExitException>(() => InstallerManualExit.ValidateOriginalToken(original, changed)).Reason);
    }

    [Fact]
    public void KnownFolderLeavesRequireCorrectOwnerWithoutImposingSettingsAclOnTheWholeProfile()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        var broadProfileAcl = new RawSecurityDescriptor($"O:{sid}G:SYD:(A;;FA;;;WD)");
        InstallerManualExit.ValidateProfileOwner(broadProfileAcl, sid);
        Assert.Throws<InstallerManualExit.ManualExitException>(() => InstallerManualExit.ValidateProfileSecurity(broadProfileAcl, sid));
        Assert.Throws<InstallerManualExit.ManualExitException>(() => InstallerManualExit.ValidateProfileOwner(
            new RawSecurityDescriptor("O:S-1-5-21-1-2-3-1002G:SYD:(A;;FA;;;WD)"), sid));
    }

    [Fact]
    public void ProfileAndIpcAclAllowsOriginalIdentityAndRejectsAnotherIdentityMutation()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        InstallerManualExit.ValidateProfileSecurity(new RawSecurityDescriptor($"O:{sid}G:SYD:(A;;FA;;;{sid})(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;WD)"), sid);
        foreach (var sddl in new[]
        {
            $"O:{sid}G:SYD:(A;;FA;;;WD)",
            $"O:S-1-5-21-1-2-3-1002G:SYD:(A;;FA;;;{sid})",
            $"O:{sid}G:SYD:(A;;FA;;;S-1-5-21-1-2-3-1002)"
        })
            Assert.Equal("profileinvalid", Assert.Throws<InstallerManualExit.ManualExitException>(() => InstallerManualExit.ValidateProfileSecurity(new RawSecurityDescriptor(sddl), sid)).Reason);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(1338)]
    public void EveryDiagnosticHasSafePhaseReasonTypeAndActualNativeHResult(int code)
    {
        var error = new InstallerManualExit.ManualExitException("profileinvalid", new IOException("COOKIE=hidden", new Win32Exception(code, "TOKEN=hidden")));
        var diagnostic = InstallerManualExit.Diagnostic.From("verify", error);
        Assert.Equal(unchecked((int)(0x80070000u | (uint)code)), diagnostic.HResult);
        Assert.Equal("profileinvalid", diagnostic.Reason);
        Assert.Equal("ManualExitException", diagnostic.Type);
        Assert.Contains("phase=verify reason=profileinvalid type=ManualExitException HRESULT=0x", diagnostic.Format());
        Assert.DoesNotContain("hidden", diagnostic.Format());
    }

    [Fact]
    public void TrustedMarkersAreAtomicClosedCompleteNonOverwritingAndIncludeRunning()
    {
        var directory = Path.Combine(Path.GetTempPath(), "danmu-manual-exit-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var name in new[] { "running", "prepared", "error.txt", "released" })
            {
                InstallerManualExit.PublishMarker(directory, name, name, () =>
                {
                    Assert.False(File.Exists(Path.Combine(directory, name)));
                    using var file = new FileStream(Path.Combine(directory, name + ".tmp"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    using var reader = new StreamReader(file);
                    Assert.Equal(name, reader.ReadToEnd());
                });
                Assert.Equal(name, File.ReadAllText(Path.Combine(directory, name)));
                Assert.False(File.Exists(Path.Combine(directory, name + ".tmp")));
                Assert.Throws<InstallerManualExit.ManualExitException>(() => InstallerManualExit.PublishMarker(directory, name, "overwritten"));
            }
            Assert.Throws<InstallerManualExit.ManualExitException>(() => InstallerManualExit.PublishMarker(directory, "../outside", "invalid"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void FailedMarkerPublicationLeavesNeitherPartialPreparedNorReusableTemporary()
    {
        var directory = Path.Combine(Path.GetTempPath(), "danmu-manual-exit-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.Throws<IOException>(() => InstallerManualExit.PublishMarker(directory, "prepared", "prepared", () => throw new IOException("publish failed")));
            Assert.False(File.Exists(Path.Combine(directory, "prepared")));
            Assert.False(File.Exists(Path.Combine(directory, "prepared.tmp")));
            File.WriteAllText(Path.Combine(directory, "prepared.tmp"), "existing");
            Assert.Throws<InstallerManualExit.ManualExitException>(() => InstallerManualExit.PublishMarker(directory, "prepared", "prepared"));
            Assert.Equal("existing", File.ReadAllText(Path.Combine(directory, "prepared.tmp")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [SkippableFact]
    [Trait("InstallerManualExitNativeLock", "manual-exit-native-lock-20261005")]
    [Trait("InstallerManualExitNativeProof", "manual-exit-native-proof-20261005")]
    public void NativeOwnedTemporaryMetadataPinsAreCompatibleWithRealModernAndLegacyInstanceLocks()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var root = Path.Combine(Path.GetTempPath(), "danmu-manual-exit-native-lock-test-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "roaming"));
        Directory.CreateDirectory(paths.SettingsDirectory);
        try
        {
            InstallerManualExit.WithCurrentOriginalTokenLockPinsForTest(paths, () =>
            {
                var diagnostics = new List<string>();
                using var first = new AppInstanceLock(paths, diagnostics.Add);
                using var second = new AppInstanceLock(paths, diagnostics.Add);
                var acquired = first.TryAcquireDetailed();
                Assert.True(acquired.Succeeded, acquired.Diagnostic);
                Assert.True(first.IsAcquired);

                var competing = second.TryAcquireDetailed();
                Assert.False(competing.Succeeded);
                Assert.True(competing.AlreadyOwned, competing.Diagnostic);
                Assert.Equal("IOException", competing.FailureType);
                Assert.Equal(unchecked((int)0x80070020), competing.FailureHResult);
                Assert.False(second.IsAcquired);
                Assert.NotEmpty(diagnostics);

                var modernConflict = Assert.Throws<IOException>(() =>
                {
                    using var conflicting = new FileStream(paths.InstanceLockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                });
                Assert.Equal(32, modernConflict.HResult & 0xffff);
                var heldRenameConflict = Assert.Throws<IOException>(() => File.Move(paths.InstanceLockFile, paths.InstanceLockFile + ".held-move-test"));
                Assert.Equal(32, heldRenameConflict.HResult & 0xffff);
                using (var legacyCompetitor = new FileStream(Path.Combine(paths.SettingsDirectory, "app.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    var legacyConflict = Assert.Throws<IOException>(() => legacyCompetitor.Lock(0, long.MaxValue));
                    Assert.Equal(33, legacyConflict.HResult & 0xffff);
                }

                var release = first.Release();
                Assert.True(release.Succeeded, release.Diagnostic);
                Assert.False(first.IsAcquired);
                var reacquired = second.TryAcquireDetailed();
                Assert.True(reacquired.Succeeded, reacquired.Diagnostic);
                Assert.True(second.IsAcquired);
                var secondRelease = second.Release();
                Assert.True(secondRelease.Succeeded, secondRelease.Diagnostic);
                Assert.False(second.IsAcquired);

                using (var legacyOnlyOwner = new FileStream(Path.Combine(paths.SettingsDirectory, "app.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    legacyOnlyOwner.Lock(0, long.MaxValue);
                    var legacyOnlyConflict = first.TryAcquireDetailed();
                    Assert.False(legacyOnlyConflict.Succeeded);
                    Assert.True(legacyOnlyConflict.AlreadyOwned);
                    Assert.Equal("IOException", legacyOnlyConflict.FailureType);
                    Assert.Equal(unchecked((int)0x80070021), legacyOnlyConflict.FailureHResult);
                    Assert.False(first.IsAcquired);
                    legacyOnlyOwner.Unlock(0, long.MaxValue);
                }

                // Metadata handles remain compatible with data locks. The locks, not metadata access, own mutual exclusion.
                using (var exclusiveAfterRelease = new FileStream(paths.InstanceLockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Assert.True(exclusiveAfterRelease.CanWrite);
                using (var legacyAfterRelease = new FileStream(Path.Combine(paths.SettingsDirectory, "app.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    legacyAfterRelease.Lock(0, long.MaxValue);
                    legacyAfterRelease.Unlock(0, long.MaxValue);
                }
                // Metadata-only file access intentionally does not prevent namespace mutation after the real locks release.
                var movedLock = paths.InstanceLockFile + ".move-test";
                File.Move(paths.InstanceLockFile, movedLock);
                Assert.True(File.Exists(movedLock));
                File.Move(movedLock, paths.InstanceLockFile);
                Assert.False(File.Exists(paths.InstanceEndpointFile));
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [SkippableFact]
    [Trait("InstallerManualExitNativeProof", "manual-exit-native-proof-20261005")]
    public void NativeOwnedTemporaryDataDirectoryPinPreventsRenameAndReplacementButPermitsSetupChildWrites()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var root = Path.Combine(Path.GetTempPath(), "danmu-manual-exit-directory-pin-test-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "original");
        var moved = Path.Combine(root, "moved");
        var paths = new AppPaths(Path.Combine(root, "local"), directory);
        Directory.CreateDirectory(directory);
        try
        {
            using (var oldMetadataOnly = InstallerUpdateLease.Native.OpenDirectory(directory))
            {
                // Retain the reproduction: metadata-only directory access does not pin the namespace.
                Directory.Move(directory, moved);
                Assert.True(Directory.Exists(moved));
                Directory.Move(moved, directory);
            }
            using (var pinned = InstallerManualExit.OpenPinnedDirectoryForTest(paths))
            {
                var rename = Assert.Throws<IOException>(() => Directory.Move(directory, moved));
                Assert.Equal(32, rename.HResult & 0xffff);
                var replacement = Assert.Throws<IOException>(() => Directory.Delete(directory));
                Assert.Equal(32, replacement.HResult & 0xffff);
                var child = Path.Combine(directory, "Setup-copy.bin");
                File.WriteAllText(child, "old contents");
                File.WriteAllText(child, "new contents");
                Assert.Equal("new contents", File.ReadAllText(child));
                var staged = Path.Combine(directory, "stage.bin");
                File.WriteAllText(staged, "replacement contents");
                File.Move(staged, child, overwrite: true);
                Assert.Equal("replacement contents", File.ReadAllText(child));
                File.Delete(child);
                Assert.False(File.Exists(child));
            }
            Directory.Move(directory, moved);
            Assert.True(Directory.Exists(moved));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [SkippableFact]
    [Trait("InstallerManualExitNativeProof", "manual-exit-native-proof-20261005")]
    public void NativeOwnedTemporaryDirectoryPinRejectsJunctionWithoutFollowingItsTarget()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var root = Path.Combine(Path.GetTempPath(), "danmu-manual-exit-reparse-pin-test-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(root, "outside");
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(outside);
        try
        {
            var marker = Path.Combine(outside, "untouched.txt");
            File.WriteAllText(marker, "untouched");
            var start = new ProcessStartInfo("cmd.exe")
            {
                // cmd /c is a command-line parser; ArgumentList would escape the nested quotes as CRT arguments.
                Arguments = $"/d /c mklink /J \"{link}\" \"{outside}\"",
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            using var create = Process.Start(start) ?? throw new InvalidOperationException("Failed to start test-owned junction creation.");
            var output = create.StandardOutput.ReadToEnd();
            var error = create.StandardError.ReadToEnd();
            Assert.True(create.WaitForExit(10_000), "Test-owned junction creation did not exit in time.");
            Assert.True(create.ExitCode == 0, $"exit={create.ExitCode}; stdout={output}; stderr={error}");
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            var paths = new AppPaths(Path.Combine(root, "local"), link);
            Assert.Equal("pathinvalid", Assert.Throws<InstallerUpdateLease.LeaseException>(() =>
            {
                using var rejected = InstallerManualExit.OpenPinnedDirectoryForTest(paths);
            }).Reason);
            Assert.Equal("untouched", File.ReadAllText(marker));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link); // Remove the junction itself, not its target.
            Directory.Delete(root, recursive: true);
        }
    }

    [SkippableFact]
    [Trait("InstallerManualExitNativeProof", "manual-exit-native-proof-20261005")]
    public void NativeOwnedTemporaryEndpointReadPinBlocksOwnerDeleteUntilClosedWhileDirectoryPinAllowsCleanup()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var root = Path.Combine(Path.GetTempPath(), "danmu-manual-exit-endpoint-pin-test-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "roaming"));
        Directory.CreateDirectory(paths.SettingsDirectory);
        try
        {
            File.WriteAllText(paths.InstanceEndpointFile, "port=12345\ntoken=test-owned-only\nexe=testhost\n");
            using var directoryPin = InstallerManualExit.OpenPinnedDirectoryForTest(paths);
            using (var endpoint = InstallerUpdateLease.OpenPlainRead(paths.InstanceEndpointFile))
            {
                var sharing = Assert.Throws<IOException>(() => File.Delete(paths.InstanceEndpointFile));
                Assert.Equal(32, sharing.HResult & 0xffff);
            }
            File.Delete(paths.InstanceEndpointFile);
            Assert.False(File.Exists(paths.InstanceEndpointFile));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    [Trait("InstallerManualExitNativeProof", "manual-exit-native-proof-20261005")]
    public void EndpointValidationHandleIsDisposedBeforeSendingTheExitCommand()
    {
        var events = new List<string>();
        var validated = new DisposeCallback(() => events.Add("disposed"));
        var result = InstallerManualExit.SendAfterEndpointValidation(() =>
        {
            events.Add("validate");
            return validated;
        }, () =>
        {
            Assert.True(validated.Disposed);
            events.Add("send");
            return new(true);
        });
        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "validate", "disposed", "send" }, events);
    }

    [Theory]
    [Trait("InstallerManualExitNativeProof", "manual-exit-native-proof-20261005")]
    [InlineData(false)]
    [InlineData(true)]
    public void EndpointValidationOrDisposalFailureNeverDispatches(bool disposeFails)
    {
        var sends = 0;
        Assert.Throws<IOException>(() => InstallerManualExit.SendAfterEndpointValidation(() =>
        {
            if (!disposeFails) throw new IOException("test-owned validation error");
            return new DisposeCallback(() => throw new IOException("test-owned dispose error"));
        }, () => { sends++; return new(true); }));
        Assert.Equal(0, sends);
    }

    private sealed class DisposeCallback(Action callback) : IDisposable
    {
        internal bool Disposed { get; private set; }
        public void Dispose() { Disposed = true; callback(); }
    }

    [SkippableFact]
    public void NativeTesthostOriginalTokenDuplicationSupportsActualFullOrUnlinkedIdentityWithoutDeElevation()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var captured = InstallerManualExit.CaptureCurrentOriginalTokenForTest();
        using var current = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        Assert.Equal(identity.User!.Value, captured.Sid);
        Assert.Equal(current.SessionId, captured.Session);
        // This host can be built-in Administrator/unlinked: the old linked-limited capture policy is deliberately not used.
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) Assert.True(captured.Elevated);
    }

    [SkippableFact]
    public void NativeTesthostImpersonationRetainsOriginalElevationAndIdentityWithoutLinkedToken()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var original = InstallerManualExit.CaptureCurrentOriginalTokenForTest();
        var impersonated = InstallerManualExit.ImpersonateCurrentOriginalTokenForTest();
        Assert.Equal(original, impersonated.Identity);
        Assert.Equal(TokenImpersonationLevel.Impersonation, impersonated.Level);
    }

    [SkippableFact]
    public void NativeTesthostProcessImageMatchesTheActualOpenedImageNtPath()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        Assert.True(InstallerManualExit.CurrentProcessNativeImageMatchesForTest());
    }

    private static TimeSpan Seconds(int value) => TimeSpan.FromSeconds(value);
    private static int Run(FakeOperations operations, CancellationToken token = default) => InstallerManualExit.RunCore(operations, ExitTimeout, FinishTimeout, token);
    private static string[] Arguments() => [InstallerManualExit.Argument, "123", @"D:\自定义安装目录\DanmuApi"];
    private static string[] Changed(int index, string value)
    {
        var args = Arguments();
        args[index] = value;
        return args;
    }
    private static void AssertFailure(FakeOperations operations, string phase, string reason)
    {
        var diagnostic = Assert.Single(operations.Diagnostics);
        Assert.Equal(phase, diagnostic.Phase);
        Assert.Equal(reason, diagnostic.Reason);
        Assert.Contains("HRESULT=0x", diagnostic.Format());
    }
    private static void AssertFailureHeldUntilFinish(FakeOperations operations)
    {
        Assert.True(operations.HeldWhenFailureReported);
        Assert.True(operations.HeldWhenFinishObserved);
        Assert.True(operations.Events.IndexOf("error") < operations.Events.IndexOf("finish"));
        Assert.True(operations.Events.IndexOf("finish") < operations.Events.IndexOf("release"));
        Assert.False(operations.Held);
    }

    private sealed class FakeOperations : InstallerManualExit.IOperations, InstallerManualExit.ILease
    {
        internal readonly List<string> Events = [];
        internal IReadOnlyList<InstallerManualExit.Diagnostic> Diagnostics = [];
        internal string Scenario = "installed-running";
        internal TimeSpan RequestAt;
        internal TimeSpan TargetExitAt = Seconds(1);
        internal TimeSpan FinishAt = Seconds(3);
        internal TimeSpan SetupExitAt = TimeSpan.MaxValue;
        internal TimeSpan NewInstanceAt = TimeSpan.MaxValue;
        internal TimeSpan MonitorErrorAt = TimeSpan.MaxValue;
        internal Exception? VerifyError;
        internal Exception? RunningError;
        internal Exception? AcquireError;
        internal Exception? PreparedError;
        internal Exception? SendError;
        internal Exception? ReportError;
        internal Exception? DisposeError;
        internal Exception? FinishErrorAfterPrepared;
        internal bool PersistentFinishError;
        internal bool ExitDuringDispatch;
        internal bool LockFilesWouldBeFree;
        internal bool Held;
        internal bool PinHeld;
        internal bool PreparedWhileHeld;
        internal bool TargetDirectoryPinnedWhenPrepared;
        internal bool HeldWhenFinishObserved;
        internal bool PinHeldWhenFinishObserved;
        internal bool HeldWhenFailureReported;
        internal bool PinHeldWhenFailureReported;
        internal int Dispatches;
        internal int TargetMonitorCalls;
        internal TimeSpan? DispatchAt;
        internal Action? AfterPrepared;
        internal InstallerManualExit.ShutdownResult Shutdown = new(true);
        internal AppInstanceLockResult ReleaseResult = AppInstanceLockResult.Success();
        private bool _verified;
        private bool _requestObserved;
        private bool _targetExitObserved;
        private bool _finishObserved;
        private bool _prepared;
        public bool HasTarget { get; init; } = true;
        public TimeSpan Elapsed { get; private set; }

        public void Verify()
        {
            Events.Add("verify");
            if (VerifyError is not null) throw VerifyError;
            _verified = true;
            PinHeld = true;
        }
        public bool SetupExited() => Elapsed >= SetupExitAt;
        public bool TargetExited()
        {
            Assert.True(HasTarget);
            if (!ExitDuringDispatch || Dispatches == 0)
                if (Elapsed < TargetExitAt) return false;
            if (!_targetExitObserved) { Events.Add("target-exited"); _targetExitObserved = true; }
            return true;
        }
        public void WriteRunning()
        {
            Assert.True(_verified);
            Assert.True(HasTarget);
            Events.Add("running");
            if (RunningError is not null) throw RunningError;
        }
        public bool Requested()
        {
            Assert.True(_verified);
            if (Elapsed < RequestAt) return false;
            if (!_requestObserved) { Events.Add("request"); _requestObserved = true; }
            return true;
        }
        public bool Finished()
        {
            Assert.True(_verified);
            if (_prepared && FinishErrorAfterPrepared is not null)
            {
                var error = FinishErrorAfterPrepared;
                if (!PersistentFinishError) FinishErrorAfterPrepared = null;
                throw error;
            }
            if (Elapsed < FinishAt) return false;
            HeldWhenFinishObserved = Held;
            PinHeldWhenFinishObserved = PinHeld;
            if (!_finishObserved) { Events.Add("finish"); _finishObserved = true; }
            return true;
        }
        public InstallerManualExit.ShutdownResult RequestExit()
        {
            Assert.True(_verified);
            Assert.True(_requestObserved);
            Assert.True(HasTarget);
            DispatchAt = Elapsed;
            Dispatches++;
            Assert.Equal(1, Dispatches);
            Events.Add("dispatch");
            if (SendError is not null) throw SendError;
            return Shutdown;
        }
        public InstallerManualExit.ILease Acquire()
        {
            Assert.True(_targetExitObserved);
            Assert.True(HasTarget);
            _ = LockFilesWouldBeFree; // A free preflight is deliberately not an input to the controller.
            Events.Add("acquire");
            if (AcquireError is not null) throw AcquireError;
            Held = true;
            return this;
        }
        public void WritePrepared()
        {
            Assert.True(_verified);
            Assert.True(PinHeld);
            if (HasTarget) Assert.True(Held);
            PreparedWhileHeld = Held;
            TargetDirectoryPinnedWhenPrepared = PinHeld;
            Events.Add("prepared");
            if (PreparedError is not null) throw PreparedError;
            _prepared = true;
            AfterPrepared?.Invoke();
        }
        public void EnsureNoNewTargets()
        {
            Assert.True(PinHeld);
            TargetMonitorCalls++;
            if (Elapsed >= MonitorErrorAt) throw new InstallerManualExit.ManualExitException("enumerationfailed", new Win32Exception(5, "TOKEN=enumeration"));
            if (Elapsed >= NewInstanceAt) throw new InstallerManualExit.ManualExitException("newinstance");
        }
        public AppInstanceLockResult Release()
        {
            Assert.True(Held);
            Events.Add("release");
            Held = false;
            return ReleaseResult;
        }
        public void WriteReleased()
        {
            Assert.False(Held);
            Events.Add("released");
        }
        public void Delay() => Elapsed += Seconds(1);
        public void ReportFailure(IReadOnlyList<InstallerManualExit.Diagnostic> diagnostics)
        {
            Events.Add("error");
            Diagnostics = diagnostics;
            HeldWhenFailureReported = Held;
            PinHeldWhenFailureReported = PinHeld;
            if (ReportError is not null) throw ReportError;
        }
        public void Dispose()
        {
            Assert.False(Held);
            Events.Add("dispose");
            PinHeld = false;
            if (DisposeError is not null) throw DisposeError;
        }
    }
}
