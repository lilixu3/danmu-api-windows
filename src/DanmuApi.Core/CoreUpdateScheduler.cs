namespace DanmuApi.Core;

public enum CoreUpdateAction
{
    Notify,
    Automatic,
}

public enum CoreUpdateTrigger
{
    Foreground,
    Background,
    Manual,
}

public sealed record CoreUpdateScheduleOptions(
    TimeSpan ForegroundInterval,
    TimeSpan BackgroundInterval,
    bool BackgroundEnabled,
    CoreUpdateAction UpdateAction)
{
    public static CoreUpdateScheduleOptions Default { get; } = new(
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(1),
        true,
        CoreUpdateAction.Notify);

    public CoreUpdateScheduleOptions Validate()
    {
        ValidateInterval(ForegroundInterval, TimeSpan.FromMinutes(5), TimeSpan.FromDays(1), "前台检查间隔");
        ValidateInterval(BackgroundInterval, TimeSpan.FromMinutes(15), TimeSpan.FromDays(7), "后台检查间隔");
        if (!Enum.IsDefined(UpdateAction))
        {
            throw new FormatException($"核心更新动作无效：{UpdateAction}");
        }

        return this;
    }

    private static void ValidateInterval(TimeSpan value, TimeSpan minimum, TimeSpan maximum, string name)
    {
        if (value < minimum || value > maximum || value.Ticks % TimeSpan.TicksPerMinute != 0)
        {
            throw new FormatException(
                $"{name}必须是 {minimum.TotalMinutes:0} 到 {maximum.TotalMinutes:0} 之间的整分钟数");
        }
    }
}

public interface ICoreUpdatePolicyStore
{
    CoreUpdateScheduleOptions Read();
    void Write(CoreUpdateScheduleOptions options);
}

public enum CoreUpdateHandlingDisposition { ConsumeClaim, ReleaseClaim }

public interface ICoreUpdateResultHandler
{
    async Task<CoreUpdateHandlingDisposition> HandleWithDispositionAsync(
        CoreUpdateTrigger trigger,
        CoreUpdateCheckResult result,
        CoreUpdateAction action,
        CancellationToken cancellationToken = default)
    {
        await HandleAsync(trigger, result, action, cancellationToken).ConfigureAwait(false);
        return CoreUpdateHandlingDisposition.ConsumeClaim;
    }

    Task HandleAsync(
        CoreUpdateTrigger trigger,
        CoreUpdateCheckResult result,
        CoreUpdateAction action,
        CancellationToken cancellationToken = default);
}

public interface ICoreUpdateScheduler : IAsyncDisposable
{
    event EventHandler<CoreUpdateCheckResult>? CheckCompleted;
    event EventHandler<string>? DiagnosticChanged;
    void Start();
    void SetBackgroundActive(bool active);
    void NotifyPolicyChanged();
    Task PauseForApplicationUpdateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    void ResumeAfterApplicationUpdate() { }
    Task<CoreUpdateCheckResult> CheckForegroundAsync(CancellationToken cancellationToken = default);
    Task<CoreUpdateCheckResult?> CheckBackgroundAsync(CancellationToken cancellationToken = default);
    Task<CoreUpdateCheckResult> CheckManualAsync(
        ManagedCoreVariant variant,
        CancellationToken cancellationToken = default);
}

