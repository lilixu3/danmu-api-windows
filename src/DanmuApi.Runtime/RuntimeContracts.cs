using System.Diagnostics;

namespace DanmuApi.Runtime;

public sealed record ProcessTerminationResult(bool Succeeded, string Diagnostic, int? ExitCode = null);

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
    Task RestartAsync(CancellationToken cancellationToken = default);
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}
