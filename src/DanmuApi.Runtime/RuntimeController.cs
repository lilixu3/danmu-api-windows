namespace DanmuApi.Runtime;

public sealed class RuntimeController : IRuntimeController, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly INodeSupervisor _supervisor;
    private readonly Func<StartConfig> _startConfigFactory;
    private readonly IRuntimeFirewall? _firewall;
    private readonly Func<string?>? _startBlockedReason;
    private RuntimeSnapshot _snapshot;
    private Task? _disposeTask;

    public RuntimeController(
        INodeSupervisor supervisor,
        Func<StartConfig> startConfigFactory,
        IRuntimeFirewall? firewall = null,
        Func<string?>? startBlockedReason = null)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _startConfigFactory = startConfigFactory ?? throw new ArgumentNullException(nameof(startConfigFactory));
        _firewall = firewall;
        _startBlockedReason = startBlockedReason;
        _snapshot = supervisor.Snapshot;
    }

    public RuntimeSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public async ValueTask<IAsyncDisposable> AcquireStoppedMaintenanceLeaseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Snapshot.State is not (DesktopRuntimeState.Stopped or DesktopRuntimeState.CoreSetupRequired) || Snapshot.Pid is not null)
                throw new InvalidOperationException("恢复前请停止核心服务，当前状态不允许修改运行配置。");
            return new MaintenanceLease(_gate);
        }
        catch { _gate.Release(); throw; }
    }

    private sealed class MaintenanceLease(SemaphoreSlim gate) : IAsyncDisposable
    {
        private int _disposed;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) gate.Release();
            return ValueTask.CompletedTask;
        }
    }

    public event EventHandler<RuntimeSnapshot>? SnapshotChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Snapshot.State is not (DesktopRuntimeState.Stopped or DesktopRuntimeState.Failed or DesktopRuntimeState.CoreSetupRequired))
            {
                return;
            }

            if (_startBlockedReason?.Invoke() is { } blocked)
            {
                Publish(Snapshot with { State = DesktopRuntimeState.Failed, FailureReason = blocked });
                return;
            }

            var wasFailed = Snapshot.State == DesktopRuntimeState.Failed;
            Publish(new RuntimeSnapshot(DesktopRuntimeState.Preparing));
            try
            {
                var config = _startConfigFactory();
                if (wasFailed && _supervisor.Snapshot.Pid is not null)
                {
                    var cleanup = await _supervisor.StopAsync("start-retry", cancellationToken).ConfigureAwait(false);
                    if (cleanup.State != DesktopRuntimeState.Stopped)
                    {
                        Publish(cleanup);
                        return;
                    }
                }

                var adoption = await _supervisor.AdoptAsync(config, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (adoption.Succeeded)
                {
                    Publish(adoption.Snapshot);
                    return;
                }

                var coreEntry = Path.Combine(config.ScriptDir, $"danmu_api_{config.Variant}", "worker.js");
                if (!File.Exists(coreEntry))
                {
                    Publish(new RuntimeSnapshot(
                        DesktopRuntimeState.CoreSetupRequired,
                        Port: config.Port,
                        FailureReason: $"核心尚未准备: {coreEntry}"));
                    return;
                }

                if (_firewall is not null && OperatingSystem.IsWindows())
                {
                    var firewall = await _firewall.EnsureInboundRuleAsync(config.NodeExe, cancellationToken).ConfigureAwait(false);
                    if (!firewall.Succeeded)
                    {
                        Publish(new RuntimeSnapshot(
                            DesktopRuntimeState.Failed,
                            Port: config.Port,
                            FailureReason: $"Windows 网络授权未完成：{firewall.Diagnostic}"));
                        return;
                    }
                }

                Publish(await _supervisor.StartAsync(config, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                var supervisorSnapshot = _supervisor.Snapshot;
                Publish(supervisorSnapshot.State == DesktopRuntimeState.Failed
                    ? supervisorSnapshot
                    : new RuntimeSnapshot(
                        DesktopRuntimeState.Failed,
                        Port: supervisorSnapshot.Port,
                        Pid: supervisorSnapshot.Pid,
                        RuntimeIdentity: supervisorSnapshot.RuntimeIdentity,
                        FailureReason: "启动已取消",
                        ExitCode: supervisorSnapshot.ExitCode));
                throw;
            }
            catch (Exception error)
            {
                Publish(new RuntimeSnapshot(DesktopRuntimeState.Failed, FailureReason: $"启动编排失败: {error.Message}"));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AdoptionResult> AdoptAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Snapshot.State is not (DesktopRuntimeState.Stopped or DesktopRuntimeState.Failed or DesktopRuntimeState.CoreSetupRequired))
            {
                var result = AdoptionResult.Failure(
                    AdoptionFailureKind.InvalidState,
                    Snapshot,
                    $"当前状态 {Snapshot.State} 不允许认领已有 Node");
                Publish(result.Snapshot);
                return result;
            }

            if (_startBlockedReason?.Invoke() is { } blocked)
            {
                Publish(Snapshot with { State = DesktopRuntimeState.Failed, FailureReason = blocked });
                return AdoptionResult.Failure(AdoptionFailureKind.InvalidState, Snapshot, blocked);
            }

            var wasFailed = Snapshot.State == DesktopRuntimeState.Failed;
            Publish(new RuntimeSnapshot(DesktopRuntimeState.Preparing));
            try
            {
                var config = _startConfigFactory();
                if (wasFailed && _supervisor.Snapshot.Pid is not null)
                {
                    var cleanup = await _supervisor.StopAsync("adoption-retry", cancellationToken).ConfigureAwait(false);
                    if (cleanup.State != DesktopRuntimeState.Stopped)
                    {
                        var failedCleanup = AdoptionResult.Failure(
                            AdoptionFailureKind.InvalidState,
                            cleanup,
                            cleanup.FailureReason ?? "认领前清理失败");
                        Publish(cleanup);
                        return failedCleanup;
                    }
                }

                var result = await _supervisor.AdoptAsync(config, cancellationToken: cancellationToken).ConfigureAwait(false);
                Publish(result.Snapshot);
                return result;
            }
            catch (OperationCanceledException error)
            {
                var supervisorSnapshot = _supervisor.Snapshot;
                var failed = supervisorSnapshot.State == DesktopRuntimeState.Failed
                    ? supervisorSnapshot
                    : new RuntimeSnapshot(
                        DesktopRuntimeState.Failed,
                        Port: supervisorSnapshot.Port,
                        Pid: supervisorSnapshot.Pid,
                        RuntimeIdentity: supervisorSnapshot.RuntimeIdentity,
                        FailureReason: $"认领已取消: {error.Message}",
                        ExitCode: supervisorSnapshot.ExitCode);
                Publish(failed);
                throw new OperationCanceledException(
                    failed.FailureReason,
                    error,
                    cancellationToken);
            }
            catch (Exception error)
            {
                var failed = new RuntimeSnapshot(DesktopRuntimeState.Failed, FailureReason: $"认领编排失败: {error.Message}");
                Publish(failed);
                return AdoptionResult.Failure(AdoptionFailureKind.InvalidHealth, failed, failed.FailureReason!);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Snapshot.State == DesktopRuntimeState.Stopped && _supervisor.Snapshot.State == DesktopRuntimeState.Stopped)
            {
                return;
            }

            Publish(Snapshot with { State = DesktopRuntimeState.Stopping, FailureReason = null });
            RuntimeSnapshot result;
            try
            {
                result = await _supervisor.StopAsync("user", cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                result = new RuntimeSnapshot(DesktopRuntimeState.Failed, FailureReason: $"停止编排失败: {error.Message}");
            }

            Publish(result);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Snapshot.State is not (DesktopRuntimeState.Running or DesktopRuntimeState.Failed))
            {
                return;
            }

            if (_startBlockedReason?.Invoke() is { } blocked)
            {
                Publish(Snapshot with { State = DesktopRuntimeState.Failed, FailureReason = blocked });
                return;
            }

            if (Snapshot.State == DesktopRuntimeState.Running || _supervisor.Snapshot.Pid is not null)
            {
                Publish(Snapshot with { State = DesktopRuntimeState.Stopping, FailureReason = null });
                var stopped = await _supervisor.StopAsync("restart", cancellationToken).ConfigureAwait(false);
                if (stopped.State != DesktopRuntimeState.Stopped)
                {
                    Publish(stopped);
                    return;
                }
            }

            Publish(new RuntimeSnapshot(DesktopRuntimeState.Preparing));
            var config = _startConfigFactory();
            var adoption = await _supervisor.AdoptAsync(config, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (adoption.Succeeded)
            {
                Publish(adoption.Snapshot);
                return;
            }

            var coreEntry = Path.Combine(config.ScriptDir, $"danmu_api_{config.Variant}", "worker.js");
            if (!File.Exists(coreEntry))
            {
                Publish(new RuntimeSnapshot(
                    DesktopRuntimeState.CoreSetupRequired,
                    Port: config.Port,
                    FailureReason: $"核心尚未准备: {coreEntry}"));
                return;
            }

            if (_firewall is not null && OperatingSystem.IsWindows())
            {
                var firewall = await _firewall.EnsureInboundRuleAsync(config.NodeExe, cancellationToken).ConfigureAwait(false);
                if (!firewall.Succeeded)
                {
                    Publish(new RuntimeSnapshot(
                        DesktopRuntimeState.Failed,
                        Port: config.Port,
                        FailureReason: $"Windows 网络授权未完成：{firewall.Diagnostic}"));
                    return;
                }
            }

            Publish(await _supervisor.StartAsync(config, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            Publish(new RuntimeSnapshot(DesktopRuntimeState.Failed, FailureReason: $"重启编排失败: {error.Message}"));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Snapshot.State == DesktopRuntimeState.Stopped &&
                _supervisor.Snapshot.State == DesktopRuntimeState.Stopped &&
                _supervisor.Snapshot.Pid is null)
            {
                return;
            }

            Publish(Snapshot with { State = DesktopRuntimeState.Stopping, FailureReason = null });
            try
            {
                Publish(await _supervisor.ForceStopAsync("application-exit", cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                Publish(new RuntimeSnapshot(DesktopRuntimeState.Failed, FailureReason: $"应用退出清理失败: {error.Message}"));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public string? ReconcileLiveness()
    {
        if (Snapshot.State != DesktopRuntimeState.Running)
        {
            return null;
        }

        var failure = _supervisor.LivenessFailure();
        if (failure is null)
        {
            return null;
        }

        Publish(_supervisor.Snapshot);
        return failure;
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
            await ShutdownAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Dispose();
        }
    }

    private void Publish(RuntimeSnapshot snapshot)
    {
        Volatile.Write(ref _snapshot, snapshot);
        SnapshotChanged?.Invoke(this, snapshot);
    }
}
