using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DanmuApi.App.Services;
using DanmuApi.Platform;

namespace DanmuApi.App.ViewModels;

/// <summary>
/// 内网穿透的**日志**页：只放 frp 进程输出与"去目录看更多"的入口。
/// 排障才需要它，所以默认不加载，进入页签时刷一次。
/// </summary>
public sealed partial class FrpTunnelLogViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IFrpTunnelService _tunnel;
    private readonly IUiDialogService _dialogService;
    private readonly IAppDiagnostics _diagnostics;
    private readonly AppPaths _paths;

    [ObservableProperty] private string _logText = "（尚未读取日志）";
    [ObservableProperty] private string _logPathText = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public FrpTunnelLogViewModel(
        IFrpTunnelService tunnel,
        IUiDialogService dialogService,
        IAppDiagnostics diagnostics,
        AppPaths paths)
    {
        _tunnel = tunnel ?? throw new ArgumentNullException(nameof(tunnel));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        LogPathText = Path.Combine(_paths.FrpLogsDirectory, "frpc-stdout.log");
        StatusText = "日志按进程覆盖写入：每次启动穿透会重写这两个文件。";
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var text = await Task.Run(() => _tunnel.ReadLogTail(120)).ConfigureAwait(true);
            LogText = text.Length == 0 ? "（日志为空）" : text;
        }
        catch (Exception error)
        {
            _diagnostics.Record("读取穿透日志失败", error);
            LogText = $"读取日志失败：{error.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OpenLogDirectoryAsync()
    {
        try
        {
            await _dialogService.OpenDirectoryAsync(_paths.FrpLogsDirectory).ConfigureAwait(true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText = $"打开日志目录失败：{error.Message}";
            _diagnostics.Record("打开穿透日志目录失败", error);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
