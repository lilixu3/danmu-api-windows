using DanmuApi.App.Services;
using DanmuApi.Core;
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
            string displayName, IProgress<CoreInstallProgress>? progress = null, CancellationToken cancellationToken = default) =>
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

    private static CoreManagementService CreateService(
        RecordingInstaller installer,
        RecordingRuntimeController runtime,
        ManagedCoreVariant activeVariant) =>
        new(installer, new FixedRemote(), runtime, () => activeVariant);

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

    private sealed class RecordingInstaller : ICoreInstaller, ICorePreparedInstaller
    {
        private readonly List<string> _calls;
        private CoreInstallationInfo _current;

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
            _current = PreparedResult ?? throw new InvalidOperationException("test prepared result missing");
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
            _current = Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", variant);
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
        public Task<GithubRepositoryMetadata> GetRepositoryAsync(GithubRepositoryReference repository, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GithubRepositoryMetadata(repository.FullName, "main", null, false));
        public Task<GithubCommit> GetCommitAsync(GithubRepositoryReference repository, string reference, CancellationToken cancellationToken = default) =>
            Task.FromResult(RemoteCommit("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
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
