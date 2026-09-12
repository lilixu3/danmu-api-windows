using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DanmuApi.App.Services;
using DanmuApi.Core;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly RuntimePreparationService? _preparation;
    private readonly IRuntimeController _controller;
    private readonly IRuntimeHealthClient _healthClient;
    private readonly ISettingsStore _settingsStore;
    private readonly ICoreCacheClient _coreCacheClient;
    private readonly IAdminSessionService _adminSession;
    private readonly IAdminWriteGate? _writeGate;
    private readonly AppPaths _paths;
    private readonly IUiDialogService _dialogService;
    private readonly SettingsPageViewModel _settingsPage;
    private readonly CorePageViewModel? _corePage;
    private readonly ICoreManagementService? _coreManagement;
    private readonly Func<ActivityPageViewModel>? _activityPageFactory;
    private readonly Func<ToolsPageViewModel>? _toolsPageFactory;
    private readonly Func<DanmuDownloadPageViewModel>? _downloadPageFactory;
    private DanmuDownloadPageViewModel? _downloadPage;
    private readonly SynchronizationContext? _uiContext;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _healthLoop;
    private readonly ICoreRequestRecordsClient? _requestRecordsClient;
    private readonly List<Task> _requestStatsTasks = [];
    private CancellationTokenSource? _requestStatsCts;

    [ObservableProperty]
    private string _todayRequestsText = "今日请求未读取";
    private readonly System.Diagnostics.Stopwatch _healthClock = System.Diagnostics.Stopwatch.StartNew();
    private int _healthReadInProgress;
    public RequestRateHistory RequestTrend { get; } = new();
    private RuntimeHealthSnapshot? _health;
    private DateTimeOffset? _healthSampledAt;
    private string? _healthDiagnostic;
    private string? _cacheDiagnostic;
    private string? _actionDiagnostic;
    private IReadOnlyDictionary<string, int> _lastClearedCacheItems =
        new Dictionary<string, int>(StringComparer.Ordinal);
    private string? _configurationDiagnostic;
    private RuntimeConfig? _config;
    private string _token = RuntimeDefaults.FallbackToken;
    private readonly object _addressCacheSync = new();
    private DateTimeOffset _addressesResolvedAt;
    private string? _cachedIpv4Address;
    private string? _cachedIpv6Address;

    [ObservableProperty]
    private RuntimeSnapshot _runtime = new(DesktopRuntimeState.Stopped);

    [ObservableProperty]
    private NavigationItem _selectedNavigationItem;

    [ObservableProperty]
    private bool _isSidebarCollapsed;

    [ObservableProperty]
    private bool _isTokenVisible;

    [ObservableProperty]
    private object _currentPage;

    public MainWindowViewModel(
        IRuntimeController controller,
        IRuntimeHealthClient healthClient,
        ISettingsStore settingsStore,
        ICoreCacheClient coreCacheClient,
        AppPaths paths,
        IUiDialogService dialogService,
        SettingsPageViewModel settingsPage,
        IAdminSessionService adminSession,
        CorePageViewModel? corePage = null,
        Func<ActivityPageViewModel>? activityPageFactory = null,
        Func<ToolsPageViewModel>? toolsPageFactory = null,
        Func<DanmuDownloadPageViewModel>? downloadPageFactory = null,
        ConfigurationPageViewModel? configurationPage = null,
        IAdminWriteGate? writeGate = null,
        ICoreRequestRecordsClient? requestRecordsClient = null,
        RuntimePreparationService? preparation = null,
        ICoreManagementService? coreManagement = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _healthClient = healthClient ?? throw new ArgumentNullException(nameof(healthClient));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _coreCacheClient = coreCacheClient ?? throw new ArgumentNullException(nameof(coreCacheClient));
        _adminSession = adminSession ?? throw new ArgumentNullException(nameof(adminSession));
        _writeGate = writeGate;
        _requestRecordsClient = requestRecordsClient;
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _settingsPage = settingsPage ?? throw new ArgumentNullException(nameof(settingsPage));
        _corePage = corePage;
        _coreManagement = coreManagement;
        _activityPageFactory = activityPageFactory;
        _toolsPageFactory = toolsPageFactory;
        _downloadPageFactory = downloadPageFactory;
        ConfigurationPage = configurationPage;
        if (ConfigurationPage is not null)
        {
            ConfigurationPage.CoreConfigurationChanged += OnCoreConfigurationChanged;
        }
        if (_writeGate is not null)
        {
            _writeGate.NavigateToSecurity = GoToSecuritySettings;
        }
        _uiContext = SynchronizationContext.Current;
        _preparation = preparation;
        if (_preparation is not null) _preparation.Changed += OnPreparationChanged;
        _settingsPage.SettingsChanged += OnSettingsChanged;
        if (_coreManagement is not null)
        {
            // 核心可能被托盘或后台自动更新换掉，这两条路径都不经过核心页；
            // 侧栏、概览与关于页的版本文字同样要立刻重读磁盘。
            _coreManagement.InstallationChanged += OnCoreInstallationChanged;
        }
        _runtime = controller.Snapshot;
        RequestTrend.Reset(_runtime.State == DesktopRuntimeState.Running);
        NavigationItems =
        [
            new("overview", "概览", "服务状态与访问入口", "\uE80F"),
            new("core", "核心", "在运行变体之间切换，并安装、更新当前选中的核心", "\uE950"),
            new("configuration", "配置", "环境变量工作台", "\uE713"),
            new("downloads", "下载", "弹幕下载任务", "\uE896"),
            new("activity", "活动", "运行日志", "\uE81C"),
            new("tools", "工具", "接口、请求记录与缓存", "\uE90F"),
            new("settings", "设置", "服务与外观设置", "\uE713"),
            new("about", "关于", "版本与帮助信息", "\uE946"),
        ];
        _selectedNavigationItem = NavigationItems[0];
        _currentPage = new OverviewPageViewModel(this);
        _config = LoadRuntimeConfig();
        _token = LoadRuntimeToken();
        _controller.SnapshotChanged += OnSnapshotChanged;
        _healthLoop = HealthLoopAsync(_disposeCts.Token);
        RestartRequestStats();
    }

    public bool ShowPreparation => _preparation is not null && _preparation.Snapshot.State != RuntimePreparationState.Ready;
    public string PreparationMessage => _preparation?.Snapshot.Message ?? string.Empty;
    public bool PreparationBusy => _preparation?.Snapshot.State is RuntimePreparationState.Pending or RuntimePreparationState.Preparing;
    public bool CanRepairPreparation => _preparation?.Snapshot.State is RuntimePreparationState.Failed or RuntimePreparationState.Canceled;
    public double PreparationProgress => _preparation?.Snapshot.Progress is { Total: > 0 } progress ? 100d * progress.Completed / progress.Total : 0;
    public bool PreparationIndeterminate => PreparationBusy && _preparation?.Snapshot.Progress is not { Total: > 0 };

    private int _preparationUpdateQueued;

    private void OnPreparationChanged(object? sender, RuntimePreparationSnapshot snapshot)
    {
        void Update()
        {
            Interlocked.Exchange(ref _preparationUpdateQueued, 0);
            OnPropertyChanged(nameof(ShowPreparation));
            OnPropertyChanged(nameof(PreparationMessage));
            OnPropertyChanged(nameof(PreparationBusy));
            OnPropertyChanged(nameof(CanRepairPreparation));
            OnPropertyChanged(nameof(PreparationProgress));
            OnPropertyChanged(nameof(PreparationIndeterminate));
            RepairPreparationCommand.NotifyCanExecuteChanged();
            // Only start/restart readiness depends on the preparation state. Endpoint and health
            // properties do not, and rebuilding them per progress event (network enumeration) used
            // to flood the UI thread for minutes on a full preparation.
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanRestart));
            StartCommand.NotifyCanExecuteChanged();
            RestartCommand.NotifyCanExecuteChanged();
        }
        if (_uiContext is null)
        {
            Update();
            return;
        }
        // A full preparation reports tens of thousands of per-file updates. Keep at most one
        // dispatcher callback in flight; it renders the latest snapshot, so the banner stays current
        // without queueing the whole stream.
        if (Interlocked.Exchange(ref _preparationUpdateQueued, 1) == 0) _uiContext.Post(_ => Update(), null);
    }

    [RelayCommand(CanExecute = nameof(CanRepairPreparation))]
    private async Task RepairPreparationAsync()
    {
        if (_preparation is null) return;
        if (await _dialogService.ConfirmAsync("确认修复运行环境", PreparationMessage + "\n将重新校验并修复内置 Node、宿主与依赖。请先停止服务；核心与配置不会被覆盖。", "修复并重新准备").ConfigureAwait(true))
            await _preparation.PrepareAsync(forceRepair: true).ConfigureAwait(true);
    }

    public IReadOnlyList<NavigationItem> NavigationItems { get; }
    [ObservableProperty] private ApplicationUpdateViewModel? _applicationUpdates;
    public bool HasActiveDownload => _downloadPage?.QueueRunningCount > 0;
    [RelayCommand] private void OpenApplicationUpdates() => NavigateTo("about");

    public double SidebarWidth => IsSidebarCollapsed ? 68 : 216;
    public bool IsSidebarExpanded => !IsSidebarCollapsed;
    public string SelectedPageTitle => SelectedNavigationItem.Title;
    public string SelectedPageDescription => SelectedNavigationItem.Description;
    public string StatusText => Runtime.State switch
    {
        DesktopRuntimeState.Stopped => "服务已停止",
        DesktopRuntimeState.Preparing => "正在准备运行时",
        DesktopRuntimeState.Starting => "正在启动服务",
        DesktopRuntimeState.Running => "服务运行中",
        DesktopRuntimeState.Stopping => "正在停止服务",
        DesktopRuntimeState.CoreSetupRequired => "等待准备核心",
        DesktopRuntimeState.Failed => "启动失败",
        _ => Runtime.State.ToString(),
    };

    public string DiagnosticText
    {
        get
        {
            var diagnostics = new[] { Runtime.FailureReason, _healthDiagnostic, _configurationDiagnostic, _cacheDiagnostic, _actionDiagnostic }
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (diagnostics.Length > 0)
            {
                return string.Join(Environment.NewLine, diagnostics);
            }

            return Runtime.State switch
            {
                DesktopRuntimeState.Running when _healthSampledAt is not null =>
                    $"健康接口响应正常 · 最近检查 {_healthSampledAt.Value.LocalDateTime:HH:mm:ss}",
                DesktopRuntimeState.Running => "服务已启动，等待首次健康检查。",
                DesktopRuntimeState.Preparing => "正在准备运行环境，请稍候。",
                DesktopRuntimeState.Starting => "正在等待服务监听端口并通过健康校验。",
                DesktopRuntimeState.Stopping => "正在停止服务进程。",
                DesktopRuntimeState.CoreSetupRequired => "请先在核心页完成安装，再启动服务。",
                DesktopRuntimeState.Failed => "服务启动失败，详细原因请查看活动页运行日志。",
                _ => "服务尚未启动，启动后自动检查运行状态。",
            };
        }
    }
    public string CoreVersionText => _config is null
        ? "配置无效"
        : CoreVersionReader.Inspect(_paths.NodeProjectDirectory, _config.Variant) switch
        {
            { Installed: false } => "未安装",
            { Version: { Length: > 0 } version } => version,
            { Diagnostic: { Length: > 0 } } => "未知",
            _ => "未知",
        };
    public string CoreVariantText => _config is null ? "配置无效" : $"{_config.Variant.ToUpperInvariant()} 变体";
    public string PortText => Runtime.Port?.ToString(CultureInfo.InvariantCulture) ?? (_config?.Port.ToString(CultureInfo.InvariantCulture) ?? "配置无效");
    public string TokenMasked => MaskToken(_token);
    public string TokenDisplay => IsTokenVisible ? _token : TokenMasked;
    public string ListenHostText => _health?.Host ?? (_config?.ListenHost ?? "配置无效");
    public string UptimeText => FormatDuration(_health?.UptimeSec);
    public string RequestCountText => _health?.RequestCount?.ToString(CultureInfo.InvariantCulture) ?? "未读取";
    public string PidText => Runtime.Pid?.ToString(CultureInfo.InvariantCulture) ?? _health?.Pid?.ToString(CultureInfo.InvariantCulture) ?? "未启动";
    public string CacheProbeText => _health?.CacheProbeWritable switch
    {
        true => "可写",
        false => "不可写",
        _ => "未检查",
    };
    public string HealthFreshnessText => _healthSampledAt is null
        ? "尚未成功检查"
        : Runtime.State != DesktopRuntimeState.Running || _healthDiagnostic is not null
            ? $"数据已过期，上次成功检查 {_healthSampledAt.Value.LocalDateTime:HH:mm:ss}"
            : $"上次成功检查 {_healthSampledAt.Value.LocalDateTime:HH:mm:ss}";
    public string TokenVisibilityActionText => IsTokenVisible ? "隐藏 Token" : "显示 Token";
    public string CacheSummary => _lastClearedCacheItems.Count == 0
        ? "核心缓存"
        : $"最近清理 {_lastClearedCacheItems.Count.ToString(CultureInfo.InvariantCulture)} 项";
    public string CacheDiagnostic => _cacheDiagnostic ?? "清理操作通过核心 API 执行，不删除本地运行目录。";
    public string CoreVersionShortText => CoreVersionText;
    public IBrush StatusBrush => Runtime.State switch
    {
        DesktopRuntimeState.Running => new SolidColorBrush(Color.Parse("#16A34A")),
        DesktopRuntimeState.Failed => new SolidColorBrush(Color.Parse("#DC2626")),
        DesktopRuntimeState.CoreSetupRequired => new SolidColorBrush(Color.Parse("#D97706")),
        DesktopRuntimeState.Preparing or DesktopRuntimeState.Starting or DesktopRuntimeState.Stopping => new SolidColorBrush(Color.Parse("#2563EB")),
        _ => new SolidColorBrush(Color.Parse("#64748B")),
    };
    public bool CanStart => _preparation?.StartBlockedReason is null && _config is not null && Runtime.State is (DesktopRuntimeState.Stopped or DesktopRuntimeState.Failed);
    public bool CanStop => Runtime.State is DesktopRuntimeState.Running or DesktopRuntimeState.Starting or DesktopRuntimeState.Preparing;
    public bool CanRestart => _preparation?.StartBlockedReason is null && Runtime.State is DesktopRuntimeState.Running or DesktopRuntimeState.Failed;
    public bool IsBusy => Runtime.State is DesktopRuntimeState.Preparing or DesktopRuntimeState.Starting or DesktopRuntimeState.Stopping;
    public bool IsServiceRunning => Runtime.State == DesktopRuntimeState.Running;
    public IReadOnlyList<EndpointItem> EndpointItems => BuildEndpointItems();
    public IReadOnlyList<CoreCacheItem> CoreCacheItems => CoreCacheCatalog.Items;
    public IReadOnlyDictionary<string, int> LastClearedCacheItems => _lastClearedCacheItems;
    public SettingsPageViewModel SettingsPage => _settingsPage;
    public CorePageViewModel? CorePage => _corePage;
    public ConfigurationPageViewModel? ConfigurationPage { get; }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        await _controller.StartAsync().ConfigureAwait(true);
        if (Runtime.State != DesktopRuntimeState.CoreSetupRequired)
        {
            return;
        }

        _actionDiagnostic = Runtime.FailureReason ?? "核心尚未安装";
        NotifyActionChanged();
        var goInstall = await _dialogService
            .ConfirmCoreSetupRequiredAsync(_actionDiagnostic)
            .ConfigureAwait(true);
        if (goInstall)
        {
            NavigateTo("core");
            _actionDiagnostic = "核心尚未安装；请在核心页完成安装后再启动服务。";
        }
        else
        {
            _actionDiagnostic = "核心尚未安装；服务未启动。";
        }

        NotifyActionChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync() => await _controller.StopAsync().ConfigureAwait(false);

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private async Task RestartAsync() => await _controller.RestartAsync().ConfigureAwait(false);

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;
        OnPropertyChanged(nameof(SidebarWidth));
        OnPropertyChanged(nameof(IsSidebarExpanded));
    }

    [RelayCommand]
    private void ToggleTokenVisibility()
    {
        IsTokenVisible = !IsTokenVisible;
        OnPropertyChanged(nameof(TokenMasked));
        OnPropertyChanged(nameof(TokenDisplay));
        OnPropertyChanged(nameof(TokenVisibilityActionText));
        OnPropertyChanged(nameof(EndpointItems));
    }

    [RelayCommand]
    private Task OpenPortEditorAsync() => _dialogService.EditPortAsync(this);

    [RelayCommand]
    private Task OpenTokenEditorAsync() => _dialogService.EditTokenAsync(this);

    [RelayCommand]
    private async Task OpenCacheAsync()
    {
        if (!await EnsureAdminModeAsync("清理缓存").ConfigureAwait(true))
        {
            return;
        }

        await _dialogService.ShowCacheAsync(this);
    }

    /// <summary>
    /// 管理员写操作前置门禁：不在管理员模式时弹窗说明并（经确认）跳转设置 > 安全。
    /// 返回 true 才允许继续执行写操作。
    /// </summary>
    private async Task<bool> EnsureAdminModeAsync(string action)
    {
        if (_writeGate is not null)
        {
            return await _writeGate.EnsureAsync(action).ConfigureAwait(true);
        }

        try
        {
            _adminSession.Refresh();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _actionDiagnostic = $"读取管理员配置失败：{error.Message}";
            NotifyActionChanged();
            await _dialogService.ShowMessageAsync("管理员模式", _actionDiagnostic, isError: true).ConfigureAwait(true);
            return false;
        }

        if (_adminSession.State.IsAdminMode)
        {
            return true;
        }

        var goSettings = await _dialogService.ConfirmAdminModeRequiredAsync(
            $"{action}属于管理员写操作，" + (_adminSession.State.HasAdminTokenConfigured
                ? "请先输入管理员密码开启管理员模式。"
                : "请先配置管理员密码并开启管理员模式。")).ConfigureAwait(true);
        if (goSettings)
        {
            GoToSecuritySettings();
        }

        return false;
    }

    private void GoToSecuritySettings()
    {
        NavigateTo("settings");
        _settingsPage.SelectedCategory = _settingsPage.Categories.First(category => category.Key == "security");
    }

    [RelayCommand]
    private async Task OpenCoreRuntimeDirectoryAsync()
    {
        try
        {
            await _dialogService.OpenDirectoryAsync(_paths.NodeProjectDirectory).ConfigureAwait(true);
            _actionDiagnostic = "核心运行目录已打开。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _actionDiagnostic = $"打开核心运行目录失败：{error.Message}";
        }

        NotifyActionChanged();
    }

    [RelayCommand]
    private void NavigateCore() => NavigateTo("core");

    public void NavigateTo(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        SelectedNavigationItem = NavigationItems.First(item => item.Key == key);
    }

    public async Task ApplyPortAsync(int port)
    {
        RuntimeValidation.ValidatePort(port);
        var currentPort = Runtime.State == DesktopRuntimeState.Running && Runtime.Port is not null
            ? Runtime.Port
            : _config?.Port;
        if (currentPort == port)
        {
            _actionDiagnostic = "端口未修改，未写入配置，也未重启服务。";
            NotifyActionChanged();
            return;
        }

        _settingsStore.Write(new Dictionary<string, string?>
        {
            ["port_override"] = port.ToString(CultureInfo.InvariantCulture),
        });
        _config = LoadRuntimeConfig();
        NotifyConfigurationChanged();
        _actionDiagnostic = Runtime.State == DesktopRuntimeState.Running
            ? "端口已保存，正在重启服务。"
            : "端口已保存，服务下次启动时生效。";
        NotifyActionChanged();
        if (Runtime.State == DesktopRuntimeState.Running)
        {
            await _controller.RestartAsync().ConfigureAwait(false);
        }
    }

    public async Task ApplyTokenAsync(string token)
    {
        var normalized = token.Trim();
        if (normalized.Length == 0)
        {
            _actionDiagnostic = "Token 未修改，未写入配置，也未重启服务。";
            NotifyActionChanged();
            return;
        }

        if (string.Equals(_token, normalized, StringComparison.Ordinal))
        {
            _actionDiagnostic = "Token 未修改，未写入配置，也未重启服务。";
            NotifyActionChanged();
            return;
        }

        if (!await EnsureAdminModeAsync("写入访问 Token").ConfigureAwait(true))
        {
            return;
        }

        DotEnvFile.UpdateValues(EnvPath, new Dictionary<string, string?>
        {
            ["TOKEN"] = normalized,
        });
        _token = LoadRuntimeToken();
        NotifyConfigurationChanged();
        _actionDiagnostic = "Token 已保存；核心会热加载配置，正在刷新状态。";
        NotifyActionChanged();
        await RefreshHealthAsync(_disposeCts.Token).ConfigureAwait(false);
    }

    public async Task<CoreCacheClearResult> ClearCoreCachesAsync(
        IEnumerable<string> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (_config is null)
        {
            return CoreCacheClearResult.Failure("当前运行配置无效，无法访问核心缓存接口");
        }

        if (Runtime.State != DesktopRuntimeState.Running || Runtime.Port is null)
        {
            return CoreCacheClearResult.Failure("服务未运行，无法调用核心缓存接口");
        }

        // 对齐移动端：缓存清理必须用会话令牌走管理路径，且要求已配置管理员密码、已开启管理员模式。
        _adminSession.Refresh();
        var adminToken = _adminSession.CurrentAdminTokenOrNull();
        if (adminToken is null)
        {
            _cacheDiagnostic = _adminSession.State.HasAdminTokenConfigured
                ? "请先在 设置 > 安全 输入管理员密码开启管理员模式，再清理缓存。"
                : "核心缓存接口要求管理员密码，请先在 设置 > 安全 配置并开启管理员模式。";
            NotifyCacheChanged();
            return CoreCacheClearResult.Failure(_cacheDiagnostic);
        }

        _cacheDiagnostic = null;
        var result = await _coreCacheClient.ClearAsync(
            "127.0.0.1",
            Runtime.Port.Value,
            _token,
            adminToken,
            keys,
            cancellationToken).ConfigureAwait(false);
        _cacheDiagnostic = result.Diagnostic;
        if (result.Succeeded)
        {
            _lastClearedCacheItems = result.ClearedItems;
        }

        NotifyCacheChanged();
        return result;
    }

    partial void OnSelectedNavigationItemChanged(NavigationItem value)
    {
        var previous = _currentPage;
        _currentPage = value.Key switch
        {
            "overview" => new OverviewPageViewModel(this),
            "core" => _corePage ?? (object)new PlaceholderPageViewModel(value.Title, value.Description, value.Icon),
            "configuration" => ConfigurationPage ?? (object)new PlaceholderPageViewModel(value.Title, value.Description, value.Icon),
            "downloads" => _downloadPage ??= _downloadPageFactory?.Invoke()
                ?? throw new InvalidOperationException("下载页工厂未注册"),
            "activity" => _activityPageFactory?.Invoke()
                ?? (object)new PlaceholderPageViewModel(value.Title, value.Description, value.Icon),
            "tools" => _toolsPageFactory?.Invoke()
                ?? (object)new PlaceholderPageViewModel(value.Title, value.Description, value.Icon),
            "settings" => _settingsPage,
            "about" => new AboutPageViewModel(this),
            _ => new PlaceholderPageViewModel(value.Title, value.Description, value.Icon),
        };
        // 配置目录必须跟随当前核心：核心页安装/切换变体后，进入配置页时强制重读 envs.js。
        if (value.Key == "configuration" && ConfigurationPage is not null)
        {
            ConfigurationPage.RefreshCommand.Execute(null);
        }
        if (previous is IAsyncDisposable disposable && !ReferenceEquals(previous, _currentPage))
        {
            _ = disposable.DisposeAsync().AsTask();
        }

        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(SelectedPageTitle));
        OnPropertyChanged(nameof(SelectedPageDescription));
    }

    partial void OnIsSidebarCollapsedChanged(bool value)
    {
        OnPropertyChanged(nameof(SidebarWidth));
        OnPropertyChanged(nameof(IsSidebarExpanded));
    }

    partial void OnRuntimeChanged(RuntimeSnapshot value)
    {
        RestartRequestStats();
        _actionDiagnostic = null;
        if (value.State != DesktopRuntimeState.Running)
        {
            // 服务停止/重启（含核心重装期间的自动停止）会让在途健康检查必然失败；
            // 这类失败不是运行异常，残留会把正常流程伪装成报错。
            _healthDiagnostic = null;
            _health = null;
            _healthSampledAt = null;
            RequestTrend.Reset();
        }

        NotifyHealthChanged();
        NotifyRuntimeChanged();
        OnPropertyChanged(nameof(CacheSummary));
        if (value.State == DesktopRuntimeState.Running)
        {
            if (RequestTrend.Points.Count == 0) RequestTrend.Reset(running: true);
            _ = RefreshHealthAsync(_disposeCts.Token);
        }
    }

    private void OnSnapshotChanged(object? sender, RuntimeSnapshot snapshot)
    {
        void Update() => Runtime = snapshot;
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
        {
            Update();
        }
        else
        {
            _uiContext.Post(_ => Update(), null);
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs args)
    {
        void Apply()
        {
            var previousHost = _config?.ListenHost;
            _config = LoadRuntimeConfig();
            NotifyConfigurationChanged();
            if (previousHost is null || _config is null || string.Equals(previousHost, _config.ListenHost, StringComparison.Ordinal))
            {
                return;
            }

            if (Runtime.State == DesktopRuntimeState.Running)
            {
                _actionDiagnostic = "IPv6 监听设置已保存，正在重启服务。";
                NotifyActionChanged();
                _ = RestartForSettingsAsync();
            }
            else
            {
                _actionDiagnostic = "IPv6 监听设置已保存，服务下次启动时生效。";
                NotifyActionChanged();
            }
        }

        DispatchToUi(Apply);
    }

    private void OnCoreConfigurationChanged(object? sender, CoreConfigurationChangedEventArgs args)
    {
        if (!string.Equals(args.Key, "TOKEN", StringComparison.Ordinal))
        {
            return;
        }

        DispatchToUi(() =>
        {
            try
            {
                _token = LoadRuntimeToken();
                _configurationDiagnostic = null;
                NotifyConfigurationChanged();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
            {
                _configurationDiagnostic = $"Token 配置已变更，但重新读取有效 Token 失败：{error.Message}";
                NotifyActionChanged();
            }
        });
    }

    private async Task RestartForSettingsAsync()
    {
        try
        {
            await _controller.RestartAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            DispatchToUi(() =>
            {
                _actionDiagnostic = $"应用 IPv6 监听设置时重启服务失败：{error.Message}";
                NotifyActionChanged();
            });
        }
    }

    private void NotifyRuntimeChanged()
    {
        foreach (var property in new[]
        {
            nameof(StatusText), nameof(DiagnosticText), nameof(PortText), nameof(PidText), nameof(StatusBrush),
            nameof(CanStart), nameof(CanStop), nameof(CanRestart), nameof(IsBusy), nameof(IsServiceRunning),
            nameof(EndpointItems), nameof(ListenHostText), nameof(HealthFreshnessText),
        })
        {
            OnPropertyChanged(property);
        }

        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
    }

    private void NotifyActionChanged()
    {
        OnPropertyChanged(nameof(DiagnosticText));
    }

    private void NotifyCacheChanged()
    {
        OnPropertyChanged(nameof(CacheSummary));
        OnPropertyChanged(nameof(CacheDiagnostic));
        OnPropertyChanged(nameof(LastClearedCacheItems));
        OnPropertyChanged(nameof(DiagnosticText));
    }

    /// <summary>核心安装被替换（含托盘与后台自动更新）后，侧栏、概览与关于页的版本文字要回 UI 线程重读磁盘。</summary>
    private void OnCoreInstallationChanged(object? sender, CoreInstallationChangedEventArgs args) =>
        DispatchToUi(() =>
        {
            OnPropertyChanged(nameof(CoreVersionText));
            OnPropertyChanged(nameof(CoreVersionShortText));
        });

    private void NotifyConfigurationChanged()
    {
        OnPropertyChanged(nameof(PortText));
        OnPropertyChanged(nameof(TokenMasked));
        OnPropertyChanged(nameof(TokenDisplay));
        OnPropertyChanged(nameof(CoreVersionText));
        OnPropertyChanged(nameof(CoreVariantText));
        OnPropertyChanged(nameof(EndpointItems));
        OnPropertyChanged(nameof(ListenHostText));
    }

    private RuntimeConfig? LoadRuntimeConfig()
    {
        try
        {
            _configurationDiagnostic = null;
            return DesktopConfigReader.Read(_settingsStore, _paths.NodeProjectDirectory);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or IOException)
        {
            _configurationDiagnostic = $"配置读取失败：{error.Message}";
            return null;
        }
    }

    private string EnvPath => Path.Combine(_paths.NodeProjectDirectory, "config", ".env");

    private string LoadRuntimeToken() => RuntimeTokenResolver.Resolve(EnvPath);

    /// <summary>Interface enumeration costs tens of milliseconds on machines with many or
    /// disconnected adapters, and endpoints are rebuilt on every notification. A short cache keeps
    /// the UI responsive while still following network changes within the TTL.</summary>
    private (string? Ipv4, string? Ipv6) ResolveCachedAddresses()
    {
        lock (_addressCacheSync)
        {
            if (DateTimeOffset.UtcNow - _addressesResolvedAt < TimeSpan.FromSeconds(2))
                return (_cachedIpv4Address, _cachedIpv6Address);
            _cachedIpv4Address = RuntimeNetworkAddressResolver.ResolvePrimaryIpv4Address();
            _cachedIpv6Address = RuntimeNetworkAddressResolver.ResolvePrimaryIpv6Address();
            _addressesResolvedAt = DateTimeOffset.UtcNow;
            return (_cachedIpv4Address, _cachedIpv6Address);
        }
    }

    private IReadOnlyList<EndpointItem> BuildEndpointItems()
    {
        if (_config is null)
        {
            return [new EndpointItem("本机 API", "当前配置无效", string.Empty, IsTokenVisible, _dialogService),
                new EndpointItem("局域网 IPv4 API", "当前配置无效", string.Empty, IsTokenVisible, _dialogService)];
        }

        // 对齐移动端双栈语义：监听 :: 时同时接受 IPv4 与 IPv6，入口两类都展示；IPv6 未分配时保留说明行。
        var items = new List<EndpointItem>
        {
            new(
                "本机 API",
                IsServiceRunning ? "本机服务入口" : "服务停止，地址暂不可用",
                IsServiceRunning ? RuntimeEndpointBuilder.BuildApiAddress("127.0.0.1", EffectivePort, _token) : string.Empty,
                IsTokenVisible,
                _dialogService),
        };

        if (_config.ListenHost is "0.0.0.0" or "::")
        {
            var addresses = ResolveCachedAddresses();
            var ipv4 = addresses.Ipv4;
            items.Add(new(
                "局域网 IPv4 API",
                ipv4 is null ? "未找到可分享的局域网 IPv4 地址" :
                    IsServiceRunning ? "兼容性最佳，推荐同一局域网使用" : "服务停止，地址暂不可用",
                ipv4 is not null && IsServiceRunning ? RuntimeEndpointBuilder.BuildApiAddress(ipv4, EffectivePort, _token) : string.Empty,
                IsTokenVisible,
                _dialogService));

            if (_config.ListenHost == "::")
            {
                var ipv6 = addresses.Ipv6;
                items.Add(new(
                    "局域网 IPv6 API",
                    ipv6 is null
                        ? "未找到可分享的稳定 IPv6 地址（需活动物理网卡上的稳定 ULA，或有默认路由的全球地址）"
                        : !IsServiceRunning ? "服务停止，地址暂不可用"
                        : RuntimeNetworkAddressResolver.IsUniqueLocalAddress(System.Net.IPAddress.Parse(ipv6))
                            ? "局域网 ULA · 仅限能够访问此 IPv6 网段的设备"
                            : "全球单播 IPv6 · 设备能否连接取决于路由与防火墙",
                    ipv6 is not null && IsServiceRunning
                        ? RuntimeEndpointBuilder.BuildApiAddress(ipv6, EffectivePort, _token)
                        : string.Empty,
                    IsTokenVisible,
                    _dialogService));
            }
        }
        else
        {
            items.Add(new("局域网 IPv4 API", "当前监听未开放局域网", string.Empty, IsTokenVisible, _dialogService));
        }

        return items;
    }

    private int EffectivePort => Runtime.Port ?? _config?.Port
        ?? throw new InvalidOperationException("当前运行配置无效");

    private void RestartRequestStats()
    {
        _requestStatsCts?.Cancel();
        _requestStatsCts?.Dispose();
        _requestStatsCts = null;
        TodayRequestsText = "今日请求未读取";
        _requestStatsTasks.RemoveAll(task => task.IsCompletedSuccessfully);
        if (_disposeCts.IsCancellationRequested || Runtime.State != DesktopRuntimeState.Running ||
            _requestRecordsClient is null) return;

        _requestStatsCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        _requestStatsTasks.Add(RequestStatsLoopAsync(Runtime, _requestStatsCts.Token));
    }

    private async Task RequestStatsLoopAsync(RuntimeSnapshot requestedRuntime, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var token = _token;
                string text;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    var result = await _requestRecordsClient!.ReadAsync(
                        "127.0.0.1", requestedRuntime.Port ?? EffectivePort, token, timeout.Token).ConfigureAwait(false);
                    text = result.Succeeded
                        ? $"今日请求 {result.TodayRequestCount.ToString(CultureInfo.InvariantCulture)} 次"
                        : $"今日请求读取失败：{RuntimeManagementClient.Redact(result.Diagnostic, token)}";
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Stopping/replacing this runtime cancels its statistics session.
                    return;
                }
                catch (OperationCanceledException)
                {
                    text = "今日请求读取失败：请求超时";
                }
                catch (Exception error)
                {
                    text = $"今日请求读取失败：{RuntimeManagementClient.Redact(error.Message, token)}";
                }

                DispatchToUi(() =>
                {
                    if (!cancellationToken.IsCancellationRequested && ReferenceEquals(Runtime, requestedRuntime))
                        TodayRequestsText = text;
                });
                // Delaying after completion also bounds load when a core responds slowly.
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal end of a statistics session; no stale failure is published.
        }
    }

    private async Task HealthLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            _controller.ReconcileLiveness();
            await RefreshHealthAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RefreshHealthAsync(CancellationToken cancellationToken)
    {
        if (Runtime.State != DesktopRuntimeState.Running)
        {
            return;
        }

        // Timer, startup and Token refresh share one read; avoid out-of-order counter samples.
        if (Interlocked.CompareExchange(ref _healthReadInProgress, 1, 0) != 0) return;
        var requestedRuntime = Runtime;
        try
        {
            var health = await _healthClient.ReadAsync("127.0.0.1", EffectivePort, cancellationToken).ConfigureAwait(false);
            var sampleSeconds = _healthClock.Elapsed.TotalSeconds;
            void Update()
            {
                if (!ReferenceEquals(Runtime, requestedRuntime))
                {
                    return;
                }

                RequestTrend.Sample(health, sampleSeconds);
                _health = health;
                _healthSampledAt = DateTimeOffset.Now;
                _healthDiagnostic = null;
                NotifyHealthChanged();
            }
            DispatchToUi(Update);
        }
        catch (RuntimeHealthException error)
        {
            void Update()
            {
                // 检查发起时仍在运行、返回时已经停止：这是正常停机竞态，不记为健康失败。
                if (ReferenceEquals(Runtime, requestedRuntime))
                {
                    _healthDiagnostic = $"健康检查失败（{error.Kind}）：{error.Message}";
                    _health = null;
                    RequestTrend.Disconnect(_healthClock.Elapsed.TotalSeconds);
                }

                NotifyHealthChanged();
            }
            DispatchToUi(Update);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disposing the host cancels the read; it does not represent a service failure.
        }
        finally
        {
            Interlocked.Exchange(ref _healthReadInProgress, 0);
        }
    }

    private void NotifyHealthChanged()
    {
        foreach (var property in new[]
        {
            nameof(DiagnosticText), nameof(ListenHostText), nameof(UptimeText), nameof(RequestCountText), nameof(PidText),
            nameof(CacheProbeText), nameof(HealthFreshnessText), nameof(EndpointItems),
        })
        {
            OnPropertyChanged(property);
        }
    }

    private void DispatchToUi(Action action)
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

    private static string MaskToken(string token) =>
        token.Length <= 2 ? new string('•', Math.Max(token.Length, 1)) : token[..2] + new string('•', Math.Min(token.Length - 2, 10));

    private static string FormatDuration(long? seconds)
    {
        if (seconds is null)
        {
            return "未运行";
        }

        var duration = TimeSpan.FromSeconds(Math.Max(seconds.Value, 0));
        return duration.TotalDays >= 1
            ? $"{duration.Days}天 {duration.Hours}时"
            : $"{duration.Hours}时 {duration.Minutes}分";
    }

    public async ValueTask DisposeAsync()
    {
        _settingsPage.SettingsChanged -= OnSettingsChanged;
        if (ConfigurationPage is not null)
        {
            ConfigurationPage.CoreConfigurationChanged -= OnCoreConfigurationChanged;
        }
        _controller.SnapshotChanged -= OnSnapshotChanged;
        if (_preparation is not null) _preparation.Changed -= OnPreparationChanged;
        if (_coreManagement is not null) _coreManagement.InstallationChanged -= OnCoreInstallationChanged;
        _disposeCts.Cancel();
        try
        {
            await Task.WhenAll(_requestStatsTasks.Append(_healthLoop)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _requestStatsCts?.Dispose();
        _disposeCts.Dispose();
    }
}
