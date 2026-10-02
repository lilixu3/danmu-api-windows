using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using DanmuApi.Core;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.App.Services;

/// <summary>
/// 「回到前台时静默检查一次更新」的接入面。核心侧由 <see cref="ICoreUpdateScheduler"/> 承担，
/// 应用侧由 ApplicationUpdateViewModel 承担：两边各自负责冷却与「正在忙就让路」，
/// 这里只负责在正确的时机敲门，不重复实现节流。
/// </summary>
public interface IForegroundUpdateCheck
{
    Task CheckOnForegroundAsync();
}

public sealed class AppLifecycleCoordinator
{
    private readonly IRuntimeController _runtimeController;
    private readonly ISettingsStore _settingsStore;
    private readonly IUiDialogService _dialogService;
    private readonly IAppDiagnostics _diagnostics;
    private readonly ICoreUpdateScheduler _updateScheduler;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly Func<Window?> _windowProvider;
    private readonly IFrpTunnelService _frpTunnel;
    private readonly object _exitSync = new();
    private Task<bool>? _exitTask;
    private int _closeHandling;
    private int _exitRequested;
    private volatile bool _allowWindowClose;

    public AppLifecycleCoordinator(
        IRuntimeController runtimeController,
        ISettingsStore settingsStore,
        IUiDialogService dialogService,
        IAppDiagnostics diagnostics,
        ICoreUpdateScheduler updateScheduler,
        IClassicDesktopStyleApplicationLifetime desktop,
        Func<Window?> windowProvider,
        IFrpTunnelService frpTunnel)
    {
        _runtimeController = runtimeController ?? throw new ArgumentNullException(nameof(runtimeController));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _updateScheduler = updateScheduler ?? throw new ArgumentNullException(nameof(updateScheduler));
        _desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
        _windowProvider = windowProvider ?? throw new ArgumentNullException(nameof(windowProvider));
        _frpTunnel = frpTunnel ?? throw new ArgumentNullException(nameof(frpTunnel));
    }

    public bool IsExitRequested => Volatile.Read(ref _exitRequested) != 0;

    /// <summary>应用自身更新的检查入口；在 App 组装完成后挂上（构造期互相引用不成环，但顺序上拿不到）。</summary>
    public IForegroundUpdateCheck? ApplicationUpdates { get; set; }

