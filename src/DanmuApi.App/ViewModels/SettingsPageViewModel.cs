using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DanmuApi.App.Services;
using DanmuApi.Platform;
using DanmuApi.Core;

namespace DanmuApi.App.ViewModels;

public sealed partial class SettingsPageViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly ThemeService? _themeService;
    public sealed record ThemeOption(string Value, string Label)
    {
        public override string ToString() => Label;
    }
    public IReadOnlyList<ThemeOption> ThemeOptions { get; } =
    [new("system", "跟随系统"), new("light", "浅色"), new("dark", "深色")];
    public bool IsThemeAvailable => _themeService is not null;

    /// <summary>当前实际呈现为深色；跟随系统时按系统外观解析。</summary>
    public bool IsDarkTheme => _themeService?.IsDarkActive ?? false;
    public bool IsSystemThemeSelected => (_themeService?.CurrentTheme ?? "system") == "system";
    public bool IsLightThemeSelected => (_themeService?.CurrentTheme ?? "system") == "light";
    public bool IsDarkThemeSelected => (_themeService?.CurrentTheme ?? "system") == "dark";

    public string ThemeToggleTooltip => !IsThemeAvailable
        ? "主题服务未接入"
        : IsDarkTheme
            ? "当前深色，点击切换为浅色"
            : "当前浅色，点击切换为深色";

    public string ThemeSummaryText => IsThemeAvailable
        ? $"当前外观：{(IsDarkTheme ? "深色" : "浅色")}（偏好：{SelectedThemeOption.Label}）"
        : "主题服务未接入";

    public ThemeOption SelectedThemeOption
    {
        get => ThemeOptions.Single(option => option.Value == (_themeService?.CurrentTheme ?? "system"));
        set
        {
            if (value is not null && value != SelectedThemeOption) SetTheme(value.Value);
        }
    }
    [ObservableProperty] private string _themeStatusText = "主题更改后立即保存并生效";

    [RelayCommand]
    private void SetTheme(string theme)
    {
        try
        {
            if (_themeService is null) throw new InvalidOperationException("主题服务未初始化");
            _themeService.SetTheme(theme);
            ThemeStatusText = $"已切换为{SelectedThemeOption.Label}，偏好已保存";
        }
        catch (Exception error)
        {
            ThemeStatusText = $"切换主题失败：{error.Message}";
            Diagnostic = ThemeStatusText;
            _diagnostics?.Record(ThemeStatusText, error);
        }
        OnPropertyChanged(nameof(SelectedThemeOption));
        NotifyThemeStateChanged();
    }

    /// <summary>标题栏太阳/月亮按钮：在浅色与深色之间立即切换并保存。</summary>
    [RelayCommand]
    private void ToggleTheme()
    {
        try
        {
            if (_themeService is null) throw new InvalidOperationException("主题服务未初始化");
            var next = _themeService.ToggleLightDark();
            ThemeStatusText = next == "dark" ? "已切换为深色，偏好已保存" : "已切换为浅色，偏好已保存";
        }
        catch (Exception error)
        {
            ThemeStatusText = $"切换主题失败：{error.Message}";
            Diagnostic = ThemeStatusText;
            _diagnostics?.Record(ThemeStatusText, error);
        }
        OnPropertyChanged(nameof(SelectedThemeOption));
        NotifyThemeStateChanged();
    }

    private void NotifyThemeStateChanged()
    {
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(IsSystemThemeSelected));
        OnPropertyChanged(nameof(IsLightThemeSelected));
        OnPropertyChanged(nameof(IsDarkThemeSelected));
        OnPropertyChanged(nameof(ThemeToggleTooltip));
        OnPropertyChanged(nameof(ThemeSummaryText));
    }

    private readonly ISettingsStore _settingsStore;
    private readonly IAutostartService _autostartService;
    private readonly IDesktopNotificationService _notifications;
    private readonly AppPaths _paths;
    private readonly IUiDialogService? _dialogService;
    private readonly ICoreUpdatePolicyStore? _updatePolicyStore;
    private readonly ICoreUpdateScheduler? _updateScheduler;
    private readonly IGithubRoutePreferenceStore? _routePreferenceStore;
    private readonly IGithubProxySpeedTester? _speedTester;
    private readonly IAdminSessionService? _adminSession;
    private readonly IGithubTokenConfigurationService? _githubTokenConfiguration;
    private readonly IAppDiagnostics? _diagnostics;

    private readonly Func<BackupPageViewModel>? _backupFactory;
    private readonly List<Task> _pendingBackupDisposals = [];
    [ObservableProperty] private BackupPageViewModel? _backup;

    public async ValueTask DisposeAsync()
    {
        if (_themeService is not null) _themeService.ThemeChanged -= OnExternalThemeChanged;
        // Settings is reused by the shell. Leaving it closes the transient backup session.
        if (IsBackupCategory) SelectedCategory = Categories[0];
        await Task.WhenAll(_pendingBackupDisposals).ConfigureAwait(false);
        _pendingBackupDisposals.Clear();
    }

    private void OnExternalThemeChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SelectedThemeOption));
        NotifyThemeStateChanged();
    }

    public event EventHandler? SettingsChanged;
    [ObservableProperty] private ApplicationUpdateViewModel? _applicationUpdates;

    [ObservableProperty]
    private string _notificationStatusText = "尚未发送测试通知";

    public sealed record NotificationLevelOption(DesktopNotificationLevel Value, string Label)
    {
        public override string ToString() => Label;
    }
    public IReadOnlyList<NotificationLevelOption> NotificationLevelOptions { get; } =
    [
        new(DesktopNotificationLevel.Off, "关闭通知"),
        new(DesktopNotificationLevel.Updates, "仅更新与依赖提醒"),
        new(DesktopNotificationLevel.StartupSuccess, "仅自启成功"),
        new(DesktopNotificationLevel.All, "全部通知"),
    ];
    private NotificationLevelOption _selectedNotificationLevelOption = null!;
    public NotificationLevelOption SelectedNotificationLevelOption
    {
        get => _selectedNotificationLevelOption;
        set
        {
            if (value is null || value == _selectedNotificationLevelOption) return;
            try
            {
                _settingsStore.Write(new Dictionary<string, string?> { [DesktopNotificationPolicy.SettingsKey] = DesktopNotificationPolicy.Serialize(value.Value) });
                if (DesktopNotificationPolicy.Read(_settingsStore.Read()) != value.Value)
                    throw new IOException("通知设置回读校验失败");
                SetProperty(ref _selectedNotificationLevelOption, value);
                NotificationStatusText = "通知偏好已保存，立即生效";
            }
            catch (Exception error)
            {
                NotificationStatusText = $"保存通知偏好失败：{error.Message}";
                Diagnostic = NotificationStatusText;
                _diagnostics?.Record(NotificationStatusText, error);
                OnPropertyChanged(nameof(SelectedNotificationLevelOption));
            }
        }
    }

    [RelayCommand]
    private async Task TestNotificationAsync()
    {
        NotificationStatusText = "正在提交 Windows 通知…";
        try
        {
            var result = await _notifications.ShowAsync(DesktopNotificationKind.ManualTest, "弹幕 API · 测试通知", "通知通道测试（绕过应用通知偏好）。点击此通知可打开应用。").ConfigureAwait(true);
            NotificationStatusText = result.Diagnostic;
        }
        catch (Exception error)
        {
            NotificationStatusText = $"通知测试失败：{error.Message}";
            _diagnostics?.Record("通知测试失败", error);
        }
    }

    [RelayCommand]
    private void OpenSystemNotifications()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true });
            if (process is null) throw new InvalidOperationException("Windows 未启动通知设置");
        }
        catch (Exception error)
        {
            NotificationStatusText = $"打开系统通知设置失败：{error.Message}";
            _diagnostics?.Record("打开系统通知设置失败", error);
        }
    }

    [ObservableProperty]
    private CloseAction _closeAction;

    [ObservableProperty]
    private CloseActionOption _selectedCloseActionOption;

    [ObservableProperty]
    private bool _isAutostartEnabled;

    [ObservableProperty]
    private bool _isAutostartSupported;

    [ObservableProperty]
    private bool _isAutostartBusy;

    [ObservableProperty]
    private string _autostartStatusText = string.Empty;

    [ObservableProperty]
    private string? _diagnostic;

    [ObservableProperty]
    private int _foregroundCheckIntervalMinutes;

    [ObservableProperty]
    private int _backgroundCheckIntervalMinutes;

    [ObservableProperty]
    private bool _backgroundCheckEnabled;

    [ObservableProperty]
    private bool _ipv6Enabled;

    [ObservableProperty]
    private CoreUpdateAction _updateAction;

    [ObservableProperty]
    private string _adminTokenInput = string.Empty;

    [ObservableProperty]
    private string _adminTokenConfirmation = string.Empty;

    [ObservableProperty]
    private string _githubTokenStatusText = "GitHub Token 状态尚未读取";

    [ObservableProperty]
    private bool _isGithubTokenBusy;

    public SettingsPageViewModel(
        ISettingsStore settingsStore,
        IAutostartService autostartService,
        IDesktopNotificationService notifications,
        AppPaths paths,
        IUiDialogService? dialogService = null,
        ICoreUpdatePolicyStore? updatePolicyStore = null,
        ICoreUpdateScheduler? updateScheduler = null,
        IGithubRoutePreferenceStore? routePreferenceStore = null,
        IGithubProxySpeedTester? speedTester = null,
        IAdminSessionService? adminSession = null,
        IGithubTokenConfigurationService? githubTokenConfiguration = null,
        IAppDiagnostics? diagnostics = null,
        Func<BackupPageViewModel>? backupFactory = null,
        ThemeService? themeService = null)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _autostartService = autostartService ?? throw new ArgumentNullException(nameof(autostartService));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _dialogService = dialogService;
        _updatePolicyStore = updatePolicyStore;
        _updateScheduler = updateScheduler;
        _routePreferenceStore = routePreferenceStore;
        _speedTester = speedTester;
        _adminSession = adminSession;
        _githubTokenConfiguration = githubTokenConfiguration;
        _diagnostics = diagnostics;
        _themeService = themeService;
        _backupFactory = backupFactory;
        if (_themeService is not null) _themeService.ThemeChanged += OnExternalThemeChanged;
        var values = _settingsStore.Read();
        _selectedNotificationLevelOption = NotificationLevelOptions.Single(option => option.Value == DesktopNotificationPolicy.Read(values));
        _closeAction = CloseActionParser.Parse(values.TryGetValue("close_action", out var value) ? value : null);
        CloseActionOptions =
        [
            new(CloseAction.Ask, "每次询问"),
            new(CloseAction.Exit, "退出并关闭服务"),
            new(CloseAction.Tray, "后台运行"),
        ];
        _selectedCloseActionOption = CloseActionOptions.First(option => option.Value == _closeAction);
        _selectedCategory = Categories[0];
        var policy = _updatePolicyStore?.Read() ?? CoreUpdateScheduleOptions.Default;
        _foregroundCheckIntervalMinutes = (int)policy.ForegroundInterval.TotalMinutes;
        _backgroundCheckIntervalMinutes = (int)policy.BackgroundInterval.TotalMinutes;
        _backgroundCheckEnabled = policy.BackgroundEnabled;
        _ipv6Enabled = ReadBoolean(values, "ipv6_enabled");
        _updateAction = policy.UpdateAction;
        RefreshGithubTokenStatus();
        RefreshAutostartStatus();
    }

    public IReadOnlyList<CloseActionOption> CloseActionOptions { get; }

    public IReadOnlyList<SettingsCategory> Categories { get; } =
    [
        new("general", "常规与启动", "关闭行为和开机自启"),
        new("theme", "主题与外观", "浅色、深色和跟随系统"),
        new("service", "服务", "端口、监听地址和运行状态"),
        new("storage", "运行目录与存储", "运行数据、日志和下载缓存"),
        new("backup", "备份与恢复", "本地备份、迁移和 WebDAV"),
        new("network", "网络与 GitHub", "代理线路和联网设置"),
        new("security", "安全", "Token 和访问控制"),
        new("diagnostics", "诊断", "运行诊断和日志入口"),
        new("about", "关于与更新", "版本和更新信息"),
    ];

    [ObservableProperty]
    private SettingsCategory _selectedCategory = null!;

    private AutostartStatus? _autostartState;
    public bool CanChangeAutostart => (_autostartState?.CanToggle ?? IsAutostartSupported) && !IsAutostartBusy;
    public bool IsAutostartStateKnown => _autostartState?.EffectiveState is not (AutostartState.Unknown or AutostartState.Unsupported);
    public string AutostartStateLabel => _autostartState?.EffectiveState switch
    {
        AutostartState.Unknown => "状态未知",
        AutostartState.Unsupported => "不支持",
        AutostartState.SystemDisabled => "系统已禁用",
        AutostartState.InvalidPath => "路径失效",
        AutostartState.Registered => "已登记",
        _ => "未登记",
    };
    public string RuntimeRoot => _paths.NodeProjectDirectory;
    public string RuntimeRootParent => _paths.Root;
    public string AutostartUserStatusText => !IsAutostartSupported
        ? "当前构建不支持"
        : IsAutostartEnabled
            ? "已启用"
            : "未启用";
    public string SelectedCategoryDescription => SelectedCategory?.Description ?? string.Empty;
    public bool HasDiagnostic => !string.IsNullOrWhiteSpace(Diagnostic);
    public bool IsGeneralCategory => SelectedCategory?.Key == "general";
    public bool IsThemeCategory => SelectedCategory?.Key == "theme";
    public bool IsServiceCategory => SelectedCategory?.Key == "service";
    public bool IsStorageCategory => SelectedCategory?.Key == "storage";
    public bool IsBackupCategory => SelectedCategory?.Key == "backup";
    public bool IsNetworkCategory => SelectedCategory?.Key == "network";
    public bool IsSecurityCategory => SelectedCategory?.Key == "security";
    public bool IsDiagnosticsCategory => SelectedCategory?.Key == "diagnostics";
    public bool IsAboutCategory => SelectedCategory?.Key == "about";

    public string CloseActionDescription => CloseAction switch
    {
        CloseAction.Ask => "关闭主窗口时显示选择对话框。",
        CloseAction.Exit => "关闭主窗口时停止服务并退出应用。",
        CloseAction.Tray => "关闭主窗口时隐藏到托盘，服务继续运行。",
        _ => string.Empty,
    };

    public string GithubTokenActionText => IsGithubTokenBusy ? "处理中…" : "验证并配置 Token";

    partial void OnIsGithubTokenBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(GithubTokenActionText));
    }

    [RelayCommand(CanExecute = nameof(CanConfigureGithubToken))]
    private async Task ConfigureGithubTokenAsync()
    {
        if (_githubTokenConfiguration is null || _dialogService is null)
        {
            SetGithubTokenDiagnostic("GitHub Token 配置服务尚未接入", null);
            return;
        }

        IsGithubTokenBusy = true;
        ConfigureGithubTokenCommand.NotifyCanExecuteChanged();
        try
        {
            var state = _githubTokenConfiguration.GetState();
            var dialogResult = await _dialogService
                .PromptGithubTokenAsync(state.Configured, state.Hint)
                .ConfigureAwait(true);
            if (dialogResult.Cancelled)
            {
                return;
            }

            GithubTokenConfigurationResult result;
            if (dialogResult.Clear)
            {
                result = await _githubTokenConfiguration.ClearAsync().ConfigureAwait(true);
            }
            else
            {
                var token = dialogResult.Token?.Trim();
                if (string.IsNullOrWhiteSpace(token))
                {
                    SetGithubTokenDiagnostic("请输入 Token；留空不会删除现有 Token。", null);
                    return;
                }

                result = await _githubTokenConfiguration
                    .ValidateAndSaveAsync(token)
                    .ConfigureAwait(true);
            }

            GithubTokenStatusText = result.State.Configured
                ? $"已配置（{result.State.Hint}）"
                : "未配置";
            OnPropertyChanged(nameof(GithubTokenActionText));
            if (!result.Succeeded)
            {
                SetGithubTokenDiagnostic(result.Diagnostic, null);
                await _dialogService.ShowMessageAsync(
                    "配置 GitHub Token 失败",
                    result.Diagnostic,
                    isError: true).ConfigureAwait(true);
                return;
            }

            Diagnostic = null;
            await _dialogService.ShowMessageAsync(
                "GitHub Token",
                result.Diagnostic).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            SetGithubTokenDiagnostic($"GitHub Token 配置失败：{error.Message}", error);
        }
        finally
        {
            IsGithubTokenBusy = false;
            ConfigureGithubTokenCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanConfigureGithubToken() => !IsGithubTokenBusy;

    private void RefreshGithubTokenStatus()
    {
        if (_githubTokenConfiguration is null)
        {
            GithubTokenStatusText = "GitHub Token 配置服务尚未接入";
            return;
        }

        try
        {
            var state = _githubTokenConfiguration.GetState();
            GithubTokenStatusText = state.Configured
                ? $"已配置（{state.Hint}）"
                : "未配置";
        }
        catch (Exception error)
        {
            SetGithubTokenDiagnostic("读取 GitHub Token 状态失败", error);
        }
    }

    private void SetGithubTokenDiagnostic(string message, Exception? error)
    {
        Diagnostic = message;
        if (error is not null)
        {
            _diagnostics?.Record(message, error);
        }
    }


    public string AutostartEnabledText => IsAutostartEnabled ? "已启用" : "未启用";
    public IReadOnlyList<CoreUpdateActionOption> UpdateActionOptions { get; } =
    [
        new(CoreUpdateAction.Notify, "发现更新后通知"),
        new(CoreUpdateAction.Automatic, "发现更新后自动更新"),
    ];
    public string UpdateActionDescription => UpdateAction switch
    {
        CoreUpdateAction.Notify => "后台发现更新后只通知，不会自动停止服务或替换核心。",
        CoreUpdateAction.Automatic => "发现更新后会在同一事务内停止服务、替换核心并尝试恢复；失败会保留待处理更新。",
        _ => string.Empty,
    };
    public string Ipv6StatusText => Ipv6Enabled
        ? "已开启：同时接受 IPv4 与 IPv6 连接（对齐移动端双栈模式），保存后服务会自动重启。"
        : "未开启：仅接受 IPv4 连接。";

    public string GithubRouteStatusText
    {
        get
        {
            if (_routePreferenceStore is null)
            {
                return "线路服务尚未接入";
            }

            var preference = _routePreferenceStore.Read();
            var option = GithubProxyCatalog.GetById(preference.ProxyId);
            return preference.Confirmed ? $"已确认：{option.Label}" : "尚未确认，首次下载前会要求选择";
        }
    }

    public bool IsAdminMode => _adminSession?.State.IsAdminMode == true;
    public bool HasAdminTokenConfigured => _adminSession?.State.HasAdminTokenConfigured == true;
    public bool IsAdminFirstSetup => !IsAdminMode && !HasAdminTokenConfigured;
    public bool IsAdminLoginRequired => !IsAdminMode && HasAdminTokenConfigured;
    public bool AdminLoginVisible => !IsAdminMode;
    public string AdminModeStatusText => _adminSession is null
        ? "管理员会话服务未接入"
        : IsAdminMode
            ? "管理员模式已开启"
            : HasAdminTokenConfigured
                ? "管理员密码已配置，当前处于普通模式"
                : "尚未设置管理员密码";
    public string AdminTokenStatusText
    {
        get
        {
            if (_adminSession is null)
            {
                return string.Empty;
            }

            var state = _adminSession.State;
            return state.HasAdminTokenConfigured
                ? $"ADMIN_TOKEN 已配置（{state.TokenHint}）"
                : "核心 .env 中没有 ADMIN_TOKEN；应用不会生成或使用内置管理员密码。";
        }
    }
    public string AdminSecurityDescription => IsAdminMode
        ? "缓存清理、访问 Token 与核心配置写入已解锁；退出后立即恢复只读门禁。"
        : HasAdminTokenConfigured
            ? "输入已配置的 ADMIN_TOKEN 验证身份。错误输入不会修改 .env。"
            : "首次设置会把你输入的密码原子写入当前核心 config/.env，并在回读一致后进入管理员模式。";
    public string AdminLoginLabel => HasAdminTokenConfigured
        ? "验证并进入管理员模式"
        : "设置密码并进入管理员模式";
    public bool CanSubmitAdmin => !string.IsNullOrWhiteSpace(AdminTokenInput) &&
                                  (HasAdminTokenConfigured ||
                                   string.Equals(AdminTokenInput, AdminTokenConfirmation, StringComparison.Ordinal));

    partial void OnAdminTokenInputChanged(string value)
    {
        OnPropertyChanged(nameof(CanSubmitAdmin));
        AdminLoginCommand.NotifyCanExecuteChanged();
    }

    partial void OnAdminTokenConfirmationChanged(string value)
    {
        OnPropertyChanged(nameof(CanSubmitAdmin));
        AdminLoginCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSubmitAdmin))]
    private void AdminLogin()
    {
        try
        {
            if (_adminSession is null)
            {
                throw new InvalidOperationException("管理员会话服务未接入");
            }

            _adminSession.Refresh();
            var state = _adminSession.State;
            if (!state.HasAdminTokenConfigured &&
                !string.Equals(AdminTokenInput, AdminTokenConfirmation, StringComparison.Ordinal))
            {
                Diagnostic = "两次输入的管理员密码不一致";
                return;
            }

            var result = state.HasAdminTokenConfigured
                ? _adminSession.Login(AdminTokenInput)
                : _adminSession.SetAdminTokenAndLogin(AdminTokenInput);
            if (!result.Succeeded)
            {
                Diagnostic = result.Diagnostic;
                return;
            }

            Diagnostic = null;
            ClearAdminInputs();
            NotifyAdminStateChanged();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            Diagnostic = $"进入管理员模式失败：{error.Message}";
        }
    }

    [RelayCommand]
    private void AdminLogout()
    {
        try
        {
            if (_adminSession is null)
            {
                throw new InvalidOperationException("管理员会话服务未接入");
            }

            var result = _adminSession.Logout();
            Diagnostic = result.Succeeded ? null : result.Diagnostic;
            ClearAdminInputs();
            NotifyAdminStateChanged();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Diagnostic = $"退出管理员模式失败：{error.Message}";
        }
    }

    private void ClearAdminInputs()
    {
        AdminTokenInput = string.Empty;
        AdminTokenConfirmation = string.Empty;
    }

    private void NotifyAdminStateChanged()
    {
        OnPropertyChanged(nameof(IsAdminMode));
        OnPropertyChanged(nameof(HasAdminTokenConfigured));
        OnPropertyChanged(nameof(IsAdminFirstSetup));
        OnPropertyChanged(nameof(IsAdminLoginRequired));
        OnPropertyChanged(nameof(AdminLoginVisible));
        OnPropertyChanged(nameof(AdminModeStatusText));
        OnPropertyChanged(nameof(AdminTokenStatusText));
        OnPropertyChanged(nameof(AdminSecurityDescription));
        OnPropertyChanged(nameof(AdminLoginLabel));
        OnPropertyChanged(nameof(CanSubmitAdmin));
        AdminLoginCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task ChooseGithubRouteAsync()
    {
        try
        {
            if (_dialogService is null || _routePreferenceStore is null || _speedTester is null)
            {
                throw new InvalidOperationException("GitHub 线路服务尚未接入");
            }

            var preference = _routePreferenceStore.Read();
            var selected = await _dialogService.ChooseGithubRouteAsync(
                "首次下载、检查更新或线路失败后，请先测速并确认一条线路。",
                preference.ProxyId,
                _speedTester).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(selected))
            {
                return;
            }

            _routePreferenceStore.Confirm(selected);
            OnPropertyChanged(nameof(GithubRouteStatusText));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            Diagnostic = $"保存 GitHub 下载线路失败：{error.Message}";
        }
    }

    private void PersistCloseAction()
    {
        try
        {
            _settingsStore.Write(new Dictionary<string, string?>
            {
                ["close_action"] = CloseActionParser.ToStorageValue(CloseAction),
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Diagnostic = $"保存关闭行为失败：{error.Message}";
        }
    }

    [RelayCommand]
    private async Task OpenRuntimeRootAsync()
    {
        try
        {
            if (_dialogService is null)
            {
                throw new InvalidOperationException("目录操作服务尚未接入");
            }

            await _dialogService.OpenDirectoryAsync(RuntimeRoot).ConfigureAwait(true);
            Diagnostic = "核心运行目录已打开。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Diagnostic = $"打开核心运行目录失败：{error.Message}";
        }
    }

    [RelayCommand]
    private async Task ChooseRuntimeRootAsync()
    {
        try
        {
            if (_dialogService is null)
            {
                throw new InvalidOperationException("目录操作服务尚未接入");
            }

            var selected = await _dialogService.ChooseRuntimeRootAsync().ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(selected))
            {
                return;
            }

            if (!Path.IsPathFullyQualified(selected))
            {
                throw new FormatException("运行数据位置必须是绝对路径");
            }

            _settingsStore.Write(new Dictionary<string, string?>
            {
                ["runtime_root"] = selected.Trim(),
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            Diagnostic = $"保存运行数据位置失败：{error.Message}";
        }
    }

    private void PersistUpdatePolicy()
    {
        try
        {
            if (_updatePolicyStore is null || _updateScheduler is null)
            {
                throw new InvalidOperationException("更新调度服务尚未接入");
            }

            var options = new CoreUpdateScheduleOptions(
                TimeSpan.FromMinutes(ForegroundCheckIntervalMinutes),
                TimeSpan.FromMinutes(BackgroundCheckIntervalMinutes),
                BackgroundCheckEnabled,
                UpdateAction);
            _updatePolicyStore.Write(options);
            _updateScheduler.NotifyPolicyChanged();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            Diagnostic = $"保存核心更新设置失败：{error.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanChangeAutostart))]
    private async Task ToggleAutostartAsync()
    {
        IsAutostartBusy = true;
        OnPropertyChanged(nameof(CanChangeAutostart));
        Diagnostic = null;

        try
        {
            var result = await _autostartService.SetEnabledAsync(!IsAutostartEnabled).ConfigureAwait(true);
            ApplyAutostartStatus(result.Status);
            OnPropertyChanged(nameof(AutostartEnabledText));
            if (!result.Succeeded)
            {
                Diagnostic = result.Diagnostic;
            }

            var notificationMessage = string.IsNullOrWhiteSpace(result.Diagnostic)
                ? (result.Succeeded ? "开机自启设置已更新" : "开机自启操作失败")
                : result.Diagnostic;
            var notification = await _notifications.ShowAsync(
                "弹幕 API",
                notificationMessage).ConfigureAwait(true);
            if (notification.Status == DesktopNotificationStatus.Failed)
            {
                Diagnostic = string.IsNullOrWhiteSpace(Diagnostic)
                    ? notification.Diagnostic
                    : $"{Diagnostic}；{notification.Diagnostic}";
            }
        }
        catch (Exception error)
        {
            Diagnostic = $"更新开机自启失败：{error.Message}";
        }
        finally
        {
            IsAutostartBusy = false;
            OnPropertyChanged(nameof(CanChangeAutostart));
            ToggleAutostartCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnUpdateActionChanged(CoreUpdateAction value)
    {
        OnPropertyChanged(nameof(UpdateActionDescription));
        PersistUpdatePolicy();
    }

    partial void OnForegroundCheckIntervalMinutesChanged(int value)
    {
        PersistUpdatePolicy();
    }

    partial void OnBackgroundCheckIntervalMinutesChanged(int value)
    {
        PersistUpdatePolicy();
    }

    partial void OnBackgroundCheckEnabledChanged(bool value)
    {
        PersistUpdatePolicy();
    }

    partial void OnIpv6EnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(Ipv6StatusText));
        try
        {
            _settingsStore.Write(new Dictionary<string, string?>
            {
                ["ipv6_enabled"] = value ? "true" : "false",
            });
            Diagnostic = null;
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
        {
            Diagnostic = $"保存 IPv6 监听设置失败：{error.Message}";
        }
    }

    partial void OnIsAutostartSupportedChanged(bool value)
    {
        OnPropertyChanged(nameof(AutostartUserStatusText));
    }

    partial void OnIsAutostartEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(AutostartUserStatusText));
    }

    partial void OnDiagnosticChanged(string? value)
    {
        OnPropertyChanged(nameof(HasDiagnostic));
    }

    partial void OnSelectedCloseActionOptionChanged(CloseActionOption value)
    {
        CloseAction = value.Value;
        OnPropertyChanged(nameof(CloseActionDescription));
        PersistCloseAction();
    }

    partial void OnSelectedCategoryChanged(SettingsCategory value)
    {
        if (Backup is not null)
        {
            _pendingBackupDisposals.Add(Backup.DisposeAsync().AsTask());
            Backup = null;
        }
        if (value.Key == "backup")
        {
            Backup = (_backupFactory ?? throw new InvalidOperationException("备份页面未注册"))();
        }
        OnPropertyChanged(nameof(IsBackupCategory));
        OnPropertyChanged(nameof(SelectedCategoryDescription));
        OnPropertyChanged(nameof(IsGeneralCategory));
        OnPropertyChanged(nameof(IsThemeCategory));
        OnPropertyChanged(nameof(IsServiceCategory));
        OnPropertyChanged(nameof(IsStorageCategory));
        OnPropertyChanged(nameof(IsNetworkCategory));
        OnPropertyChanged(nameof(IsSecurityCategory));
        OnPropertyChanged(nameof(IsDiagnosticsCategory));
        OnPropertyChanged(nameof(IsAboutCategory));
        if (value?.Key == "security")
        {
            ClearAdminInputs();
            // .env 可能被核心工具链外部修改，进入安全页时以磁盘为准收敛会话状态。
            try
            {
                _adminSession?.Refresh();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Diagnostic = $"读取管理员配置失败：{error.Message}";
            }

            NotifyAdminStateChanged();
        }
    }

    private static bool ReadBoolean(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!bool.TryParse(value, out var result))
        {
            throw new FormatException($"设置 {key} 必须是 true 或 false");
        }

        return result;
    }

    [RelayCommand]
    private void RefreshAutostartStatus() => ApplyAutostartStatus(_autostartService.GetStatus());

    private void ApplyAutostartStatus(AutostartStatus status)
    {
        _autostartState = status;
        IsAutostartSupported = status.IsSupported;
        if (status.IsRegistered is bool registered) IsAutostartEnabled = registered;
        AutostartStatusText = status.Diagnostic;
        OnPropertyChanged(nameof(CanChangeAutostart));
        OnPropertyChanged(nameof(IsAutostartStateKnown));
        OnPropertyChanged(nameof(AutostartStateLabel));
        ToggleAutostartCommand.NotifyCanExecuteChanged();
        _diagnostics?.Record($"自启状态：{status.EffectiveState}；{status.Diagnostic}");
    }
}

public sealed record CloseActionOption(CloseAction Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record SettingsCategory(string Key, string Title, string Description)
{
    public override string ToString() => Title;
}

public sealed record CoreUpdateActionOption(CoreUpdateAction Value, string Label)
{
    public override string ToString() => Label;
}
