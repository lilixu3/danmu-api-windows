using CommunityToolkit.Mvvm.ComponentModel;
using DanmuApi.App.Services;
using DanmuApi.Platform;

namespace DanmuApi.App.ViewModels;

public enum FrpTunnelTab
{
    Monitor,
    Configuration,
    Logs,
}

public sealed record FrpTunnelTabOption(FrpTunnelTab Value, string Title, string Description)
{
    public override string ToString() => Title;
}

/// <summary>
/// 工具页「内网穿透」的宿主：把**监控**、**配置**、**日志**拆成三个页签。
///
/// 之所以拆：这三件事的信息密度和操作节奏完全不同——监控要一眼看到"通没通、地址是什么"，
/// 配置是一次性填完就很少再看的一大堆字段，日志是排障时才翻的流水。
/// 挤在一页里会让"状态"和"表单"争夺注意力，这也是用户报"界面太乱、不能把配置和日志混在一起"的根源。
/// 三个子页只在首次进入时各建一次，切页签不会丢掉表单里没保存的输入。
/// </summary>
public sealed partial class FrpTunnelPageViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly Func<FrpTunnelMonitorViewModel> _monitorFactory;
    private readonly Func<FrpTunnelConfigViewModel> _configFactory;
    private readonly Func<FrpTunnelLogViewModel> _logFactory;
    private FrpTunnelMonitorViewModel? _monitor;
    private FrpTunnelConfigViewModel? _config;
    private FrpTunnelLogViewModel? _logs;

    [ObservableProperty]
    private FrpTunnelTabOption _selectedTabOption = null!;

    [ObservableProperty]
    private object _currentTab = null!;

    public FrpTunnelPageViewModel(
        Func<FrpTunnelMonitorViewModel> monitorFactory,
        Func<FrpTunnelConfigViewModel> configFactory,
        Func<FrpTunnelLogViewModel> logFactory)
    {
        _monitorFactory = monitorFactory ?? throw new ArgumentNullException(nameof(monitorFactory));
        _configFactory = configFactory ?? throw new ArgumentNullException(nameof(configFactory));
        _logFactory = logFactory ?? throw new ArgumentNullException(nameof(logFactory));
        TabOptions =
        [
            new(FrpTunnelTab.Monitor, "监控", "穿透状态、外网地址与启停操作"),
            new(FrpTunnelTab.Configuration, "配置", "可视化 / 原生 JSON 两种独立配置方式"),
            new(FrpTunnelTab.Logs, "日志", "frp 进程输出，用于排障"),
        ];
        _selectedTabOption = TabOptions[0];
        _monitor = _monitorFactory();
        _currentTab = _monitor;
    }

    public IReadOnlyList<FrpTunnelTabOption> TabOptions { get; }

    /// <summary>供概览页等外部入口直接落到某个页签（例如"去配置穿透"）。</summary>
    public bool SelectTab(FrpTunnelTab tab)
    {
        var option = TabOptions.FirstOrDefault(candidate => candidate.Value == tab);
        if (option is null)
        {
            return false;
        }

        SelectedTabOption = option;
        return true;
    }

    partial void OnSelectedTabOptionChanged(FrpTunnelTabOption value)
    {
        CurrentTab = value.Value switch
        {
            FrpTunnelTab.Monitor => _monitor ??= _monitorFactory(),
            FrpTunnelTab.Configuration => _config ??= _configFactory(),
            FrpTunnelTab.Logs => _logs ??= _logFactory(),
            _ => throw new InvalidOperationException("穿透页签未注册"),
        };

        // 日志页签进来就刷新一次：排障时最不想干的事就是再点一次「刷新」。
        if (CurrentTab is FrpTunnelLogViewModel logs)
        {
            _ = logs.RefreshAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_monitor is not null)
        {
            await _monitor.DisposeAsync().ConfigureAwait(false);
        }

        if (_config is not null)
        {
            await _config.DisposeAsync().ConfigureAwait(false);
        }

        if (_logs is not null)
        {
            await _logs.DisposeAsync().ConfigureAwait(false);
        }

        _monitor = null;
        _config = null;
        _logs = null;
    }
}
