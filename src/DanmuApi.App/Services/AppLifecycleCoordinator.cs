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
        Func<Window?> windowProvider)
    {
        _runtimeController = runtimeController ?? throw new ArgumentNullException(nameof(runtimeController));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _updateScheduler = updateScheduler ?? throw new ArgumentNullException(nameof(updateScheduler));
        _desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
        _windowProvider = windowProvider ?? throw new ArgumentNullException(nameof(windowProvider));
    }

    public bool IsExitRequested => Volatile.Read(ref _exitRequested) != 0;

    /// <summary>应用自身更新的检查入口；在 App 组装完成后挂上（构造期互相引用不成环，但顺序上拿不到）。</summary>
    public IForegroundUpdateCheck? ApplicationUpdates { get; set; }

    public async Task HandleMainWindowClosingAsync(Window window, WindowClosingEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(args);

        if (_allowWindowClose || IsExitRequested)
        {
            return;
        }

        args.Cancel = true;
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

    public async Task<bool> TryExitForUpdateAsync()
    {
        if (IsExitRequested) return false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await _updateScheduler.PauseForApplicationUpdateAsync(timeout.Token);
        var exited = await ExitApplicationCoreAsync();
        if (!exited) _updateScheduler.ResumeAfterApplicationUpdate();
        return exited;
    }

    private async Task<bool> ExitApplicationCoreAsync()
    {
        if (Interlocked.Exchange(ref _exitRequested, 1) != 0)
        {
            return true;
        }

        _allowWindowClose = true;
        try
        {
            await _runtimeController.ShutdownAsync().ConfigureAwait(true);
            var snapshot = _runtimeController.Snapshot;
            if (snapshot.State != DesktopRuntimeState.Stopped)
            {
                throw new InvalidOperationException(
                    $"应用退出时运行时未进入 Stopped: {snapshot.State}; {snapshot.FailureReason ?? "未提供失败原因"}");
            }

            _desktop.Shutdown(0);
            return true;
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException)
        {
            _allowWindowClose = false;
            Volatile.Write(ref _exitRequested, 0);
            _diagnostics.Record("退出应用失败，主窗口保持打开", error);
            return false;
        }
    }

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
