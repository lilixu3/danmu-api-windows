using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using DanmuApi.Core;
using DanmuApi.Runtime;

namespace DanmuApi.App.Services;

public sealed class TrayService : IDisposable
{
    private readonly IRuntimeController _runtimeController;
    private readonly IPendingCoreUpdateService _pendingCoreUpdate;
    private readonly IAppDiagnostics _diagnostics;
    private readonly Action _openConsole;
    private readonly Action _openCoreConfiguration;
    private readonly Action _openSettings;
    private readonly Func<Task> _exitApplication;
    private readonly TrayIcon _trayIcon;
    private readonly TrayIcons _trayIcons;
    private readonly NativeMenu _menu;
    private readonly NativeMenuItem _statusItem;
    private readonly NativeMenuItem _startItem;
    private readonly NativeMenuItem _stopItem;
    private readonly NativeMenuItem _restartItem;
    private readonly NativeMenuItem _updateStatusItem;
    private readonly NativeMenuItem _applyUpdateItem;
    private bool _disposed;

    public TrayService(
        IRuntimeController runtimeController,
        IPendingCoreUpdateService pendingCoreUpdate,
        IAppDiagnostics diagnostics,
        Action openConsole,
        Action openCoreConfiguration,
        Action openSettings,
        Func<Task> exitApplication)
    {
        _runtimeController = runtimeController ?? throw new ArgumentNullException(nameof(runtimeController));
        _pendingCoreUpdate = pendingCoreUpdate ?? throw new ArgumentNullException(nameof(pendingCoreUpdate));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _openConsole = openConsole ?? throw new ArgumentNullException(nameof(openConsole));
        _openCoreConfiguration = openCoreConfiguration ?? throw new ArgumentNullException(nameof(openCoreConfiguration));
        _openSettings = openSettings ?? throw new ArgumentNullException(nameof(openSettings));
        _exitApplication = exitApplication ?? throw new ArgumentNullException(nameof(exitApplication));

        _statusItem = new NativeMenuItem { IsEnabled = false };
        _startItem = CreateActionItem("启动服务", () => _runtimeController.StartAsync());
        _stopItem = CreateActionItem("停止服务", () => _runtimeController.StopAsync());
        _restartItem = CreateActionItem("重启服务", () => _runtimeController.RestartAsync());
        _updateStatusItem = new NativeMenuItem { IsEnabled = false, IsVisible = false };
        _applyUpdateItem = CreateActionItem("立即更新核心", ApplyPendingUpdateAsync);
        _applyUpdateItem.IsVisible = false;
        _menu = new NativeMenu();
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new NativeMenuItemSeparator());
        _menu.Items.Add(_startItem);
        _menu.Items.Add(_stopItem);
        _menu.Items.Add(_restartItem);
        _menu.Items.Add(new NativeMenuItemSeparator());
        _menu.Items.Add(_updateStatusItem);
        _menu.Items.Add(_applyUpdateItem);
        _menu.Items.Add(new NativeMenuItemSeparator());
        _menu.Items.Add(CreateActionItem("打开控制台", () =>
        {
            _openConsole();
            return Task.CompletedTask;
        }));
        _menu.Items.Add(CreateActionItem("打开核心配置", () =>
        {
            _openCoreConfiguration();
            return Task.CompletedTask;
        }));
        _menu.Items.Add(CreateActionItem("打开设置", () =>
        {
            _openSettings();
            return Task.CompletedTask;
        }));
        _menu.Items.Add(new NativeMenuItemSeparator());
        _menu.Items.Add(CreateActionItem("退出", _exitApplication));
        _menu.NeedsUpdate += (_, _) => UpdateMenu();

        _trayIcon = new TrayIcon
        {
            Icon = LoadIcon(),
            Menu = _menu,
            ToolTipText = "弹幕 API",
            IsVisible = true,
        };
        _trayIcon.Clicked += OnTrayClicked;
        _trayIcons = new TrayIcons();
        _trayIcons.Add(_trayIcon);
        _runtimeController.SnapshotChanged += OnSnapshotChanged;
        _pendingCoreUpdate.StateChanged += OnPendingUpdateChanged;

