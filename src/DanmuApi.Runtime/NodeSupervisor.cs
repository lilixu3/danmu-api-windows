using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace DanmuApi.Runtime;

public sealed class NodeSupervisor : INodeSupervisor
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IRuntimeHealthClient _healthClient;
    private readonly IProcessTerminator _terminator;
    private RuntimeSnapshot _snapshot = new(DesktopRuntimeState.Stopped);
    private Process? _process;
    private StartConfig? _config;
    private string? _identity;
    private string? _stdoutPath;
    private string? _stderrPath;
    private CancellationTokenSource? _lifetime;
    private Task? _stdoutPump;
    private Task? _stderrPump;
    private Task? _disposeTask;

    public NodeSupervisor(IRuntimeHealthClient? healthClient = null, IProcessTerminator? terminator = null)
    {
        _healthClient = healthClient ?? new RuntimeHealthClient();
        _terminator = terminator ?? throw new ArgumentNullException(nameof(terminator), "必须由组合根注入 Windows 进程终止器");
    }

    public RuntimeSnapshot Snapshot
    {
        get
        {
            lock (this)
            {
                return _snapshot;
            }
        }
    }

    public async Task<RuntimeSnapshot> StartAsync(StartConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Snapshot.State is not (DesktopRuntimeState.Stopped or DesktopRuntimeState.Failed))
            {
                throw new InvalidOperationException($"当前状态 {Snapshot.State} 不允许启动，请先停止服务");
            }

            SetSnapshot(new(DesktopRuntimeState.Preparing));
            try
            {
                ValidateStartConfig(config);
                cancellationToken.ThrowIfCancellationRequested();
                _config = config;
                _identity = EnsureIdentity(config.IdentityFile ?? Path.Combine(config.ScriptDir, "instance-id"));
                PrepareRuntime(config);
                cancellationToken.ThrowIfCancellationRequested();
                PreflightPort(config.Port, _identity);
            }
            catch (OperationCanceledException error)
            {
                SetFailure($"启动已取消（运行时准备阶段）: {error.Message}");
                throw;
            }
            catch (Exception error)
            {
                return await FailAsync(FormatFailure("运行时准备失败", error)).ConfigureAwait(false);
            }

            SetSnapshot(new(DesktopRuntimeState.Starting, config.Port, null, _identity));
            Process process;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                process = StartProcess(config, _identity);
            }
            catch (OperationCanceledException error)
            {
                SetFailure($"启动已取消（创建 Node 进程前）: {error.Message}");
                throw;
            }
            catch (Exception error)
            {
                return await FailAsync(FormatFailure("node.exe 启动失败", error)).ConfigureAwait(false);
            }

            _process = process;
            var startupDeadline = DateTimeOffset.UtcNow + config.EffectiveStartupTimeout;
            RuntimeHealthException? lastHealthError = null;
            try
            {
                while (DateTimeOffset.UtcNow < startupDeadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!process.HasExited)
                    {
                        try
                        {
                            var health = await _healthClient.ReadAsync("127.0.0.1", config.Port, cancellationToken).ConfigureAwait(false);
                            if (MatchesRunning(config, process, _identity!, health))
                            {
                                SetSnapshot(new(DesktopRuntimeState.Running, config.Port, process.Id, _identity));
                                return Snapshot;
                            }
                        }
                        catch (RuntimeHealthException error)
                        {
                            lastHealthError = error;
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception error)
                        {
                            return await FailAsync($"健康检查异常: {error.Message}").ConfigureAwait(false);
                        }

                        await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    return await FailAsync(ExitedFailure(process)).ConfigureAwait(false);
                }

                var healthSuffix = lastHealthError is null ? string.Empty : $"；最近健康检查失败: {lastHealthError.Message}";
                return await FailAsync($"健康检查超时（{config.EffectiveStartupTimeout.TotalMilliseconds:0}ms）{healthSuffix}；stderr 尾部:\n{Tail(_stderrPath)}").ConfigureAwait(false);
            }
            catch (OperationCanceledException error)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    return await FailAsync($"健康检查被意外取消: {error.Message}").ConfigureAwait(false);
                }

                var cleanupDiagnostic = await CleanupTrackedProcessAsync(CancellationToken.None).ConfigureAwait(false);
                var pumpDiagnostic = cleanupDiagnostic is null
                    ? await CompletePumpsAsync(cancel: false).ConfigureAwait(false)
                    : null;
                var reason = "启动已取消";
                if (cleanupDiagnostic is not null)
                {
                    reason += $"；取消后的 Node 清理失败: {cleanupDiagnostic}";
                }

                if (pumpDiagnostic is not null)
                {
                    reason += $"；取消后的日志泵收尾失败: {pumpDiagnostic}";
                }

                SetFailure(reason, process);
                if (cleanupDiagnostic is null && process.HasExited)
                {
                    ClearProcess(process);
                }

                throw new OperationCanceledException(reason, error, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AdoptionResult> AdoptAsync(
        StartConfig config,
        RuntimeHealthSnapshot? health = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Snapshot;
            if (current.State is not (DesktopRuntimeState.Stopped or DesktopRuntimeState.Failed))
            {
                return AdoptionResult.Failure(
                    AdoptionFailureKind.InvalidState,
                    current,
                    $"当前状态 {current.State} 不允许认领已有 Node");
            }

            SetSnapshot(new(DesktopRuntimeState.Preparing));
            string identity;
            try
            {
                ValidateAdoptionConfig(config);
                identity = ReadIdentity(config.IdentityFile ?? Path.Combine(config.ScriptDir, "instance-id"));
            }
            catch (Exception error)
            {
                SetSnapshot(new(DesktopRuntimeState.Stopped));
                return AdoptionResult.Failure(
                    AdoptionFailureKind.InvalidConfiguration,
                    Snapshot,
                    $"认领配置无效: {error.Message}");
            }

            RuntimeHealthSnapshot actualHealth;
            try
            {
                actualHealth = health ?? await _healthClient.ReadAsync("127.0.0.1", config.Port, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SetSnapshot(new(DesktopRuntimeState.Stopped));
                throw;
            }
            catch (RuntimeHealthException error)
            {
                SetSnapshot(new(DesktopRuntimeState.Stopped));
                return AdoptionResult.Failure(
                    AdoptionFailureKind.NotFound,
                    Snapshot,
                    $"未发现可认领的 Node: {error.Message}");
            }
            catch (Exception error)
            {
                SetSnapshot(new(DesktopRuntimeState.Stopped));
                return AdoptionResult.Failure(
                    AdoptionFailureKind.InvalidHealth,
                    Snapshot,
                    $"认领健康探测失败: {error.Message}");
            }

            if (!RuntimeOwnership.IsOwned(
                    identity,
                    config.Port,
                    config.ScriptDir,
                    new RuntimeOwnership.Health(
                        actualHealth.RuntimeIdentity,
                        actualHealth.MainPort,
                        actualHealth.EnvHome,
                        actualHealth.ResolvedHome,
                        actualHealth.Cwd)))
            {
                SetSnapshot(new(DesktopRuntimeState.Stopped));
                return AdoptionResult.Failure(
                    AdoptionFailureKind.NotOwned,
                    Snapshot,
                    "健康接口身份、端口或运行目录与本安装不匹配，拒绝认领");
            }

            if (actualHealth.Pid is not > 0 or > int.MaxValue)
            {
                SetSnapshot(new(DesktopRuntimeState.Stopped));
                return AdoptionResult.Failure(
                    AdoptionFailureKind.InvalidPid,
                    Snapshot,
                    $"健康接口返回了非法 PID: {actualHealth.Pid?.ToString() ?? "null"}");
            }

            if (actualHealth.Pid.Value == Environment.ProcessId)
            {
                SetSnapshot(new(DesktopRuntimeState.Stopped));
                return AdoptionResult.Failure(
                    AdoptionFailureKind.InvalidPid,
                    Snapshot,
                    $"拒绝认领当前 Desktop 进程 PID={Environment.ProcessId}");
            }

            Process? process = null;
            try
            {
                process = Process.GetProcessById((int)actualHealth.Pid.Value);
                if (process.HasExited)
                {
                    process.Dispose();
                    process = null;
                    SetSnapshot(new(DesktopRuntimeState.Stopped));
                    return AdoptionResult.Failure(
                        AdoptionFailureKind.ProcessUnavailable,
                        Snapshot,
                        $"健康接口 PID={actualHealth.Pid.Value} 对应进程已经退出");
                }
            }
            catch (ArgumentException error)
            {
                process?.Dispose();
                SetSnapshot(new(DesktopRuntimeState.Stopped));
                return AdoptionResult.Failure(
                    AdoptionFailureKind.ProcessUnavailable,
                    Snapshot,
                    $"无法找到健康接口 PID={actualHealth.Pid.Value}: {error.Message}");
            }
            catch (InvalidOperationException error)
            {
                process?.Dispose();
                SetSnapshot(new(DesktopRuntimeState.Stopped));
                return AdoptionResult.Failure(
                    AdoptionFailureKind.ProcessUnavailable,
                    Snapshot,
                    $"无法读取健康接口 PID={actualHealth.Pid.Value}: {error.Message}");
            }
            catch (Win32Exception error)
            {
                process?.Dispose();
                SetSnapshot(new(DesktopRuntimeState.Stopped));
                return AdoptionResult.Failure(
                    AdoptionFailureKind.ProcessUnavailable,
                    Snapshot,
                    $"无法打开健康接口 PID={actualHealth.Pid.Value}: {error.Message}");
            }

            if (process is null || process.Id != actualHealth.Pid.Value || process.HasExited)
            {
                process?.Dispose();
                SetSnapshot(new(DesktopRuntimeState.Stopped));
                return AdoptionResult.Failure(
                    AdoptionFailureKind.ProcessUnavailable,
                    Snapshot,
                    $"健康接口 PID={actualHealth.Pid.Value} 在认领前已失效");
            }

            _config = config;
            _identity = identity;
            _process = process;
            _stdoutPath = Path.Combine(config.ScriptDir, "logs", "node-stdout.log");
            _stderrPath = Path.Combine(config.ScriptDir, "logs", "node-stderr.log");
            SetSnapshot(new(DesktopRuntimeState.Running, config.Port, process.Id, identity));
            return AdoptionResult.Success(Snapshot, $"已认领运行中的 Node 实例（pid={process.Id}，port={config.Port}）");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuntimeSnapshot> StopAsync(string reason = "user", CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Snapshot;
            if (current.State == DesktopRuntimeState.Stopped && _process is null)
            {
                return current;
            }

            SetSnapshot(current with { State = DesktopRuntimeState.Stopping, FailureReason = null });
            var process = _process;
            if (process is null)
            {
                if (current.Port is not null && !await WaitForPortFreeAsync(current.Port.Value, cancellationToken).ConfigureAwait(false))
                {
                    return SetFailure($"停止失败：没有可验证的 Node 子进程，端口 {current.Port} 仍被占用");
                }

                SetSnapshot(new(DesktopRuntimeState.Stopped));
                return Snapshot;
            }

            ProcessTerminationResult result;
            try
            {
                result = await _terminator.TerminateAsync(
                    process,
                    _config?.NodeExe ?? string.Empty,
                    _config is null ? string.Empty : Path.Combine(_config.ScriptDir, "main.js"),
                    _config?.EffectiveShutdownTimeout ?? TimeSpan.FromSeconds(10),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                return SetFailure($"停止 Node 异常（reason={reason}）: {error.Message}");
            }

            if (!result.Succeeded)
            {
                return SetFailure($"停止 Node 失败（reason={reason}）: {result.Diagnostic}");
            }

            if (!await WaitForPortFreeAsync(current.Port ?? -1, cancellationToken).ConfigureAwait(false))
            {
                return SetFailure($"Node 已退出，但端口 {current.Port} 仍被占用，拒绝报告为已停止");
            }

            var pumpDiagnostic = await CompletePumpsAsync(cancel: false).ConfigureAwait(false);
            if (pumpDiagnostic is not null)
            {
                return SetFailure($"Node 已退出，但 stdout/stderr 日志泵收尾失败: {pumpDiagnostic}");
            }

            var exitCode = ReadExitCode(process);
            ClearProcess(process);
            SetSnapshot(new(DesktopRuntimeState.Stopped, ExitCode: exitCode));
            return Snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuntimeSnapshot> ForceStopAsync(string reason = "application-exit", CancellationToken cancellationToken = default)
    {
        return await StopAsync(reason, cancellationToken).ConfigureAwait(false);
    }

    public string? LivenessFailure()
    {
        var current = Snapshot;
        if (current.State != DesktopRuntimeState.Running)
        {
            return null;
        }

        var process = _process;
        if (process is null)
        {
            var reason = "Node 子进程句柄不存在，活动运行时不可继续确认存活";
            SetFailure(reason);
            return reason;
        }

        if (!process.HasExited)
        {
            return null;
        }

        var reasonForExit = ExitedFailure(process);
        SetFailure(reasonForExit, process);
        return reasonForExit;
    }

    public ValueTask DisposeAsync()
    {
        lock (this)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await ForceStopAsync("dispose").ConfigureAwait(false);
        }
        finally
        {
            var pumpDiagnostic = await CompletePumpsAsync(cancel: true).ConfigureAwait(false);
            if (pumpDiagnostic is not null && Snapshot.State != DesktopRuntimeState.Failed)
            {
                SetFailure($"释放 NodeSupervisor 时 stdout/stderr 日志泵收尾失败: {pumpDiagnostic}");
            }

            _lifetime?.Dispose();
            _lifetime = null;
            _gate.Dispose();
        }
    }

    private Process StartProcess(StartConfig config, string identity)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = config.NodeExe,
            Arguments = $"\"{Path.Combine(config.ScriptDir, "main.js")}\"",
            WorkingDirectory = config.ScriptDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        startInfo.Environment["DANMU_API_RUNTIME_IDENTITY"] = identity;
        startInfo.Environment["HOME"] = Directory.GetParent(config.ScriptDir)?.FullName ?? config.ScriptDir;
        startInfo.Environment["TEMP"] = Path.Combine(config.ScriptDir, "tmp");
        startInfo.Environment["TMP"] = Path.Combine(config.ScriptDir, "tmp");
        startInfo.Environment["NODE_COMPILE_CACHE"] = Path.Combine(config.ScriptDir, "compile-cache");

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Process.Start 返回 false");
        }

        _lifetime = new CancellationTokenSource();
        _stdoutPath = Path.Combine(config.ScriptDir, "logs", "node-stdout.log");
        _stderrPath = Path.Combine(config.ScriptDir, "logs", "node-stderr.log");
        _stdoutPump = PumpAsync(process.StandardOutput, _stdoutPath, _lifetime.Token);
        _stderrPump = PumpAsync(process.StandardError, _stderrPath, _lifetime.Token);
        return process;
    }

    private async Task<RuntimeSnapshot> FailAsync(string reason)
    {
        var cleanupDiagnostic = await CleanupTrackedProcessAsync(CancellationToken.None).ConfigureAwait(false);
        if (cleanupDiagnostic is not null)
        {
            reason += $"；失败清理 Node 也失败: {cleanupDiagnostic}";
        }

        var pumpDiagnostic = await CompletePumpsAsync(cancel: cleanupDiagnostic is not null).ConfigureAwait(false);
        if (pumpDiagnostic is not null)
        {
            reason += $"；日志泵收尾失败: {pumpDiagnostic}";
        }

        return SetFailure(reason);
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
            result = await _terminator.TerminateAsync(
                process,
                _config?.NodeExe ?? string.Empty,
                _config is null ? string.Empty : Path.Combine(_config.ScriptDir, "main.js"),
                _config?.EffectiveShutdownTimeout ?? TimeSpan.FromSeconds(10),
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

    private RuntimeSnapshot SetFailure(string reason, Process? process = null)
    {
        process ??= _process;
        var exitCode = ReadExitCode(process) ?? Snapshot.ExitCode;
        var pid = process?.Id ?? Snapshot.Pid;
        var port = Snapshot.Port ?? _config?.Port;
        SetSnapshot(new(DesktopRuntimeState.Failed, port, pid, _identity, reason, exitCode));
        return Snapshot;
    }

    private void ClearProcess(Process process)
    {
        if (ReferenceEquals(_process, process))
        {
            _process = null;
        }

        process.Dispose();
    }

    private void SetSnapshot(RuntimeSnapshot snapshot)
    {
        lock (this)
        {
            _snapshot = snapshot;
        }
    }

    private static void ValidateStartConfig(StartConfig config)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("NodeSupervisor 仅支持 Windows 宿主");
        }

        RuntimeValidation.ValidatePort(config.Port);
        RuntimeValidation.ValidateHost(config.ListenHost);
        RuntimeValidation.ValidateVariant(config.Variant);
        if (!File.Exists(config.NodeExe))
        {
            throw new FileNotFoundException("node.exe 不存在", config.NodeExe);
        }

        if (!File.Exists(Path.Combine(config.ScriptDir, "main.js")))
        {
            throw new FileNotFoundException("入口 main.js 不存在", Path.Combine(config.ScriptDir, "main.js"));
        }

        if (config.EffectiveStartupTimeout <= TimeSpan.Zero || config.EffectiveShutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(config), "启停超时必须大于零");
        }
    }

    private static void ValidateAdoptionConfig(StartConfig config)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("NodeSupervisor 仅支持 Windows 宿主");
        }

        RuntimeValidation.ValidatePort(config.Port);
        RuntimeValidation.ValidateHost(config.ListenHost);
        RuntimeValidation.ValidateVariant(config.Variant);
        if (!File.Exists(config.NodeExe))
        {
            throw new FileNotFoundException("预期 node.exe 不存在", config.NodeExe);
        }

        if (!File.Exists(Path.Combine(config.ScriptDir, "main.js")))
        {
            throw new FileNotFoundException("预期入口 main.js 不存在", Path.Combine(config.ScriptDir, "main.js"));
        }

        if (config.EffectiveShutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(config), "停止超时时间必须大于零");
        }
    }

    private static void PrepareRuntime(StartConfig config)
    {
        foreach (var directory in new[] { "config", "logs", ".cache", "tmp", "compile-cache" })
        {
            Directory.CreateDirectory(Path.Combine(config.ScriptDir, directory));
        }

        var updates = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["DANMU_API_PORT"] = config.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["DANMU_API_HOST"] = config.ListenHost,
            ["DANMU_API_VARIANT"] = config.Variant,
        };
        if (config.AdminToken is not null)
        {
            updates["ADMIN_TOKEN"] = config.AdminToken;
        }

        DotEnvFile.UpdateValues(Path.Combine(config.ScriptDir, "config", ".env"), updates);
    }

    private void PreflightPort(int port, string identity)
    {
        if (IsPortFree(port))
        {
            return;
        }

        try
        {
            var health = _healthClient.ReadAsync("127.0.0.1", port).GetAwaiter().GetResult();
            if (string.Equals(health.RuntimeIdentity, identity, StringComparison.Ordinal))
            {
                throw new IOException($"端口 {port} 已被本应用实例占用，请在已有窗口或托盘中停止服务");
            }
        }
        catch (RuntimeHealthException)
        {
            // A non-health process still owns the port; the explicit occupancy failure below is retained.
        }

        throw new IOException($"端口 {port} 已有其他实例在运行，请先停止外部进程");
    }

    private static bool MatchesRunning(StartConfig config, Process process, string identity, RuntimeHealthSnapshot health)
    {
        return health.RuntimeIdentity == identity && health.MainPort == config.Port && health.Pid == process.Id &&
               PathsEqual(health.ResolvedHome, config.ScriptDir) && PathsEqual(health.Cwd, config.ScriptDir);
    }

    private static bool PathsEqual(string? actual, string expected)
    {
        if (actual is null)
        {
            return false;
        }

        try
        {
            return string.Equals(RuntimeValidation.CanonicalPath(actual), RuntimeValidation.CanonicalPath(expected), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string EnsureIdentity(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length > 0)
            {
                return existing;
            }
        }

        var identity = $"desktop-{Guid.NewGuid():D}";
        File.WriteAllText(path, identity + Environment.NewLine, System.Text.Encoding.UTF8);
        return identity;
    }

    private static string ReadIdentity(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("安装身份文件不存在", path);
        }

        var identity = File.ReadAllText(path).Trim();
        if (identity.Length == 0)
        {
            throw new InvalidDataException($"安装身份文件为空: {path}");
        }

        return identity;
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static async Task<bool> WaitForPortFreeAsync(int port, CancellationToken cancellationToken)
    {
        if (port is < 1 or > 65_535)
        {
            return false;
        }

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (IsPortFree(port))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }

        return IsPortFree(port);
    }

    private string ExitedFailure(Process process)
    {
        var exitCode = ReadExitCode(process)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "未知";
        return $"node.exe 提前退出，exitCode={exitCode}；stdout 尾部:\n{Tail(_stdoutPath)}；stderr 尾部:\n{Tail(_stderrPath)}";
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

    private static string Tail(string? path, int maxLines = 40)
    {
        if (path is null || !File.Exists(path))
        {
            return "（无日志）";
        }

        try
        {
            return string.Join(Environment.NewLine, File.ReadLines(path).TakeLast(maxLines));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"（读取日志失败: {error.Message}）";
        }
    }

    private static string FormatFailure(string prefix, Exception error) =>
        $"{prefix}: {error.Message}";
}
