using DanmuApi.App.Services;
using DanmuApi.Core.Frp;
using DanmuApi.Runtime;
using DanmuApi.Runtime.Frp;

namespace DanmuApi.Tests;

public sealed class FrpLifecycleFailureTests
{
    [Theory]
    [InlineData(DesktopRuntimeState.Stopped)]
    [InlineData(DesktopRuntimeState.Stopping)]
    [InlineData(DesktopRuntimeState.Failed)]
    [InlineData(DesktopRuntimeState.Preparing)]
    [InlineData(DesktopRuntimeState.Starting)]
    [InlineData(DesktopRuntimeState.CoreSetupRequired)]
    public async Task CorruptedNewSettingsCannotBlockStopOnAnyNonRunningCoreState(DesktopRuntimeState coreState)
    {
        using var fixture = FrpTestHarness.Create();
        fixture.Installer.Version = "0.71.0";
        fixture.Store.Save(FrpSettings.Default(9321) with
        { InstalledVersion = "0.71.0", Client = FrpSettings.Default(9321).Client with { ServerAddress = "unused.invalid" } });
        fixture.Runtime.SetState(DesktopRuntimeState.Running);
        Assert.True((await fixture.Service.StartTunnelAsync()).Succeeded);
        File.WriteAllText(fixture.Paths.SettingsFile, "frp_server_port=bad-int\n");
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.Changed += (_, state) => { if (state.State == FrpTunnelState.Stopped) stopped.TrySetResult(); };
        fixture.Runtime.SetState(coreState);
        // 只等真正的停必停联动，不调用 Shutdown 来替错误实现掩盖遗漏。
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(FrpTunnelState.Stopped, fixture.Service.Snapshot.State);
        Assert.Contains("bad-int", File.ReadAllText(fixture.Paths.SettingsFile), StringComparison.Ordinal);
        await fixture.Service.DisposeAsync();
    }

    [Theory]
    [InlineData("start")]
    [InlineData("restart")]
    [InlineData("follow")]
    [InlineData("automatic")]
    public async Task FailedDrainResumeBeforeGateContinuationCannotReviveOldStartRequests(string operation)
    {
        using var fixture = FrpTestHarness.Create();
        await fixture.Service.DisposeAsync();
        fixture.Runtime.SetState(DesktopRuntimeState.Running);
        fixture.Installer.Version = "0.71.0";
        var settings = FrpSettings.Default(9321) with
        {
            InstalledVersion = "0.71.0",
            FollowService = operation == "automatic",
            Client = FrpSettings.Default(9321).Client with
            { ServerAddress = "unused.invalid", ServerPort = 7123, ProxyName = "preserve-this-proxy", UseCompression = true },
        };
        fixture.Store.Save(settings);
        var persisted = File.ReadAllText(fixture.Paths.SettingsFile);
        var starts = 0;
        var snapshot = new FrpSnapshot(FrpTunnelState.Stopped);
        var supervisor = LifecycleContractProxy.Create<IFrpSupervisor>((method, _) => method.Name switch
        {
            "get_Snapshot" => snapshot,
            "get_HasOwnedProcess" => snapshot.HasOwnedProcess,
            "StartAsync" => Start(),
            "StopAsync" => Task.FromResult(snapshot = new FrpSnapshot(FrpTunnelState.Stopped)),
            "DisposeAsync" => ValueTask.CompletedTask,
            _ => null,
        });
        var service = new FrpTunnelService(fixture.Store, fixture.Installer, supervisor, fixture.Paths, fixture.Diagnostics, fixture.Runtime);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var gate = (SemaphoreSlim)typeof(FrpTunnelService).GetField("_operationGate", flags)!.GetValue(service)!;
        var pendingField = typeof(FrpTunnelService).GetField("_pendingLifecycleTask", flags)!;
        await gate.WaitAsync();
        var released = false;
        try
        {
            var oldRequest = Request();
            Assert.False(oldRequest.IsCompleted);
            // 精确注入排空故障，屏障显式失败但不取得 gate；让 Resume 确定先于旧等待者续跑。
            // 不用线程池抢跑、Task.Delay 或放宽安全停止的断言制造时序。
            pendingField.SetValue(service, Task.FromException(new IOException("injected lifecycle drain failure")));
            var shutdown = await service.ShutdownAsync();
            Assert.False(shutdown.Succeeded);
            Assert.Contains("drain failure", shutdown.Message, StringComparison.Ordinal);
            var pausedRequest = Request();
            Assert.True(pausedRequest.IsCompleted); // 请求层拒绝，而不是等恢复后再检查 bool。
            await AssertRejectedAsync(pausedRequest);
            service.ResumeAfterFailedShutdown();
            Assert.False(service.IsShutdownRequested);
            Assert.False(oldRequest.IsCompleted);
            gate.Release();
            released = true;
            await AssertRejectedAsync(oldRequest);
            Assert.Equal(0, starts);
            Assert.Equal(persisted, File.ReadAllText(fixture.Paths.SettingsFile));
            // 移除本例人为故障，下一次真正新请求与最终 Dispose 使用真实队列。
            pendingField.SetValue(service, Task.CompletedTask);
            await Request();
            Assert.Equal(1, starts);
            Assert.Equal(FrpTunnelState.Running, service.Snapshot.State);
            var saved = fixture.Store.Read(9321);
            Assert.True(saved.Succeeded);
            Assert.Equal(settings.Client.CustomDomains, saved.Settings.Client.CustomDomains);
            Assert.Equal(settings.Client with { CustomDomains = saved.Settings.Client.CustomDomains }, saved.Settings.Client); // Follow 快捷入口绝不覆盖其它设置。
            Assert.Equal(operation is "automatic" or "follow", saved.Settings.FollowService);
        }
        finally
        {
            if (!released) gate.Release();
            pendingField.SetValue(service, Task.CompletedTask);
            await service.DisposeAsync();
            await fixture.Service.DisposeAsync();
        }

        Task Request() => operation switch
        {
            "start" => service.StartTunnelAsync(),
            "restart" => service.RestartTunnelAsync(),
            "follow" => service.SetFollowServiceAsync(true),
            _ => (Task)typeof(FrpTunnelService).GetMethod("QueueLifecycleAsync", flags)!
                .Invoke(service, [fixture.Runtime.Snapshot, CancellationToken.None])!,
        };
        async Task AssertRejectedAsync(Task request)
        {
            await request;
            if (request is Task<FrpOperationResult> result)
            {
                Assert.False(result.Result.Succeeded);
                Assert.Contains("屏障", result.Result.Message, StringComparison.Ordinal);
            }
            Assert.Equal(0, starts);
        }
        Task<FrpSnapshot> Start()
        {
            starts++;
            snapshot = new FrpSnapshot(FrpTunnelState.Running, Pid: 99, RemoteAddress: "unused.invalid:19321", HasOwnedProcess: true);
            return Task.FromResult(snapshot);
        }
    }

