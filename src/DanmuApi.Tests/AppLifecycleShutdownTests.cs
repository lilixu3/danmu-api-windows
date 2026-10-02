using System.Reflection;
using Avalonia.Controls.ApplicationLifetimes;
using DanmuApi.App.Services;
using DanmuApi.Core;
using DanmuApi.Core.Frp;
using DanmuApi.Platform;
using DanmuApi.Runtime;
using DanmuApi.Runtime.Frp;

namespace DanmuApi.Tests;

public sealed class AppLifecycleShutdownTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentExitCallersWaitForTheSameTunnelBarrierResult(bool succeeds)
    {
        using var directory = new TemporaryDirectory();
        var barrier = new TaskCompletionSource<FrpOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        var tunnelCalls = 0;
        var desktopCalls = 0;
        var resumes = 0;
        var runtime = LifecycleContractProxy.Create<IRuntimeController>((method, _) => method.Name switch
        {
            "get_Snapshot" => new RuntimeSnapshot(DesktopRuntimeState.Stopped),
            "get_HasOwnedProcess" => false,
            "ShutdownAsync" => Task.CompletedTask,
            "ResumeAfterFailedShutdown" => null,
            _ => null,
        });
        var tunnel = LifecycleContractProxy.Create<IFrpTunnelService>((method, _) => method.Name switch
        {
            "get_Snapshot" => new FrpSnapshot(stopped ? FrpTunnelState.Stopped : FrpTunnelState.Failed, Pid: stopped ? null : 99, HasOwnedProcess: !stopped),
            "get_HasOwnedProcess" => !stopped,
            "ShutdownAsync" => CountTunnel(),
            "ResumeAfterFailedShutdown" => null,
            _ => null,
        });
        var desktop = LifecycleContractProxy.Create<IClassicDesktopStyleApplicationLifetime>((method, _) =>
        {
            if (method.Name != "TryShutdown") return null;
            desktopCalls++;
            Assert.True(stopped);
            return true;
        });
        var scheduler = LifecycleContractProxy.Create<ICoreUpdateScheduler>((method, _) =>
        {
            if (method.Name == "PauseForApplicationUpdateAsync") return Task.CompletedTask;
            if (method.Name == "ResumeAfterApplicationUpdate") resumes++;
            return null;
        });
        var diagnostics = new FrpTestDiagnostics();
        var lifecycle = new AppLifecycleCoordinator(runtime, new SettingsStore(Path.Combine(directory.Path, "settings.properties")),
            LifecycleContractProxy.Create<IUiDialogService>((_, _) => null), diagnostics, scheduler, desktop, () => null, tunnel);

        var first = lifecycle.TryExitApplicationAsync();
        var second = lifecycle.TryExitForUpdateAsync();
        var third = lifecycle.ExitApplicationAsync();
        Assert.Same(first, second);
        Assert.Same(first, third);
        Assert.False(first.IsCompleted);
        Assert.True(lifecycle.IsExitRequested);
        Assert.Equal(0, desktopCalls);
        Assert.Equal(1, tunnelCalls);
        stopped = succeeds;
        barrier.SetResult(succeeds ? FrpOperationResult.Success("stopped") : FrpOperationResult.Failure("ownership refusal"));
        Assert.Equal(succeeds, await first);
        Assert.Equal(succeeds, await second);
        await third;
        Assert.Equal(succeeds ? 1 : 0, desktopCalls);
        Assert.Equal(succeeds ? 0 : 1, resumes);
        Assert.Equal(succeeds, lifecycle.IsExitRequested);
        if (!succeeds) Assert.Contains(diagnostics.Messages, value => value.Contains("ownership refusal", StringComparison.Ordinal));

        Task<FrpOperationResult> CountTunnel() { tunnelCalls++; return barrier.Task; }
    }

    [Fact]
    public async Task AStoppedSnapshotCannotAuthorizeExitWithRetainedNodeOwnership()
    {
        using var directory = new TemporaryDirectory();
        var desktopCalls = 0;
        var runtime = LifecycleContractProxy.Create<IRuntimeController>((method, _) => method.Name switch
        {
            "get_Snapshot" => new RuntimeSnapshot(DesktopRuntimeState.Stopped),
            "get_HasOwnedProcess" => true,
            "ShutdownAsync" => Task.CompletedTask,
            _ => null,
        });
        var tunnel = LifecycleContractProxy.Create<IFrpTunnelService>((method, _) => method.Name switch
        {
            "get_Snapshot" => new FrpSnapshot(FrpTunnelState.Stopped),
            "get_HasOwnedProcess" => false,
            "ShutdownAsync" => Task.FromResult(FrpOperationResult.Success("stopped")),
            _ => null,
        });
        var scheduler = LifecycleContractProxy.Create<ICoreUpdateScheduler>((method, _) => method.Name == "PauseForApplicationUpdateAsync" ? Task.CompletedTask : null);
        var lifecycle = new AppLifecycleCoordinator(runtime, new SettingsStore(Path.Combine(directory.Path, "settings.properties")),
            LifecycleContractProxy.Create<IUiDialogService>((_, _) => null), new FrpTestDiagnostics(), scheduler,
            LifecycleContractProxy.Create<IClassicDesktopStyleApplicationLifetime>((method, _) => { if (method.Name == "TryShutdown") desktopCalls++; return false; }), () => null, tunnel);
        Assert.False(await lifecycle.TryExitApplicationAsync());
        Assert.Equal(0, desktopCalls);
    }
}

public class LifecycleContractProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Callback { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? arguments) => Callback(method!, arguments);
    public static T Create<T>(Func<MethodInfo, object?[]?, object?> callback) where T : class
    {
        var proxy = DispatchProxy.Create<T, LifecycleContractProxy>();
        ((LifecycleContractProxy)(object)proxy).Callback = callback;
        return proxy;
    }
}
