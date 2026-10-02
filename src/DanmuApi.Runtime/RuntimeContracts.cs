using System.Diagnostics;

namespace DanmuApi.Runtime;

public sealed record ProcessTerminationResult(bool Succeeded, string Diagnostic, int? ExitCode = null,
    bool OwnershipVerified = false);

public interface IProcessTerminator
{
    Task<ProcessTerminationResult> TerminateAsync(
        Process process,
        string expectedNodeExe,
        string expectedMainScript,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public interface INodeSupervisor : IAsyncDisposable
{
    RuntimeSnapshot Snapshot { get; }
    bool HasOwnedProcess => Snapshot.Pid is not null;
    Task<RuntimeSnapshot> StartAsync(StartConfig config, CancellationToken cancellationToken = default);
    Task<AdoptionResult> AdoptAsync(
        StartConfig config,
        RuntimeHealthSnapshot? health = null,
        CancellationToken cancellationToken = default);
    Task<RuntimeSnapshot> StopAsync(string reason = "user", CancellationToken cancellationToken = default);
    Task<RuntimeSnapshot> ForceStopAsync(string reason = "application-exit", CancellationToken cancellationToken = default);
    string? LivenessFailure();
}

public interface IRuntimeHealthClient
{
    Task<RuntimeHealthSnapshot> ReadAsync(string host, int port, CancellationToken cancellationToken = default);
}

public interface IRuntimeController
{
    RuntimeSnapshot Snapshot { get; }
    event EventHandler<RuntimeSnapshot>? SnapshotChanged;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task<AdoptionResult> AdoptAsync(CancellationToken cancellationToken = default);
    string? ReconcileLiveness();
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Re-evaluates a parked <see cref="DesktopRuntimeState.CoreSetupRequired"/> state against
    /// the selected variant on disk. Deleting the active core parks the runtime there on purpose, and
    /// both start entry points stay disabled while it is parked, so the state must not outlive the
    /// missing core. A no-op in every other state; it never starts the service.</summary>
    Task RefreshCoreSetupRequiredAsync(CancellationToken cancellationToken = default);
    Task RestartAsync(CancellationToken cancellationToken = default);
    /// <summary>同步暂停新启动，排空已排队操作并停止受管进程；失败不释放清理所有权。</summary>
    Task ShutdownAsync(CancellationToken cancellationToken = default);
    bool IsShutdownRequested => false;
    bool HasOwnedProcess => Snapshot.Pid is not null || Snapshot.State == DesktopRuntimeState.Running;
    /// <summary>退出门控失败后允许用户继续操作；不得在仍在排空时恢复。</summary>
    void ResumeAfterFailedShutdown() { }
}
