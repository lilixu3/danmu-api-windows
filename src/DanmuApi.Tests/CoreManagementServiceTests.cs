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
    public async Task PullRequestInstallsFromForkHeadRepositoryIntoCustomVariant()
    {
        var calls = new List<string>();
        var custom = Installed("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", ManagedCoreVariant.Custom);
        var installer = new RecordingInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), calls)
        {
            InstallResult = custom,
        };
        var pullRequest = new GithubPullRequest(
            12,
            "feature",
            "body",
            "open",
            "dev",
            "main",
            "fork-owner/fork-repo",
            "feature/core",
            custom.Manifest!.CommitSha,
            false,
            false,
            DateTimeOffset.UtcNow,
            "https://github.com/huangxd-/danmu_api/pull/12",
            1,
            2,
            3);
        var service = new CoreManagementService(
            installer,
            new FixedRemote(pullRequest),
            new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped), calls),
            () => ManagedCoreVariant.Stable);

        var result = await service.InstallPullRequestAsync(
            GithubRepositoryReference.Official("main"),
            12,
            "PR #12",
            GithubProxyCatalog.OriginalId);

        Assert.True(result.Succeeded);
        Assert.NotNull(installer.LastRequest);
        Assert.Equal(ManagedCoreVariant.Custom, installer.LastRequest.Variant);
        Assert.Equal("fork-owner/fork-repo", installer.LastRequest.Repository.FullName);
        Assert.Equal(CoreInstallKind.PullRequest, installer.LastRequest.Kind);
        Assert.Equal(12, installer.LastRequest.PullRequestNumber);
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
    }

    private static CoreManagementService CreateService(
        RecordingInstaller installer,
        RecordingRuntimeController runtime,
        ManagedCoreVariant activeVariant) =>
        new(installer, new FixedRemote(), runtime, () => activeVariant);

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

    private sealed class RecordingInstaller : ICoreInstaller
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
        public RuntimeSnapshot Snapshot { get; private set; } = initial;
        public bool StartWithoutCore { get; init; }
        public Func<Task>? AsyncStart { get; init; }
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
