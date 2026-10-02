using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class RuntimeShutdownBarrierTests
{
    [Theory]
    [InlineData("start")]
    [InlineData("adopt")]
    [InlineData("restart")]
    public async Task ResumeBeforeQueuedContinuationCannotRevivePrePauseOrPausedRequests(string operation)
    {
        using var directory = new TemporaryDirectory();
        var config = new StartConfig("node.exe", directory.Path, 9321);
        var snapshot = new RuntimeSnapshot(DesktopRuntimeState.Failed, FailureReason: "previous failure; no owned process");
        var configReads = 0;
        var startsOrAdoptions = 0;
        var supervisor = LifecycleContractProxy.Create<INodeSupervisor>((method, _) => method.Name switch
        {
            "get_Snapshot" => snapshot,
            "get_HasOwnedProcess" => snapshot.Pid is not null,
            "AdoptAsync" => Adopt(),
            "StartAsync" => throw new InvalidOperationException("adoption should succeed"),
            "ForceStopAsync" => Task.FromResult(snapshot = new RuntimeSnapshot(DesktopRuntimeState.Stopped)),
            "DisposeAsync" => ValueTask.CompletedTask,
            _ => null,
        });
        var controller = new RuntimeController(supervisor, () => { configReads++; return config; });
        var gate = (SemaphoreSlim)typeof(RuntimeController).GetField("_gate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(controller)!;
        await gate.WaitAsync();
        var released = false;
        try
        {
            var queuedBeforePause = Request();
            Assert.False(queuedBeforePause.IsCompleted);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.ShutdownAsync(cancelled.Token));
            Assert.True(controller.IsShutdownRequested);
            var requestedDuringPause = Request();
            Assert.True(requestedDuringPause.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => requestedDuringPause);
            controller.ResumeAfterFailedShutdown();
            Assert.False(controller.IsShutdownRequested);
            Assert.False(queuedBeforePause.IsCompleted); // gate 仍被控制性持有，Resume 确定发生在续跑前。
            gate.Release();
            released = true;
            await Assert.ThrowsAsync<InvalidOperationException>(() => queuedBeforePause);
            Assert.Equal(0, configReads);
            Assert.Equal(0, startsOrAdoptions);
            await Request(); // 恢复后真正的新请求必须可用。
            Assert.Equal(1, configReads);
            Assert.Equal(1, startsOrAdoptions);
            Assert.Equal(DesktopRuntimeState.Running, controller.Snapshot.State);
        }
        finally
        {
            if (!released) gate.Release();
            await controller.DisposeAsync();
        }

        Task Request() => operation switch
        {
            "start" => controller.StartAsync(),
            "adopt" => controller.AdoptAsync(),
            _ => controller.RestartAsync(),
        };
        Task<AdoptionResult> Adopt()
        {
            startsOrAdoptions++;
            snapshot = new RuntimeSnapshot(DesktopRuntimeState.Running, Pid: 99);
            return Task.FromResult(AdoptionResult.Success(snapshot, "owned fake adopted"));
        }
    }

    [Fact]
    public async Task ShutdownRejectsQueuedStartsAndDisposeFailureKeepsStopRetryable()
    {
        using var directory = new TemporaryDirectory();
        var config = new StartConfig("node.exe", directory.Path, 9321);
        var forceStop = new TaskCompletionSource<RuntimeSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = new RuntimeSnapshot(DesktopRuntimeState.Running, Pid: 99);
        var calls = 0;
        var starts = 0;
        var supervisor = LifecycleContractProxy.Create<INodeSupervisor>((method, _) => method.Name switch
        {
            "get_Snapshot" => snapshot,
            "get_HasOwnedProcess" => snapshot.Pid is not null,
            "ForceStopAsync" => Stop(),
            "StartAsync" => Start(),
            "DisposeAsync" => ValueTask.CompletedTask,
            _ => null,
        });
        var controller = new RuntimeController(supervisor, () => config);
        var shutdown = controller.ShutdownAsync();
        Assert.Same(shutdown, controller.ShutdownAsync());
        Assert.True(controller.IsShutdownRequested);
        var queued = controller.StartAsync();
        Assert.True(queued.IsCompleted); // 暂停期间请求在等待 gate 前立即拒绝。
        await Assert.ThrowsAsync<InvalidOperationException>(() => queued);
        snapshot = new RuntimeSnapshot(DesktopRuntimeState.Failed, Pid: 99, FailureReason: "safety refusal");
        forceStop.SetResult(snapshot);
        await shutdown;
        await Assert.ThrowsAsync<InvalidOperationException>(() => queued);
        Assert.Equal(0, starts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.DisposeAsync().AsTask());
        Assert.True(controller.HasOwnedProcess);
        controller.ResumeAfterFailedShutdown();
        Assert.False(controller.IsShutdownRequested);
        snapshot = new RuntimeSnapshot(DesktopRuntimeState.Stopped);
        await controller.DisposeAsync();
        Assert.True(calls >= 3);

        Task<RuntimeSnapshot> Stop() { calls++; return calls == 1 ? forceStop.Task : Task.FromResult(snapshot); }
        Task<RuntimeSnapshot> Start() { starts++; return Task.FromResult(snapshot); }
    }
}
