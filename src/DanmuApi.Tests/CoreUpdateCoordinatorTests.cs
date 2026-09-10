using DanmuApi.Core;

namespace DanmuApi.Tests;

public sealed class CoreUpdateCoordinatorTests
{
    [Fact]
    public async Task FirstCheckComparesManifestShaAndPersistsTime()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var timestamps = new MemoryTimestampStore();
        var remote = new RecordingRemote(RemoteCommit("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        var coordinator = new CoreUpdateCoordinator(
            new FixedInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")),
            remote,
            timestamps,
            timeProvider: time);

        var result = await coordinator.CheckAsync(ManagedCoreVariant.Stable, force: false);

        Assert.Equal(CoreUpdateCheckStatus.Checked, result.Status);
        Assert.True(result.UpdateAvailable);
        Assert.Equal(1, remote.GetCommitCalls);
        Assert.Equal(time.GetUtcNow(), timestamps.ReadLastCheck(ManagedCoreVariant.Stable));
    }

    [Fact]
    public async Task AutomaticCheckSkipsBeforeTenMinutesAndRunsAtBoundary()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var timestamps = new MemoryTimestampStore();
        var remote = new RecordingRemote(RemoteCommit("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        var coordinator = new CoreUpdateCoordinator(
            new FixedInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")),
            remote,
            timestamps,
            timeProvider: time);
        await coordinator.CheckAsync(ManagedCoreVariant.Stable, force: false);

        time.Advance(TimeSpan.FromMinutes(9));
        var skipped = await coordinator.CheckAsync(ManagedCoreVariant.Stable, force: false);
        time.Advance(TimeSpan.FromMinutes(1));
        var checkedResult = await coordinator.CheckAsync(ManagedCoreVariant.Stable, force: false);

        Assert.Equal(CoreUpdateCheckStatus.SkippedCooldown, skipped.Status);
        Assert.Equal(CoreUpdateCheckStatus.Checked, checkedResult.Status);
        Assert.Equal(2, remote.GetCommitCalls);
    }

    [Fact]
    public async Task ManualCheckIgnoresCooldown()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var remote = new RecordingRemote(RemoteCommit("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        var coordinator = new CoreUpdateCoordinator(
            new FixedInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")),
            remote,
            new MemoryTimestampStore(),
            timeProvider: time);
        await coordinator.CheckAsync(ManagedCoreVariant.Stable, force: false);

        var forced = await coordinator.CheckAsync(ManagedCoreVariant.Stable, force: true);

        Assert.Equal(CoreUpdateCheckStatus.Checked, forced.Status);
        Assert.Equal(2, remote.GetCommitCalls);
    }

