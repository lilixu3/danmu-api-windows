using DanmuApi.Core.Frp;

namespace DanmuApi.Runtime.Frp;

/// <summary>
/// 启动一次穿透所需的全部输入。<see cref="AdminUser"/>/<see cref="AdminPassword"/> 是生成配置时
/// 一并写进 webServer 的凭据：状态判定要靠本机管理接口，没有凭据就读不到状态。
/// </summary>
public sealed record FrpRunPlan(
    FrpRole Role,
    string ExecutablePath,
    string ConfigPath,
    string WorkingDirectory,
    string LogDirectory,
    int AdminPort,
    string AdminUser,
    string AdminPassword,
    string ProxyName,
    TimeSpan StartupTimeout,
    TimeSpan ShutdownTimeout)
{
    public string LogFileName => Role == FrpRole.Client ? "frpc" : "frps";

    public string ExecutableLabel => Role == FrpRole.Client ? "frpc.exe" : "frps.exe";
}

/// <summary>
/// 穿透链路快照。<see cref="RemoteAddress"/> 只在 frpc 报告代理 running 时才有值，
/// 且必须来自 frp 自己返回的 <c>remote_addr</c>，不由本应用拼凑。
/// </summary>
public sealed record FrpSnapshot(
    FrpTunnelState State,
    int? Pid = null,
    string? RemoteAddress = null,
    IReadOnlyList<FrpProxyStatus>? Proxies = null,
    string? Diagnostic = null,
    int? ExitCode = null,
    FrpServerInfo? Server = null,
    bool HasOwnedProcess = false)
{
    public IReadOnlyList<FrpProxyStatus> ProxyList => Proxies ?? [];

    public bool IsActive => State is FrpTunnelState.Starting or FrpTunnelState.Running
        or FrpTunnelState.Reconnecting or FrpTunnelState.Stopping;

    /// <summary>Failed 不代表进程已清理；保留所有权时必须仍能停止，不能允许安装/重新启动。</summary>
    public bool RequiresStop => IsActive || HasOwnedProcess
        || (State == FrpTunnelState.Failed && Pid is not null && ExitCode is null);
}

public interface IFrpSupervisor : IAsyncDisposable
{
    FrpSnapshot Snapshot { get; }

    /// <summary>监督器仍持有需确认清理的进程。状态为 Failed 时也可能为 true。</summary>
    bool HasOwnedProcess => Snapshot.HasOwnedProcess
        || (Snapshot.Pid is not null && Snapshot.RequiresStop);

    /// <summary>每次状态落定都会触发；订阅方负责切回自己的线程（本事件可能来自日志泵或轮询线程）。</summary>
    event EventHandler<FrpSnapshot>? SnapshotChanged;

    Task<FrpSnapshot> StartAsync(FrpRunPlan plan, CancellationToken cancellationToken = default);

    Task<FrpSnapshot> StopAsync(string reason = "user", CancellationToken cancellationToken = default);

    /// <summary>按管理接口重新对账一次（周期性调用）；未启动时是空操作。</summary>
    Task<FrpSnapshot> RefreshAsync(CancellationToken cancellationToken = default);

    string? LivenessFailure();
}
