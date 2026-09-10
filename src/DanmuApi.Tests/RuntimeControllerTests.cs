using System.Diagnostics;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class RuntimeControllerTests
{
    [Fact]
    public async Task PreparationGateRejectsStartAdoptionAndRestartBeforeAnySideEffect()
    {
        var supervisor = new FakeSupervisor();
        var configReads = 0;
        string? reason = "运行环境尚未就绪";
        await using var controller = new RuntimeController(supervisor, () =>
        {
            configReads++;
            throw new InvalidOperationException("配置不应被读取");
        }, startBlockedReason: () => reason);
        await controller.StartAsync();
        var adoption = await controller.AdoptAsync();
        await controller.RestartAsync();
        Assert.False(adoption.Succeeded);
        Assert.Equal(reason, controller.Snapshot.FailureReason);
        Assert.Equal(0, configReads);
        Assert.Empty(supervisor.Calls);
        reason = null;
        await controller.StartAsync();
        Assert.Equal(1, configReads);
        Assert.Contains("配置不应被读取", controller.Snapshot.FailureReason);
    }

    [Fact]
    public async Task BackupMaintenanceLeaseSerializesStartUntilRestoreCompletes()
    {
        using var directory = new TemporaryDirectory();
        var config = CreateConfig(directory.Path);
        var supervisor = new FakeSupervisor { AdoptionResult = AdoptionResult.Success(new RuntimeSnapshot(DesktopRuntimeState.Running, config.Port, 1234, "lease-test")) };
        await using var controller = new RuntimeController(supervisor, () => config);
        var lease = await controller.AcquireStoppedMaintenanceLeaseAsync();
        var start = controller.StartAsync();
        Assert.False(start.IsCompleted);
        Assert.Empty(supervisor.Calls);
        await lease.DisposeAsync();
        await start;
        Assert.Equal(DesktopRuntimeState.Running, controller.Snapshot.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.AcquireStoppedMaintenanceLeaseAsync().AsTask());
    }

    [Fact]
    public async Task StartAdoptsBeforeCheckingCoreAndPublishesRunning()
    {
        using var directory = new TemporaryDirectory();
        var config = CreateConfig(directory.Path);
        var supervisor = new FakeSupervisor
        {
            AdoptionResult = AdoptionResult.Success(new RuntimeSnapshot(
                DesktopRuntimeState.Running,
                config.Port,
                1234,
                "desktop-controller-test")),
        };
        await using var controller = new RuntimeController(supervisor, () => config);
        controller.SnapshotChanged += (_, snapshot) => supervisor.PublishedStates.Add(snapshot.State.ToString());

        await controller.StartAsync();

        Assert.Equal(DesktopRuntimeState.Running, controller.Snapshot.State);
        Assert.Equal(["adopt"], supervisor.Calls);
        Assert.Contains("Running", supervisor.PublishedStates);
    }

    [Fact]
    public async Task StartPublishesCoreSetupRequiredWhenAdoptionMissesAndCoreIsAbsent()
    {
        using var directory = new TemporaryDirectory();
        var config = CreateConfig(directory.Path);
        var supervisor = new FakeSupervisor
        {
            AdoptionResult = AdoptionResult.Failure(
                AdoptionFailureKind.NotFound,
                new RuntimeSnapshot(DesktopRuntimeState.Stopped),
                "没有后台实例"),
        };
        await using var controller = new RuntimeController(supervisor, () => config);

        await controller.StartAsync();

        Assert.Equal(DesktopRuntimeState.CoreSetupRequired, controller.Snapshot.State);
        Assert.Contains("核心尚未准备", controller.Snapshot.FailureReason, StringComparison.Ordinal);
        Assert.Equal(["adopt"], supervisor.Calls);
    }

    [Fact]
    public async Task FirewallFailureStopsBeforeStartingNode()
    {
        using var directory = new TemporaryDirectory();
        var config = CreateConfig(directory.Path);
        File.WriteAllText(Path.Combine(config.ScriptDir, "danmu_api_stable", "worker.js"), "// core");
        var supervisor = new FakeSupervisor
        {
            AdoptionResult = AdoptionResult.Failure(
                AdoptionFailureKind.NotFound,
                new RuntimeSnapshot(DesktopRuntimeState.Stopped),
                "没有后台实例"),
        };
        var firewall = new FakeFirewall(RuntimeFirewallResult.Failure("用户取消 UAC 授权", authorizationAttempted: true));
        await using var controller = new RuntimeController(supervisor, () => config, firewall);

        await controller.StartAsync();

        Assert.Equal(DesktopRuntimeState.Failed, controller.Snapshot.State);
        Assert.Contains("网络授权未完成", controller.Snapshot.FailureReason, StringComparison.Ordinal);
        Assert.Equal(["adopt"], supervisor.Calls);
        Assert.Equal(config.NodeExe, firewall.LastNodeExe);
    }

    [Fact]
    public async Task StartAfterFailedStateCleansSupervisorBeforeRetry()
    {
        using var directory = new TemporaryDirectory();
        var config = CreateConfig(directory.Path);
        File.WriteAllText(Path.Combine(config.ScriptDir, "danmu_api_stable", "worker.js"), "// core");
        var supervisor = new FakeSupervisor
        {
            InitialSnapshot = new RuntimeSnapshot(DesktopRuntimeState.Failed, config.Port, 1234, "desktop-test", "previous failure"),
            AdoptionResult = AdoptionResult.Failure(
                AdoptionFailureKind.NotFound,
                new RuntimeSnapshot(DesktopRuntimeState.Stopped),
                "没有后台实例"),
            StartResult = new RuntimeSnapshot(DesktopRuntimeState.Running, config.Port, 2345, "desktop-test"),
        };
        await using var controller = new RuntimeController(supervisor, () => config);

        await controller.StartAsync();

        Assert.Equal(DesktopRuntimeState.Running, controller.Snapshot.State);
        Assert.Equal(["stop:start-retry", "adopt", "start"], supervisor.Calls);
    }

    [Theory]
    [InlineData(DesktopRuntimeState.Preparing)]
    [InlineData(DesktopRuntimeState.Starting)]
    public async Task ReconcileLivenessDoesNotOverridePreparationStates(DesktopRuntimeState state)
    {
        using var directory = new TemporaryDirectory();
        var supervisor = new FakeSupervisor
        {
            InitialSnapshot = new RuntimeSnapshot(state),
            LivenessResult = "不应在启动准备阶段检查",
        };
        await using var controller = new RuntimeController(supervisor, () => CreateConfig(directory.Path));

        Assert.Null(controller.ReconcileLiveness());
        Assert.Equal(state, controller.Snapshot.State);
        Assert.Empty(supervisor.LivenessCalls);
    }

    [Fact]
    public async Task ReconcileLivenessDuringBlockedStartupLeavesPreparingUntouched()
    {
        using var directory = new TemporaryDirectory();
        var supervisor = new FakeSupervisor { BlockAdoption = true };
        await using var controller = new RuntimeController(supervisor, () => CreateConfig(directory.Path));
        using var cancellation = new CancellationTokenSource();

        var startTask = controller.StartAsync(cancellation.Token);
        await supervisor.AdoptionStarted.Task;

        Assert.Equal(DesktopRuntimeState.Preparing, controller.Snapshot.State);
        Assert.Null(controller.ReconcileLiveness());
        Assert.Equal(DesktopRuntimeState.Preparing, controller.Snapshot.State);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startTask);
        Assert.Equal(DesktopRuntimeState.Failed, controller.Snapshot.State);
    }

    [Fact]
    public async Task CancellingAdoptionPublishesFailedInsteadOfLeavingPreparing()
    {
        using var directory = new TemporaryDirectory();
        var config = CreateConfig(directory.Path);
        var supervisor = new FakeSupervisor
        {
            BlockAdoption = true,
        };
        await using var controller = new RuntimeController(supervisor, () => config);
        using var cancellation = new CancellationTokenSource();

        var adoptionTask = controller.AdoptAsync(cancellation.Token);
        await supervisor.AdoptionStarted.Task;
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => adoptionTask);

        Assert.Equal(DesktopRuntimeState.Failed, controller.Snapshot.State);
        Assert.Contains("认领已取消", controller.Snapshot.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShutdownFromFailedStateStillAttemptsCleanup()
    {
        using var directory = new TemporaryDirectory();
        var config = CreateConfig(directory.Path);
        var supervisor = new FakeSupervisor
        {
            InitialSnapshot = new RuntimeSnapshot(DesktopRuntimeState.Failed, config.Port, 1234, "desktop-test", "startup failed"),
            ForceStopResult = new RuntimeSnapshot(DesktopRuntimeState.Stopped),
        };
        await using var controller = new RuntimeController(supervisor, () => config);

        await controller.ShutdownAsync();

        Assert.Equal(DesktopRuntimeState.Stopped, controller.Snapshot.State);
        Assert.Equal(["force-stop"], supervisor.Calls);
    }

    [Fact]
    public async Task ControllerDisposeDoesNotDisposeSupervisorTwice()
    {
        using var directory = new TemporaryDirectory();
        var supervisor = new FakeSupervisor();
        var controller = new RuntimeController(supervisor, () => CreateConfig(directory.Path));

        await controller.DisposeAsync();
        await controller.DisposeAsync();

        Assert.Equal(0, supervisor.DisposeCalls);
    }

    private static StartConfig CreateConfig(string scriptDir)
    {
        Directory.CreateDirectory(Path.Combine(scriptDir, "danmu_api_stable"));
        File.WriteAllText(Path.Combine(scriptDir, "main.js"), "// runtime");
        return new StartConfig(
            Environment.ProcessPath ?? throw new InvalidOperationException("测试进程路径不可用"),
            scriptDir,
            Port: 19321,
            IdentityFile: Path.Combine(scriptDir, "instance-id"));
    }

    private sealed class FakeFirewall(RuntimeFirewallResult result) : IRuntimeFirewall
    {
        public string? LastNodeExe { get; private set; }

        public Task<RuntimeFirewallResult> EnsureInboundRuleAsync(
            string nodeExe,
            CancellationToken cancellationToken = default)
        {
            LastNodeExe = nodeExe;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeSupervisor : INodeSupervisor
    {
        private RuntimeSnapshot _snapshot = new(DesktopRuntimeState.Stopped);

        public RuntimeSnapshot InitialSnapshot
        {
            init => _snapshot = value;
        }

        public AdoptionResult AdoptionResult { get; init; } = AdoptionResult.Failure(
            AdoptionFailureKind.NotFound,
            new RuntimeSnapshot(DesktopRuntimeState.Stopped),
            "no adoption");

        public RuntimeSnapshot StartResult { get; init; } = new(DesktopRuntimeState.Failed, FailureReason: "fake start not configured");
        public RuntimeSnapshot StopResult { get; init; } = new(DesktopRuntimeState.Stopped);
        public RuntimeSnapshot ForceStopResult { get; init; } = new(DesktopRuntimeState.Stopped);
        public bool BlockAdoption { get; init; }
        public string? LivenessResult { get; init; }
        public List<int> LivenessCalls { get; } = [];
        public TaskCompletionSource<bool> AdoptionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Calls { get; } = [];
        public List<string> PublishedStates { get; } = [];
        public int DisposeCalls { get; private set; }
        public RuntimeSnapshot Snapshot => _snapshot;

        public Task<RuntimeSnapshot> StartAsync(StartConfig config, CancellationToken cancellationToken = default)
        {
            Calls.Add("start");
            _snapshot = StartResult;
            return Task.FromResult(_snapshot);
        }

        public async Task<AdoptionResult> AdoptAsync(StartConfig config, RuntimeHealthSnapshot? health = null, CancellationToken cancellationToken = default)
        {
            Calls.Add("adopt");
            if (BlockAdoption)
            {
                AdoptionStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            _snapshot = AdoptionResult.Snapshot;
            return AdoptionResult;
        }

        public Task<RuntimeSnapshot> StopAsync(string reason = "user", CancellationToken cancellationToken = default)
        {
            Calls.Add($"stop:{reason}");
            _snapshot = StopResult;
            return Task.FromResult(_snapshot);
        }

        public Task<RuntimeSnapshot> ForceStopAsync(string reason = "application-exit", CancellationToken cancellationToken = default)
        {
            Calls.Add("force-stop");
            _snapshot = ForceStopResult;
            return Task.FromResult(_snapshot);
        }

        public string? LivenessFailure()
        {
            LivenessCalls.Add(1);
            return LivenessResult;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }
}
