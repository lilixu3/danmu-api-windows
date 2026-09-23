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
            new MemoryCoreUpdateDiscoveryStore(),
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
            new MemoryCoreUpdateDiscoveryStore(),
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

    /// <summary>
    /// 回归：冷却跳过曾经会把「刚发现的新提交」从 LastResult 里抹掉，侧栏那张卡跟着消失。
    /// 冷却只是"这次没查"，不是"没有更新"—— 它绝不能改变卡片依据的结论。
    /// </summary>
    [Fact]
    public async Task CooldownSkipKeepsTheDiscoveredUpdateVisible()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var remote = new RecordingRemote(RemoteCommit("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        var coordinator = new CoreUpdateCoordinator(
            new FixedInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")),
            remote,
            new MemoryTimestampStore(),
            new MemoryCoreUpdateDiscoveryStore(),
            timeProvider: time);
        var discovered = await coordinator.CheckAsync(ManagedCoreVariant.Stable, force: false);
        Assert.True(discovered.UpdateAvailable);
        var broadcasts = 0;
        coordinator.ResultChanged += (_, _) => broadcasts++;

        time.Advance(TimeSpan.FromMinutes(1));
        var skipped = await coordinator.CheckAsync(ManagedCoreVariant.Stable, force: false);

        Assert.Equal(CoreUpdateCheckStatus.SkippedCooldown, skipped.Status);
        Assert.Equal(discovered, coordinator.LastResult);
        Assert.Equal(0, broadcasts);
    }

    /// <summary>
    /// 回归：网络失败也曾经把结论冲掉。一次请求失败说明不了"没有新提交"，
    /// 保留已知发现，用户仍能从卡片进快速更新弹窗。
    /// </summary>
    [Fact]
    public async Task RemoteFailureKeepsTheDiscoveredUpdateVisible()
    {
        var successful = true;
        var remote = new RecordingRemote(() => successful
            ? Task.FromResult(RemoteCommit("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"))
            : Task.FromException<GithubCommit>(new GithubRemoteException(GithubFailureKind.Network, "offline")));
        var coordinator = new CoreUpdateCoordinator(
            new FixedInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")),
            remote,
            new MemoryTimestampStore(),
            new MemoryCoreUpdateDiscoveryStore());
        var discovered = await coordinator.CheckAsync(ManagedCoreVariant.Stable, force: true);

        successful = false;
        var failed = await coordinator.CheckAsync(ManagedCoreVariant.Stable, force: true);

        Assert.Equal(CoreUpdateCheckStatus.Failed, failed.Status);
        Assert.Equal(discovered, coordinator.LastResult);
    }

    /// <summary>
    /// 结论要落盘，并在下个进程里靠 <see cref="ICoreUpdateCoordinator.ReconcileDiscovery"/>
    /// 接回来 —— 这就是"重启后卡片还在"。
    /// </summary>
    [Fact]
    public async Task PersistedDiscoverySurvivesRestartAndNeedsNoNetwork()
    {
        var installer = new FixedInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        var discoveries = new MemoryCoreUpdateDiscoveryStore();
        var first = new CoreUpdateCoordinator(
            installer,
            new RecordingRemote(RemoteCommit("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")),
            new MemoryTimestampStore(),
            discoveries);
        await first.CheckAsync(ManagedCoreVariant.Stable, force: true);
        Assert.NotNull(discoveries.Read(ManagedCoreVariant.Stable));

        // 新进程：未发任何请求，仅靠落盘结论就要能给出「核心可更新」。
        var restartedRemote = new RecordingRemote(Task.FromException<GithubCommit>(
            new GithubRemoteException(GithubFailureKind.Network, "offline")));
        var restarted = new CoreUpdateCoordinator(
            installer,
            restartedRemote,
            new MemoryTimestampStore(),
            discoveries);
        CoreUpdateCheckResult? broadcast = null;
        restarted.ResultChanged += (_, result) => broadcast = result;

        restarted.ReconcileDiscovery(ManagedCoreVariant.Stable);

        Assert.Equal(0, restartedRemote.GetCommitCalls);
        var restored = Assert.IsType<CoreUpdateCheckResult>(restarted.LastResult);
        Assert.Equal(CoreUpdateCheckStatus.Checked, restored.Status);
        Assert.True(restored.UpdateAvailable);
        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", restored.Remote!.Sha);
        Assert.Equal("title", restored.Remote.Title);
        Assert.Equal(restored, broadcast);
    }

    /// <summary>
    /// 遗留问题回归：从核心页/托盘装完那个提交后，内存里的结论必须立刻作废并广播，
    /// 而不是等下一次联网检查 —— 否则侧栏卡片与托盘菜单会一直挂着一个其实已经装上的「新版本」。
    /// </summary>
    [Fact]
    public async Task InstallingTheRemoteCommitVoidsTheInMemoryConclusionAndBroadcasts()
    {
        var discoveries = new MemoryCoreUpdateDiscoveryStore();
        var installer = new MutableInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        var coordinator = new CoreUpdateCoordinator(
            installer,
            new RecordingRemote(RemoteCommit("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")),
            new MemoryTimestampStore(),
            discoveries);
        var discovered = await coordinator.CheckAsync(ManagedCoreVariant.Stable, force: true);
        Assert.True(discovered.UpdateAvailable);

        // 用户点「立即更新」：磁盘上换成了那个新提交。
        var broadcasts = new List<CoreUpdateCheckResult?>();
        coordinator.ResultChanged += (_, result) => broadcasts.Add(result);
        installer.Current = Installed("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        coordinator.ReconcileDiscovery(ManagedCoreVariant.Stable);

        Assert.Null(coordinator.LastResult);
        Assert.Equal([null], broadcasts);
        Assert.Null(discoveries.Read(ManagedCoreVariant.Stable));
    }

    /// <summary>回退/重装成别的提交时同样作废（本地提交对不上了）。</summary>
    [Fact]
    public async Task RollingBackToADifferentCommitVoidsTheConclusion()
    {
        var installer = new MutableInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        var coordinator = new CoreUpdateCoordinator(
            installer,
            new RecordingRemote(RemoteCommit("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")),
            new MemoryTimestampStore(),
            new MemoryCoreUpdateDiscoveryStore());
        await coordinator.CheckAsync(ManagedCoreVariant.Stable, force: true);

        var broadcasts = new List<CoreUpdateCheckResult?>();
        coordinator.ResultChanged += (_, result) => broadcasts.Add(result);
        installer.Current = Installed("cccccccccccccccccccccccccccccccccccccccc");
        coordinator.ReconcileDiscovery(ManagedCoreVariant.Stable);

        Assert.Null(coordinator.LastResult);
        Assert.Equal([null], broadcasts);
    }

    /// <summary>删核心后结论作废（核心不可用就谈不上"有更新"）。</summary>
    [Fact]
    public async Task DeletingTheCoreVoidsTheConclusion()
    {
        var installer = new MutableInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        var coordinator = new CoreUpdateCoordinator(
            installer,
            new RecordingRemote(RemoteCommit("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")),
            new MemoryTimestampStore(),
            new MemoryCoreUpdateDiscoveryStore());
        await coordinator.CheckAsync(ManagedCoreVariant.Stable, force: true);

        installer.Current = new CoreInstallationInfo(
            ManagedCoreVariant.Stable, "missing", false, false, null, null, "核心尚未安装");
        coordinator.ReconcileDiscovery(ManagedCoreVariant.Stable);

        Assert.Null(coordinator.LastResult);
    }

    /// <summary>
    /// 结论仍然成立时不该广播：改名只换 manifest 里的显示名、不动提交，
    /// 对账要刷新 <c>Local</c>（界面显示的当前提交）但不能惊动订阅方。
    /// </summary>
    [Fact]
    public async Task ReconcilingWhenNothingChangedRefreshesLocalWithoutBroadcasting()
    {
        var installer = new MutableInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        var coordinator = new CoreUpdateCoordinator(
            installer,
            new RecordingRemote(RemoteCommit("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")),
            new MemoryTimestampStore(),
            new MemoryCoreUpdateDiscoveryStore());
        var discovered = await coordinator.CheckAsync(ManagedCoreVariant.Stable, force: true);

        var broadcasts = new List<CoreUpdateCheckResult?>();
        coordinator.ResultChanged += (_, result) => broadcasts.Add(result);
        // 只改显示名：提交没变，结论仍成立。
        installer.Current = Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa") with { Version = "1.0.1" };
        coordinator.ReconcileDiscovery(ManagedCoreVariant.Stable);

        Assert.Empty(broadcasts);
        Assert.Equal(discovered.Remote!.Sha, coordinator.LastResult!.Remote!.Sha);
        Assert.True(coordinator.LastResult.UpdateAvailable);
    }

    /// <summary>读不到磁盘状态时保留结论并记诊断：读不到不等于"没有更新"，绝不静默清卡片。</summary>
    [Fact]
    public async Task UnreadableInstallationKeepsTheConclusionAndReportsDiagnostic()
    {
        var installer = new MutableInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        var diagnostics = new List<string>();
        var coordinator = new CoreUpdateCoordinator(
            installer,
            new RecordingRemote(RemoteCommit("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")),
            new MemoryTimestampStore(),
            new MemoryCoreUpdateDiscoveryStore(),
            diagnosticSink: diagnostics.Add);
        var discovered = await coordinator.CheckAsync(ManagedCoreVariant.Stable, force: true);

        installer.InspectError = new IOException("磁盘暂时读不到");
        coordinator.ReconcileDiscovery(ManagedCoreVariant.Stable);

        Assert.Equal(discovered, coordinator.LastResult);
        Assert.Contains(diagnostics, message => message.Contains("读取本地核心状态失败", StringComparison.Ordinal));
    }

    /// <summary>
    /// 结论槽位只有一个（"最后检查过的那个变体"）。别的变体的安装变动绝不能顺手把它清掉 ——
    /// 否则装一个自定义核心，会连带把刚发现的稳定核心更新一起弄没。
    /// </summary>
    [Fact]
    public async Task ReconcilingAnotherVariantLeavesTheCurrentConclusionAlone()
    {
        var installer = new VariantInstaller(
            Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ManagedCoreVariant.Stable),
            Installed("cccccccccccccccccccccccccccccccccccccccc", ManagedCoreVariant.Custom));
        var coordinator = new CoreUpdateCoordinator(
            installer,
            new RecordingRemote(RemoteCommit("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")),
            new MemoryTimestampStore(),
            new MemoryCoreUpdateDiscoveryStore());
        var discovered = await coordinator.CheckAsync(ManagedCoreVariant.Stable, force: true);
        Assert.True(discovered.UpdateAvailable);

        var broadcasts = new List<CoreUpdateCheckResult?>();
        coordinator.ResultChanged += (_, result) => broadcasts.Add(result);
        coordinator.ReconcileDiscovery(ManagedCoreVariant.Custom);

        Assert.Empty(broadcasts);
        Assert.Equal(discovered, coordinator.LastResult);
    }

    /// <summary>安装已被替换（重装/回退/换分支）后，旧结论必须作废，不能凭空留一张卡。</summary>
    [Fact]
    public void PersistedDiscoveryIsDiscardedWhenTheInstallationChanged()
    {
        var discoveries = new MemoryCoreUpdateDiscoveryStore();
        discoveries.Seed(new CoreUpdateDiscovery(
            ManagedCoreVariant.Stable,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "title",
            DateTimeOffset.Parse("2026-09-01T00:00:00Z")));
        var coordinator = new CoreUpdateCoordinator(
            new FixedInstaller(Installed("cccccccccccccccccccccccccccccccccccccccc")),
            new RecordingRemote(RemoteCommit("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")),
            new MemoryTimestampStore(),
            discoveries);

        coordinator.ReconcileDiscovery(ManagedCoreVariant.Stable);

        Assert.Null(coordinator.LastResult);
        Assert.Null(discoveries.Read(ManagedCoreVariant.Stable));
    }

    /// <summary>记录里那个远端提交已经装上时，结论同样作废（并清掉记录）。</summary>
    [Fact]
    public void PersistedDiscoveryIsDiscardedWhenTheRemoteCommitIsAlreadyInstalled()
    {
        var discoveries = new MemoryCoreUpdateDiscoveryStore();
        discoveries.Seed(new CoreUpdateDiscovery(
            ManagedCoreVariant.Stable,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "title",
            DateTimeOffset.Parse("2026-09-01T00:00:00Z")));
        var coordinator = new CoreUpdateCoordinator(
            new FixedInstaller(Installed("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")),
            new RecordingRemote(RemoteCommit("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")),
            new MemoryTimestampStore(),
            discoveries);

        coordinator.ReconcileDiscovery(ManagedCoreVariant.Stable);

        Assert.Null(coordinator.LastResult);
        Assert.Null(discoveries.Read(ManagedCoreVariant.Stable));
    }

    /// <summary>确认已是最新的那次检查要把旧结论撤掉，否则卡片会一直挂着过期的更新。</summary>
    [Fact]
    public async Task CheckConfirmingUpToDateClearsThePersistedDiscovery()
    {
        var installer = new FixedInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        var discoveries = new MemoryCoreUpdateDiscoveryStore();
        var coordinator = new CoreUpdateCoordinator(
            installer,
            new RecordingRemote(RemoteCommit("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")),
            new MemoryTimestampStore(),
            discoveries);
        await coordinator.CheckAsync(ManagedCoreVariant.Stable, force: true);

        var upToDate = new CoreUpdateCoordinator(
            installer,
            new RecordingRemote(RemoteCommit("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")),
            new MemoryTimestampStore(),
            discoveries);
        var result = await upToDate.CheckAsync(ManagedCoreVariant.Stable, force: true);

        Assert.False(result.UpdateAvailable);
        Assert.Null(discoveries.Read(ManagedCoreVariant.Stable));
        Assert.False(upToDate.LastResult!.UpdateAvailable);
    }

    /// <summary>写入失败只记诊断，不能让一次成功的检查变成失败（落盘是加分项，不是前提）。</summary>
    [Fact]
    public async Task DiscoveryWriteFailureIsReportedWithoutFailingTheCheck()
    {
        var discoveries = new FailingDiscoveryStore();
        var diagnostics = new List<string>();
        var coordinator = new CoreUpdateCoordinator(
            new FixedInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")),
            new RecordingRemote(RemoteCommit("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")),
            new MemoryTimestampStore(),
            discoveries,
            diagnosticSink: diagnostics.Add);

        var result = await coordinator.CheckAsync(ManagedCoreVariant.Stable, force: true);

        Assert.Equal(CoreUpdateCheckStatus.Checked, result.Status);
        Assert.True(result.UpdateAvailable);
        Assert.Contains(diagnostics, message => message.Contains("保存核心更新发现结果失败", StringComparison.Ordinal));
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
            new MemoryCoreUpdateDiscoveryStore(),
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
            new MemoryTimestampStore(),
            new MemoryCoreUpdateDiscoveryStore());

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
        var coordinator = new CoreUpdateCoordinator(installer, remote, new MemoryTimestampStore(), new MemoryCoreUpdateDiscoveryStore());

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
            new MemoryCoreUpdateDiscoveryStore(),
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
            new MemoryTimestampStore(),
            new MemoryCoreUpdateDiscoveryStore());

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
        var coordinator = new CoreUpdateCoordinator(new FixedInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")), remote, timestamps, new MemoryCoreUpdateDiscoveryStore());
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
        var coordinator = new CoreUpdateCoordinator(new FixedInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")), remote, new MemoryTimestampStore(), new MemoryCoreUpdateDiscoveryStore());
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
        using var coordinator = new CoreUpdateCoordinator(new FixedInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")), remote, new MemoryTimestampStore(), new MemoryCoreUpdateDiscoveryStore());
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
        var coordinator = new CoreUpdateCoordinator(new FixedInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")), remote, timestamps, new MemoryCoreUpdateDiscoveryStore());
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
        using var coordinator = new CoreUpdateCoordinator(new FixedInstaller(Installed("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")), remote, new MemoryTimestampStore(), new MemoryCoreUpdateDiscoveryStore(), timeProvider: time);
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

    /// <summary>每次调用都重新决定成功还是失败，用来在同一个协调器上演"先成功、后断网"。</summary>
    private sealed class FailingDiscoveryStore : ICoreUpdateDiscoveryStore
    {
        public CoreUpdateDiscovery? Read(ManagedCoreVariant variant) => null;
        public void Write(CoreUpdateDiscovery discovery) => throw new IOException("settings 只读");
        public void Clear(ManagedCoreVariant variant) => throw new IOException("settings 只读");
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

    /// <summary>安装状态可变的替身：用来演"检查之后磁盘被换掉"（更新/回退/重装/删除）。</summary>
    private sealed class MutableInstaller(CoreInstallationInfo current) : ICoreInstaller
    {
        public CoreInstallationInfo Current { get; set; } = current;
        public Exception? InspectError { get; set; }

        public CoreInstallationInfo Inspect(ManagedCoreVariant variant)
        {
            if (InspectError is not null) throw InspectError;
            return Current;
        }

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
        private readonly Func<Task<GithubCommit>> _commit;
        public RecordingRemote(GithubCommit commit) : this(Task.FromResult(commit)) { }
        public RecordingRemote(Task<GithubCommit> commit) : this(() => commit) { }
        public RecordingRemote(Func<Task<GithubCommit>> commit) => _commit = commit;
        public int GetCommitCalls { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public GithubRateLimit? LastRateLimit => null;
        public Task<GithubCommit> GetCommitAsync(GithubRepositoryReference repository, string reference, CancellationToken cancellationToken = default)
        {
            GetCommitCalls++;
            LastToken = cancellationToken;
            return _commit().WaitAsync(cancellationToken);
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
