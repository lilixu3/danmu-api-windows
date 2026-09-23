using DanmuApi.App.Services;
using DanmuApi.Platform;
using DanmuApi.Core;
using System.Reflection;

namespace DanmuApi.Tests;

public sealed class NotificationPreferenceTests
{
    public static IEnumerable<object[]> Matrix()
    {
        foreach (var level in new[] { "off", "updates", "startup_success", "all" })
        foreach (var kind in Enum.GetValues<DesktopNotificationKind>())
            yield return new object[] { level, kind, kind == DesktopNotificationKind.ManualTest || level == "all" ||
                (level == "updates" && kind is DesktopNotificationKind.UpdateDiscovered or DesktopNotificationKind.UpdateCompleted or DesktopNotificationKind.UpdateFailed or DesktopNotificationKind.CoreDependencyMissing) ||
                (level == "startup_success" && kind is DesktopNotificationKind.StartupSucceeded or DesktopNotificationKind.StartupFailed) };
    }

    [Theory, MemberData(nameof(Matrix))]
    public async Task Routes_only_allowed_notifications(string level, DesktopNotificationKind kind, bool allowed)
    {
        var settings = new MemorySettings { Level = level };
        var transport = new RecordingTransport();
        IDesktopNotificationService service = new PreferenceDesktopNotificationService(transport, settings);
        var result = await service.ShowAsync(kind, "title", "message");
        Assert.Equal(allowed ? DesktopNotificationStatus.Submitted : DesktopNotificationStatus.SuppressedByPreference, result.Status);
        Assert.Equal(allowed, result.Succeeded);
        Assert.Equal(allowed ? 1 : 0, transport.Calls);
    }

