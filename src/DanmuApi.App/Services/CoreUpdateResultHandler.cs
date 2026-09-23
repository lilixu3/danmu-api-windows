using DanmuApi.Core;
using DanmuApi.Platform;

namespace DanmuApi.App.Services;

public interface IPendingCoreUpdateService
{
    CoreUpdateCheckResult? PendingUpdate { get; }
    bool IsApplying { get; }
    event EventHandler? StateChanged;
    Task<CoreManagementOperationResult?> ApplyPendingAsync(CancellationToken cancellationToken = default);
}

public sealed class CoreUpdateResultHandler : ICoreUpdateResultHandler, IPendingCoreUpdateService, ICoreUpdateConclusionReconciler
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly ICoreManagementService _management;
    private readonly ICoreUpdateCoordinator _coordinator;
    private readonly IGithubRoutePreferenceStore _routePreferences;
    private readonly IDesktopNotificationService _notifications;
    private readonly IAppDiagnostics _diagnostics;
    private CoreUpdateCheckResult? _pendingUpdate;
    private bool _isApplying;
    private readonly HashSet<string> _submittedDiscoveries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _notificationGate = new(1, 1);

    public CoreUpdateResultHandler(
        ICoreManagementService management,
        ICoreUpdateCoordinator coordinator,
        IGithubRoutePreferenceStore routePreferences,
        IDesktopNotificationService notifications,
        IAppDiagnostics diagnostics)
    {
        _management = management ?? throw new ArgumentNullException(nameof(management));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _routePreferences = routePreferences ?? throw new ArgumentNullException(nameof(routePreferences));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        // 托盘菜单读的是本服务这份待更新项，它必须跟着协调器的结论走：
        // 结论被作废（装完了 / 回退 / 删核心）时，这里也要同步撤掉，否则托盘会一直
        // 挂着一个"立即更新核心"——点下去只会把已经装上的版本再装一遍。
        _coordinator.ResultChanged += OnCoordinatorResultChanged;
    }

    /// <summary>
    /// 安装变更后重新对账：交给协调器按磁盘判断结论还成不成立，它会广播（可能推来 null），
    /// 本服务在 <see cref="OnCoordinatorResultChanged"/> 里同步自己的待更新项。
    /// </summary>
    public void ReconcileDiscovery(ManagedCoreVariant variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        _coordinator.ReconcileDiscovery(variant);
    }

    private void OnCoordinatorResultChanged(object? sender, CoreUpdateCheckResult? result)
    {
        var cleared = false;
        lock (_sync)
        {
            if (_pendingUpdate is null)
            {
                return;
            }

            if (result is { UpdateAvailable: true })
            {
                // 仍有更新：换成协调器那份最新的（本地 manifest 可能已经刷新）。
                _pendingUpdate = result;
                return;
            }

            // result 为 null（结论作废）或已无更新；都是同一个变体的事，直接撤掉待更新项。
            if (result is { } checkedResult && checkedResult.Variant != _pendingUpdate.Variant)
            {
                return;
            }

            _pendingUpdate = null;
            cleared = true;
        }

        if (cleared) StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public CoreUpdateCheckResult? PendingUpdate
    {
        get
        {
            lock (_sync)
            {
                return _pendingUpdate;
            }
        }
    }

    public bool IsApplying
    {
        get
        {
            lock (_sync)
            {
                return _isApplying;
            }
        }
    }

    public event EventHandler? StateChanged;

    public async Task HandleAsync(
        CoreUpdateTrigger trigger,
        CoreUpdateCheckResult result,
        CoreUpdateAction action,
        CancellationToken cancellationToken = default) =>
        _ = await HandleWithDispositionAsync(trigger, result, action, cancellationToken).ConfigureAwait(false);

    public async Task<CoreUpdateHandlingDisposition> HandleWithDispositionAsync(
        CoreUpdateTrigger trigger,
        CoreUpdateCheckResult result,
        CoreUpdateAction action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Status == CoreUpdateCheckStatus.Failed)
        {
            _diagnostics.Record($"{FormatTrigger(trigger)}核心更新检查失败：{result.Diagnostic}");
            if (result.FailureKind is GithubFailureKind.Authentication or GithubFailureKind.Forbidden or
                GithubFailureKind.RateLimited or GithubFailureKind.Protocol)
            {
                await ShowNotificationAsync(
                    DesktopNotificationKind.UpdateFailed,
                    "核心更新检查需要处理",
                    result.Diagnostic,
                    null,
                    cancellationToken).ConfigureAwait(false);
            }

            return CoreUpdateHandlingDisposition.ReleaseClaim;
        }

        if (!result.UpdateAvailable || result.Remote is null)
        {
            return CoreUpdateHandlingDisposition.ReleaseClaim;
        }

        lock (_sync)
        {
            _pendingUpdate = result;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);

        if (action == CoreUpdateAction.Automatic)
        {
            await ApplyPendingAsync(cancellationToken).ConfigureAwait(false);
            // The automatic operation ran even if its completion notification was suppressed.
            return CoreUpdateHandlingDisposition.ConsumeClaim;
        }

        await _notificationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = $"{result.Variant}:{result.Remote.Sha}";
            if (_submittedDiscoveries.Contains(key)) return CoreUpdateHandlingDisposition.ConsumeClaim;
            var notification = await ShowNotificationAsync(
                DesktopNotificationKind.UpdateDiscovered,
                "发现核心更新",
                $"{FormatVariant(result.Variant)}有新提交 {result.Remote.ShortSha}。可立即更新，或稍后从托盘菜单处理。",
                new DesktopNotificationAction("立即更新", "danmuapi://update-core"),
                cancellationToken).ConfigureAwait(false);
            if (notification.Status == DesktopNotificationStatus.Submitted) _submittedDiscoveries.Add(key);
            return notification.Status == DesktopNotificationStatus.SuppressedByPreference
                ? CoreUpdateHandlingDisposition.ReleaseClaim
                : CoreUpdateHandlingDisposition.ConsumeClaim;
        }
        finally { _notificationGate.Release(); }
    }

    public async Task<CoreManagementOperationResult?> ApplyPendingAsync(
        CancellationToken cancellationToken = default)
    {
        await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CoreUpdateCheckResult? pending;
            lock (_sync)
            {
                pending = _pendingUpdate;
                if (pending is null)
                {
                    return null;
                }

                _isApplying = true;
            }
            StateChanged?.Invoke(this, EventArgs.Empty);

            CoreManagementOperationResult result;
            try
            {
                result = await _management.ApplyUpdateAsync(
                    pending,
                    ReadProxyId(),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
            {
                result = new CoreManagementOperationResult(
                    false,
                    false,
                    false,
                    null,
                    $"应用核心更新失败：{error.Message}");
            }

            if (result.Succeeded)
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_pendingUpdate, pending))
                    {
                        _pendingUpdate = null;
                    }
                }
                await ShowNotificationAsync(
                    DesktopNotificationKind.UpdateCompleted,
                    "核心更新完成",
                    $"{FormatVariant(pending.Variant)}已更新到 {pending.Remote!.ShortSha}。",
                    null,
                    CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                if (result.RouteInvalid)
                {
                    _routePreferences.Invalidate();
                }

                _diagnostics.Record(result.Diagnostic);
                var message = result.RouteInvalid
                    ? $"{result.Diagnostic}。已选 GitHub 线路超时或不可达，请重新选择线路。"
                    : $"{result.Diagnostic}。待处理更新已保留，可从托盘重试。";
                await ShowNotificationAsync(
                    DesktopNotificationKind.UpdateFailed,
                    "核心自动更新失败",
                    message,
                    new DesktopNotificationAction("重试更新", "danmuapi://update-core"),
                    CancellationToken.None).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            lock (_sync)
            {
                _isApplying = false;
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
            _applyGate.Release();
        }
    }

    private string ReadProxyId()
    {
        var preference = _routePreferences.Read();
        return GithubProxyCatalog.GetById(preference.ProxyId).Id;
    }

    private async Task<DesktopNotificationResult> ShowNotificationAsync(
        DesktopNotificationKind kind,
        string title,
        string message,
        DesktopNotificationAction? action,
        CancellationToken cancellationToken)
    {
        var notification = await _notifications.ShowAsync(
            kind,
            title,
            message,
            action,
            cancellationToken).ConfigureAwait(false);
        if (notification.Status == DesktopNotificationStatus.Failed)
        {
            _diagnostics.Record($"核心更新通知失败：{notification.Diagnostic}");
            throw new IOException($"核心更新通知未提交：{notification.Diagnostic}");
        }
        return notification;
    }

    private static string FormatTrigger(CoreUpdateTrigger trigger) => trigger switch
    {
        CoreUpdateTrigger.Foreground => "前台",
        CoreUpdateTrigger.Background => "后台",
        CoreUpdateTrigger.Manual => "手动",
        _ => trigger.ToString(),
    };

    private static string FormatVariant(ManagedCoreVariant variant) => variant.ToLabel();
}