    [Fact]
    public async Task ConcurrentSameVariantChecksShareOneRemoteRequest()
    {
        var release = new TaskCompletionSource<GithubCommit>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remote = new RecordingRemote(release.Task);
        var coordinator = new CoreUpdateCoordinator(
            new FixedInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")),
            remote,
            new MemoryTimestampStore());

        var first = coordinator.CheckAsync(ManagedCoreVariant.Stable, force: true);
        var second = coordinator.CheckAsync(ManagedCoreVariant.Stable, force: true);
        release.SetResult(RemoteCommit("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        await Task.WhenAll(first, second);

        Assert.Equal(1, remote.GetCommitCalls);
        Assert.Equal(first.Result, second.Result);
    }

    [Fact]
    public async Task DifferentVariantsDoNotShareActiveResult()
    {
        var remote = new RecordingRemote(RemoteCommit("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        var installer = new VariantInstaller(
            Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ManagedCoreVariant.Stable),
            Installed("cccccccccccccccccccccccccccccccccccccccc", ManagedCoreVariant.Custom));
        var coordinator = new CoreUpdateCoordinator(installer, remote, new MemoryTimestampStore());

        var stable = coordinator.CheckAsync(ManagedCoreVariant.Stable, force: true);
        var custom = coordinator.CheckAsync(ManagedCoreVariant.Custom, force: true);
        var results = await Task.WhenAll(stable, custom);

        Assert.Equal(2, remote.GetCommitCalls);
        Assert.Contains(results, result => result.Variant == ManagedCoreVariant.Stable);
        Assert.Contains(results, result => result.Variant == ManagedCoreVariant.Custom);
    }

    [Fact]
    public async Task ClockRollbackMakesPersistedCheckDue()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var timestamps = new MemoryTimestampStore();
        timestamps.WriteLastCheck(ManagedCoreVariant.Stable, time.GetUtcNow().AddHours(1));
        var remote = new RecordingRemote(RemoteCommit("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        var coordinator = new CoreUpdateCoordinator(
            new FixedInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")),
            remote,
            timestamps,
            timeProvider: time);

        var result = await coordinator.CheckAsync(ManagedCoreVariant.Stable, force: false);

        Assert.Equal(CoreUpdateCheckStatus.Checked, result.Status);
        Assert.Equal(1, remote.GetCommitCalls);
    }

    [Fact]
    public async Task RemoteFailureIsNonThrowingFailedResult()
    {
        var remote = new RecordingRemote(Task.FromException<GithubCommit>(
            new GithubRemoteException(GithubFailureKind.Network, "offline")));
        var coordinator = new CoreUpdateCoordinator(
            new FixedInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")),
            remote,
            new MemoryTimestampStore());

        var result = await coordinator.CheckAsync(ManagedCoreVariant.Stable, force: true);

        Assert.Equal(CoreUpdateCheckStatus.Failed, result.Status);
        Assert.Contains("offline", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LastWaiterCancellationStopsUnderlyingRequestAndAllowsNewCheck()
    {
        var release = new TaskCompletionSource<GithubCommit>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remote = new RecordingRemote(release.Task);
        var timestamps = new MemoryTimestampStore();
        var coordinator = new CoreUpdateCoordinator(new FixedInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")), remote, timestamps);
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        var first = coordinator.CheckAsync(ManagedCoreVariant.Stable, true, firstCancellation.Token);
        var second = coordinator.CheckAsync(ManagedCoreVariant.Stable, true, secondCancellation.Token);
        var underlying = remote.LastToken;
        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(underlying.IsCancellationRequested);
        Assert.False(second.IsCompleted);
        secondCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.True(underlying.IsCancellationRequested);
        Assert.Null(timestamps.ReadLastCheck(ManagedCoreVariant.Stable));
        var third = coordinator.CheckAsync(ManagedCoreVariant.Stable, true);
        Assert.Equal(2, remote.GetCommitCalls);
        release.SetResult(RemoteCommit("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        Assert.Equal(CoreUpdateCheckStatus.Checked, (await third).Status);
    }

    [Fact]
    public async Task PreCanceledCallerDoesNotStartRemoteRequest()
    {
        var release = new TaskCompletionSource<GithubCommit>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remote = new RecordingRemote(release.Task);
        var coordinator = new CoreUpdateCoordinator(new FixedInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")), remote, new MemoryTimestampStore());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.CheckAsync(ManagedCoreVariant.Stable, true, cancellation.Token));
        Assert.Equal(0, remote.GetCommitCalls);
    }

    [Fact]
    public async Task CancelingOneCallerStillDeliversResultToOtherCaller()
    {
        var release = new TaskCompletionSource<GithubCommit>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remote = new RecordingRemote(release.Task);
        using var coordinator = new CoreUpdateCoordinator(new FixedInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")), remote, new MemoryTimestampStore());
        using var cancellation = new CancellationTokenSource();
        var first = coordinator.CheckAsync(ManagedCoreVariant.Stable, true, cancellation.Token);
        var second = coordinator.CheckAsync(ManagedCoreVariant.Stable, true);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult(RemoteCommit("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        Assert.Equal(CoreUpdateCheckStatus.Checked, (await second).Status);
        Assert.Equal(1, remote.GetCommitCalls);
    }

    [Fact]
    public async Task DisposeCancelsActiveCheckAndRejectsFurtherCalls()
    {
        var release = new TaskCompletionSource<GithubCommit>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remote = new RecordingRemote(release.Task);
        var timestamps = new MemoryTimestampStore();
        var coordinator = new CoreUpdateCoordinator(new FixedInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")), remote, timestamps);
        var operation = coordinator.CheckAsync(ManagedCoreVariant.Stable, true);
        coordinator.Dispose();
        coordinator.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(remote.LastToken.IsCancellationRequested);
        Assert.Null(timestamps.ReadLastCheck(ManagedCoreVariant.Stable));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => coordinator.CheckAsync(ManagedCoreVariant.Stable, true));
    }

    [Fact]
    public async Task DeadlineStopsRemoteAtFortyFiveSecondsAndReturnsExplicitFailure()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var release = new TaskCompletionSource<GithubCommit>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remote = new RecordingRemote(release.Task);
        using var coordinator = new CoreUpdateCoordinator(new FixedInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")), remote, new MemoryTimestampStore(), timeProvider: time);
        var operation = coordinator.CheckAsync(ManagedCoreVariant.Stable, true);
        time.Advance(TimeSpan.FromSeconds(44));
        Assert.False(operation.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        var result = await operation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(remote.LastToken.IsCancellationRequested);
        Assert.Equal(CoreUpdateCheckStatus.Failed, result.Status);
        Assert.Equal(GithubFailureKind.Network, result.FailureKind);
        Assert.Contains("45", result.Diagnostic);
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

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp;
        private readonly List<ManualTimer> _timers = [];
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, _utcNow + dueTime);
            _timers.Add(timer);
            return timer;
        }
        private sealed class ManualTimer(TimerCallback callback, object? state, DateTimeOffset due) : ITimer
        {
            private bool _disposed;
            public void Fire(DateTimeOffset now)
            {
                if (!_disposed && now >= due)
                {
                    _disposed = true;
                    callback(state);
                }
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override long GetTimestamp() => _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan value)
        {
            _utcNow += value;
            _timestamp += value.Ticks;
            foreach (var timer in _timers.ToArray()) timer.Fire(_utcNow);
        }
    }

    private sealed class MemoryTimestampStore : ICoreUpdateTimestampStore
    {
        private readonly Dictionary<ManagedCoreVariant, DateTimeOffset> _values = [];
        public DateTimeOffset? ReadLastCheck(ManagedCoreVariant variant) =>
            _values.TryGetValue(variant, out var value) ? value : null;
        public void WriteLastCheck(ManagedCoreVariant variant, DateTimeOffset checkedAt) =>
            _values[variant] = checkedAt;
    }

    private sealed class FixedInstaller(CoreInstallationInfo info) : ICoreInstaller
    {
        public CoreInstallationInfo Inspect(ManagedCoreVariant variant) => info;
        public IReadOnlyList<CoreVersionRecord> GetHistory(ManagedCoreVariant variant) => [];
        public Task<CoreInstallationInfo> InstallAsync(CoreInstallRequest request, IProgress<CoreInstallProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoreInstallationInfo> RestoreHistoryAsync(ManagedCoreVariant variant, string historyId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Delete(ManagedCoreVariant variant) => throw new NotSupportedException();
        public void UpdateDisplayName(ManagedCoreVariant variant, string displayName) => throw new NotSupportedException();
    }

    private sealed class VariantInstaller(CoreInstallationInfo stable, CoreInstallationInfo custom) : ICoreInstaller
    {
        public CoreInstallationInfo Inspect(ManagedCoreVariant variant) => variant == ManagedCoreVariant.Stable ? stable : custom;
        public IReadOnlyList<CoreVersionRecord> GetHistory(ManagedCoreVariant variant) => [];
        public Task<CoreInstallationInfo> InstallAsync(CoreInstallRequest request, IProgress<CoreInstallProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoreInstallationInfo> RestoreHistoryAsync(ManagedCoreVariant variant, string historyId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Delete(ManagedCoreVariant variant) => throw new NotSupportedException();
        public void UpdateDisplayName(ManagedCoreVariant variant, string displayName) => throw new NotSupportedException();
    }

    private sealed class RecordingRemote : IGithubCoreRemote
    {
        private readonly Task<GithubCommit> _commit;
        public RecordingRemote(GithubCommit commit) : this(Task.FromResult(commit)) { }
        public RecordingRemote(Task<GithubCommit> commit) => _commit = commit;
        public int GetCommitCalls { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public GithubRateLimit? LastRateLimit => null;
        public Task<GithubCommit> GetCommitAsync(GithubRepositoryReference repository, string reference, CancellationToken cancellationToken = default)
        {
            GetCommitCalls++;
            LastToken = cancellationToken;
            return _commit.WaitAsync(cancellationToken);
        }
        public Task<GithubRepositoryMetadata> GetRepositoryAsync(GithubRepositoryReference repository, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GithubBranch>> GetAllBranchesAsync(GithubRepositoryReference repository, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GithubCommitPage> GetCommitsAsync(GithubRepositoryReference repository, string reference, int page = 1, int pageSize = 30, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GithubCommitDetails> GetCommitDetailsAsync(GithubRepositoryReference repository, string sha, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GithubPullRequestPage> GetPullRequestsAsync(GithubRepositoryReference repository, string baseBranch, string state = "open", int page = 1, int pageSize = 30, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GithubPullRequest> GetPullRequestAsync(GithubRepositoryReference repository, int number, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GithubFileChange>> GetAllPullRequestFilesAsync(GithubRepositoryReference repository, int number, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GithubCompareResult> GetCompareAsync(GithubRepositoryReference repository, string baseSha, string headSha, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GithubRateLimit> GetRateLimitAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