    [Fact]
    public async Task ShutdownDrainsInFlightStartBlocksQueuedStartsAndKeepsCleanupRetryable()
    {
        using var fixture = FrpTestHarness.Create();
        // This test substitutes its own supervisor/service; only that service may subscribe to this runtime.
        await fixture.Service.DisposeAsync();
        fixture.Installer.Version = "0.71.0";
        fixture.Store.Save(FrpSettings.Default(9321) with
        { InstalledVersion = "0.71.0", FollowService = true, Client = FrpSettings.Default(9321).Client with { ServerAddress = "unused.invalid" } });
        fixture.Runtime.SetState(DesktopRuntimeState.Running);
        var started = new TaskCompletionSource<FrpSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = new FrpSnapshot(FrpTunnelState.Stopped);
        var starts = 0;
        var stops = 0;
        var failStop = true;
        var owned = false;
        var supervisor = LifecycleContractProxy.Create<IFrpSupervisor>((method, _) => method.Name switch
        {
            "get_Snapshot" => snapshot,
            "get_HasOwnedProcess" => owned,
            "StartAsync" => BeginStart(),
            "StopAsync" => Stop(),
            "RefreshAsync" => Task.FromResult(snapshot),
            "DisposeAsync" => ValueTask.CompletedTask,
            _ => null,
        });
        var service = new FrpTunnelService(fixture.Store, fixture.Installer, supervisor, fixture.Paths, fixture.Diagnostics, fixture.Runtime);
        var firstStart = service.StartTunnelAsync();
        var firstCompleted = await Task.WhenAny(firstStart, enteredStart.Task).WaitAsync(TimeSpan.FromSeconds(5));
        if (firstCompleted == firstStart)
            Assert.True((await firstStart).Succeeded, (await firstStart).Message + "；" + string.Join("；", fixture.Diagnostics.Messages));
        Assert.Equal(1, starts);
        var queuedStart = service.StartTunnelAsync();
        var shutdown = service.ShutdownAsync();
        Assert.Same(shutdown, service.ShutdownAsync());
        Assert.True(service.IsShutdownRequested);
        Assert.False(shutdown.IsCompleted);
        fixture.Runtime.SetState(DesktopRuntimeState.Running); // 旧/新事件都不得绕过屏障。
        owned = true;
        snapshot = new FrpSnapshot(FrpTunnelState.Running, Pid: 99, HasOwnedProcess: true);
        started.SetResult(snapshot);
        Assert.True((await firstStart).Succeeded);
        Assert.False((await queuedStart).Succeeded);
        Assert.False((await shutdown).Succeeded);
        Assert.Equal(1, starts);
        Assert.True(service.HasOwnedProcess);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DisposeAsync().AsTask());
        Assert.True(stops >= 2);
        service.ResumeAfterFailedShutdown();
        Assert.False((await service.StartTunnelAsync()).Succeeded);
        failStop = false;
        Assert.True((await service.StopTunnelAsync()).Succeeded);
        Assert.False(service.HasOwnedProcess);
        await service.DisposeAsync();

        Task<FrpSnapshot> BeginStart() { starts++; enteredStart.TrySetResult(); return started.Task; }
        Task<FrpSnapshot> Stop()
        {
            stops++;
            owned = failStop;
            snapshot = failStop
                ? new(FrpTunnelState.Failed, Pid: 99, Diagnostic: "verified cleanup refused", HasOwnedProcess: true)
                : new(FrpTunnelState.Stopped);
            return Task.FromResult(snapshot);
        }
    }
}