    public async Task HandleMainWindowClosingAsync(Window window, WindowClosingEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(args);

        if (_allowWindowClose) return;

        args.Cancel = true;
        if (IsExitRequested)
        {
            await ExitApplicationAsync().ConfigureAwait(true);
            return;
        }
        if (Interlocked.Exchange(ref _closeHandling, 1) != 0)
        {
            return;
        }

        try
        {
            var action = ReadCloseAction();
            switch (action)
            {
                case CloseAction.Exit:
                    await ExitApplicationAsync().ConfigureAwait(true);
                    break;
                case CloseAction.Tray:
                    HideToTray(window);
                    break;
                case CloseAction.Ask:
                    var decision = await _dialogService.AskCloseActionAsync().ConfigureAwait(true);
                    if (decision is null)
                    {
                        return;
                    }

                    if (decision.RememberChoice)
                    {
                        _settingsStore.Write(new Dictionary<string, string?>
                        {
                            ["close_action"] = CloseActionParser.ToStorageValue(decision.Action),
                        });
                    }

                    if (decision.Action == CloseAction.Exit)
                    {
                        await ExitApplicationAsync().ConfigureAwait(true);
                    }
                    else
                    {
                        HideToTray(window);
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
        catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _diagnostics.Record("处理主窗口关闭请求失败", error);
        }
        finally
        {
            Volatile.Write(ref _closeHandling, 0);
        }
    }

    public Task ExitApplicationAsync() => ExitApplicationCoreAsync();

    public Task<bool> TryExitApplicationAsync() => ExitApplicationCoreAsync();

    public Task<bool> TryExitForUpdateAsync() => ExitApplicationCoreAsync();

    private Task<bool> ExitApplicationCoreAsync()
    {
        lock (_exitSync)
        {
            if (_exitTask is { IsCompleted: false } || _exitTask is { IsCompletedSuccessfully: true } && _exitTask.Result)
                return _exitTask;
            Volatile.Write(ref _exitRequested, 1);
            _exitTask = ExecuteExitAsync();
            return _exitTask;
        }
    }

    private async Task<bool> ExecuteExitAsync()
    {
        // 各入口共享同一清理结果。窗口在真正通过门控之前仍拒绝关闭，实例锁不能提前释放。
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            // 两个 Shutdown 调用在首个 await 前同步暂停新启动，并排空各自进行中的工作。
            var runtimeShutdown = InvokeShutdownAsync(() => _runtimeController.ShutdownAsync());
            var tunnelShutdown = InvokeTunnelShutdownAsync();
            var pauseUpdates = InvokeShutdownAsync(() => _updateScheduler.PauseForApplicationUpdateAsync(timeout.Token));
            await Task.WhenAll(runtimeShutdown, tunnelShutdown, pauseUpdates).ConfigureAwait(true);
            var snapshot = _runtimeController.Snapshot;
            if (snapshot.State != DesktopRuntimeState.Stopped || snapshot.Pid is not null || _runtimeController.HasOwnedProcess)
                throw new InvalidOperationException(
                    $"应用退出时运行时未确认停止: {snapshot.State}; {snapshot.FailureReason ?? "仍有受管进程"}");
            var tunnelResult = await tunnelShutdown.ConfigureAwait(true);
            if (!tunnelResult.Succeeded || _frpTunnel.Snapshot.State != DanmuApi.Core.Frp.FrpTunnelState.Stopped
                || _frpTunnel.HasOwnedProcess)
                throw new InvalidOperationException($"应用退出时穿透未确认停止：{tunnelResult.Message}");
            _allowWindowClose = true;
            if (!_desktop.TryShutdown(0))
                throw new InvalidOperationException("桌面生命周期拒绝关闭，应用保持运行");
            return true;
        }
        catch (Exception error)
        {
            _allowWindowClose = false;
            _diagnostics.Record("退出应用失败，主窗口保持打开", error);
            try { _runtimeController.ResumeAfterFailedShutdown(); }
            catch (Exception resumeError) { _diagnostics.Record("恢复运行时操作失败", resumeError); }
            try { _frpTunnel.ResumeAfterFailedShutdown(); }
            catch (Exception resumeError) { _diagnostics.Record("恢复穿透操作失败", resumeError); }
            try { _updateScheduler.ResumeAfterApplicationUpdate(); }
            catch (Exception resumeError) { _diagnostics.Record("恢复更新调度失败", resumeError); }
            Volatile.Write(ref _exitRequested, 0);
            return false;
        }
    }

    private static async Task InvokeShutdownAsync(Func<Task> operation) => await operation().ConfigureAwait(false);
    private async Task<FrpOperationResult> InvokeTunnelShutdownAsync() => await _frpTunnel.ShutdownAsync().ConfigureAwait(false);

    public void HideToTray(Window? window = null)
    {
        var target = window ?? _windowProvider();
        if (target is null)
        {
            _diagnostics.Record("隐藏到托盘失败：主窗口尚未创建");
            return;
        }

        target.Hide();
        _updateScheduler.SetBackgroundActive(true);
    }

    public void RestoreMainWindow()
    {
        var window = _windowProvider();
        if (window is null)
        {
            _diagnostics.Record("恢复主窗口失败：主窗口尚未创建");
            return;
        }

        _updateScheduler.SetBackgroundActive(false);
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
        window.Focus();
        _ = CheckForegroundUpdatesAsync();
    }

    public void NotifyMainWindowActivated()
    {
        _updateScheduler.SetBackgroundActive(false);
        _ = CheckForegroundUpdatesAsync();
    }

    private async Task CheckForegroundUpdatesAsync()
    {
        try
        {
            await _updateScheduler.CheckForegroundAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            _diagnostics.Record("前台核心更新检查失败", error);
        }

        // 应用自身更新走同一条前台时机，但用自己的 30 分钟冷却；一条失败不影响另一条。
        // 这里故意不 ConfigureAwait(false)：VM 要改绑定的属性，续跑必须留在 UI 线程。
        try
        {
            if (ApplicationUpdates is { } updates)
            {
                await updates.CheckOnForegroundAsync();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            _diagnostics.Record("前台软件更新检查失败", error);
        }
    }

    private CloseAction ReadCloseAction()
    {
        var values = _settingsStore.Read();
        return CloseActionParser.Parse(values.TryGetValue("close_action", out var value) ? value : null);
    }
}
