using System.Diagnostics;
using DanmuApi.Core.Frp;

namespace DanmuApi.Runtime.Frp;

/// <summary>
/// frpc.exe / frps.exe 的进程监督器：状态机与日志泵的形状沿用 <see cref="NodeSupervisor"/>，
/// 但"启动成功"的判据完全不同——frp 没有 <c>/__health</c> 那种自带身份的端点，能作为证据的只有
/// <b>它自己的管理接口</b>（<c>/api/status</c>、<c>/api/serverinfo</c>，均只监听 127.0.0.1）：
///
///  - 代理 running 才算穿透成功（进程活着但连不上服务器时，管理接口会回空对象或代理处于 pending）；
///  - 代理 start error 直接判失败，并把 frp 给出的原因原样带出来（典型：公网端口已被占用）；
///  - 管理接口读不出来也判失败，绝不用"进程还在"冒充"穿透正常"。
///
/// frpc 的日志写到自己的 stdout/stderr（生成的配置不设 <c>log.to</c>，保持日志可被宿主捕获），
/// 由本类泵到 <c>logs\frpc-stdout.log</c> / <c>frpc-stderr.log</c>，失败原因里一律附带尾部。
/// </summary>
public sealed class FrpSupervisor : IFrpSupervisor
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IFrpAdminClient _adminClient;
    private readonly IVerifiedProcessTerminator _terminator;
    private readonly Action<string>? _report;
    private readonly TimeSpan _pollInterval;
    private FrpSnapshot _snapshot = new(FrpTunnelState.Stopped);
    private Process? _process;
    private FrpRunPlan? _plan;
    private string? _stdoutPath;
    private string? _stderrPath;
    private CancellationTokenSource? _lifetime;
    private Task? _stdoutPump;
    private Task? _stderrPump;
    private Task? _disposeTask;
    private volatile bool _disposing;
    private bool _disposed;

    public bool HasOwnedProcess => _process is not null;

    public FrpSupervisor(
        IFrpAdminClient adminClient,
        IVerifiedProcessTerminator terminator,
        Action<string>? diagnosticSink = null,
        TimeSpan? pollInterval = null)
    {
        _adminClient = adminClient ?? throw new ArgumentNullException(nameof(adminClient));
        _terminator = terminator ?? throw new ArgumentNullException(nameof(terminator));
        _report = diagnosticSink;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(300);
        if (_pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), "状态轮询间隔必须大于零");
        }
    }

    public FrpSnapshot Snapshot
    {
        get
        {
            lock (this)
            {
                return _snapshot;
            }
        }
    }

    public event EventHandler<FrpSnapshot>? SnapshotChanged;

    public async Task<FrpSnapshot> StartAsync(FrpRunPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposing || HasOwnedProcess || Snapshot.State is not (FrpTunnelState.Stopped or FrpTunnelState.Failed))
            {
                throw new InvalidOperationException($"当前状态 {Snapshot.State} 不允许启动，请先停止穿透");
            }

            SetSnapshot(new(FrpTunnelState.Starting));
            try
            {
                ValidatePlan(plan);
                cancellationToken.ThrowIfCancellationRequested();
                // 上次异常退出（比如应用崩溃）可能留下一个还在跑的 frpc：它占着本地状态端口，
                // 新进程会因绑不上该端口而退出，而状态查询读到的却是它——先把它收掉再起。
                var cleaned = await CleanUpOwnOrphansAsync(plan, cancellationToken).ConfigureAwait(false);
                if (cleaned > 0)
                {
                    _report?.Invoke($"已清理上次退出遗留的 {plan.ExecutableLabel}（{cleaned} 个），它占用了本地状态端口 {plan.AdminPort}");
                }

                EnsureAdminPortFree(plan);
                _plan = plan;
            }
            catch (OperationCanceledException error)
            {
                SetFailure($"启动已取消（准备阶段）: {error.Message}");
                throw;
            }
            catch (Exception error)
            {
                return await FailAsync(FormatFailure("穿透启动准备失败", error), process: null).ConfigureAwait(false);
            }

            Process process;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                process = StartProcess(plan);
            }
            catch (OperationCanceledException error)
            {
                SetFailure($"启动已取消（创建 {plan.ExecutableLabel} 前）: {error.Message}");
                throw;
            }
            catch (Exception error)
            {
                return await FailAsync(FormatFailure($"{plan.ExecutableLabel} 启动失败", error), process: null).ConfigureAwait(false);
            }

            _process = process;
            SetSnapshot(new(FrpTunnelState.Starting, Pid: process.Id, HasOwnedProcess: true));
            var deadline = DateTimeOffset.UtcNow + plan.StartupTimeout;
            string? lastProbeFailure = null;
            string? lastProxyState = null;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (process.HasExited)
                    {
                        return await FailAsync(ExitedFailure(process), process).ConfigureAwait(false);
                    }

                    try
                    {
                        var probe = await ProbeAsync(plan, cancellationToken).ConfigureAwait(false);
                        var settled = CreateProbeSnapshot(process, probe);
                        if (settled is not null)
                        {
                            // 代理起不来（典型：公网端口被别的客户端占着）时 frp 会给出确切原因，
                            // 但 frpc 进程本身还活着并在后台重试。启动路径的契约是"要么 Running、
                            // 要么把进程收掉"，否则界面显示"失败"而机器上还留着一个 frpc 占着状态端口。
                            if (settled.State == FrpTunnelState.Failed)
                            {
                                return await FailAsync(settled.Diagnostic ?? "穿透启动失败", process).ConfigureAwait(false);
                            }

                            // 落定 Running 前再确认进程还活着：管理接口是回环上的共享资源，
                            // 万一它答的是别的进程（或我们的进程刚刚因为绑不上而退出），
                            // 只在"下一次循环"才发现会让界面先报一次成功。
                            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
                            if (process.HasExited)
                            {
                                return await FailAsync(ExitedFailure(process), process).ConfigureAwait(false);
                            }

                            return SetSnapshotState(settled);
                        }

                        lastProbeFailure = null;
                        lastProxyState = probe.Note ?? lastProxyState;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception error)
                    {
                        lastProbeFailure = error.Message;
                    }

                    if (DateTimeOffset.UtcNow >= deadline)
                    {
                        var reasons = new List<string>
                        {
                            $"在 {plan.StartupTimeout.TotalSeconds:0} 秒内没有确认穿透成功",
                        };
                        if (lastProxyState is not null)
                        {
                            reasons.Add(lastProxyState);
                        }

                        if (lastProbeFailure is not null)
                        {
                            reasons.Add($"管理接口读取失败：{lastProbeFailure}");
                        }

                        reasons.Add($"{plan.ExecutableLabel} 输出尾部:\n{Tail(_stdoutPath)}");
                        if (!string.IsNullOrWhiteSpace(Tail(_stderrPath, 20)))
                        {
                            reasons.Add($"标准错误尾部:\n{Tail(_stderrPath, 20)}");
                        }

                        return await FailAsync(string.Join("；", reasons), process).ConfigureAwait(false);
                    }

                    await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException error)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    return await FailAsync($"状态检查被意外取消: {error.Message}", process).ConfigureAwait(false);
                }

                var cleanupDiagnostic = await CleanupTrackedProcessAsync(CancellationToken.None).ConfigureAwait(false);
                var reason = "启动已取消";
                if (cleanupDiagnostic is not null)
                {
                    reason += $"；取消后的 {plan.ExecutableLabel} 清理失败: {cleanupDiagnostic}";
                }

                SetFailure(reason, process);
                throw new OperationCanceledException(reason, error, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<FrpSnapshot> StopAsync(string reason = "user", CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Snapshot;
            if (current.State == FrpTunnelState.Stopped && _process is null)
            {
                return current;
            }

            SetSnapshot(current with { State = FrpTunnelState.Stopping, Diagnostic = null, RemoteAddress = null, Proxies = [] });
            var process = _process;
            if (process is null)
            {
                SetSnapshot(new(FrpTunnelState.Stopped));
                return Snapshot;
            }

            var plan = _plan;
            try
            {
                var result = await _terminator.TerminateVerifiedAsync(
                    process,
                    plan?.ExecutablePath ?? string.Empty,
                    plan?.ConfigPath ?? string.Empty,
                    plan?.ExecutableLabel ?? "frp",
                    plan?.ShutdownTimeout ?? TimeSpan.FromSeconds(10),
                    cancellationToken).ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    return SetFailure($"停止穿透失败（reason={reason}）: {result.Diagnostic}", process);
                }

                if (!process.HasExited)
                {
                    return SetFailure($"停止穿透失败（reason={reason}）: 终止器报告成功但 PID={process.Id} 仍存活", process);
                }

                var pumpDiagnostic = await CompletePumpsAsync(cancel: false).ConfigureAwait(false);
                if (pumpDiagnostic is not null)
                {
                    return SetFailure($"进程已退出，但日志泵收尾失败: {pumpDiagnostic}", process);
                }
            }
            catch (OperationCanceledException)
            {
                // 与 NodeSupervisor 一致：取消不能把状态留在中间态，否则启动/停止按钮会同时不可用。
                SetFailure($"停止穿透被取消（reason={reason}），{plan?.ExecutableLabel ?? "frp"} 状态未知，可能仍在运行", process);
                throw;
            }
            catch (Exception error)
            {
                return SetFailure($"停止穿透异常（reason={reason}）: {error.Message}", process);
            }

            var exitCode = ReadExitCode(process);
            ClearProcess(process);
            _plan = null;
            SetSnapshot(new(FrpTunnelState.Stopped, ExitCode: exitCode));
            return Snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<FrpSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Snapshot;
            var process = _process;
            var plan = _plan;
            if (process is null || plan is null || current.State is FrpTunnelState.Stopped or FrpTunnelState.Stopping)
            {
                return current;
            }

            if (process.HasExited)
            {
                return FailFromExit(process);
            }

            ProbeResult probe;
            try
            {
                probe = await ProbeAsync(plan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                // 已经连上过又读不到管理接口 = 链路在重连，如实标成"重连中"并保留原因；
                // 从未连上过（Starting 期间）就保持 Starting，由启动超时给出结论。
                if (process.HasExited) return FailFromExit(process);
                if (current.State is FrpTunnelState.Running or FrpTunnelState.Reconnecting)
                {
                    return SetSnapshotState(new(
                        FrpTunnelState.Reconnecting,
                        process.Id,
                        RemoteAddress: null,
                        Proxies: [],
                        Diagnostic: $"管理接口读取失败，正在重连：{error.Message}",
                        HasOwnedProcess: true));
                }

                return SetSnapshotState(current with { RemoteAddress = null, Proxies = [], Diagnostic = $"管理接口读取失败：{error.Message}" });
            }

            // 探测只生成候选；确认受管进程存活后才能发布任何 Running 结果。
            if (process.HasExited) return FailFromExit(process);
            if (current.State == FrpTunnelState.Failed) return current;
            var settled = CreateProbeSnapshot(process, probe);
            if (settled is not null && settled.State == FrpTunnelState.Running)
                return SetSnapshotState(settled);

            var reason = settled?.Diagnostic ?? probe.Diagnostic ?? probe.Note
                ?? "管理接口未确认代理运行";
            return SetSnapshotState(new(
                current.State == FrpTunnelState.Starting ? FrpTunnelState.Starting : FrpTunnelState.Reconnecting,
                process.Id,
                RemoteAddress: null,
                Proxies: probe.Proxies,
                Diagnostic: reason,
                HasOwnedProcess: true));
        }
        finally
        {
            _gate.Release();
        }
    }

    public string? LivenessFailure()
    {
        var current = Snapshot;
        if (current.State is FrpTunnelState.Stopped or FrpTunnelState.Failed)
        {
            return null;
        }

        var process = _process;
        if (process is null)
        {
            var reason = "穿透进程句柄不存在，无法继续确认存活";
            SetFailure(reason);
            return reason;
        }

        if (!process.HasExited)
        {
            return null;
        }

        return FailFromExit(process)?.Diagnostic;
    }

    /// <summary>
    /// 进程已经退出：先把退出码与日志尾部取出来再释放句柄 —— <see cref="Process"/> 被 Dispose 之后
    /// 这些属性不可再读，先释放再读会抛异常，失败原因就丢了。
    /// </summary>
    private FrpSnapshot FailFromExit(Process process)
    {
        var reason = ExitedFailure(process);
        var exitCode = ReadExitCode(process);
        var pid = process.Id;
        // 仍保留句柄/计划直到异步 Stop 把日志泵收尾，避免 liveness 路径遗失清理责任。
        _report?.Invoke(reason);
        return SetSnapshotState(new(FrpTunnelState.Failed, pid, Diagnostic: reason, ExitCode: exitCode, HasOwnedProcess: true));
    }

    public ValueTask DisposeAsync()
    {
        lock (this)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposing = true;
            if (_disposeTask is null || _disposeTask.IsCompleted && !_disposeTask.IsCompletedSuccessfully)
                _disposeTask = DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        var stopped = await StopAsync("dispose").ConfigureAwait(false);
        if (stopped.State != FrpTunnelState.Stopped || HasOwnedProcess)
            throw new InvalidOperationException(stopped.Diagnostic ?? "释放穿透监督器失败：仍有受管进程待清理");
        var pumpDiagnostic = await CompletePumpsAsync(cancel: false).ConfigureAwait(false);
        if (pumpDiagnostic is not null) throw new IOException($"释放穿透监督器时日志泵收尾失败：{pumpDiagnostic}");
        _disposed = true;
        _gate.Dispose();
    }

    /// <summary>一次状态探测的结果。要么给出"可以定论"的快照，要么只更新观测值。</summary>
    private sealed record ProbeResult(
        FrpSnapshot? Settled,
        IReadOnlyList<FrpProxyStatus> Proxies,
        string? RemoteAddress,
        string? Note,
        string? Diagnostic,
        FrpServerInfo? Server);

    private async Task<ProbeResult> ProbeAsync(FrpRunPlan plan, CancellationToken cancellationToken)
    {
        if (plan.Role == FrpRole.Client)
        {
            var proxies = await _adminClient
                .ReadClientStatusAsync(plan.AdminPort, plan.AdminUser, plan.AdminPassword, cancellationToken)
                .ConfigureAwait(false);
            var proxy = proxies.FirstOrDefault(item => string.Equals(item.Name, plan.ProxyName, StringComparison.Ordinal));
            if (proxy is null)
            {
                // 空对象 / 没有我们的代理 = frpc 还没登录成功，代理尚未被创建。
                var note = proxies.Count == 0
                    ? "frpc 尚未与服务器建立连接（管理接口还没有任何代理）"
                    : $"管理接口里没有名为 {plan.ProxyName} 的代理（现有：{string.Join("、", proxies.Select(item => item.Name))}）";
                return new ProbeResult(null, proxies, null, note, null, null);
            }

            switch (proxy.State)
            {
                case FrpProxyState.Running:
                    var remote = string.IsNullOrWhiteSpace(proxy.RemoteAddress) ? null : proxy.RemoteAddress;
                    var diagnostic = remote is null ? "frp 报告代理已运行，但没有返回远端地址" : null;
                    return new ProbeResult(
                        new(FrpTunnelState.Running, Pid: null, RemoteAddress: remote, Proxies: proxies, Diagnostic: diagnostic),
                        proxies,
                        remote,
                        null,
                        diagnostic,
                        null);
                case FrpProxyState.StartError:
                    return new ProbeResult(
                        new(
                            FrpTunnelState.Failed,
                            Pid: null,
                            RemoteAddress: null,
                            Proxies: proxies,
                            Diagnostic: $"代理 {proxy.Name} 启动失败：{(proxy.Error.Length == 0 ? "frp 未给出原因" : proxy.Error)}"),
                        proxies,
                        null,
                        null,
                        null,
                        null);
                default:
                    return new ProbeResult(
                        null,
                        proxies,
                        null,
                        $"代理 {proxy.Name} 当前状态：{proxy.Status}{(proxy.Error.Length == 0 ? string.Empty : $"（{proxy.Error}）")}",
                        null,
                        null);
            }
        }

        var server = await _adminClient
            .ReadServerInfoAsync(plan.AdminPort, plan.AdminUser, plan.AdminPassword, cancellationToken)
            .ConfigureAwait(false);
        return new ProbeResult(
            new(FrpTunnelState.Running, Pid: null, RemoteAddress: null, Proxies: [], Diagnostic: null, Server: server),
            [],
            null,
            null,
            null,
            server);
    }

    /// <summary>仅生成候选，不触发事件；调用方在最终存活复核之后发布。</summary>
    private static FrpSnapshot? CreateProbeSnapshot(Process process, ProbeResult probe) =>
        probe.Settled is { } settled ? settled with
        {
            Pid = process.Id,
            Server = probe.Server ?? settled.Server,
            HasOwnedProcess = true,
        } : null;

    private Process StartProcess(FrpRunPlan plan)
    {
        Directory.CreateDirectory(plan.LogDirectory);
        var startInfo = new ProcessStartInfo
        {
            FileName = plan.ExecutablePath,
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(plan.ConfigPath);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Process.Start 返回 false");
        }

        _lifetime = new CancellationTokenSource();
        var baseName = plan.LogFileName;
        _stdoutPath = Path.Combine(plan.LogDirectory, $"{baseName}-stdout.log");
        _stderrPath = Path.Combine(plan.LogDirectory, $"{baseName}-stderr.log");
        _stdoutPump = PumpAsync(process.StandardOutput, _stdoutPath, _lifetime.Token);
        _stderrPump = PumpAsync(process.StandardError, _stderrPath, _lifetime.Token);
        return process;
    }

    private static void ValidatePlan(FrpRunPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.ExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.ConfigPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.WorkingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.LogDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.AdminUser);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.AdminPassword);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.ProxyName);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("穿透监督器仅支持 Windows 宿主");
        }

        if (plan.AdminPort is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(plan), plan.AdminPort, "本地状态端口必须在 1 到 65535 之间");
        }

        if (!File.Exists(plan.ExecutablePath))
        {
            throw new FileNotFoundException($"{plan.ExecutableLabel} 不存在，请先在穿透页下载安装", plan.ExecutablePath);
        }

        if (!File.Exists(plan.ConfigPath))
        {
            throw new FileNotFoundException("frp 配置文件不存在", plan.ConfigPath);
        }

        if (!Directory.Exists(plan.WorkingDirectory))
        {
            throw new DirectoryNotFoundException($"frp 工作目录不存在：{plan.WorkingDirectory}");
        }

        if (plan.StartupTimeout <= TimeSpan.Zero || plan.ShutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(plan), "启停超时必须大于零");
        }
    }

    /// <summary>
    /// 清掉"本应用上一次运行时留下、还活着的"frp 进程。
    ///
    /// 安全性交给终止器自己判：它要求可执行文件路径与命令行里的配置文件路径**都对得上**，
    /// 对不上就明确拒绝。因此这里按进程名（frpc / frps）逐个试即可——
    /// 用户手工起的 frp（别的 exe 或别的配置）会被终止器拒绝，最后那种占用由预检如实报出。
    /// </summary>
    private async Task<int> CleanUpOwnOrphansAsync(FrpRunPlan plan, CancellationToken cancellationToken)
    {
        var processName = Path.GetFileNameWithoutExtension(plan.ExecutablePath);
        var cleaned = 0;
        var candidates = Process.GetProcessesByName(processName);
        try
        {
            foreach (var candidate in candidates)
            {
                if (candidate.Id == Environment.ProcessId || candidate.HasExited) continue;
                ProcessTerminationResult result;
                try
                {
                    result = await _terminator.TerminateVerifiedAsync(
                        candidate, plan.ExecutablePath, plan.ConfigPath, plan.ExecutableLabel,
                        plan.ShutdownTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    _report?.Invoke($"遗留进程检查拒绝 PID={candidate.Id}：{error.GetType().Name}, HResult=0x{error.HResult:X8}");
                    continue;
                }

                if (result.Succeeded && candidate.HasExited)
                {
                    cleaned++;
                }
                else if (result.OwnershipVerified)
                {
                    // 已证明这是自己的遗留进程，终止失败不能丢掉句柄/PID 后再启动一个。
                    _process = candidate;
                    _plan = plan;
                    throw new IOException($"清理自有遗留 PID={candidate.Id} 失败：{result.Diagnostic}");
                }
                else
                {
                    _report?.Invoke($"跳过清理 PID={candidate.Id} 的 {plan.ExecutableLabel}：{result.Diagnostic}");
                }
            }
        }
        finally
        {
            foreach (var candidate in candidates)
                if (!ReferenceEquals(candidate, _process)) candidate.Dispose();
        }

        return cleaned;
    }

    /// <summary>
    /// 启动前只读探测本地管理接口端口。占用说明另一个 frpc（或别的程序）正拿着它，
    /// 此时新进程的管理接口起不来，状态就无从判定，必须当场说清楚而不是等超时。
    /// </summary>
    private void EnsureAdminPortFree(FrpRunPlan plan)
    {
        var state = PortAvailability.Probe(plan.AdminPort, System.Net.IPAddress.Loopback);
        if (state == PortAvailabilityState.Free)
        {
            return;
        }

        var detail = state == PortAvailabilityState.Listening
            ? $"有进程正在监听 127.0.0.1:{plan.AdminPort}"
            : $"127.0.0.1:{plan.AdminPort} 目前无法绑定（可能被系统临时借用，稍后重试）";
        throw new IOException(
            $"本地状态端口 {plan.AdminPort} 不可用：{detail}。"
            + "该端口用于读取穿透状态，被别的程序（或手工启动的 frp）占用时无法确认穿透是否真的通了。"
            + "请关掉占用它的程序，或在穿透页的「配置 → 高级」里改用其它状态端口。");
    }

    private async Task<string?> CleanupTrackedProcessAsync(CancellationToken cancellationToken)
    {
        var process = _process;
        if (process is null || process.HasExited)
        {
            return null;
        }

        ProcessTerminationResult result;
        try
        {
            result = await _terminator.TerminateVerifiedAsync(
                process,
                _plan?.ExecutablePath ?? string.Empty,
                _plan?.ConfigPath ?? string.Empty,
                _plan?.ExecutableLabel ?? "frp",
                _plan?.ShutdownTimeout ?? TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return $"调用进程终止器异常: {error.Message}";
        }

        if (!result.Succeeded)
        {
            return result.Diagnostic;
        }

        return process.HasExited ? null : $"终止器报告成功但 PID={process.Id} 仍存活";
    }

    private async Task<FrpSnapshot> FailAsync(string reason, Process? process)
    {
        if (process is null && _process is not null) return SetFailure(reason, _process);
        if (process is not null)
        {
            var cleanupDiagnostic = await CleanupTrackedProcessAsync(CancellationToken.None).ConfigureAwait(false);
            if (cleanupDiagnostic is not null)
            {
                reason += $"；失败清理 {_plan?.ExecutableLabel ?? "frp"} 也失败: {cleanupDiagnostic}";
            }
        }

        // 失败清理仍未确认退出：句柄、计划、PID、日志泵全部保留，下一次 Stop 必须再次尝试。
        if (process is not null && !process.HasExited) return SetFailure(reason, process);

        var pumpDiagnostic = await CompletePumpsAsync(cancel: false).ConfigureAwait(false);
        if (pumpDiagnostic is not null)
        {
            reason += $"；日志泵收尾失败: {pumpDiagnostic}";
            return SetFailure(reason, process);
        }

        if (process is not null)
        {
            var exitCode = ReadExitCode(process);
            ClearProcess(process);
            _plan = null;
            _report?.Invoke(reason);
            return SetSnapshotState(new(FrpTunnelState.Failed, ExitCode: exitCode, Diagnostic: reason));
        }

        _plan = null;
        return SetFailure(reason);
    }

    private async Task<string?> CompletePumpsAsync(bool cancel)
    {
        var lifetime = _lifetime;
        var stdoutPump = _stdoutPump;
        var stderrPump = _stderrPump;
        if (lifetime is null && stdoutPump is null && stderrPump is null)
        {
            return null;
        }

        if (cancel)
        {
            lifetime?.Cancel();
        }

        try
        {
            var tasks = new[] { stdoutPump, stderrPump }.Where(task => task is not null).Cast<Task>().ToArray();
            if (tasks.Length > 0)
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            return null;
        }
        catch (OperationCanceledException) when (cancel && lifetime?.IsCancellationRequested == true)
        {
            return null;
        }
        catch (Exception error)
        {
            return error.Message;
        }
        finally
        {
            _stdoutPump = null;
            _stderrPump = null;
            _lifetime?.Dispose();
            _lifetime = null;
        }
    }

    private FrpSnapshot SetFailure(string reason, Process? process = null)
    {
        process ??= _process;
        _report?.Invoke(reason);
        return SetSnapshotState(new(
            FrpTunnelState.Failed,
            process?.Id ?? Snapshot.Pid,
            RemoteAddress: null,
            Proxies: [],
            Diagnostic: reason,
            ExitCode: ReadExitCode(process) ?? Snapshot.ExitCode,
            HasOwnedProcess: HasOwnedProcess));
    }

    private void ClearProcess(Process process)
    {
        if (ReferenceEquals(_process, process))
        {
            _process = null;
        }

        process.Dispose();
    }

    private FrpSnapshot SetSnapshotState(FrpSnapshot snapshot)
    {
        lock (this)
        {
            _snapshot = snapshot;
        }

        var handlers = SnapshotChanged;
        if (handlers is not null)
        {
            foreach (EventHandler<FrpSnapshot> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, snapshot);
                }
                catch (Exception error)
                {
                    // 订阅方出错不能影响状态机本身；但也绝不能悄悄消失。
                    _report?.Invoke($"穿透状态订阅者抛出异常: {error.Message}");
                }
            }
        }

        return snapshot;
    }

    private void SetSnapshot(FrpSnapshot snapshot) => SetSnapshotState(snapshot);

    private string ExitedFailure(Process process)
    {
        var exitCode = ReadExitCode(process)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "未知";
        var stderr = Tail(_stderrPath, 20);
        var suffix = string.IsNullOrWhiteSpace(stderr) || stderr == "（无日志）"
            ? string.Empty
            : $"；标准错误尾部:\n{stderr}";
        return $"{_plan?.ExecutableLabel ?? "frp"} 已退出，exitCode={exitCode}；输出尾部:\n{Tail(_stdoutPath)}{suffix}";
    }

    private static int? ReadExitCode(Process? process)
    {
        if (process is null)
        {
            return null;
        }

        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task PumpAsync(StreamReader reader, string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 16 * 1024, useAsync: true);
        await using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8) { AutoFlush = true };
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Tail(string? path, int maxLines = 40) => Core.LogTail.Read(path, maxLines);

    private static string FormatFailure(string prefix, Exception error) => $"{prefix}: {error.Message}";
}
