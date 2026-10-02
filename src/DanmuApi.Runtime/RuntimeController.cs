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
    private readonly object _shutdownSync = new();
    private Task? _shutdownTask;
    private volatile bool _shutdownRequested;
    private long _startRequestEpoch;
    private bool _disposed;

    public bool IsShutdownRequested => _shutdownRequested;
    public bool HasOwnedProcess => _supervisor.HasOwnedProcess;

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
        var requestEpoch = CaptureStartRequestEpoch();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateStartRequestEpoch(requestEpoch);
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

                var coreEntry = CoreEntry(config);
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
                PublishCancellation("启动已取消");
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
        var requestEpoch = CaptureStartRequestEpoch();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateStartRequestEpoch(requestEpoch);
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
                PublishCancellation("停止请求被取消，Node 子进程状态未知。可以再点一次停止，或直接点启动（启动前会先清理残留进程）。");
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

    public async Task RefreshCoreSetupRequiredAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Snapshot.State != DesktopRuntimeState.CoreSetupRequired)
            {
                return;
            }

            // The core is back on disk, so the parked state and its reason no longer describe reality.
            var config = _startConfigFactory();
            if (!File.Exists(CoreEntry(config)))
            {
                return;
            }

            Publish(new RuntimeSnapshot(DesktopRuntimeState.Stopped, Port: config.Port));
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string CoreEntry(StartConfig config) =>
        Path.Combine(config.ScriptDir, $"danmu_api_{config.Variant}", "worker.js");

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        var requestEpoch = CaptureStartRequestEpoch();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateStartRequestEpoch(requestEpoch);
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

            var coreEntry = CoreEntry(config);
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
        catch (InvalidOperationException) when (IsStartRequestInvalid(requestEpoch))
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

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        lock (_shutdownSync)
        {
            if (!_shutdownRequested) _startRequestEpoch++;
            _shutdownRequested = true;
            if (_shutdownTask is null || _shutdownTask.IsCompleted
                && (Snapshot.State != DesktopRuntimeState.Stopped || Snapshot.Pid is not null || !_shutdownTask.IsCompletedSuccessfully))
                _shutdownTask = ShutdownCoreAsync(cancellationToken);
            return _shutdownTask;
        }
    }

    public void ResumeAfterFailedShutdown()
    {
        lock (_shutdownSync)
        {
            if (_shutdownTask is { IsCompleted: false })
                throw new InvalidOperationException("运行时退出清理尚未完成，不能恢复启动");
            if (_disposed) throw new ObjectDisposedException(nameof(RuntimeController));
            _startRequestEpoch++;
            _shutdownRequested = false;
            _shutdownTask = null;
        }
    }

    // 请求抵达时先决定是否允许排队；仅在拿锁后看 bool 会把暂停期间的旧请求当成恢复后的新请求。
    private long CaptureStartRequestEpoch()
    {
        lock (_shutdownSync)
        {
            if (_shutdownRequested) throw new InvalidOperationException("应用退出清理期间拒绝启动、认领或重启运行时");
            if (_disposed) throw new ObjectDisposedException(nameof(RuntimeController));
            return _startRequestEpoch;
        }
    }

    private bool IsStartRequestInvalid(long requestEpoch)
    {
        lock (_shutdownSync) return _shutdownRequested || requestEpoch != _startRequestEpoch;
    }

    private void ValidateStartRequestEpoch(long requestEpoch)
    {
        if (IsStartRequestInvalid(requestEpoch))
            throw new InvalidOperationException("运行时启动请求已被应用退出屏障失效，请在恢复后重新发起操作");
    }

    private async Task ShutdownCoreAsync(CancellationToken cancellationToken)
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
                PublishCancellation("应用退出清理被取消，Node 子进程状态未知。");
                throw;
            }
            catch (Exception error)
            {
                Publish(_supervisor.Snapshot with { State = DesktopRuntimeState.Failed, FailureReason = $"应用退出清理失败: {error.Message}" });
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
            if (_disposed) return ValueTask.CompletedTask;
            if (_disposeTask is null || _disposeTask.IsCompleted && !_disposeTask.IsCompletedSuccessfully)
                _disposeTask = DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        if (Snapshot.State != DesktopRuntimeState.Stopped || Snapshot.Pid is not null
            || _supervisor.Snapshot.State != DesktopRuntimeState.Stopped || HasOwnedProcess)
            throw new InvalidOperationException(Snapshot.FailureReason ?? "释放运行时失败：仍有受管进程待清理");
        _disposed = true;
        _gate.Dispose();
    }

    /// <summary>
    /// 启停被取消时的落地：绝不允许状态停在中间态——CanStart 与 CanStop 会同时为 false，
    /// 用户只能重启应用才能再操作服务。这里统一落成 Failed 并保留端口/PID/身份，
    /// 下一次启动的 start-retry 分支会先把残留进程收掉。
    /// </summary>
    private void PublishCancellation(string reason)
    {
        var supervisorSnapshot = _supervisor.Snapshot;
        Publish(supervisorSnapshot.State == DesktopRuntimeState.Failed
            ? supervisorSnapshot
            : new RuntimeSnapshot(
                DesktopRuntimeState.Failed,
                Port: supervisorSnapshot.Port,
                Pid: supervisorSnapshot.Pid,
                RuntimeIdentity: supervisorSnapshot.RuntimeIdentity,
                FailureReason: reason,
                ExitCode: supervisorSnapshot.ExitCode));
    }

    private void Publish(RuntimeSnapshot snapshot)
    {
        Volatile.Write(ref _snapshot, snapshot);
        SnapshotChanged?.Invoke(this, snapshot);
    }
}
