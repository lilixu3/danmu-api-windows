using DanmuApi.App.Services;
using DanmuApi.Core;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class CoreManagementServiceTests
{
    private sealed class RecordingLease(List<string> calls) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { calls.Add("release"); return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task PreparationLeaseCoversStopMutationAndRestorationEvenOnFailure()
    {
        var calls = new List<string>();
        var installer = new RecordingInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), calls)
        { InstallError = new IOException("download failed") };
        var runtime = new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 42), calls);
        var service = new CoreManagementService(installer, new FixedRemote(), runtime, () => ManagedCoreVariant.Stable,
            _ => { calls.Add("acquire"); return ValueTask.FromResult<IAsyncDisposable>(new RecordingLease(calls)); });
        var result = await service.InstallCommitAsync(ManagedCoreVariant.Stable, GithubRepositoryReference.Official("main"),
            "main", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "核心", GithubProxyCatalog.OriginalId);
        Assert.False(result.Succeeded);
        Assert.True(result.ServiceRestored);
        Assert.Equal(new[] { "acquire", "stop", "install", "start", "release" }, calls);
    }

    [Fact]
    public async Task PendingPreparationRejectsDeleteAndRenameWithoutWritesOrStop()
    {
        var calls = new List<string>();
        var installer = new RecordingInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), calls);
        var runtime = new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 42), calls);
        await using var preparation = new RuntimePreparationService((_, _, _) => Task.CompletedTask);
        var service = new CoreManagementService(installer, new FixedRemote(), runtime, () => ManagedCoreVariant.Stable,
            preparation.AcquireReadyLeaseAsync);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(ManagedCoreVariant.Stable));
        var rename = await service.RenameAsync(ManagedCoreVariant.Stable, "改名");
        Assert.False(rename.Succeeded);
        Assert.Contains("等待准备", rename.Diagnostic);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task RunningActiveCoreStopsInstallsAndRestartsInOrder()
    {
        var calls = new List<string>();
        var installer = new RecordingInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), calls)
        {
            InstallResult = Installed("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
        };
        var runtime = new RecordingRuntimeController(
            new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 42),
            calls);
        var service = CreateService(installer, runtime, ManagedCoreVariant.Stable);

        var result = await service.InstallCommitAsync(
            ManagedCoreVariant.Stable,
            GithubRepositoryReference.Official("main"),
            "main",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "官方核心",
            GithubProxyCatalog.OriginalId);

        Assert.True(result.Succeeded);
        Assert.True(result.DiskChangeApplied);
        Assert.True(result.ServiceRestored);
        Assert.Equal(["stop", "install", "start"], calls);
    }

    /// <summary>回归：删核心会把运行时停在 CoreSetupRequired（见 DeleteRunningActiveCoreStopsAndEndsCoreSetupRequired），
    /// 而该状态下主窗口与托盘的启动入口都是禁用的。装回核心后必须重新评估这个停驻状态，
    /// 否则用户装回核心也点不动"启动服务"，只能重启应用才能恢复。</summary>
    [Fact]
    public async Task InstallingAfterDeleteClearsTheParkedSetupRequiredState()
    {
        var calls = new List<string>();
        var installer = new RecordingInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), calls)
        {
            InstallResult = Installed("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
        };
        var runtime = new RecordingRuntimeController(
            new RuntimeSnapshot(DesktopRuntimeState.CoreSetupRequired, 9321, FailureReason: "核心尚未准备"),
            calls);
        var service = CreateService(installer, runtime, ManagedCoreVariant.Stable);

        var result = await service.InstallCommitAsync(
            ManagedCoreVariant.Stable,
            GithubRepositoryReference.Official("main"),
            "main",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "官方核心",
            GithubProxyCatalog.OriginalId);

        Assert.True(result.Succeeded);
        Assert.Contains("refresh", calls);
        Assert.Equal(DesktopRuntimeState.Stopped, runtime.Snapshot.State);
    }

    [Fact]
    public async Task SuccessfulInstallAnnouncesTheChangedInstallation()
    {
        var calls = new List<string>();
        var installer = new RecordingInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), calls)
        {
            InstallResult = Installed("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
        };
        var runtime = new RecordingRuntimeController(
            new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 42),
            calls);
        var service = CreateService(installer, runtime, ManagedCoreVariant.Stable);
        var announced = new List<ManagedCoreVariant>();
        service.InstallationChanged += (_, args) => announced.Add(args.Variant);

        await service.InstallCommitAsync(
            ManagedCoreVariant.Stable,
            GithubRepositoryReference.Official("main"),
            "main",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "官方核心",
            GithubProxyCatalog.OriginalId);

        Assert.Equal([ManagedCoreVariant.Stable], announced);
    }

    [Fact]
    public async Task FailedInstallThatLeavesDiskUntouchedAnnouncesNothing()
    {
        var calls = new List<string>();
        var installer = new RecordingInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), calls)
        {
            InstallError = new IOException("download failed"),
        };
        var runtime = new RecordingRuntimeController(
            new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 42),
            calls);
        var service = CreateService(installer, runtime, ManagedCoreVariant.Stable);
        var announced = new List<ManagedCoreVariant>();
        service.InstallationChanged += (_, args) => announced.Add(args.Variant);

        await service.InstallCommitAsync(
            ManagedCoreVariant.Stable,
            GithubRepositoryReference.Official("main"),
            "main",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "官方核心",
            GithubProxyCatalog.OriginalId);

        Assert.Empty(announced);
    }

    [Fact]
    public async Task DeletingTheActiveCoreAnnouncesThatItIsGone()
    {
        var calls = new List<string>();
        var installer = new RecordingInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), calls);
        var runtime = new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped), calls)
        {
            StartWithoutCore = true,
        };
        var service = CreateService(installer, runtime, ManagedCoreVariant.Stable);
        var announced = new List<ManagedCoreVariant>();
        service.InstallationChanged += (_, args) => announced.Add(args.Variant);

        var result = await service.DeleteAsync(ManagedCoreVariant.Stable);

        Assert.True(result.Succeeded);
        Assert.Equal([ManagedCoreVariant.Stable], announced);
    }

    [Fact]
    public async Task FailedInstallRestartsPreviousServiceAndPreservesFailure()
    {
        var calls = new List<string>();
        var installer = new RecordingInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), calls)
        {
            InstallError = new IOException("download failed"),
        };
        var runtime = new RecordingRuntimeController(
            new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 42),
            calls);
        var service = CreateService(installer, runtime, ManagedCoreVariant.Stable);

        var result = await service.InstallCommitAsync(
            ManagedCoreVariant.Stable,
            GithubRepositoryReference.Official("main"),
            "main",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "官方核心",
            GithubProxyCatalog.OriginalId);

        Assert.False(result.Succeeded);
        Assert.False(result.DiskChangeApplied);
        Assert.True(result.ServiceRestored);
        Assert.Contains("download failed", result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(["stop", "install", "start"], calls);
    }

    [Fact]
    public async Task InstallingInactiveCustomCoreDoesNotInterruptStableRuntime()
    {
        var calls = new List<string>();
        var custom = Installed("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", ManagedCoreVariant.Custom);
        var installer = new RecordingInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), calls)
        {
            InstallResult = custom,
        };
        var runtime = new RecordingRuntimeController(
            new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 42),
            calls);
        var service = CreateService(installer, runtime, ManagedCoreVariant.Stable);

        var result = await service.InstallCommitAsync(
            ManagedCoreVariant.Custom,
            GithubRepositoryReference.Parse("owner/custom").WithBranch("feature/test"),
            "feature/test",
            custom.Manifest!.CommitSha,
            "实验核心",
            GithubProxyCatalog.OriginalId);

        Assert.True(result.Succeeded);
        Assert.Equal(["install"], calls);
        Assert.Equal(DesktopRuntimeState.Running, runtime.Snapshot.State);
    }

    [Fact]
    public async Task DeleteRunningActiveCoreStopsAndEndsCoreSetupRequired()
    {
        var calls = new List<string>();
        var installer = new RecordingInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), calls);
        var runtime = new RecordingRuntimeController(
            new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 42),
            calls)
        {
            StartWithoutCore = true,
        };
        var service = CreateService(installer, runtime, ManagedCoreVariant.Stable);

        var result = await service.DeleteAsync(ManagedCoreVariant.Stable);

        Assert.True(result.Succeeded);
        Assert.True(result.DiskChangeApplied);
        Assert.False(result.ServiceRestored);
        Assert.Equal(DesktopRuntimeState.CoreSetupRequired, runtime.Snapshot.State);
        Assert.Equal(["stop", "delete", "start"], calls);
    }

    [Fact]
    public async Task ApplyingStaleUpdateIsRejectedBeforeStoppingService()
    {
        var calls = new List<string>();
        var current = Installed("cccccccccccccccccccccccccccccccccccccccc");
        var installer = new RecordingInstaller(current, calls);
        var runtime = new RecordingRuntimeController(
            new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 42),
            calls);
        var service = CreateService(installer, runtime, ManagedCoreVariant.Stable);
        var checkedLocal = current.Manifest! with { CommitSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
        var update = new CoreUpdateCheckResult(
            ManagedCoreVariant.Stable,
            CoreUpdateCheckStatus.Checked,
            true,
            checkedLocal,
            RemoteCommit("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            null,
            DateTimeOffset.UtcNow,
            "update");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyUpdateAsync(update, GithubProxyCatalog.OriginalId));

        Assert.Contains("已变化", error.Message, StringComparison.Ordinal);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task TrayOrBackgroundUpdateAnnouncesTheNewInstallation()
    {
        var calls = new List<string>();
        var current = Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var installer = new RecordingInstaller(current, calls)
        {
            InstallResult = Installed("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
        };
        var runtime = new RecordingRuntimeController(
            new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 42),
            calls);
        var service = CreateService(installer, runtime, ManagedCoreVariant.Stable);
        var announced = new List<ManagedCoreVariant>();
        service.InstallationChanged += (_, args) => announced.Add(args.Variant);
        var update = new CoreUpdateCheckResult(
            ManagedCoreVariant.Stable,
            CoreUpdateCheckStatus.Checked,
            true,
            current.Manifest,
            RemoteCommit("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            null,
            DateTimeOffset.UtcNow,
            "update");

        // 托盘「立即更新核心」与后台自动更新走的就是这条路径：不经过核心页，也必须通知界面。
        var result = await service.ApplyUpdateAsync(update, GithubProxyCatalog.OriginalId);

        Assert.True(result.Succeeded);
        Assert.Equal([ManagedCoreVariant.Stable], announced);
    }

    /// <summary>回归：旧的「单 PR 实验安装」已按移动端语义删除，接口上不再有该入口。
    /// 留在这里的是替代语义 —— 重装一条旧的 PR 安装记录会退化成普通重装（不再伪装成 PR 安装）。</summary>
    [Fact]
    public async Task ReinstallingLegacySinglePullRequestRecordBecomesAPlainReinstall()
    {
        var calls = new List<string>();
        var legacy = Installed("cccccccccccccccccccccccccccccccccccccccc") with
        {
            Manifest = Installed("cccccccccccccccccccccccccccccccccccccccc").Manifest! with
            {
                InstallKind = CoreInstallKind.PullRequest,
                PullRequestNumber = 12,
            },
        };
        var installer = new RecordingInstaller(legacy, calls)
        {
            InstallResult = Installed("cccccccccccccccccccccccccccccccccccccccc"),
        };
        var service = CreateService(installer,
            new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped), calls),
            ManagedCoreVariant.Stable);

        var result = await service.ReinstallAsync(ManagedCoreVariant.Stable, GithubProxyCatalog.OriginalId);

        Assert.True(result.Succeeded);
        Assert.NotNull(installer.LastRequest);
        Assert.Equal(CoreInstallKind.Reinstall, installer.LastRequest.Kind);
        Assert.Null(installer.LastRequest.PullRequestNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsynchronousMutationAndRestoreFailuresPreserveBothDiagnostics(bool canceled)
    {
        var mutation = new TaskCompletionSource<CoreInstallationInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restoreEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var installer = new RecordingInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), calls)
        {
            AsyncInstall = mutation.Task,
        };
        var runtime = new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 42), calls)
        {
            AsyncStart = () => { restoreEntered.SetResult(); return restore.Task; },
        };
        var service = CreateService(installer, runtime, ManagedCoreVariant.Stable);
        var operation = service.InstallCommitAsync(ManagedCoreVariant.Stable,
            GithubRepositoryReference.Official("main"), "main", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "core", GithubProxyCatalog.OriginalId);
        Exception original = canceled ? new OperationCanceledException("mutation canceled") : new IOException("mutation failed");
        mutation.SetException(original);
        await restoreEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        restore.SetException(new IOException("restore failed"));
        if (canceled)
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.Contains("mutation canceled", error.ToString());
            Assert.Contains("restore failed", error.ToString());
            var combined = Assert.IsType<CoreManagementCanceledException>(error);
            Assert.Same(original, combined.Result.MutationError);
            Assert.NotNull(combined.Result.RestorationError);
            Assert.False(combined.Result.DiskChangeApplied);
            Assert.False(combined.Result.ServiceRestored);
        }
        else
        {
            var result = await operation;
            Assert.False(result.Succeeded);
            Assert.False(result.DiskChangeApplied);
            Assert.False(result.ServiceRestored);
            Assert.Contains("mutation failed", result.Diagnostic);
            Assert.Contains("restore failed", result.Diagnostic);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiskCommitRemainsVisibleWhenRestorationThrows(bool mutationAlsoFails)
    {
        var mutation = new TaskCompletionSource<CoreInstallationInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var original = mutationAlsoFails ? new IOException("post-commit failure") : null;
        var installer = new RecordingInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), calls)
        {
            AsyncInstall = mutation.Task,
            InstallError = original,
        };
        var runtime = new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 42), calls)
        {
            AsyncStart = () => { entered.SetResult(); return restore.Task; },
        };
        var service = CreateService(installer, runtime, ManagedCoreVariant.Stable);
        var announced = new List<ManagedCoreVariant>();
        service.InstallationChanged += (_, args) => announced.Add(args.Variant);
        var operation = service.InstallCommitAsync(ManagedCoreVariant.Stable,
            GithubRepositoryReference.Official("main"), "main", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "core", GithubProxyCatalog.OriginalId);
        mutation.SetResult(Installed("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var recovery = new IOException("restore failure");
        restore.SetException(recovery);
        var result = await operation;
        Assert.False(result.Succeeded);
        Assert.True(result.DiskStateKnown);
        Assert.True(result.DiskChangeApplied);
        Assert.False(result.ServiceRestored);
        Assert.Same(original, result.MutationError);
        Assert.Same(recovery, result.RestorationError);
        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", result.Installation!.Manifest!.CommitSha);
        // 服务没能恢复也要通知：磁盘已经是新核心了，界面不能继续显示旧版本。
        Assert.Equal([ManagedCoreVariant.Stable], announced);
    }

    [Fact]
    public async Task PreparedMergeOnActiveVariantRestoresOldCoreWhenNewLaunchFails()
    {
        var calls = new List<string>();
        var previous = Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var installer = new RecordingInstaller(previous, calls)
        {
            PreparedResult = Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa") with
            {
                Manifest = previous.Manifest! with
                {
                    SchemaVersion = 2,
                    InstallKind = CoreInstallKind.LocalPullRequestStack,
                    BaseCommitSha = previous.Manifest!.CommitSha,
                    LocalMergeSha = new string('b', 40),
                    PullRequests = [new CorePullRequestSource(12, "fork/core", "feature", new string('c', 40), null)],
                },
            },
        };
        var starts = 0;
        var runtime = new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 42), calls);
        runtime.AsyncStart = () =>
        {
            starts++;
            runtime.SetSnapshot(starts == 1
                ? new RuntimeSnapshot(DesktopRuntimeState.Failed, FailureReason: "candidate failed health")
                : new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 44));
            return Task.CompletedTask;
        };
        var service = CreateService(installer, runtime, ManagedCoreVariant.Stable);
        var request = new CorePreparedInstallRequest(
            ManagedCoreVariant.Stable, "staging", previous.Manifest!, previous.Manifest!.CommitSha,
            new string('b', 40), [new CorePullRequestSource(12, "fork/core", "feature", new string('c', 40), null)]);

        var result = await service.ApplyPreparedPullRequestMergeAsync(request);

        Assert.False(result.Succeeded);
        Assert.True(result.ServiceRestored);
        Assert.False(result.DiskChangeApplied);
        Assert.Equal(previous.Manifest, result.Installation!.Manifest);
        Assert.Contains("candidate failed health", result.Diagnostic);
        Assert.Equal(["stop", "prepared-install", "start", "stop", "prepared-restore", "start"], calls);
    }

    [Fact]
    public async Task PreparationRejectsChangedRemotePrBeforeRunningGitOrStoppingService()
    {
        var calls = new List<string>();
        var previous = Installed(new string('a', 40));
        var selected = new GithubPullRequest(12, "PR", "", "open", null, "main", "fork/core", "feature",
            new string('c', 40), false, false, null, null, null, null, null);
        var remote = selected with { BaseBranch = "other" };
        var runtime = new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 42), calls);
        var service = new CoreManagementService(new RecordingInstaller(previous, calls), new FixedRemote(remote),
            runtime, () => ManagedCoreVariant.Stable, pullRequestMerge: new UnexpectedMergeService());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreparePullRequestMergeAsync(
            ManagedCoreVariant.Stable, [selected], GithubProxyCatalog.OriginalId));

        Assert.Contains("已变化", error.Message);
        Assert.Empty(calls);
    }

    private sealed class UnexpectedMergeService : ICorePullRequestMergeService
    {
        public Task<CorePreparedInstallRequest> PrepareAsync(ManagedCoreVariant variant, CoreInstallationInfo installed,
            GithubRepositoryReference repository, IReadOnlyList<GithubPullRequest> pullRequests, string proxyId,
            string displayName, IProgress<CoreInstallProgress>? progress = null, CancellationToken cancellationToken = default,
            string? baseShaOverride = null, bool inheritExistingStack = true) =>
            throw new Xunit.Sdk.XunitException("Git preparation must not run for a stale PR");
    }

    [Fact]
    public async Task PreparedMergeRejectsManifestChangeAtSameCommitBeforeStopping()
    {
        var calls = new List<string>();
        var previous = Installed(new string('a', 40));
        var changed = previous with { Manifest = previous.Manifest! with { DisplayName = "renamed" } };
        var installer = new RecordingInstaller(changed, calls);
        var runtime = new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 42), calls);
        var service = CreateService(installer, runtime, ManagedCoreVariant.Stable);
        var request = new CorePreparedInstallRequest(ManagedCoreVariant.Stable, "staging", previous.Manifest!,
            previous.Manifest!.CommitSha, new string('b', 40),
            [new CorePullRequestSource(12, "fork/core", "feature", new string('c', 40), null)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyPreparedPullRequestMergeAsync(request));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task PreparedRecoveryHoldsMutationGateUntilOldCoreIsRestored()
    {
        var calls = new List<string>();
        var previous = Installed(new string('a', 40));
        var installer = new RecordingInstaller(previous, calls)
        {
            PreparedResult = previous with
            {
                Manifest = previous.Manifest! with
                {
                    SchemaVersion = 2,
                    InstallKind = CoreInstallKind.LocalPullRequestStack,
                    BaseCommitSha = previous.Manifest!.CommitSha,
                    LocalMergeSha = new string('b', 40),
                    PullRequests = [new CorePullRequestSource(12, "fork/core", "feature", new string('c', 40), null)],
                },
            },
        };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        installer.AsyncPreparedRestore = async () => { entered.SetResult(); await release.Task; };
        var starts = 0;
        RecordingRuntimeController? runtime = null;
        runtime = new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 42), calls)
        {
            AsyncStart = () =>
            {
                starts++;
                runtime!.SetSnapshot(new RuntimeSnapshot(starts == 1 ? DesktopRuntimeState.Failed : DesktopRuntimeState.Running,
                    FailureReason: starts == 1 ? "candidate health failed" : null));
                return Task.CompletedTask;
            },
        };
        var service = CreateService(installer, runtime, ManagedCoreVariant.Stable);
        var request = new CorePreparedInstallRequest(ManagedCoreVariant.Stable, "staging", previous.Manifest!,
            previous.Manifest!.CommitSha, new string('b', 40),
            [new CorePullRequestSource(12, "fork/core", "feature", new string('c', 40), null)]);

        var applying = service.ApplyPreparedPullRequestMergeAsync(request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deleting = service.DeleteAsync(ManagedCoreVariant.Stable);
        try
        {
            Assert.False(deleting.IsCompleted);
        }
        finally
        {
            release.SetResult();
        }
        var result = await applying;
        Assert.True(result.ServiceRestored);
        await deleting;
        Assert.Equal(["stop", "prepared-install", "start", "stop", "prepared-restore", "start", "stop", "delete", "start"], calls);
    }

    [Fact]
    public async Task AuditBuildingSameBaseStackBroadcastsPendingIdentityAndRejectsOldOrdinaryResult()
    {
        var calls = new List<string>();
        var previous = Installed(new string('a', 40));
        var stack = InstalledStack(new string('a', 40), new string('c', 40));
        var installer = new RecordingInstaller(previous, calls) { PreparedResult = stack };
        var runtime = new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 42), calls);
        using var coordinator = new CoreUpdateCoordinator(installer, new FixedRemote(), new TestTimestampStore(),
            new MemoryCoreUpdateDiscoveryStore());
        CoreUpdateResultHandler? handler = null;
        var service = new CoreManagementService(installer, new FixedRemote(), runtime, () => ManagedCoreVariant.Stable,
            conclusionReconciler: () => handler!);
        handler = new CoreUpdateResultHandler(service, coordinator, new TestRoutes(), new TestNotifications(), new TestDiagnostics());
        var oldUpdate = await coordinator.CheckAsync(ManagedCoreVariant.Stable, force: true);
        await handler.HandleAsync(CoreUpdateTrigger.Background, oldUpdate, CoreUpdateAction.Notify);
        var broadcasts = new List<CoreUpdateCheckResult?>();
        var pendingNotifications = 0;
        coordinator.ResultChanged += (_, result) => broadcasts.Add(result);
        handler.StateChanged += (_, _) => pendingNotifications++;
        var request = new CorePreparedInstallRequest(ManagedCoreVariant.Stable, "staging", previous.Manifest!,
            previous.Manifest!.CommitSha, stack.Manifest!.LocalMergeSha!, stack.Manifest.PullRequests);

        var build = await service.ApplyPreparedPullRequestMergeAsync(request);

        Assert.True(build.Succeeded);
        Assert.True(Assert.Single(broadcasts)!.Local!.IsLocalPullRequestStack);
        Assert.True(handler.PendingUpdate!.Local!.IsLocalPullRequestStack);
        Assert.Equal(1, pendingNotifications);
        calls.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyUpdateAsync(oldUpdate, "original"));
        Assert.Empty(calls);
        var refused = await handler.ApplyPendingAsync();
        Assert.False(refused!.Succeeded);
        Assert.Contains("核对", refused.Diagnostic);
        Assert.Empty(calls);
        Assert.True(installer.Inspect(ManagedCoreVariant.Stable).Manifest!.IsLocalPullRequestStack);
    }

    [Fact]
    public async Task AuditRenameRefreshesPendingSnapshotWithoutNetworkOrDuplicateDiscoveryNotification()
    {
        var calls = new List<string>();
        var previous = Installed(new string('a', 40));
        var installer = new RecordingInstaller(previous, calls);
        var runtime = new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped), calls);
        var remote = new FixedRemote();
        using var coordinator = new CoreUpdateCoordinator(installer, remote, new TestTimestampStore(),
            new MemoryCoreUpdateDiscoveryStore());
        var service = CreateService(installer, runtime, ManagedCoreVariant.Stable);
        var notifications = new TestNotifications();
        var handler = new CoreUpdateResultHandler(service, coordinator, new TestRoutes(), notifications, new TestDiagnostics());
        var update = await coordinator.CheckAsync(ManagedCoreVariant.Stable, force: true);
        await handler.HandleAsync(CoreUpdateTrigger.Background, update, CoreUpdateAction.Notify);
        Assert.Equal(1, notifications.Calls);
        var pendingEvents = 0;
        handler.StateChanged += (_, _) => pendingEvents++;
        var renamed = previous with { Manifest = previous.Manifest! with { DisplayName = "new display name" } };
        installer.SetCurrent(renamed);

        coordinator.ReconcileDiscovery(ManagedCoreVariant.Stable);

        Assert.Equal(1, pendingEvents);
        Assert.True(handler.PendingUpdate!.UpdateAvailable);
        Assert.True(CoreInstallationManifest.SourcesEqual(renamed.Manifest, handler.PendingUpdate.Local));
        Assert.Equal(1, remote.CommitCalls);
        await handler.HandleAsync(CoreUpdateTrigger.Foreground, coordinator.LastResult!, CoreUpdateAction.Notify);
        Assert.Equal(1, notifications.Calls); // Dedup remains variant+remote, not display name/InstalledAt.
        Assert.Equal(1, remote.CommitCalls);
    }

    [Fact]
    public async Task AuditOrdinaryUpdateRechecksSourceAfterWaitingForMutationLock()
    {
        var calls = new List<string>();
        var previous = Installed(new string('a', 40));
        var stack = InstalledStack(new string('a', 40), new string('c', 40));
        var installer = new RecordingInstaller(previous, calls) { PreparedResult = stack };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 42), calls);
        runtime.AsyncStart = async () =>
        {
            entered.TrySetResult();
            await release.Task;
            runtime.SetSnapshot(new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 44));
        };
        var service = CreateService(installer, runtime, ManagedCoreVariant.Stable);
        var request = new CorePreparedInstallRequest(ManagedCoreVariant.Stable, "staging", previous.Manifest!,
            previous.Manifest!.CommitSha, stack.Manifest!.LocalMergeSha!, stack.Manifest.PullRequests);
        var build = service.ApplyPreparedPullRequestMergeAsync(request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var applyOld = service.ApplyUpdateAsync(StackUpdate(previous.Manifest!, new string('b', 40)), "original");
        try { Assert.False(applyOld.IsCompleted); }
        finally { release.SetResult(); }
        await build;
        var beforeRejected = calls.ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(() => applyOld);
        Assert.Equal(beforeRejected, calls);
    }

    [Theory]
    [MemberData(nameof(CoreInstallationModelsTests.ChangedSourceFields), MemberType = typeof(CoreInstallationModelsTests))]
    public async Task AuditPreparedAndStackUpdatesRejectEveryChangedSourceField(string field)
    {
        var calls = new List<string>();
        var expected = CoreInstallationModelsTests.Source();
        var changed = Installed(expected.CommitSha) with { Manifest = CoreInstallationModelsTests.Change(expected, field) };
        var installer = new RecordingInstaller(changed, calls);
        var runtime = new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 42), calls);
        var service = CreateService(installer, runtime, ManagedCoreVariant.Stable);
        var request = new CorePreparedInstallRequest(ManagedCoreVariant.Stable, "staging", expected,
            expected.CommitSha, new string('f', 40), expected.PullRequests);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyPreparedPullRequestMergeAsync(request));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyPullRequestStackUpdateAsync(
            StackUpdate(expected, new string('f', 40)), [], "original"));
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("restoreDiskFails")]
    [InlineData("restoreHealthFails")]
    [InlineData("candidateStopFails")]
    [InlineData("wrongRestoredSource")]
    public async Task AuditUpdateOnlyHealthFailureRollsBackAndKeepsBothFailureDiagnostics(string recovery)
    {
        var calls = new List<string>();
        var previous = InstalledStack(new string('a', 40), new string('c', 40));
        var installer = new RecordingInstaller(previous, calls)
        {
            InstallResult = Installed(new string('b', 40)),
            FailRestorePrepared = recovery == "restoreDiskFails",
            RestoredOverride = recovery == "wrongRestoredSource" ? Installed(new string('a', 40)) : null,
        };
        var starts = 0;
        var stops = 0;
        var runtime = new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 42), calls);
        runtime.AsyncStart = () =>
        {
            starts++;
            runtime.SetSnapshot(starts == 1
                ? new RuntimeSnapshot(DesktopRuntimeState.Failed, FailureReason: "candidate health rejected")
                : recovery == "restoreHealthFails"
                    ? new RuntimeSnapshot(DesktopRuntimeState.Failed, FailureReason: "old health rejected")
                    : new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 44));
            return Task.CompletedTask;
        };
        runtime.AsyncStop = () =>
        {
            stops++;
            runtime.SetSnapshot(stops == 2 && recovery == "candidateStopFails"
                ? new RuntimeSnapshot(DesktopRuntimeState.Failed, 9321, 99, FailureReason: "candidate termination rejected")
                : new RuntimeSnapshot(DesktopRuntimeState.Stopped));
            return Task.CompletedTask;
        };
        var service = CreateService(installer, runtime, ManagedCoreVariant.Stable);

        var result = await service.ApplyPullRequestStackUpdateAsync(
            StackUpdate(previous.Manifest!, new string('b', 40)), [], "original");

        Assert.False(result.Succeeded);
        Assert.Contains("candidate health rejected", result.Diagnostic);
        Assert.DoesNotContain("install", calls);
        Assert.DoesNotContain("prepared-confirm", calls);
        Assert.Equal(CoreInstallKind.Branch, installer.LastRequest!.Kind);
        Assert.NotNull(result.RestorationError); // Original candidate failure stays structured even after successful recovery.
        if (recovery == "success")
        {
            Assert.True(result.ServiceRestored);
            Assert.False(result.DiskChangeApplied);
            Assert.True(CoreInstallationManifest.SourcesEqual(previous.Manifest, result.Installation!.Manifest));
            Assert.Equal(DesktopRuntimeState.Running, runtime.Snapshot.State);
            Assert.Equal(["stop", "candidate-install", "start", "stop", "prepared-restore", "start"], calls);
        }
        else
        {
            Assert.False(result.ServiceRestored);
            Assert.Contains("旧核心恢复失败", result.Diagnostic);
            var combined = Assert.IsType<AggregateException>(result.RestorationError);
            Assert.Contains("candidate health rejected", combined.ToString());
            Assert.Contains(recovery switch
            {
                "restoreDiskFails" => "restore failed",
                "restoreHealthFails" => "old health rejected",
                "candidateStopFails" => "candidate termination rejected",
                _ => "完整来源",
            }, combined.ToString());
            if (recovery == "candidateStopFails") Assert.DoesNotContain("prepared-restore", calls);
            if (recovery == "restoreHealthFails") Assert.False(result.DiskChangeApplied);
        }
    }

    [Fact]
    public async Task AuditUpdateOnlyRecoveryHoldsLockUntilOldServiceHealthVerified()
    {
        var calls = new List<string>();
        var previous = InstalledStack(new string('a', 40), new string('c', 40));
        var installer = new RecordingInstaller(previous, calls) { InstallResult = Installed(new string('b', 40)) };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var runtime = new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 42), calls);
        runtime.AsyncStart = async () =>
        {
            starts++;
            if (starts == 1) runtime.SetSnapshot(new RuntimeSnapshot(DesktopRuntimeState.Failed, FailureReason: "candidate failed"));
            else
            {
                if (starts == 2) { entered.SetResult(); await release.Task; }
                runtime.SetSnapshot(new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 44));
            }
        };
        var service = CreateService(installer, runtime, ManagedCoreVariant.Stable);
        var update = service.ApplyPullRequestStackUpdateAsync(StackUpdate(previous.Manifest!, new string('b', 40)), [], "original");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deleting = service.DeleteAsync(ManagedCoreVariant.Stable);
        try { Assert.False(deleting.IsCompleted); Assert.DoesNotContain("delete", calls); }
        finally { release.SetResult(); }
        var result = await update;
        Assert.False(result.Succeeded);
        Assert.True(result.ServiceRestored);
        await deleting;
        Assert.Equal(["stop", "candidate-install", "start", "stop", "prepared-restore", "start", "stop", "delete", "start"], calls);
    }

    private sealed class TestTimestampStore : ICoreUpdateTimestampStore
    {
        public DateTimeOffset? ReadLastCheck(ManagedCoreVariant variant) => null;
        public void WriteLastCheck(ManagedCoreVariant variant, DateTimeOffset checkedAt) { }
    }
    private sealed class TestRoutes : IGithubRoutePreferenceStore
    {
        public GithubRoutePreference Read() => new("original", true);
        public void Confirm(string proxyId) => throw new NotSupportedException();
        public void Invalidate() => throw new NotSupportedException();
    }
    private sealed class TestNotifications : IDesktopNotificationService
    {
        public int Calls { get; private set; }
        public Task<DesktopNotificationResult> ShowAsync(string title, string message, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new DesktopNotificationResult(true, "submitted"));
        }
    }
    private sealed class TestDiagnostics : IAppDiagnostics
    {
        public string? LastDiagnostic { get; private set; }
        public void Record(string message, Exception? error = null) => LastDiagnostic = message;
    }

    private static CoreManagementService CreateService(
        RecordingInstaller installer,
        RecordingRuntimeController runtime,
        ManagedCoreVariant activeVariant) =>
        new(installer, new FixedRemote(), runtime, () => activeVariant);

    /// <summary>
    /// 本地 PR 组合的更新：用户选了"只更新"（远端已包含全部 PR，或明确放弃那些本地改动），
    /// 就按普通分支安装远端提交，组合身份随之结束。
    /// </summary>
    [Fact]
    public async Task StackUpdateWithoutReMergeInstallsTheRemoteCommitAsAPlainBranch()
    {
        var calls = new List<string>();
        var remoteSha = new string('b', 40);
        var previous = InstalledStack(new string('a', 40), new string('c', 40));
        var installer = new RecordingInstaller(previous, calls) { InstallResult = Installed(remoteSha) };
        var runtime = new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped), calls);
        var service = new CoreManagementService(installer, new FixedRemote(), runtime, () => ManagedCoreVariant.Stable);

        var result = await service.ApplyPullRequestStackUpdateAsync(
            StackUpdate(previous.Manifest!, remoteSha), [], GithubProxyCatalog.OriginalId);

        Assert.True(result.Succeeded);
        Assert.Equal(["candidate-install", "refresh", "prepared-confirm"], calls);
        Assert.NotNull(installer.LastRequest);
        Assert.Equal(CoreInstallKind.Branch, installer.LastRequest!.Kind);
        Assert.Equal(remoteSha, installer.LastRequest.CommitSha);
    }

    /// <summary>
    /// 用户选了"更新并重新并入"：基线换成远端提交（不是旧基线），只并指定的 PR，
    /// 不再继承旧组合里其余那些（远端已经有了，再并一遍没有意义）。
    /// </summary>
    [Fact]
    public async Task StackUpdateWithReMergePreparesOnTheRemoteBaseAndMergesOnlyTheRequestedPullRequests()
    {
        var calls = new List<string>();
        var remoteSha = new string('b', 40);
        var previous = InstalledStack(new string('a', 40), new string('c', 40));
        var open = new GithubPullRequest(15, "PR 15", "", "open", null, "main", "fork/core", "feature",
            new string('d', 40), false, false, null, null, null, null, null);
        var installer = new RecordingInstaller(previous, calls) { PreparedResult = Installed(remoteSha) };
        var runtime = new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped), calls);
        var merge = new RecordingMergeService(previous.Manifest!);
        var service = new CoreManagementService(
            installer, new FixedRemote(open), runtime, () => ManagedCoreVariant.Stable, pullRequestMerge: merge);

        var result = await service.ApplyPullRequestStackUpdateAsync(
            StackUpdate(previous.Manifest!, remoteSha), [15], GithubProxyCatalog.OriginalId);

        Assert.True(result.Succeeded);
        Assert.Equal([15], merge.Numbers);
        Assert.Equal(remoteSha, merge.BaseShaOverride);
        Assert.False(merge.InheritExistingStack);
        Assert.Equal(["prepared-install", "refresh", "prepared-confirm"], calls);
    }

    /// <summary>要重新并入的 PR 已经不是 open：明确拒绝，不静默跳过（跳过就等于悄悄丢改动）。</summary>
    [Fact]
    public async Task StackUpdateRejectsPullRequestsThatAreNoLongerOpen()
    {
        var calls = new List<string>();
        var previous = InstalledStack(new string('a', 40), new string('c', 40));
        var closed = new GithubPullRequest(15, "PR 15", "", "closed", null, "main", "fork/core", "feature",
            new string('d', 40), false, true, null, null, null, null, null);
        var installer = new RecordingInstaller(previous, calls);
        var runtime = new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped), calls);
        var merge = new RecordingMergeService(previous.Manifest!);
        var service = new CoreManagementService(
            installer, new FixedRemote(closed), runtime, () => ManagedCoreVariant.Stable, pullRequestMerge: merge);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyPullRequestStackUpdateAsync(
            StackUpdate(previous.Manifest!, new string('b', 40)), [15], GithubProxyCatalog.OriginalId));

        Assert.Contains("可合并状态", error.Message, StringComparison.Ordinal);
        Assert.Empty(calls);
        Assert.Null(merge.BaseShaOverride);
    }

    /// <summary>不是 PR 组合的核心不能走这条路径（避免把普通核心"更新"成组合语义）。</summary>
    [Fact]
    public async Task StackUpdateRefusesCoresThatAreNotLocalPullRequestStacks()
    {
        var calls = new List<string>();
        var previous = Installed(new string('a', 40));
        var installer = new RecordingInstaller(previous, calls);
        var runtime = new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped), calls);
        var service = CreateService(installer, runtime, ManagedCoreVariant.Stable);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyPullRequestStackUpdateAsync(
            StackUpdate(previous.Manifest!, new string('b', 40)), [], GithubProxyCatalog.OriginalId));

        Assert.Contains("不是本地 PR 组合", error.Message, StringComparison.Ordinal);
        Assert.Empty(calls);
    }

    /// <summary>核对入口：不是组合直接拒绝；是组合时把落盘的 PR 来源交给核对器。</summary>
    [Fact]
    public async Task PresenceAnalysisUsesTheInstalledStackSources()
    {
        var calls = new List<string>();
        var previous = InstalledStack(new string('a', 40), new string('c', 40));
        var runtime = new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped), calls);
        var analyzer = new RecordingPresenceAnalyzer();
        var service = new CoreManagementService(
            new RecordingInstaller(previous, calls), new FixedRemote(), runtime, () => ManagedCoreVariant.Stable,
            presenceAnalyzer: analyzer);

        var report = await service.AnalyzePullRequestPresenceAsync(ManagedCoreVariant.Stable, new string('b', 40));

        Assert.Equal(1, analyzer.Calls);
        Assert.Equal([12], analyzer.Numbers);
        Assert.Equal(new string('b', 40), analyzer.RemoteSha);
        Assert.Equal("huangxd-/danmu_api", report.Repository);
    }

    private static CoreInstallationInfo InstalledStack(string baseSha, string localMergeSha) =>
        Installed(baseSha) with
        {
            Manifest = Installed(baseSha).Manifest! with
            {
                SchemaVersion = CoreInstallationManifest.CurrentSchemaVersion,
                InstallKind = CoreInstallKind.LocalPullRequestStack,
                BaseCommitSha = baseSha,
                LocalMergeSha = localMergeSha,
                PullRequests =
                [
                    new CorePullRequestSource(12, "fork/core", "feature", new string('d', 40), null),
                ],
            },
        };

    private static CoreUpdateCheckResult StackUpdate(CoreInstallationManifest manifest, string remoteSha) =>
        new(manifest.Variant, CoreUpdateCheckStatus.Checked, true, manifest,
            RemoteCommit(remoteSha), null, DateTimeOffset.UtcNow, "本地 PR 组合的基线有新提交");

    private sealed class RecordingMergeService(CoreInstallationManifest expected) : ICorePullRequestMergeService
    {
        public int[] Numbers { get; private set; } = [];
        public string? BaseShaOverride { get; private set; }
        public bool InheritExistingStack { get; private set; } = true;

        public Task<CorePreparedInstallRequest> PrepareAsync(
            ManagedCoreVariant variant,
            CoreInstallationInfo installed,
            GithubRepositoryReference repository,
            IReadOnlyList<GithubPullRequest> pullRequests,
            string proxyId,
            string displayName,
            IProgress<CoreInstallProgress>? progress = null,
            CancellationToken cancellationToken = default,
            string? baseShaOverride = null,
            bool inheritExistingStack = true)
        {
            Numbers = pullRequests.Select(item => item.Number).ToArray();
            BaseShaOverride = baseShaOverride;
            InheritExistingStack = inheritExistingStack;
            return Task.FromResult(new CorePreparedInstallRequest(
                variant,
                "staging",
                expected,
                baseShaOverride ?? expected.CommitSha,
                new string('e', 40),
                [new CorePullRequestSource(15, "fork/core", "feature", new string('f', 40), null)]));
        }
    }

    private sealed class RecordingPresenceAnalyzer : ICorePullRequestPresenceAnalyzer
    {
        public int Calls { get; private set; }
        public int[] Numbers { get; private set; } = [];
        public string? RemoteSha { get; private set; }

        public Task<CorePullRequestPresenceReport> AnalyzeAsync(
            GithubRepositoryReference repository,
            string remoteSha,
            IReadOnlyList<CorePullRequestSource> sources,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            RemoteSha = remoteSha;
            Numbers = sources.Select(source => source.Number).ToArray();
            return Task.FromResult(new CorePullRequestPresenceReport(
                repository.FullName, repository.Branch ?? "main", remoteSha, [], []));
        }
    }

    /// <summary>记录对账调用次数的替身：安装变更后必须被敲一次。</summary>
    private sealed class RecordingReconciler : ICoreUpdateConclusionReconciler
    {
        public List<ManagedCoreVariant> Calls { get; } = [];
        public void ReconcileDiscovery(ManagedCoreVariant variant) => Calls.Add(variant);
    }

    /// <summary>
    /// 遗留问题回归（服务层）：把那个新提交装上后，必须立刻让结论持有人重新对账。
    /// 漏了这一步，侧栏卡片与托盘菜单就会一直显示一个已经装上的「新版本」。
    /// </summary>
    [Fact]
    public async Task InstallingTheUpdateReconcilesTheConclusionRightAway()
    {
        var calls = new List<string>();
        var installer = new RecordingInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), calls)
        {
            InstallResult = Installed("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
        };
        var runtime = new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped), calls);
        var reconciler = new RecordingReconciler();
        var service = new CoreManagementService(
            installer, new FixedRemote(), runtime, () => ManagedCoreVariant.Stable,
            conclusionReconciler: () => reconciler);

        var result = await service.InstallCommitAsync(
            ManagedCoreVariant.Stable,
            GithubRepositoryReference.Official("main"),
            "main",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "官方核心",
            GithubProxyCatalog.OriginalId);

        Assert.True(result.Succeeded);
        Assert.Equal([ManagedCoreVariant.Stable], reconciler.Calls);
    }

    /// <summary>删除核心同样要作废结论（核心不可用了，谈不上"有更新"）。</summary>
    [Fact]
    public async Task DeletingTheCoreReconcilesTheConclusion()
    {
        var calls = new List<string>();
        var installer = new RecordingInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), calls);
        var runtime = new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped), calls);
        var reconciler = new RecordingReconciler();
        var service = new CoreManagementService(
            installer, new FixedRemote(), runtime, () => ManagedCoreVariant.Stable,
            conclusionReconciler: () => reconciler);

        await service.DeleteAsync(ManagedCoreVariant.Stable);

        Assert.Equal([ManagedCoreVariant.Stable], reconciler.Calls);
    }

    /// <summary>
    /// 对账抛错不能反过来把已经落盘成功的安装判成失败：那只是刷新提示。
    /// 但必须留下诊断，不能静默吞掉。
    /// </summary>
    [Fact]
    public async Task ReconcilerFailureIsDiagnosedWithoutFailingTheInstall()
    {
        var calls = new List<string>();
        var installer = new RecordingInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), calls)
        {
            InstallResult = Installed("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
        };
        var runtime = new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped), calls);
        var diagnostics = new List<string>();
        var service = new CoreManagementService(
            installer, new FixedRemote(), runtime, () => ManagedCoreVariant.Stable,
            conclusionReconciler: () => new ThrowingReconciler(),
            diagnosticSink: diagnostics.Add);

        var result = await service.InstallCommitAsync(
            ManagedCoreVariant.Stable,
            GithubRepositoryReference.Official("main"),
            "main",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "官方核心",
            GithubProxyCatalog.OriginalId);

        Assert.True(result.Succeeded);
        Assert.Contains(diagnostics, message => message.Contains("重新对账更新结论失败", StringComparison.Ordinal));
    }

    private sealed class ThrowingReconciler : ICoreUpdateConclusionReconciler
    {
        public void ReconcileDiscovery(ManagedCoreVariant variant) => throw new IOException("settings 只读");
    }

    private static CoreInstallationInfo Installed(
        string sha,
        ManagedCoreVariant variant = ManagedCoreVariant.Stable)
    {
        var manifest = new CoreInstallationManifest(
            1,
            variant,
            variant == ManagedCoreVariant.Stable ? "huangxd-/danmu_api" : "owner/custom",
            "main",
            sha,
            "1.0.0",
            "core",
            CoreInstallKind.Branch,
            null,
            DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        return new CoreInstallationInfo(variant, "test", true, true, "1.0.0", manifest, null);
    }

    private static GithubCommit RemoteCommit(string sha) =>
        new(sha, "title", "title", "dev", DateTimeOffset.UtcNow, []);

    private sealed class RecordingInstaller : ICoreInstaller, ICorePreparedInstaller, ICoreUpdateCandidateInstaller
    {
        private readonly List<string> _calls;
        private CoreInstallationInfo _current;
        private CoreInstallationInfo? _backup;
        public CoreInstallationInfo? RestoredOverride { get; set; }
        public void SetCurrent(CoreInstallationInfo current) => _current = current;

        public RecordingInstaller(CoreInstallationInfo current, List<string> calls)
        {
            _current = current;
            _calls = calls;
        }

        public CoreInstallationInfo? InstallResult { get; init; }
        public Exception? InstallError { get; init; }
        public Task<CoreInstallationInfo>? AsyncInstall { get; init; }
        public CoreInstallRequest? LastRequest { get; private set; }
        public CoreInstallationInfo? PreparedResult { get; set; }
        public string? BackupDirectory { get; set; } = "test-backup";
        public bool FailRestorePrepared { get; set; }
        public Func<Task>? AsyncPreparedRestore { get; set; }

        public Task<CorePreparedInstallation> InstallPreparedAsync(
            CorePreparedInstallRequest request,
            IProgress<CoreInstallProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            _calls.Add("prepared-install");
            _backup = _current;
            _current = PreparedResult ?? throw new InvalidOperationException("test prepared result missing");
            return Task.FromResult(new CorePreparedInstallation(_current, BackupDirectory));
        }

        public Task<CorePreparedInstallation> InstallUpdateCandidateAsync(
            CoreInstallRequest request,
            CoreInstallationManifest expectedManifest,
            IProgress<CoreInstallProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            _calls.Add("candidate-install");
            Assert.True(CoreInstallationManifest.SourcesEqual(_current.Manifest, expectedManifest));
            _backup = _current;
            LastRequest = request;
            if (InstallError is not null) throw InstallError;
            _current = InstallResult ?? throw new InvalidOperationException("test candidate result missing");
            return Task.FromResult(new CorePreparedInstallation(_current, BackupDirectory));
        }

        public async Task<CoreInstallationInfo> RestorePreparedBackupAsync(
            ManagedCoreVariant variant,
            string backupDirectory,
            CancellationToken cancellationToken = default)
        {
            _calls.Add("prepared-restore");
            if (AsyncPreparedRestore is not null) await AsyncPreparedRestore();
            if (FailRestorePrepared) throw new IOException("restore failed");
            _current = RestoredOverride ?? _backup ?? throw new InvalidOperationException("test backup missing");
            return _current;
        }

        public Task ConfirmPreparedBackupAsync(
            ManagedCoreVariant variant,
            string? backupDirectory,
            CancellationToken cancellationToken = default)
        {
            _calls.Add("prepared-confirm");
            return Task.CompletedTask;
        }

        public CoreInstallationInfo Inspect(ManagedCoreVariant variant)
        {
            if (_current.Variant == variant)
            {
                return _current;
            }

            return new CoreInstallationInfo(variant, "missing", false, false, null, null, "missing");
        }

        public IReadOnlyList<CoreVersionRecord> GetHistory(ManagedCoreVariant variant) => [];

        public async Task<CoreInstallationInfo> InstallAsync(CoreInstallRequest request, IProgress<CoreInstallProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            _calls.Add("install");
            LastRequest = request;
            if (AsyncInstall is not null)
            {
                _current = await AsyncInstall;
                if (InstallError is not null) throw InstallError;
                return _current;
            }
            if (InstallError is not null)
            {
                throw InstallError;
            }

            _current = InstallResult ?? throw new InvalidOperationException("test install result missing");
            return _current;
        }

        public Task<CoreInstallationInfo> RestoreHistoryAsync(ManagedCoreVariant variant, string historyId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Delete(ManagedCoreVariant variant)
        {
            _calls.Add("delete");
            _current = new CoreInstallationInfo(variant, "missing", false, false, null, null, "missing");
        }

        public void UpdateDisplayName(ManagedCoreVariant variant, string displayName) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingRuntimeController(RuntimeSnapshot initial, List<string> calls) : IRuntimeController
    {
        public Task RefreshCoreSetupRequiredAsync(CancellationToken cancellationToken = default)
        {
            calls.Add("refresh");
            if (Snapshot.State == DesktopRuntimeState.CoreSetupRequired)
            {
                Snapshot = new RuntimeSnapshot(DesktopRuntimeState.Stopped);
                SnapshotChanged?.Invoke(this, Snapshot);
            }
            return Task.CompletedTask;
        }
        public RuntimeSnapshot Snapshot { get; private set; } = initial;
        public void SetSnapshot(RuntimeSnapshot snapshot)
        {
            Snapshot = snapshot;
            SnapshotChanged?.Invoke(this, snapshot);
        }
        public bool StartWithoutCore { get; init; }
        public Func<Task>? AsyncStart { get; set; }
        public Func<Task>? AsyncStop { get; set; }
        public event EventHandler<RuntimeSnapshot>? SnapshotChanged;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            calls.Add("start");
            if (AsyncStart is not null) return AsyncStart();
            Snapshot = StartWithoutCore
                ? new RuntimeSnapshot(DesktopRuntimeState.CoreSetupRequired, FailureReason: "missing")
                : new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 43);
            SnapshotChanged?.Invoke(this, Snapshot);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            calls.Add("stop");
            if (AsyncStop is not null) return AsyncStop();
            Snapshot = new RuntimeSnapshot(DesktopRuntimeState.Stopped);
            SnapshotChanged?.Invoke(this, Snapshot);
            return Task.CompletedTask;
        }

        public Task<AdoptionResult> AdoptAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(AdoptionResult.Failure(AdoptionFailureKind.NotFound, Snapshot, "unused"));
        public string? ReconcileLiveness() => null;
        public Task RestartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ShutdownAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FixedRemote(GithubPullRequest? pullRequest = null) : IGithubCoreRemote
    {
        public GithubRateLimit? LastRateLimit => null;
        public int CommitCalls { get; private set; }
        public Task<GithubRepositoryMetadata> GetRepositoryAsync(GithubRepositoryReference repository, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GithubRepositoryMetadata(repository.FullName, "main", null, false));
        public Task<GithubCommit> GetCommitAsync(GithubRepositoryReference repository, string reference, CancellationToken cancellationToken = default)
        {
            CommitCalls++;
            return Task.FromResult(RemoteCommit("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        }
        public Task<GithubPullRequest> GetPullRequestAsync(GithubRepositoryReference repository, int number, CancellationToken cancellationToken = default) =>
            Task.FromResult(pullRequest ?? throw new NotSupportedException());
        public Task<IReadOnlyList<GithubBranch>> GetAllBranchesAsync(GithubRepositoryReference repository, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GithubCommitPage> GetCommitsAsync(GithubRepositoryReference repository, string reference, int page = 1, int pageSize = 30, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GithubCommitDetails> GetCommitDetailsAsync(GithubRepositoryReference repository, string sha, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GithubPullRequestPage> GetPullRequestsAsync(GithubRepositoryReference repository, string baseBranch, string state = "open", int page = 1, int pageSize = 30, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GithubFileChange>> GetAllPullRequestFilesAsync(GithubRepositoryReference repository, int number, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GithubCompareResult> GetCompareAsync(GithubRepositoryReference repository, string baseSha, string headSha, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GithubRateLimit> GetRateLimitAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
