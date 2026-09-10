using DanmuApi.App.Services;

namespace DanmuApi.Tests;

public sealed class RuntimePreparationServiceTests
{
    [Fact]
    public async Task PendingAndPreparingRejectMutationsAndFailureNeedsExplicitRepair()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = new List<bool>();
        await using var service = new RuntimePreparationService(async (repair, progress, token) =>
        {
            attempts.Add(repair);
            if (repair) return;
            progress(new("VerifyingTarget", 1, 3));
            entered.SetResult();
            await finish.Task.WaitAsync(token);
            throw new IOException("损坏，需要确认修复");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AcquireReadyLeaseAsync().AsTask());
        var task = service.PrepareAsync();
        await entered.Task;
        Assert.Same(task, service.PrepareAsync());
        Assert.Equal(1, service.Snapshot.Progress!.Completed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AcquireReadyLeaseAsync().AsTask());
        finish.SetResult();
        await task;
        Assert.Equal(RuntimePreparationState.Failed, service.Snapshot.State);
        Assert.Contains("损坏", service.StartBlockedReason);
        await service.PrepareAsync(true);
        Assert.Null(service.StartBlockedReason);
        Assert.Equal(new[] { false, true }, attempts);
    }

    [Fact]
    public async Task PreparationWaitsForWholeReadyMutationAndMaintenanceSharesGate()
    {
        var calls = 0;
        await using var service = new RuntimePreparationService((_, _, _) => { calls++; return Task.CompletedTask; });
        await service.PrepareAsync();
        var ready = await service.AcquireReadyLeaseAsync();
        var prepare = service.PrepareAsync(true);
        Assert.False(prepare.IsCompleted);
        Assert.Null(service.StartBlockedReason);
        Assert.Equal(1, calls);
        await ready.DisposeAsync();
        await prepare;
        var maintenance = await service.AcquireMutationLeaseAsync();
        prepare = service.PrepareAsync(true);
        Assert.False(prepare.IsCompleted);
        await maintenance.DisposeAsync();
        await prepare;
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData("VerifyingSource", "校验内置运行环境")]
    [InlineData("Staging", "暂存并校验新依赖")]
    [InlineData("Replacing", "替换依赖文件")]
    [InlineData("Completed", "运行环境准备完成")]
    [InlineData("UnknownPhase", "处理运行环境（UnknownPhase）")]
    public void PhaseTextMapsPreparerPhasesForTheBanner(string phase, string expected) =>
        Assert.Equal(expected, RuntimePreparationService.PhaseText(phase));

    [Fact]
    public async Task ShutdownCancelsInFlightPreparationAndNeverMarksReady()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new RuntimePreparationService(async (_, _, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        var prepare = service.PrepareAsync();
        await entered.Task;
        await service.CancelAndWaitAsync();
        await prepare;
        Assert.Equal(RuntimePreparationState.Canceled, service.Snapshot.State);
        Assert.NotNull(service.StartBlockedReason);
    }
}