    [Fact]
    public async Task Missing_defaults_to_all_and_changes_are_read_for_each_send()
    {
        var settings = new MemorySettings();
        var transport = new RecordingTransport();
        IDesktopNotificationService service = new PreferenceDesktopNotificationService(transport, settings);
        Assert.Equal(DesktopNotificationStatus.Submitted, (await service.ShowAsync(DesktopNotificationKind.UpdateDiscovered, "t", "m")).Status);
        settings.Level = "off";
        Assert.Equal(DesktopNotificationStatus.SuppressedByPreference, (await service.ShowAsync(DesktopNotificationKind.UpdateDiscovered, "t", "m")).Status);
        Assert.Equal(1, transport.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Broken_preference_is_explicit_failure_but_manual_test_bypasses_it(bool readFails)
    {
        var settings = new MemorySettings { Level = "invalid", ReadFails = readFails };
        var transport = new RecordingTransport();
        IDesktopNotificationService service = new PreferenceDesktopNotificationService(transport, settings);
        var result = await service.ShowAsync(DesktopNotificationKind.UpdateFailed, "t", "m");
        Assert.Equal(DesktopNotificationStatus.Failed, result.Status);
        Assert.NotEmpty(result.Diagnostic);
        Assert.Equal(0, transport.Calls);
        Assert.Equal(DesktopNotificationStatus.Submitted, (await service.ShowAsync(DesktopNotificationKind.ManualTest, "t", "m")).Status);
    }

    [Fact]
    public async Task Transport_failure_is_preserved()
    {
        var transport = new RecordingTransport { Result = DesktopNotificationResult.Failure("transport failure") };
        IDesktopNotificationService service = new PreferenceDesktopNotificationService(transport, new MemorySettings());
        var result = await service.ShowAsync(DesktopNotificationKind.StartupSucceeded, "t", "m");
        Assert.Equal(DesktopNotificationStatus.Failed, result.Status);
        Assert.Equal("transport failure", result.Diagnostic);
    }

    [Fact]
    public async Task CoreDiscoverySuppressionKeepsPendingAndDoesNotConsumeDeduplication()
    {
        var settings = new MemorySettings { Level = "off" };
        var transport = new RecordingTransport();
        var diagnostics = new RecordingDiagnostics();
        var handler = new CoreUpdateResultHandler(DispatchProxy.Create<ICoreManagementService, UnusedManagement>(), new FixedCoordinator(FixedUpdate()), new Routes(), new PreferenceDesktopNotificationService(transport, settings), diagnostics);
        var update = new CoreUpdateCheckResult(ManagedCoreVariant.Stable, CoreUpdateCheckStatus.Checked, true, null,
            new GithubCommit("123456789", "title", "message", null, null, []), null, DateTimeOffset.UtcNow, "available");
        await handler.HandleAsync(CoreUpdateTrigger.Background, update, CoreUpdateAction.Notify);
        Assert.Same(update, handler.PendingUpdate);
        Assert.Null(diagnostics.LastDiagnostic);
        Assert.Equal(0, transport.Calls);
        settings.Level = "updates";
        await handler.HandleAsync(CoreUpdateTrigger.Background, update, CoreUpdateAction.Notify);
        await handler.HandleAsync(CoreUpdateTrigger.Background, update, CoreUpdateAction.Notify);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task FailedCoreDiscoveryIsDiagnosedAndCanRetry()
    {
        var transport = new RecordingTransport { Result = DesktopNotificationResult.Failure("transport broken") };
        var diagnostics = new RecordingDiagnostics();
        var handler = new CoreUpdateResultHandler(DispatchProxy.Create<ICoreManagementService, UnusedManagement>(), new FixedCoordinator(FixedUpdate()), new Routes(), transport, diagnostics);
        var update = new CoreUpdateCheckResult(ManagedCoreVariant.Stable, CoreUpdateCheckStatus.Checked, true, null,
            new GithubCommit("abcdefghi", "title", "message", null, null, []), null, DateTimeOffset.UtcNow, "available");
        await Assert.ThrowsAsync<IOException>(() => handler.HandleAsync(CoreUpdateTrigger.Background, update, CoreUpdateAction.Notify));
        Assert.Contains("transport broken", diagnostics.LastDiagnostic);
        transport.Result = new(true, "submitted");
        await handler.HandleAsync(CoreUpdateTrigger.Background, update, CoreUpdateAction.Notify);
        Assert.Equal(2, transport.Calls);
    }

    [Theory]
    [InlineData("off", true, 0)]
    [InlineData("off", false, 0)]
    [InlineData("updates", true, 1)]
    [InlineData("updates", false, 1)]
    [InlineData("startup_success", true, 0)]
    [InlineData("startup_success", false, 0)]
    public async Task AutomaticCoreUpdateResultRemainsAccurateWhenNotificationIsSuppressed(string level, bool succeeded, int sends)
    {
        var management = DispatchProxy.Create<ICoreManagementService, UnusedManagement>();
        ((UnusedManagement)(object)management).ApplyResult = new(succeeded, succeeded, succeeded, null, succeeded ? "done" : "apply failed");
        var transport = new RecordingTransport();
        var diagnostics = new RecordingDiagnostics();
        var handler = new CoreUpdateResultHandler(management, new FixedCoordinator(FixedUpdate()), new Routes(), new PreferenceDesktopNotificationService(transport, new MemorySettings { Level = level }), diagnostics);
        var update = new CoreUpdateCheckResult(ManagedCoreVariant.Stable, CoreUpdateCheckStatus.Checked, true, null,
            new GithubCommit("123456789", "title", "message", null, null, []), null, DateTimeOffset.UtcNow, "available");
        await handler.HandleAsync(CoreUpdateTrigger.Background, update, CoreUpdateAction.Automatic);
        Assert.Equal(sends, transport.Calls);
        Assert.Equal(succeeded, handler.PendingUpdate is null);
        Assert.False(handler.IsApplying);
        if (!succeeded) Assert.Equal("apply failed", diagnostics.LastDiagnostic);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SchedulerReleasesSuppressedClaimAndConsumesSubmittedClaim(bool background)
    {
        var settings = new MemorySettings { Level = "off" };
        var transport = new RecordingTransport();
        var diagnostics = new RecordingDiagnostics();
        var handler = new CoreUpdateResultHandler(DispatchProxy.Create<ICoreManagementService, UnusedManagement>(), new FixedCoordinator(FixedUpdate()), new Routes(), new PreferenceDesktopNotificationService(transport, settings), diagnostics);
        var update = new CoreUpdateCheckResult(ManagedCoreVariant.Stable, CoreUpdateCheckStatus.Checked, true, null,
            new GithubCommit("123456789", "title", "message", null, null, []), null, DateTimeOffset.UtcNow, "available");
        await using var scheduler = new CoreUpdateScheduler(new FixedCoordinator(update), new FixedPolicy(), handler, () => ManagedCoreVariant.Stable);
        async Task Check()
        {
            if (background) await scheduler.CheckBackgroundAsync();
            else await scheduler.CheckForegroundAsync();
        }
        await Check();
        Assert.Equal(0, transport.Calls);
        Assert.Same(update, handler.PendingUpdate);
        Assert.Null(diagnostics.LastDiagnostic);
        settings.Level = "updates";
        await Check();
        Assert.Equal(1, transport.Calls);
        await Check();
        Assert.Equal(1, transport.Calls);
        Assert.Null(diagnostics.LastDiagnostic);
    }

    [Fact]
    public async Task SchedulerConsumesAutomaticOperationEvenWhenCompletionNotificationIsSuppressed()
    {
        var management = DispatchProxy.Create<ICoreManagementService, UnusedManagement>();
        var recorder = (UnusedManagement)(object)management;
        recorder.ApplyResult = new(true, true, true, null, "done");
        var transport = new RecordingTransport();
        var handler = new CoreUpdateResultHandler(management, new FixedCoordinator(FixedUpdate()), new Routes(), new PreferenceDesktopNotificationService(transport, new MemorySettings { Level = "off" }), new RecordingDiagnostics());
        var update = new CoreUpdateCheckResult(ManagedCoreVariant.Stable, CoreUpdateCheckStatus.Checked, true, null,
            new GithubCommit("123456789", "title", "message", null, null, []), null, DateTimeOffset.UtcNow, "available");
        await using var scheduler = new CoreUpdateScheduler(new FixedCoordinator(update), new FixedPolicy(CoreUpdateAction.Automatic), handler, () => ManagedCoreVariant.Stable);
        await scheduler.CheckBackgroundAsync();
        await scheduler.CheckBackgroundAsync();
        Assert.Equal(1, recorder.ApplyCalls);
        Assert.Equal(0, transport.Calls);
        Assert.Null(handler.PendingUpdate);
    }

    private sealed class FixedPolicy(CoreUpdateAction action = CoreUpdateAction.Notify) : ICoreUpdatePolicyStore
    {
        public CoreUpdateScheduleOptions Read() => CoreUpdateScheduleOptions.Default with { UpdateAction = action };
        public void Write(CoreUpdateScheduleOptions options) => throw new NotSupportedException();
    }

    /// <summary>处理器现在要订阅协调器的结论变化（托盘待更新项跟着结论走），
    /// 这些用例只关心通知策略，给一个永不广播的空协调器即可。</summary>
    private static CoreUpdateCheckResult FixedUpdate() => new(
        ManagedCoreVariant.Stable,
        CoreUpdateCheckStatus.Checked,
        false,
        null,
        null,
        null,
        DateTimeOffset.UnixEpoch,
        "test");

    private sealed class FixedCoordinator(CoreUpdateCheckResult result) : ICoreUpdateCoordinator
    {
        public TimeSpan AutomaticInterval => TimeSpan.FromMinutes(10);
        public CoreUpdateCheckResult? LastResult => result;
        public event EventHandler<CoreUpdateCheckResult?>? ResultChanged { add { } remove { } }
        public void ReconcileDiscovery(ManagedCoreVariant variant) { }
        public Task<CoreUpdateCheckResult> CheckAsync(ManagedCoreVariant variant, bool force, CancellationToken cancellationToken = default) => Task.FromResult(result);
        public Task<CoreUpdateCheckResult> CheckAsync(ManagedCoreVariant variant, bool force, TimeSpan automaticInterval, CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    /// <summary>
    /// 遗留问题回归（托盘）：托盘菜单「立即更新核心」读的是这份 PendingUpdate，
    /// 它必须跟着协调器的结论走。结论被作废（装完了 / 回退 / 删核心）时这里要同步撤掉，
    /// 否则托盘会一直挂着一个"立即更新核心"——点下去只是把已经装上的版本再装一遍。
    /// </summary>
    [Fact]
    public async Task VoidingTheConclusionClearsThePendingUpdateAndNotifies()
    {
        var coordinator = new BroadcastCoordinator();
        var handler = new CoreUpdateResultHandler(
            DispatchProxy.Create<ICoreManagementService, UnusedManagement>(),
            coordinator,
            new Routes(),
            new RecordingTransport(),
            new RecordingDiagnostics());
        var update = new CoreUpdateCheckResult(ManagedCoreVariant.Stable, CoreUpdateCheckStatus.Checked, true, null,
            new GithubCommit("123456789", "title", "message", null, null, []), null, DateTimeOffset.UtcNow, "available");
        await handler.HandleAsync(CoreUpdateTrigger.Background, update, CoreUpdateAction.Notify);
        Assert.Same(update, handler.PendingUpdate);
        var notifications = 0;
        handler.StateChanged += (_, _) => notifications++;

        coordinator.Void();

        Assert.Null(handler.PendingUpdate);
        Assert.Equal(1, notifications);
    }

    /// <summary>结论仍成立（协调器推来同一条更新）时不能误清待更新项。</summary>
    [Fact]
    public async Task AStillValidConclusionKeepsThePendingUpdate()
    {
        var coordinator = new BroadcastCoordinator();
        var handler = new CoreUpdateResultHandler(
            DispatchProxy.Create<ICoreManagementService, UnusedManagement>(),
            coordinator,
            new Routes(),
            new RecordingTransport(),
            new RecordingDiagnostics());
        var update = new CoreUpdateCheckResult(ManagedCoreVariant.Stable, CoreUpdateCheckStatus.Checked, true, null,
            new GithubCommit("123456789", "title", "message", null, null, []), null, DateTimeOffset.UtcNow, "available");
        await handler.HandleAsync(CoreUpdateTrigger.Background, update, CoreUpdateAction.Notify);
        var notifications = 0;
        handler.StateChanged += (_, _) => notifications++;

        coordinator.Push(update);

        Assert.Same(update, handler.PendingUpdate);
        Assert.Equal(0, notifications);
    }

    /// <summary>能主动广播结论变化的协调器替身（含 null = 作废）。</summary>
    private sealed class BroadcastCoordinator : ICoreUpdateCoordinator
    {
        public TimeSpan AutomaticInterval => TimeSpan.FromMinutes(10);
        public CoreUpdateCheckResult? LastResult { get; private set; }
        public event EventHandler<CoreUpdateCheckResult?>? ResultChanged;

        public void Push(CoreUpdateCheckResult result)
        {
            LastResult = result;
            ResultChanged?.Invoke(this, result);
        }

        public void Void()
        {
            LastResult = null;
            ResultChanged?.Invoke(this, null);
        }

        public void ReconcileDiscovery(ManagedCoreVariant variant) { }
        public Task<CoreUpdateCheckResult> CheckAsync(ManagedCoreVariant variant, bool force, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoreUpdateCheckResult> CheckAsync(ManagedCoreVariant variant, bool force, TimeSpan automaticInterval, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    public class UnusedManagement : DispatchProxy
    {
        public CoreManagementOperationResult? ApplyResult { get; set; }
        public int ApplyCalls { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(ICoreManagementService.ApplyUpdateAsync) || ApplyResult is null)
                throw new InvalidOperationException("Unexpected management call: " + targetMethod?.Name);
            ApplyCalls++;
            return Task.FromResult(ApplyResult);
        }
    }
    private sealed class Routes : IGithubRoutePreferenceStore
    {
        public GithubRoutePreference Read() => new("original", true);
        public void Confirm(string proxyId) => throw new NotSupportedException();
        public void Invalidate() => throw new NotSupportedException();
    }
    private sealed class RecordingDiagnostics : IAppDiagnostics
    {
        public string? LastDiagnostic { get; private set; }
        public void Record(string message, Exception? error = null) => LastDiagnostic = message;
    }

    private sealed class MemorySettings : ISettingsStore
    {
        public string? Level { get; set; }
        public bool ReadFails { get; set; }
        public IReadOnlyDictionary<string, string> Read() => ReadFails ? throw new IOException("read failure") : Level is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["notification_level"] = Level };
        public void Write(IReadOnlyDictionary<string, string?> changes) => Level = changes["notification_level"];
    }
    private sealed class RecordingTransport : IDesktopNotificationService
    {
        public int Calls { get; private set; }
        public DesktopNotificationResult Result { get; set; } = new(true, "submitted");
        public Task<DesktopNotificationResult> ShowAsync(string title, string message, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(Result); }
    }
}