public sealed class CoreUpdateScheduler : ICoreUpdateScheduler
{
    private readonly object _sync = new();
    private readonly ICoreUpdateCoordinator _coordinator;
    private readonly ICoreUpdatePolicyStore _policyStore;
    private readonly ICoreUpdateResultHandler _resultHandler;
    private readonly Func<ManagedCoreVariant> _variantProvider;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly SemaphoreSlim _policyChanged = new(0, 1);
    private readonly Dictionary<ManagedCoreVariant, string> _handledUpdateShas = [];
    private Task? _backgroundLoop;
    private bool _backgroundActive;
    private bool _disposed;
    private volatile bool _applicationUpdatePaused;
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);

    public async Task PauseForApplicationUpdateAsync(CancellationToken cancellationToken = default)
    {
        await _dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { _applicationUpdatePaused = true; }
        finally { _dispatchGate.Release(); }
    }

    public void ResumeAfterApplicationUpdate() => _applicationUpdatePaused = false;

    public CoreUpdateScheduler(
        ICoreUpdateCoordinator coordinator,
        ICoreUpdatePolicyStore policyStore,
        ICoreUpdateResultHandler resultHandler,
        Func<ManagedCoreVariant> variantProvider,
        TimeProvider? timeProvider = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _policyStore = policyStore ?? throw new ArgumentNullException(nameof(policyStore));
        _resultHandler = resultHandler ?? throw new ArgumentNullException(nameof(resultHandler));
        _variantProvider = variantProvider ?? throw new ArgumentNullException(nameof(variantProvider));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler<CoreUpdateCheckResult>? CheckCompleted;
    public event EventHandler<string>? DiagnosticChanged;

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_backgroundLoop is null || _backgroundLoop.IsCompleted)
            {
                _backgroundLoop = BackgroundLoopAsync(_disposeCts.Token);
            }
        }
    }

    public void SetBackgroundActive(bool active)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _backgroundActive = active;
        }

        SignalScheduleChanged();
    }

    public void NotifyPolicyChanged()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        SignalScheduleChanged();
        Start();
    }

    private void SignalScheduleChanged()
    {
        if (_policyChanged.CurrentCount == 0)
        {
            _policyChanged.Release();
        }
    }

    public Task<CoreUpdateCheckResult> CheckForegroundAsync(CancellationToken cancellationToken = default)
    {
        var options = _policyStore.Read().Validate();
        return ExecuteAsync(
            CoreUpdateTrigger.Foreground,
            _variantProvider(),
            force: false,
            options.ForegroundInterval,
            options,
            cancellationToken);
    }

    public async Task<CoreUpdateCheckResult?> CheckBackgroundAsync(CancellationToken cancellationToken = default)
    {
        var options = _policyStore.Read().Validate();
        if (!options.BackgroundEnabled)
        {
            return null;
        }

        return await ExecuteAsync(
            CoreUpdateTrigger.Background,
            _variantProvider(),
            force: false,
            options.BackgroundInterval,
            options,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<CoreUpdateCheckResult> CheckManualAsync(
        ManagedCoreVariant variant,
        CancellationToken cancellationToken = default)
    {
        var options = _policyStore.Read().Validate();
        return ExecuteAsync(
            CoreUpdateTrigger.Manual,
            variant,
            force: true,
            options.ForegroundInterval,
            options,
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Task? backgroundLoop;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            backgroundLoop = _backgroundLoop;
        }

        _disposeCts.Cancel();
        if (backgroundLoop is not null)
        {
            try
            {
                await backgroundLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
            {
            }
        }

        _policyChanged.Dispose();
        _disposeCts.Dispose();
    }

    private async Task<CoreUpdateCheckResult> ExecuteAsync(
        CoreUpdateTrigger trigger,
        ManagedCoreVariant variant,
        bool force,
        TimeSpan interval,
        CoreUpdateScheduleOptions options,
        CancellationToken cancellationToken)
    {
        var result = await _coordinator.CheckAsync(
            variant,
            force,
            interval,
            cancellationToken).ConfigureAwait(false);
        CheckCompleted?.Invoke(this, result);

        var handleFailure = trigger == CoreUpdateTrigger.Background &&
                            result.Status == CoreUpdateCheckStatus.Failed;
        var claimedUpdate = result.UpdateAvailable && TryClaimUpdate(result);
        if (!handleFailure && !claimedUpdate)
        {
            return result;
        }

        await _dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_applicationUpdatePaused)
            {
                if (claimedUpdate) ReleaseUpdateClaim(result);
                return result;
            }
            var disposition = await _resultHandler.HandleWithDispositionAsync(
                trigger,
                result,
                options.UpdateAction,
                cancellationToken).ConfigureAwait(false);
            if (claimedUpdate && disposition == CoreUpdateHandlingDisposition.ReleaseClaim)
                ReleaseUpdateClaim(result);
        }
        catch
        {
            if (claimedUpdate)
            {
                ReleaseUpdateClaim(result);
            }

            throw;
        }
        finally { _dispatchGate.Release(); }

        return result;
    }

    private async Task BackgroundLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CoreUpdateScheduleOptions options;
            try
            {
                options = _policyStore.Read().Validate();
            }
            catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException)
            {
                DiagnosticChanged?.Invoke(this, $"读取核心更新调度设置失败：{error.Message}");
                return;
            }

            var delay = IsBackgroundActive()
                ? options.BackgroundInterval
                : options.ForegroundInterval;
            if (await WaitForPolicyChangeOrDelayAsync(delay, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            if (IsBackgroundActive() && !options.BackgroundEnabled)
            {
                continue;
            }

            try
            {
                if (IsBackgroundActive())
                    await CheckBackgroundAsync(cancellationToken).ConfigureAwait(false);
                else
                    await CheckForegroundAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                DiagnosticChanged?.Invoke(this, $"后台核心更新调度已停止：{error.Message}");
                return;
            }
        }
    }

    private bool IsBackgroundActive()
    {
        lock (_sync)
        {
            return _backgroundActive;
        }
    }

    private async Task<bool> WaitForPolicyChangeOrDelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        using var changedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var changed = _policyChanged.WaitAsync(changedCts.Token);
        var elapsed = Task.Delay(delay, _timeProvider, delayCts.Token);
        var completed = await Task.WhenAny(changed, elapsed).ConfigureAwait(false);
        if (completed == changed)
        {
            delayCts.Cancel();
            await changed.ConfigureAwait(false);
            await ObserveCancellationAsync(elapsed, delayCts.Token).ConfigureAwait(false);
            return true;
        }

        changedCts.Cancel();
        await elapsed.ConfigureAwait(false);
        await ObserveCancellationAsync(changed, changedCts.Token).ConfigureAwait(false);
        return false;
    }

    private static async Task ObserveCancellationAsync(Task task, CancellationToken cancellationToken)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private bool TryClaimUpdate(CoreUpdateCheckResult result)
    {
        var sha = result.Remote?.Sha
            ?? throw new InvalidOperationException("更新检查标记有可用更新，但没有远端提交 SHA");
        lock (_sync)
        {
            if (_handledUpdateShas.TryGetValue(result.Variant, out var handled) &&
                string.Equals(handled, sha, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _handledUpdateShas[result.Variant] = sha;
            return true;
        }
    }

    private void ReleaseUpdateClaim(CoreUpdateCheckResult result)
    {
        var sha = result.Remote?.Sha;
        if (sha is null)
        {
            return;
        }

        lock (_sync)
        {
            if (_handledUpdateShas.TryGetValue(result.Variant, out var handled) &&
                string.Equals(handled, sha, StringComparison.OrdinalIgnoreCase))
            {
                _handledUpdateShas.Remove(result.Variant);
            }
        }
    }
}
