using DanmuApi.Core;
using DanmuApi.Platform;

namespace DanmuApi.Tests;

public sealed class CoreUpdateSchedulerTests
{
    [Fact]
    public async Task SoftwareUpdatePausePreventsCoreInstallDispatchAndCanResume()
    {
        var remote = new RecordingRemote(RemoteCommit(new string('b',40)));
        var coordinator = new CoreUpdateCoordinator(new FixedInstaller(Installed(new string('a',40))),remote,new MemoryTimestampStore(),new MemoryCoreUpdateDiscoveryStore());
        var handler = new RecordingHandler();
        await using var scheduler = new CoreUpdateScheduler(coordinator,new FixedPolicyStore(CoreUpdateScheduleOptions.Default),handler,()=>ManagedCoreVariant.Stable);
        await scheduler.PauseForApplicationUpdateAsync();
        await scheduler.CheckManualAsync(ManagedCoreVariant.Stable);
        Assert.Empty(handler.Calls);
        scheduler.ResumeAfterApplicationUpdate();
        await scheduler.CheckManualAsync(ManagedCoreVariant.Stable);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task FailedNotificationClaimIsReleasedButSuccessfulSubmissionIsDeduplicated()
    {
        var remote = new RecordingRemote(RemoteCommit(new string('b', 40)));
        var coordinator = new CoreUpdateCoordinator(new FixedInstaller(Installed(new string('a', 40))), remote, new MemoryTimestampStore(), new MemoryCoreUpdateDiscoveryStore());
        var handler = new RecordingHandler { FailNext = true };
        await using var scheduler = new CoreUpdateScheduler(coordinator, new FixedPolicyStore(CoreUpdateScheduleOptions.Default), handler, () => ManagedCoreVariant.Stable);
        await Assert.ThrowsAsync<IOException>(() => scheduler.CheckManualAsync(ManagedCoreVariant.Stable));
        await scheduler.CheckManualAsync(ManagedCoreVariant.Stable);
        await scheduler.CheckManualAsync(ManagedCoreVariant.Stable);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task ForegroundAndBackgroundOverlapShareRequestAndHandleUpdateOnce()
    {
        var release = new TaskCompletionSource<GithubCommit>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remote = new RecordingRemote(release.Task);
        var installer = new FixedInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        var coordinator = new CoreUpdateCoordinator(installer, remote, new MemoryTimestampStore(), new MemoryCoreUpdateDiscoveryStore());
        var handler = new RecordingHandler();
        await using var scheduler = new CoreUpdateScheduler(
            coordinator,
            new FixedPolicyStore(CoreUpdateScheduleOptions.Default),
            handler,
            () => ManagedCoreVariant.Stable);

        var foreground = scheduler.CheckForegroundAsync();
        var background = scheduler.CheckBackgroundAsync();
        release.SetResult(RemoteCommit("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        await Task.WhenAll(foreground, background!);

        Assert.Equal(1, remote.GetCommitCalls);
        Assert.Single(handler.Calls);
        Assert.True(handler.Calls[0].Result.UpdateAvailable);
    }

    [Fact]
    public async Task BackgroundUsesItsOwnIntervalAgainstSharedLastCheckLedger()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var remote = new RecordingRemote(RemoteCommit("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        var coordinator = new CoreUpdateCoordinator(
            new FixedInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")),
            remote,
            new MemoryTimestampStore(),
            new MemoryCoreUpdateDiscoveryStore(),
            timeProvider: time);
        var options = CoreUpdateScheduleOptions.Default;
        await using var scheduler = new CoreUpdateScheduler(
            coordinator,
            new FixedPolicyStore(options),
            new RecordingHandler(),
            () => ManagedCoreVariant.Stable,
            time);
        await scheduler.CheckForegroundAsync();

        time.Advance(TimeSpan.FromMinutes(11));
        var backgroundSkipped = await scheduler.CheckBackgroundAsync();
        var foregroundDue = await scheduler.CheckForegroundAsync();

        Assert.NotNull(backgroundSkipped);
        Assert.Equal(CoreUpdateCheckStatus.SkippedCooldown, backgroundSkipped.Status);
        Assert.Equal(CoreUpdateCheckStatus.Checked, foregroundDue.Status);
        Assert.Equal(2, remote.GetCommitCalls);
    }

    [Fact]
    public async Task ManualCheckForcesButJoinsEquivalentActiveRequest()
    {
        var release = new TaskCompletionSource<GithubCommit>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remote = new RecordingRemote(release.Task);
        var coordinator = new CoreUpdateCoordinator(
            new FixedInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")),
            remote,
            new MemoryTimestampStore(),
            new MemoryCoreUpdateDiscoveryStore());
        await using var scheduler = new CoreUpdateScheduler(
            coordinator,
            new FixedPolicyStore(CoreUpdateScheduleOptions.Default),
            new RecordingHandler(),
            () => ManagedCoreVariant.Stable);

        var background = scheduler.CheckBackgroundAsync();
        var manual = scheduler.CheckManualAsync(ManagedCoreVariant.Stable);
        release.SetResult(RemoteCommit("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        await Task.WhenAll(background!, manual);

        Assert.Equal(1, remote.GetCommitCalls);
    }

    [Fact]
    public async Task DisabledBackgroundCheckMakesNoRequest()
    {
        var remote = new RecordingRemote(RemoteCommit("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        var coordinator = new CoreUpdateCoordinator(
            new FixedInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")),
            remote,
            new MemoryTimestampStore(),
            new MemoryCoreUpdateDiscoveryStore());
        await using var scheduler = new CoreUpdateScheduler(
            coordinator,
            new FixedPolicyStore(CoreUpdateScheduleOptions.Default with { BackgroundEnabled = false }),
            new RecordingHandler(),
            () => ManagedCoreVariant.Stable);

        var result = await scheduler.CheckBackgroundAsync();

        Assert.Null(result);
        Assert.Equal(0, remote.GetCommitCalls);
    }

    [Theory]
    [InlineData(4, 60)]
    [InlineData(10, 14)]
    [InlineData(10, 10081)]
    public void InvalidIntervalsFailExplicitly(int foregroundMinutes, int backgroundMinutes)
    {
        var options = new CoreUpdateScheduleOptions(
            TimeSpan.FromMinutes(foregroundMinutes),
            TimeSpan.FromMinutes(backgroundMinutes),
            true,
            CoreUpdateAction.Notify);

        Assert.Throws<FormatException>(() => options.Validate());
    }

    private static CoreInstallationInfo Installed(string sha)
    {
        var manifest = new CoreInstallationManifest(
            1,
            ManagedCoreVariant.Stable,
            "huangxd-/danmu_api",
            "main",
            sha,
            "1.0.0",
            "core",
            CoreInstallKind.Branch,
            null,
            DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        return new CoreInstallationInfo(ManagedCoreVariant.Stable, "test", true, true, "1.0.0", manifest, null);
    }

    private static GithubCommit RemoteCommit(string sha) =>
        new(sha, "title", "title", "dev", DateTimeOffset.UtcNow, []);

    private sealed class FixedPolicyStore(CoreUpdateScheduleOptions options) : ICoreUpdatePolicyStore
    {
        public CoreUpdateScheduleOptions Read() => options;
        public void Write(CoreUpdateScheduleOptions value) => throw new NotSupportedException();
    }

    private sealed class RecordingHandler : ICoreUpdateResultHandler
    {
        public bool FailNext { get; set; }
        private readonly object _sync = new();
        private readonly List<(CoreUpdateTrigger Trigger, CoreUpdateCheckResult Result, CoreUpdateAction Action)> _calls = [];
        public IReadOnlyList<(CoreUpdateTrigger Trigger, CoreUpdateCheckResult Result, CoreUpdateAction Action)> Calls
        {
            get
            {
                lock (_sync)
                {
                    return _calls.ToArray();
                }
            }
        }

        public Task HandleAsync(CoreUpdateTrigger trigger, CoreUpdateCheckResult result, CoreUpdateAction action, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                _calls.Add((trigger, result, action));
                if (FailNext)
                {
                    FailNext = false;
                    throw new IOException("Notification submission failed");
                }
            }

            return Task.CompletedTask;
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override long GetTimestamp() => _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan value)
        {
            _utcNow += value;
            _timestamp += value.Ticks;
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

    private sealed class RecordingRemote : IGithubCoreRemote
    {
        private readonly Task<GithubCommit> _commit;
        public RecordingRemote(GithubCommit commit) : this(Task.FromResult(commit)) { }
        public RecordingRemote(Task<GithubCommit> commit) => _commit = commit;
        public int GetCommitCalls { get; private set; }
        public GithubRateLimit? LastRateLimit => null;
        public Task<GithubCommit> GetCommitAsync(GithubRepositoryReference repository, string reference, CancellationToken cancellationToken = default)
        {
            GetCommitCalls++;
            return _commit;
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

public sealed class SettingsCoreUpdatePolicyStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"danmu-policy-{Guid.NewGuid():N}");

    [Fact]
    public void MissingSettingsUseConservativeDefaults()
    {
        var store = CreateStore();

        Assert.Equal(CoreUpdateScheduleOptions.Default, store.Read());
    }

    [Fact]
    public void SettingsRoundTripCustomValues()
    {
        var store = CreateStore();
        var expected = new CoreUpdateScheduleOptions(
            TimeSpan.FromMinutes(25),
            TimeSpan.FromMinutes(90),
            false,
            CoreUpdateAction.Automatic);

        store.Write(expected);

        Assert.Equal(expected, store.Read());
    }

    [Fact]
    public void CorruptSettingFailsInsteadOfFallingBack()
    {
        var settings = new SettingsStore(Path.Combine(_root, "settings.properties"));
        settings.Write(new Dictionary<string, string?>
        {
            [SettingsCoreUpdatePolicyStore.BackgroundEnabledKey] = "yes",
        });
        var store = new SettingsCoreUpdatePolicyStore(settings);

        var error = Assert.Throws<FormatException>(() => store.Read());
        Assert.Contains(SettingsCoreUpdatePolicyStore.BackgroundEnabledKey, error.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SettingsCoreUpdatePolicyStore CreateStore() =>
        new(new SettingsStore(Path.Combine(_root, "settings.properties")));
}

/// <summary>
/// 核心更新发现记录的落盘：这是"重启后侧栏卡片还在"的唯一依据，所以
/// ① 每变体独立、② 值能原样读回、③ 残缺记录不许拼半个卡片出来。
/// </summary>
public sealed class SettingsCoreUpdateDiscoveryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"danmu-discovery-{Guid.NewGuid():N}");

    [Fact]
    public void SavedDiscoverySurvivesAFreshStoreInstance()
    {
        var expected = new CoreUpdateDiscovery(
            ManagedCoreVariant.Dev,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "fix: 修复合并规则",
            DateTimeOffset.FromUnixTimeMilliseconds(1790000000000));
        CreateStore().Write(expected);

        // 新实例 = 新进程：只认磁盘上的内容。
        Assert.Equal(expected, CreateStore().Read(ManagedCoreVariant.Dev));
    }

    [Fact]
    public void VariantsKeepSeparateRecordsAndClearIsPerVariant()
    {
        var store = CreateStore();
        var checkedAt = DateTimeOffset.FromUnixTimeMilliseconds(1790000000000);
        store.Write(new CoreUpdateDiscovery(
            ManagedCoreVariant.Stable, new string('a', 40), new string('b', 40), "stable", checkedAt));
        store.Write(new CoreUpdateDiscovery(
            ManagedCoreVariant.Custom, new string('c', 40), new string('d', 40), "custom", checkedAt));

        store.Clear(ManagedCoreVariant.Stable);

        Assert.Null(store.Read(ManagedCoreVariant.Stable));
        Assert.Equal("custom", store.Read(ManagedCoreVariant.Custom)!.RemoteTitle);
    }

    [Fact]
    public void MissingRecordReadsAsNullWithoutDiagnostics()
    {
        var diagnostics = new List<string>();

        Assert.Null(CreateStore(diagnostics).Read(ManagedCoreVariant.Stable));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void IncompleteRecordIsReportedInsteadOfGuessed()
    {
        var settings = new SettingsStore(Path.Combine(_root, "settings.properties"));
        settings.Write(new Dictionary<string, string?>
        {
            [SettingsCoreUpdateDiscoveryStore.Key(ManagedCoreVariant.Stable, SettingsCoreUpdateDiscoveryStore.RemoteShaSuffix)] =
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        });
        var diagnostics = new List<string>();

        Assert.Null(new SettingsCoreUpdateDiscoveryStore(settings, diagnostics.Add).Read(ManagedCoreVariant.Stable));
        Assert.Contains(diagnostics, message => message.Contains("不完整", StringComparison.Ordinal));
    }

    [Fact]
    public void GarbageTimestampIsReportedInsteadOfGuessed()
    {
        var settings = new SettingsStore(Path.Combine(_root, "settings.properties"));
        settings.Write(new Dictionary<string, string?>
        {
            [SettingsCoreUpdateDiscoveryStore.Key(ManagedCoreVariant.Stable, SettingsCoreUpdateDiscoveryStore.RemoteShaSuffix)] = "b",
            [SettingsCoreUpdateDiscoveryStore.Key(ManagedCoreVariant.Stable, SettingsCoreUpdateDiscoveryStore.LocalShaSuffix)] = "a",
            [SettingsCoreUpdateDiscoveryStore.Key(ManagedCoreVariant.Stable, SettingsCoreUpdateDiscoveryStore.TitleSuffix)] = "title",
            [SettingsCoreUpdateDiscoveryStore.Key(ManagedCoreVariant.Stable, SettingsCoreUpdateDiscoveryStore.CheckedAtSuffix)] = "not-a-number",
        });
        var diagnostics = new List<string>();

        Assert.Null(new SettingsCoreUpdateDiscoveryStore(settings, diagnostics.Add).Read(ManagedCoreVariant.Stable));
        Assert.Contains(diagnostics, message => message.Contains("时间无效", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SettingsCoreUpdateDiscoveryStore CreateStore(List<string>? diagnostics = null) =>
        new(new SettingsStore(Path.Combine(_root, "settings.properties")),
            diagnostics is null ? null : diagnostics.Add);
}