        var application = Application.Current
            ?? throw new InvalidOperationException("Avalonia Application 尚未初始化，无法创建托盘");
        TrayIcon.SetIcons(application, _trayIcons);
        UpdateMenu();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runtimeController.SnapshotChanged -= OnSnapshotChanged;
        _pendingCoreUpdate.StateChanged -= OnPendingUpdateChanged;
        _trayIcon.Clicked -= OnTrayClicked;
        _trayIcon.Dispose();
        if (Application.Current is { } application)
        {
            TrayIcon.SetIcons(application, new TrayIcons());
        }
    }

    private NativeMenuItem CreateActionItem(string header, Func<Task> action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => _ = RunActionAsync(header, action);
        return item;
    }

    private async Task RunActionAsync(string actionName, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
            UpdateMenu();
        }
        catch (Exception error)
        {
            _diagnostics.Record($"托盘动作失败: {actionName}", error);
            UpdateMenu();
        }
    }

    private async Task ApplyPendingUpdateAsync()
    {
        var result = await _pendingCoreUpdate.ApplyPendingAsync().ConfigureAwait(true);
        if (result is { Succeeded: false })
        {
            _diagnostics.Record(result.Diagnostic);
        }
    }

    private void OnPendingUpdateChanged(object? sender, EventArgs args)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            UpdateMenu();
        }
        else
        {
            Dispatcher.UIThread.Post(UpdateMenu);
        }
    }

    private void OnTrayClicked(object? sender, EventArgs args)
    {
        try
        {
            _openConsole();
        }
        catch (Exception error)
        {
            _diagnostics.Record("恢复主窗口失败", error);
        }
    }

    private void OnSnapshotChanged(object? sender, RuntimeSnapshot snapshot)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            UpdateMenu();
        }
        else
        {
            Dispatcher.UIThread.Post(UpdateMenu);
        }
    }

    private void UpdateMenu()
    {
        if (_disposed)
        {
            return;
        }

        var snapshot = _runtimeController.Snapshot;
        var reason = string.IsNullOrWhiteSpace(snapshot.FailureReason) ? string.Empty : $"：{snapshot.FailureReason}";
        _statusItem.Header = $"状态：{FormatState(snapshot.State)}{reason}";
        _startItem.IsEnabled = snapshot.State is DesktopRuntimeState.Stopped or DesktopRuntimeState.Failed;
        _stopItem.IsEnabled = snapshot.State is DesktopRuntimeState.Running or DesktopRuntimeState.Starting or DesktopRuntimeState.Preparing;
        _restartItem.IsEnabled = snapshot.State is DesktopRuntimeState.Running or DesktopRuntimeState.Failed;
        var pending = _pendingCoreUpdate.PendingUpdate;
        var updateVisible = pending is not null;
        _updateStatusItem.IsVisible = updateVisible;
        _applyUpdateItem.IsVisible = updateVisible;
        if (pending is not null)
        {
            _updateStatusItem.Header = $"核心更新：{FormatVariant(pending.Variant)} {pending.Remote?.ShortSha ?? "未知版本"}";
            _applyUpdateItem.Header = _pendingCoreUpdate.IsApplying ? "正在更新核心…" : "立即更新核心";
            _applyUpdateItem.IsEnabled = !_pendingCoreUpdate.IsApplying;
        }
        _trayIcon.ToolTipText = $"弹幕 API · {FormatState(snapshot.State)}";
    }

    private static string FormatVariant(ManagedCoreVariant variant) => variant switch
    {
        ManagedCoreVariant.Stable => "稳定核心",
        ManagedCoreVariant.Custom => "自定义核心",
        _ => variant.ToString(),
    };

    private static string FormatState(DesktopRuntimeState state) => state switch
    {
        DesktopRuntimeState.Stopped => "服务已停止",
        DesktopRuntimeState.Preparing => "正在准备运行时",
        DesktopRuntimeState.Starting => "正在启动服务",
        DesktopRuntimeState.Running => "服务运行中",
        DesktopRuntimeState.Stopping => "正在停止服务",
        DesktopRuntimeState.CoreSetupRequired => "等待准备核心",
        DesktopRuntimeState.Failed => "启动失败",
        _ => state.ToString(),
    };

    private static WindowIcon LoadIcon()
    {
        var uri = new Uri("avares://DanmuApi.App/Assets/danmuapi.ico");
        using var stream = AssetLoader.Open(uri);
        return new WindowIcon(stream);
    }
}
