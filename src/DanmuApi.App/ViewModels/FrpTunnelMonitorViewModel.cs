using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DanmuApi.App.Services;
using DanmuApi.Core;
using DanmuApi.Core.Frp;
using DanmuApi.Platform;
using DanmuApi.Runtime;
using DanmuApi.Runtime.Frp;

namespace DanmuApi.App.ViewModels;

/// <summary>
/// 内网穿透的**监控**页：只讲"现在通没通、外网地址是什么、能按哪些按钮"，
/// 一行表单字段都不放（那是配置页的事），日志也只保留失败原因，不铺流水。
///
/// 版式是一张状态卡（状态 → 外网地址 → 链路 → 代理 → 诊断）+ 一张操作卡 + 一张行为卡。
/// 这里的原则是**每件事只说一遍**：公网入口只在链路块出现，地址只在地址行出现，
/// 代理行只补充"这条代理自己的、上面没说过的"信息。
/// </summary>
public sealed partial class FrpTunnelMonitorViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IFrpTunnelService _tunnel;
    private readonly IRuntimeController _runtime;
    private readonly IUiDialogService _dialogService;
    private readonly IAppDiagnostics _diagnostics;
    private readonly AppPaths _paths;
    private readonly FrpReleaseDiscovery _discovery;
    private readonly IGithubRoutePreferenceStore _routePreferences;
    private readonly IGithubProxySpeedTester _speedTester;
    private readonly Func<int> _defaultLocalPort;
    private readonly Action? _openConfiguration;
    private readonly SynchronizationContext? _uiContext;
    private bool _disposed;

    [ObservableProperty] private string _statusText = "未启动";
    [ObservableProperty] private string _serviceAddressText = string.Empty;
    [ObservableProperty] private string _serverText = string.Empty;
    [ObservableProperty] private string _publicEntryText = string.Empty;
    [ObservableProperty] private string _binaryText = string.Empty;
    [ObservableProperty] private string _operationMessage = string.Empty;
    [ObservableProperty] private string _diagnosticText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isTokenVisible;
    [ObservableProperty] private bool _isFollowServiceEnabled;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private string _installProgressText = string.Empty;
    [ObservableProperty] private string _lastUpdatedText = string.Empty;

    public FrpTunnelMonitorViewModel(
        IFrpTunnelService tunnel,
        IRuntimeController runtime,
        IUiDialogService dialogService,
        IAppDiagnostics diagnostics,
        AppPaths paths,
        FrpReleaseDiscovery discovery,
        IGithubRoutePreferenceStore routePreferences,
        IGithubProxySpeedTester speedTester,
        Func<int> defaultLocalPort,
        Action? openConfiguration = null)
    {
        _tunnel = tunnel ?? throw new ArgumentNullException(nameof(tunnel));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _routePreferences = routePreferences ?? throw new ArgumentNullException(nameof(routePreferences));
        _speedTester = speedTester ?? throw new ArgumentNullException(nameof(speedTester));
        _defaultLocalPort = defaultLocalPort ?? throw new ArgumentNullException(nameof(defaultLocalPort));
        _openConfiguration = openConfiguration;
        _uiContext = SynchronizationContext.Current;

        _tunnel.Changed += OnTunnelChanged;
        _runtime.SnapshotChanged += OnRuntimeChanged;
        IsFollowServiceEnabled = _tunnel.Settings.FollowService;
        Refresh();
        _ = LoadAsync();
    }

    /// <summary>外网地址（含核心 Token）；脱敏与复制都用与局域网地址同一套 EndpointItem 语义。</summary>
    [ObservableProperty] private EndpointItem _publicEndpoint = null!;

    /// <summary>代理状态逐条渲染（名称 · 类型 + 状态胶囊 + 需要补充的细节）。</summary>
    public ObservableCollection<FrpProxyLine> ProxyItems { get; } = [];

    public bool HasPublicAddress => PublicEndpoint?.HasAddress == true;

    /// <summary>没有地址时不重复显示提示语：那两行是同一句话。</summary>
    public bool ShowAddressHint => !HasPublicAddress;

    public bool IsServiceRunning => _runtime.Snapshot.State == DesktopRuntimeState.Running;

    /// <summary>弹幕服务没跑：穿透即使"正常"也送不出任何东西，状态区必须当场点出来。</summary>
    public bool ShowServiceWarning => !IsServiceRunning;

    public bool IsClientRole => _tunnel.Settings.Role == FrpRole.Client;

    public bool IsTunnelActive => _tunnel.Snapshot.RequiresStop || _tunnel.HasOwnedProcess;

    public bool CanStart => !IsBusy && !IsTunnelActive && !_tunnel.IsShutdownRequested && StartBlockedReason.Length == 0;

    public bool CanStop => !IsBusy && IsTunnelActive;

    public bool CanRestart => !IsBusy && !_tunnel.IsShutdownRequested && _tunnel.Snapshot.State is FrpTunnelState.Running
        or FrpTunnelState.Reconnecting or FrpTunnelState.Failed;

    public bool CanInstall => _tunnel.IsSupportedPlatform && !IsBusy && !IsInstalling && !IsTunnelActive && !_tunnel.IsShutdownRequested && _tunnel.InstalledVersion is null;

    public bool IsUpdatingFrp => _tunnel.IsSupportedPlatform && !IsBusy && !IsInstalling && !IsTunnelActive && !_tunnel.IsShutdownRequested && _tunnel.InstalledVersion is not null;

    /// <summary>按钮按状态出现而不是"永远六颗、大半点不动"：停机时只留启动，运行中只留停止/重启。</summary>
    public bool ShowStart => !IsTunnelActive;

    public bool ShowStop => IsTunnelActive;

    public bool ShowRestart => _tunnel.Snapshot.State is FrpTunnelState.Running or FrpTunnelState.Reconnecting or FrpTunnelState.Failed;

    public bool ShowInstall => _tunnel.InstalledVersion is null;

    public bool ShowUpdate => _tunnel.InstalledVersion is not null;

    public bool CanNavigateToConfiguration => _openConfiguration is not null;

    /// <summary>
    /// 启动被挡住的原因，按"先决条件"的顺序给出唯一的一条，而不是把三件事一起倒出来。
    /// 服务没跑最重要（穿透无处可送），其次是没装 frp，再次是设置本身还不可用。
    /// </summary>
    public string StartBlockedReason => !_tunnel.IsSupportedPlatform
        ? _tunnel.PlatformDiagnostic
        : !IsServiceRunning
            ? "弹幕服务未运行。穿透只能把正在运行的本机服务送出去，请先在概览页启动服务（或在下面开启「随弹幕服务启动」）。"
            : _tunnel.InstalledVersion is null
                ? "尚未安装 frp，请先点「安装 frp」。"
                : _tunnel.SettingsProblems.Count > 0
                    ? $"穿透设置还不能用：{string.Join("；", _tunnel.SettingsProblems)}"
                    : IsClientRole && _tunnel.Settings.Client.ServerAddress.Length == 0
                        ? "尚未填写 frps 服务器地址，请到「配置」页填写后再启动。"
                        : string.Empty;

    public bool HasStartBlockedReason => StartBlockedReason.Length > 0;

    public string TokenVisibilityActionText => IsTokenVisible ? "隐藏 Token" : "显示 Token";

    public string PlatformNotice => _tunnel.PlatformDiagnostic;

    public bool HasPlatformNotice => PlatformNotice.Length > 0;

    public bool HasDiagnostic => DiagnosticText.Length > 0;

    public bool HasProxies => ProxyItems.Count > 0;

    public bool HasLastUpdated => LastUpdatedText.Length > 0;

    public bool ShowProgress => IsBusy || IsInstalling;

    /// <summary>链路中间节点的名字：客户端模式指向远端的 frps，服务端模式是本机在监听。</summary>
    public string ServerLabel => IsClientRole ? "frps 服务器" : "本机穿透端口";

    /// <summary>链路第三节：客户端看服务器分配的公网入口，服务端看有多少客户端在线。</summary>
    public string PublicEntryLabel => IsClientRole ? "公网入口" : "在线客户端";

    public string TokenStateText => _tunnel.HasToken
        ? "已保存 Token（在「配置」页可修改或清除）"
        : "未设置 Token（服务器启用 Token 时必须填写）";

    public string RoleText => IsClientRole
        ? "客户端（frpc）：把本机服务送到远端 frps"
        : "服务端（frps）：本机对外提供穿透端口";

    public IBrush StatusBrush => _tunnel.Snapshot.State switch
    {
        FrpTunnelState.Running => new SolidColorBrush(Color.Parse("#16A34A")),
        FrpTunnelState.Failed => new SolidColorBrush(Color.Parse("#DC2626")),
        FrpTunnelState.Starting or FrpTunnelState.Stopping or FrpTunnelState.Reconnecting => new SolidColorBrush(Color.Parse("#2563EB")),
        _ => new SolidColorBrush(Color.Parse("#64748B")),
    };

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartTunnelAsync() =>
        await RunAsync(async () => (FrpOperationResult?)await _tunnel.StartTunnelAsync().ConfigureAwait(true)).ConfigureAwait(true);

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopTunnelAsync() =>
        await RunAsync(async () => (FrpOperationResult?)await _tunnel.StopTunnelAsync().ConfigureAwait(true)).ConfigureAwait(true);

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private async Task RestartTunnelAsync() =>
        await RunAsync(async () => (FrpOperationResult?)await _tunnel.RestartTunnelAsync().ConfigureAwait(true)).ConfigureAwait(true);

    /// <summary>跳到「配置」页签。Token 与服务器地址都在那里，比让用户自己去猜页签强。</summary>
    [RelayCommand(CanExecute = nameof(CanNavigateToConfiguration))]
    private void OpenConfiguration() => _openConfiguration?.Invoke();

    [RelayCommand]
    private void ToggleTokenVisibility()
    {
        IsTokenVisible = !IsTokenVisible;
        RebuildPublicEndpoint();
        OnPropertyChanged(nameof(TokenVisibilityActionText));
    }

    /// <summary>
    /// 开关立即落盘并立即生效（这是行为设置，不是穿透参数的一部分，所以不跟配置页的「保存」绑定）。
    /// 整个方法自带兜底：命令由 AsyncRelayCommand 执行，异常逃出去会在 UI 线程重抛并把应用弄崩。
    /// </summary>
    [RelayCommand]
    private async Task ToggleFollowServiceAsync()
    {
        try
        {
            var target = !IsFollowServiceEnabled;
            IsFollowServiceEnabled = target;
            var result = await _tunnel.SetFollowServiceAsync(target).ConfigureAwait(true);
            OperationMessage = result.Message;
            if (!result.Succeeded)
            {
                _diagnostics.Record($"设置「随弹幕服务启动」失败：{result.Message}");
                IsFollowServiceEnabled = _tunnel.Settings.FollowService;
            }
        }
        catch (Exception error)
        {
            _diagnostics.Record("设置「随弹幕服务启动」异常", error);
            OperationMessage = $"设置失败：{error.Message}";
            IsFollowServiceEnabled = _tunnel.Settings.FollowService;
        }
        finally
        {
            Refresh();
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task InstallFrpAsync() => InstallCoreAsync(forceUpdate: false);

    [RelayCommand(CanExecute = nameof(IsUpdatingFrp))]
    private Task UpdateFrpAsync() => InstallCoreAsync(forceUpdate: true);

    [RelayCommand]
    private async Task CopyAddressAsync()
    {
        try
        {
            if (PublicEndpoint?.HasAddress == true)
            {
                await _dialogService.CopyTextAsync(PublicEndpoint.Address).ConfigureAwait(true);
                OperationMessage = "外网地址已复制（含 Token，请只发给可信的人）。";
            }
        }
        catch (Exception error)
        {
            _diagnostics.Record("复制穿透地址失败", error);
            OperationMessage = $"复制失败：{error.Message}";
        }
    }

    [RelayCommand]
    private async Task OpenDirectoryAsync()
    {
        try
        {
            await _dialogService.OpenDirectoryAsync(_paths.FrpDirectory).ConfigureAwait(true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            OperationMessage = $"打开穿透目录失败：{error.Message}";
            _diagnostics.Record("打开穿透目录失败", error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tunnel.Changed -= OnTunnelChanged;
        _runtime.SnapshotChanged -= OnRuntimeChanged;
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    private async Task LoadAsync()
    {
        try
        {
            await _tunnel.ReloadSettingsAsync().ConfigureAwait(true);
            Refresh();
        }
        catch (Exception error)
        {
            _diagnostics.Record("读取穿透设置失败", error);
            OperationMessage = $"读取穿透设置失败：{error.Message}";
        }
    }

    private async Task InstallCoreAsync(bool forceUpdate)
    {
        if (!_tunnel.IsSupportedPlatform)
        {
            await _dialogService.ShowMessageAsync("无法安装 frp", _tunnel.PlatformDiagnostic, isError: true).ConfigureAwait(true);
            return;
        }

        await RunAsync(async () =>
        {
            var release = await ResolveLatestAsync().ConfigureAwait(true);
            if (release is null)
            {
                return null;
            }

            if (!forceUpdate && string.Equals(_tunnel.InstalledVersion, release.Version, StringComparison.Ordinal))
            {
                return FrpOperationResult.Success($"已安装 frp {release.Version}，无需重复安装。");
            }

            var action = forceUpdate ? "更新到" : "安装";
            var asset = FrpReleaseCatalog.ForVersion(release.Version, RuntimeInformation.ProcessArchitecture);
            if (!await _dialogService.ConfirmAsync(
                    $"{action} frp {release.Version}",
                    $"将从 GitHub 官方仓库下载 frp {release.Version}（{asset.FileName}），"
                    + "并用官方 SHA256 校验后安装到程序数据目录。安装期间会先停止穿透。",
                    $"{action} frp {release.Version}").ConfigureAwait(true))
            {
                return null;
            }

            if (_tunnel.Snapshot.IsActive
                && !(await _tunnel.StopTunnelAsync("install").ConfigureAwait(true)).Succeeded)
            {
                return FrpOperationResult.Failure("安装前停止穿透失败，已中止安装。");
            }

            IsInstalling = true;
            InstallProgressText = "准备下载…";
            Refresh();
            try
            {
                var progress = new Progress<GithubDownloadProgress>(item =>
                    InstallProgressText = item.TotalBytes is { } total && total > 0
                        ? $"正在下载 {asset.FileName}：{item.DownloadedBytes / 1024 / 1024} / {total / 1024 / 1024} MB（{item.RouteLabel}）"
                        : $"正在下载 {asset.FileName}：{item.DownloadedBytes / 1024 / 1024} MB（{item.RouteLabel}）");

                return await _tunnel.InstallAsync(release.Version, progress).ConfigureAwait(true);
            }
            catch (GithubRouteSelectionRequiredException)
            {
                return FrpOperationResult.Failure("GitHub 线路尚未确认，请先测速并选择线路后重试。");
            }
            catch (GithubRouteException error)
            {
                return FrpOperationResult.Failure(error.Message);
            }
            finally
            {
                IsInstalling = false;
                InstallProgressText = string.Empty;
            }
        }).ConfigureAwait(true);
    }

    private async Task<FrpLatestRelease?> ResolveLatestAsync()
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var proxyId = _routePreferences.Read();
            if (!proxyId.Confirmed)
            {
                var selected = await _dialogService
                    .ChooseGithubRouteAsync("下载 frp 前请先测速并选择 GitHub 线路。", proxyId.ProxyId, _speedTester)
                    .ConfigureAwait(true);
                if (selected is null)
                {
                    OperationMessage = "未选择 GitHub 线路，已取消。";
                    return null;
                }

                continue;
            }

            try
            {
                return await _discovery.GetLatestAsync(RuntimeInformation.ProcessArchitecture).ConfigureAwait(true);
            }
            catch (Exception error) when (error is IOException or NotSupportedException)
            {
                if (error.Message.Contains("没有适用于本机架构", StringComparison.Ordinal))
                {
                    await _dialogService.ShowMessageAsync("无法安装 frp", error.Message, isError: true).ConfigureAwait(true);
                    return null;
                }

                if (attempt == 0)
                {
                    _routePreferences.Invalidate();
                    continue;
                }

                await _dialogService.ShowMessageAsync("无法获取 frp 版本", error.Message, isError: true).ConfigureAwait(true);
                return null;
            }
        }

        OperationMessage = "GitHub 线路仍不可用，请稍后重试。";
        return null;
    }

    /// <summary>统一的操作外壳：忙标志、结果提示与诊断。返回 null 表示"操作自己已经报过结果"（例如用户取消）。</summary>
    private async Task RunAsync(Func<Task<FrpOperationResult?>> operation)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Refresh();
        try
        {
            var result = await operation().ConfigureAwait(true);
            if (result is null)
            {
                return;
            }

            OperationMessage = result.Message;
            if (!result.Succeeded)
            {
                _diagnostics.Record($"穿透操作失败：{result.Message}");
            }
        }
        catch (Exception error)
        {
            _diagnostics.Record("穿透操作失败", error);
            OperationMessage = $"操作失败：{error.Message}";
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    private void OnTunnelChanged(object? sender, FrpSnapshot snapshot) => Dispatch(() =>
    {
        if (!_disposed)
        {
            Refresh();
        }
    });

    private void OnRuntimeChanged(object? sender, RuntimeSnapshot snapshot) => Dispatch(() =>
    {
        if (!_disposed)
        {
            Refresh();
        }
    });

    private void Dispatch(Action action)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
        {
            action();
        }
        else
        {
            _uiContext.Post(_ => action(), null);
        }
    }

    private void Refresh()
    {
        var snapshot = _tunnel.Snapshot;
        var settings = _tunnel.Settings;

        StatusText = snapshot.State switch
        {
            FrpTunnelState.Stopped => "未启动",
            FrpTunnelState.Starting => "正在连接服务器",
            FrpTunnelState.Running => "穿透正常",
            FrpTunnelState.Reconnecting => "连接中断，重连中",
            FrpTunnelState.Stopping => "正在停止",
            FrpTunnelState.Failed => "穿透失败",
            _ => snapshot.State.ToString(),
        };

        // frp 自己的话原样带出来（含 exitCode 与 stderr 尾部），只显示不加工。
        DiagnosticText = snapshot.Diagnostic ?? string.Empty;

        ServiceAddressText = $"127.0.0.1:{settings.Client.LocalPort.ToString(CultureInfo.InvariantCulture)}";
        ServerText = settings.Role == FrpRole.Client
            ? settings.Client.ServerAddress.Length == 0
                ? "未配置"
                : $"{settings.Client.ServerAddress}:{settings.Client.ServerPort.ToString(CultureInfo.InvariantCulture)}"
            : settings.Server.BindPort.ToString(CultureInfo.InvariantCulture);

        // 公网入口只认 frp 报告回来的 remote_addr；服务端角色没有"分配给你的入口"这回事，
        // 改报在线客户端数，不编造地址。
        PublicEntryText = settings.Role == FrpRole.Client
            ? snapshot.State == FrpTunnelState.Running && snapshot.RemoteAddress is { Length: > 0 } remote ? remote : "未分配"
            : snapshot.Server is { } server ? $"{server.ClientCounts.ToString(CultureInfo.InvariantCulture)} 台在线" : "未报告";

        var installed = _tunnel.InstalledVersion;
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        BinaryText = !_tunnel.IsSupportedPlatform
            ? $"本机 {arch} 不支持"
            : installed is null
                ? $"未安装（本机 {arch}）"
                : $"frp {installed}（本机 {arch}）";

        RebuildProxyItems(snapshot, PublicEntryText);

        IsFollowServiceEnabled = settings.FollowService;
        if (snapshot.State != FrpTunnelState.Stopped || DiagnosticText.Length > 0)
        {
            LastUpdatedText = $"界面最近更新 {DateTimeOffset.Now:HH:mm:ss}";
        }

        RebuildPublicEndpoint();

        foreach (var property in new[]
        {
            nameof(HasPublicAddress), nameof(ShowAddressHint), nameof(IsServiceRunning), nameof(ShowServiceWarning),
            nameof(StartBlockedReason), nameof(HasStartBlockedReason), nameof(TokenStateText), nameof(RoleText),
            nameof(StatusBrush), nameof(PlatformNotice), nameof(HasPlatformNotice), nameof(HasDiagnostic),
            nameof(HasProxies), nameof(HasLastUpdated), nameof(ShowProgress), nameof(ServerLabel),
            nameof(PublicEntryLabel), nameof(IsClientRole), nameof(IsTunnelActive), nameof(ShowStart),
            nameof(ShowStop), nameof(ShowRestart), nameof(ShowInstall), nameof(ShowUpdate),
            nameof(CanNavigateToConfiguration),
        })
        {
            OnPropertyChanged(property);
        }

        NotifyCommandStates();
    }

    /// <summary>
    /// 一条代理一行：名称 · 类型 + 状态胶囊 + 只有这条代理才有的细节。
    /// running 的远端地址在链路块里已经出现过，只有"与公网入口不一致"时才在这里补一次，
    /// 否则同一屏会把同一个地址说三遍（旧版「详细状态」正是这么做的）。
    /// </summary>
    private void RebuildProxyItems(FrpSnapshot snapshot, string publicEntry)
    {
        ProxyItems.Clear();
        foreach (var proxy in snapshot.ProxyList)
        {
            var stateText = proxy.State switch
            {
                FrpProxyState.Running => "已连接",
                FrpProxyState.StartError => "启动失败",
                FrpProxyState.Pending => "等待服务器分配",
                FrpProxyState.Closed => "已关闭",
                _ => $"未知状态：{proxy.Status}",
            };

            var detail = proxy.State switch
            {
                FrpProxyState.Running when proxy.RemoteAddress is { Length: > 0 } remote
                    && !string.Equals(remote, publicEntry, StringComparison.Ordinal) => remote,
                FrpProxyState.StartError => proxy.Error,
                _ => string.Empty,
            };

            ProxyItems.Add(new FrpProxyLine(
                proxy.Name,
                proxy.Type.ToUpperInvariant(),
                stateText,
                detail,
                IsRunning: proxy.IsRunning,
                IsFailed: proxy.State == FrpProxyState.StartError));
        }
    }

    /// <summary>地址随 Token 可见性与快照变化重建（EndpointItem 的脱敏/复制是构造期固定的）。</summary>
    private void RebuildPublicEndpoint()
    {
        var settings = _tunnel.Settings;
        var address = FrpPublicAddress.Build(new FrpPublicAddressInput(
            _tunnel.Snapshot.State,
            settings.Role,
            settings.Client.ProxyKind,
            _tunnel.Snapshot.RemoteAddress,
            ResolveCoreToken()));

        // 地址就在上一行，提示语不再重复一遍地址本身，只说"这串东西该怎么用"。
        var hint = address.Length == 0
            ? "穿透运行后在此显示带 Token 的外网访问地址。"
            : "地址含 Token，只分享给可信的人。";

        PublicEndpoint = new EndpointItem("外网 API", hint, address, IsTokenVisible, _dialogService);
    }

    /// <summary>核心的访问 TOKEN 取自核心的 config/.env —— 与局域网地址用的是同一个值。</summary>
    private string ResolveCoreToken()
    {
        try
        {
            return RuntimeTokenResolver.Resolve(Path.Combine(_paths.NodeProjectDirectory, "config", ".env"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
        {
            _diagnostics.Record("读取弹幕 Token 失败（穿透地址将不带 Token）", error);
            return string.Empty;
        }
    }

    private void NotifyCommandStates()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanRestart));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(IsUpdatingFrp));
        StartTunnelCommand.NotifyCanExecuteChanged();
        StopTunnelCommand.NotifyCanExecuteChanged();
        RestartTunnelCommand.NotifyCanExecuteChanged();
        InstallFrpCommand.NotifyCanExecuteChanged();
        UpdateFrpCommand.NotifyCanExecuteChanged();
        OpenConfigurationCommand.NotifyCanExecuteChanged();
    }
}
