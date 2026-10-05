using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DanmuApi.App.Services;
using DanmuApi.Core;
using DanmuApi.Core.Frp;
using DanmuApi.Runtime.Frp;

namespace DanmuApi.App.ViewModels;

/// <summary>Token intent is part of the unsaved draft, not a side effect of importing or clearing.</summary>
public enum FrpTokenEditIntent { Keep, Set, Clear }

public sealed record FrpRoleOption(FrpRole Value, string Title, string Description)
{
    public override string ToString() => Title;
}

public sealed record FrpProxyKindOption(FrpProxyKind Value, string Title, string Description)
{
    public override string ToString() => Title;
}

/// <summary>两个独立的配置草稿；保存当前模式后才改变下次启动的配置来源。</summary>
public enum FrpConfigSection
{
    Visual,
    Text,
}

public sealed record FrpConfigSectionOption(FrpConfigSection Value, string Title, string Description)
{
    public override string ToString() => Title;
}

/// <summary>可视化与原生文本分别维护草稿、校验和保存；状态刷新不得覆盖任何未保存的输入。</summary>
public sealed partial class FrpTunnelConfigViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IFrpTunnelService _tunnel;
    private readonly IUiDialogService _dialogService;
    private readonly IAppDiagnostics _diagnostics;
    private readonly Func<int> _defaultLocalPort;
    private readonly SynchronizationContext? _uiContext;
    private readonly System.Collections.ObjectModel.ObservableCollection<string> _problems = [];
    private bool _disposed;
    private bool _isLoadingFields;
    private string _lastSavedFingerprint = string.Empty;
    private string _formFingerprintAtLoad = string.Empty;
    private string _textAtLoad = string.Empty;
    private FrpConfigSection _sectionAtLoad;

    [ObservableProperty] private FrpRoleOption _selectedRoleOption = null!;
    [ObservableProperty] private FrpConfigSectionOption _selectedSectionOption = null!;
    [ObservableProperty] private FrpProxyKindOption _selectedProxyKindOption = null!;
    [ObservableProperty] private string _serverAddress = string.Empty;
    [ObservableProperty] private string _serverPortText = string.Empty;
    [ObservableProperty] private string _user = string.Empty;
    [ObservableProperty] private string? _tokenInput;
    [ObservableProperty] private FrpTokenEditIntent _tokenIntent;
    [ObservableProperty] private bool _tokenConfigured;
    [ObservableProperty] private string _proxyName = string.Empty;
    [ObservableProperty] private string _localAddress = string.Empty;
    [ObservableProperty] private string _localPortText = string.Empty;
    [ObservableProperty] private string _remotePortText = string.Empty;
    [ObservableProperty] private string _customDomainsText = string.Empty;
    [ObservableProperty] private bool _useEncryption;
    [ObservableProperty] private bool _useCompression;
    [ObservableProperty] private bool _transportTls;
    [ObservableProperty] private string _clientAdminPortText = string.Empty;
    [ObservableProperty] private string _bindPortText = string.Empty;
    [ObservableProperty] private string _vhostHttpPortText = string.Empty;
    [ObservableProperty] private string _subdomainHost = string.Empty;
    [ObservableProperty] private string _serverAdminPortText = string.Empty;
    [ObservableProperty] private string _operationMessage = string.Empty;
    [ObservableProperty] private string _validationText = string.Empty;
    /// <summary>「配置文本」页签自己的结果框：粘贴/生成的结果与问题都在这里，与表单校验分开。</summary>
    [ObservableProperty] private string _configText = string.Empty;
    [ObservableProperty] private string _configTextMessage = string.Empty;
    [ObservableProperty] private bool _configTextFailed;
    [ObservableProperty] private bool _showConfigText = true;
    [ObservableProperty] private string _configTextSummary = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public FrpTunnelConfigViewModel(
        IFrpTunnelService tunnel,
        IUiDialogService dialogService,
        IAppDiagnostics diagnostics,
        Func<int> defaultLocalPort)
    {
        _tunnel = tunnel ?? throw new ArgumentNullException(nameof(tunnel));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _defaultLocalPort = defaultLocalPort ?? throw new ArgumentNullException(nameof(defaultLocalPort));
        _uiContext = SynchronizationContext.Current;

        RoleOptions =
        [
            new(FrpRole.Client, "客户端（frpc）", "把本机弹幕服务送到你的 frps，供外网访问"),
            new(FrpRole.Server, "服务端（frps）", "本机自己充当穿透服务器，需要公网 IP 或端口映射"),
        ];
        ProxyKindOptions =
        [
            new(FrpProxyKind.Tcp, "TCP 端口", "在服务器上开一个端口直连本机服务，兼容性最好"),
            new(FrpProxyKind.Http, "HTTP 域名", "用域名访问，需服务器配置 vhostHTTPPort"),
            new(FrpProxyKind.Https, "HTTPS 域名", "用域名访问，需服务器配置 vhostHTTPSPort"),
        ];
        SectionOptions =
        [
            new(FrpConfigSection.Visual, "可视化配置", "按字段配置服务器与端口映射"),
            new(FrpConfigSection.Text, "JSON 配置", "完整 JSON / TOML，保存后直接启动"),
        ];
        _selectedSectionOption = SectionOptions[0];

        LoadSavedDrafts(_tunnel.Settings);
        _tunnel.Changed += OnTunnelChanged;
        _ = LoadAsync();
    }

    public IReadOnlyList<FrpRoleOption> RoleOptions { get; }

    public IReadOnlyList<FrpProxyKindOption> ProxyKindOptions { get; }

    public IReadOnlyList<FrpConfigSectionOption> SectionOptions { get; }

    public IReadOnlyList<string> Problems => _problems;

    public bool HasProblems => _problems.Count > 0;

    public bool IsClientRole => SelectedRoleOption.Value == FrpRole.Client;

    public bool IsServerRole => !IsClientRole;

    public bool ShowTcpFields => IsClientRole && SelectedProxyKindOption.Value == FrpProxyKind.Tcp;

    public bool ShowDomainFields => IsClientRole && !ShowTcpFields;

    public bool IsVisualSection => SelectedSectionOption.Value == FrpConfigSection.Visual;

    public bool IsTextSection => !IsVisualSection;

    public bool CanClearToken => TokenConfigured || TokenIntent == FrpTokenEditIntent.Set;

    public bool HasTokenEdit => TokenIntent != FrpTokenEditIntent.Keep;

    public string TokenHint => TokenIntent switch
    {
        FrpTokenEditIntent.Set => "新的 Token 尚未保存；导出使用此草稿 Token",
        FrpTokenEditIntent.Clear => "将清除 Token（尚未保存）；导出不含 Token",
        _ => TokenConfigured ? "已保存 Token；留空表示保持不变" : "未设置",
    };

    public string ConfigPathText => _tunnel.ConfigPath;

    public string RoleHint => IsClientRole
        ? "本机作为 frpc 连接你的 frps；穿透成功后的外网地址由服务器分配。"
        : "本机作为 frps 对外提供穿透端口；请在路由器/安全组放行端口，并把服务器地址给客户端。";

    public bool IsReadOnly => IsBusy;

    public bool HasConfigTextMessage => ConfigTextMessage.Length > 0;

    public bool HasConfigText => !string.IsNullOrWhiteSpace(ConfigText);

    public bool ShowVisualProblems => IsVisualSection && HasProblems;

    public string SaveButtonText => IsVisualSection ? "保存可视化配置" : "保存 JSON 配置";

    public string SavedModeText => _tunnel.Settings.ConfigMode == FrpConfigMode.Text
        ? "已保存的启动来源：JSON / TOML 配置"
        : "已保存的启动来源：可视化配置";

    public bool HasUnsavedVisualEdits => !string.Equals(SettingsFormFingerprint(), _formFingerprintAtLoad, StringComparison.Ordinal);

    public bool HasUnsavedTextEdits => !string.Equals(ConfigText, _textAtLoad, StringComparison.Ordinal);

    public bool HasUnsavedEdits => HasUnsavedVisualEdits || HasUnsavedTextEdits || SelectedSectionOption.Value != _sectionAtLoad;

    public string DraftHint => SelectedSectionOption.Value != _sectionAtLoad
        ? "保存当前模式后，下次启动将改用此配置；另一种配置保留。"
        : (IsTextSection ? HasUnsavedTextEdits : HasUnsavedVisualEdits)
            ? "当前配置有未保存的更改；启动仍使用上次保存的配置。"
            : "当前配置已保存；保存不会自动启动或中断现有连接。";

    public string HostOverlayHint => "运行时由宿主管理回环状态接口及其凭据、控制台日志和断线重连；原始文本、服务商认证与全部代理保持独立保存。";

    [RelayCommand]
    private async Task ReloadAsync()
    {
        await RunAsync(async () =>
        {
            if (HasUnsavedEdits && !await _dialogService.ConfirmAsync(
                    "重新读取穿透配置",
                    "这会放弃两种模式中尚未保存的输入，恢复已保存的配置。切换模式不会丢弃输入，无需重新读取。",
                    "放弃修改并重新读取").ConfigureAwait(true))
                return;

            var result = await _tunnel.ReloadSettingsAsync().ConfigureAwait(true);
            if (result.Succeeded)
            {
                LoadSavedDrafts(_tunnel.Settings);
                OperationMessage = "已重新读取保存的配置。";
            }
            else
            {
                ShowOperationFailure(result.Message);
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        await RunAsync(async () =>
        {
            if (IsTextSection)
            {
                var textResult = await _tunnel.SaveTextAsync(ConfigText).ConfigureAwait(true);
                ConfigTextFailed = !textResult.Succeeded;
                ConfigTextMessage = textResult.Message;
                OperationMessage = textResult.Message;
                if (textResult.Succeeded)
                {
                    _textAtLoad = _tunnel.Settings.RawConfig;
                    MarkSavedMode();
                }
                return;
            }

            var settings = BuildSettings(out var problems);
            if (settings is null)
            {
                ShowProblems(problems);
                return;
            }

            var result = await _tunnel.SaveAsync(
                settings,
                TokenIntent == FrpTokenEditIntent.Set ? TokenInput!.Trim() : null,
                clearToken: TokenIntent == FrpTokenEditIntent.Clear).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                ShowProblems([result.Message]);
                OperationMessage = result.Message;
                return;
            }

            OperationMessage = result.Message;
            LoadFields(_tunnel.Settings);
            MarkSavedMode();
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private void UseCurrentServicePort()
    {
        var port = _defaultLocalPort();
        LocalPortText = port.ToString(CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(LocalAddress))
        {
            LocalAddress = FrpClientSettings.DefaultLocalAddress;
        }

        OperationMessage = $"已填入本机弹幕服务端口 {port.ToString(CultureInfo.InvariantCulture)}。";
    }

    [RelayCommand]
    private async Task ClearTokenAsync()
    {
        if (IsBusy || !CanClearToken)
        {
            return;
        }

        await RunAsync(async () =>
        {
            if (!await _dialogService.ConfirmAsync(
                    "清除穿透 Token",
                    "清除将暂存到表单，点「保存可视化配置」后生效。客户端连接与服务端认证都将不再使用 Token；若远端仍要求 Token，连接会被拒绝。",
                    "暂存清除").ConfigureAwait(true))
            {
                return;
            }

            TokenInput = null;
            TokenIntent = FrpTokenEditIntent.Clear;
            OperationMessage = "已暂存清除 Token；点「保存可视化配置」后生效。";
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private void KeepToken()
    {
        if (IsBusy) return;
        TokenInput = null;
        TokenIntent = FrpTokenEditIntent.Keep;
        OperationMessage = "已撤销 Token 草稿修改；其他表单输入保持不变。";
    }

    /// <summary>当前 Token 草稿对应的实际值（导出与生成文本都取这个口径，不能把新参数与旧 Token 混在一起）。</summary>
    private string EffectiveToken() => TokenIntent switch
    {
        FrpTokenEditIntent.Set => TokenInput!.Trim(),
        FrpTokenEditIntent.Clear => string.Empty,
        _ => _tunnel.ReadAuthToken(),
    };

    /// <summary>生成只初始化文本草稿，不保存或转换另一种配置来源。</summary>
    [RelayCommand]
    private async Task GenerateConfigTextAsync()
    {
        await RunAsync(async () =>
        {
            if (HasConfigText && !await _dialogService.ConfirmAsync(
                    "替换 JSON 配置草稿",
                    "将用当前可视化草稿生成的 JSON 替换文本编辑器里的内容；已保存的文本配置不会改变，直到再次保存。",
                    "替换文本草稿").ConfigureAwait(true))
                return;

            var settings = BuildSettings(out var problems);
            if (settings is null)
            {
                ConfigTextFailed = true;
                ConfigTextMessage = "无法从可视化草稿生成：" + string.Join("；", problems);
                return;
            }

            ConfigText = FrpConfigJson.Export(settings, EffectiveToken());
            ShowConfigText = true;
            SelectedSectionOption = SectionOptions.Single(option => option.Value == FrpConfigSection.Text);
            ConfigTextFailed = false;
            ConfigTextMessage = "已生成独立的 JSON 草稿，尚未保存。可直接编辑并保存，无需再导入可视化配置。";
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CopyConfigTextAsync()
    {
        if (!HasConfigText)
        {
            return;
        }

        try
        {
            await _dialogService.CopyTextAsync(ConfigText).ConfigureAwait(true);
            ConfigTextMessage = "已复制到剪贴板。";
            ConfigTextFailed = false;
        }
        catch (Exception error)
        {
            var failure = $"复制配置失败：{error.GetType().Name}（0x{error.HResult:X8}）";
            _diagnostics.Record(failure + Environment.NewLine + error.StackTrace);
            ConfigTextFailed = true;
            ConfigTextMessage = failure;
        }
    }

    [RelayCommand]
    private async Task ValidateConfigTextAsync()
    {
        await RunAsync(async () =>
        {
            var result = await _tunnel.ValidateTextAsync(ConfigText).ConfigureAwait(true);
            ConfigTextFailed = !result.Succeeded;
            ConfigTextMessage = result.Message;
        }).ConfigureAwait(true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tunnel.Changed -= OnTunnelChanged;
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    partial void OnSelectedRoleOptionChanged(FrpRoleOption value)
    {
        OnPropertyChanged(nameof(IsClientRole));
        OnPropertyChanged(nameof(IsServerRole));
        OnPropertyChanged(nameof(ShowTcpFields));
        OnPropertyChanged(nameof(ShowDomainFields));
        OnPropertyChanged(nameof(RoleHint));
    }

    partial void OnSelectedProxyKindOptionChanged(FrpProxyKindOption value)
    {
        OnPropertyChanged(nameof(ShowTcpFields));
        OnPropertyChanged(nameof(ShowDomainFields));
    }

    partial void OnSelectedSectionOptionChanged(FrpConfigSectionOption value)
    {
        OnPropertyChanged(nameof(IsVisualSection));
        OnPropertyChanged(nameof(IsTextSection));
        OnPropertyChanged(nameof(ShowVisualProblems));
        OnPropertyChanged(nameof(SaveButtonText));
        NotifyDraftState();
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsReadOnly));

    partial void OnConfigTextMessageChanged(string value) => OnPropertyChanged(nameof(HasConfigTextMessage));

    partial void OnConfigTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasConfigText));
        ConfigTextMessage = string.Empty;
        ConfigTextFailed = false;
        try
        {
            ConfigTextSummary = FrpNativeConfig.Parse(value).Summary;
        }
        catch (FrpConfigurationException error)
        {
            ConfigTextSummary = string.Join("；", error.Problems);
        }
        catch (Exception error)
        {
            var safeFailure = $"解析配置异常：{error.GetType().Name}（0x{error.HResult:X8}）";
            _diagnostics.Record(safeFailure + Environment.NewLine + error.StackTrace);
            ConfigTextFailed = true;
            ConfigTextSummary = safeFailure;
        }
        NotifyDraftState();
    }

    private async Task LoadAsync()
    {
        try
        {
            var result = await _tunnel.ReloadSettingsAsync().ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            if (result.Succeeded)
            {
                ApplySavedChanges();
            }
            else
            {
                ShowOperationFailure(result.Message);
            }
        }
        catch (Exception error)
        {
            _diagnostics.Record("读取穿透设置失败", error);
            ShowProblems([$"读取穿透设置失败：{error.Message}"]);
        }
    }

    private void OnTunnelChanged(object? sender, FrpSnapshot snapshot) => Dispatch(() =>
    {
        if (_disposed)
        {
            return;
        }

        TokenConfigured = _tunnel.HasToken;
        OnPropertyChanged(nameof(SavedModeText));
        OnPropertyChanged(nameof(ConfigPathText));
        if (!IsBusy && _tunnel.SettingsProblems.Count == 0)
            ApplySavedChanges();
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

    private void LoadFields(FrpSettings settings)
    {
        _isLoadingFields = true;
        try
        {
            SelectedRoleOption = RoleOptions.First(option => option.Value == settings.Role);
            SelectedProxyKindOption = ProxyKindOptions.First(option => option.Value == settings.Client.ProxyKind);
            ServerAddress = settings.Client.ServerAddress;
            ServerPortText = Int(settings.Client.ServerPort);
            User = settings.Client.User;
            ProxyName = settings.Client.ProxyName;
            LocalAddress = settings.Client.LocalAddress;
            LocalPortText = Int(settings.Client.LocalPort);
            RemotePortText = Int(settings.Client.RemotePort);
            CustomDomainsText = string.Join(", ", settings.Client.CustomDomains);
            UseEncryption = settings.Client.UseEncryption;
            UseCompression = settings.Client.UseCompression;
            TransportTls = settings.Client.TransportTls;
            ClientAdminPortText = Int(settings.Client.AdminPort);
            BindPortText = Int(settings.Server.BindPort);
            VhostHttpPortText = Int(settings.Server.VhostHttpPort);
            SubdomainHost = settings.Server.SubdomainHost;
            ServerAdminPortText = Int(settings.Server.AdminPort);
            TokenConfigured = _tunnel.HasToken;
            TokenInput = null;
            TokenIntent = FrpTokenEditIntent.Keep;
            _formFingerprintAtLoad = SettingsFormFingerprint();
        }
        finally
        {
            _isLoadingFields = false;
        }
        ValidateFields();
        NotifyDraftState();
    }

    private void LoadText(FrpSettings settings)
    {
        ConfigText = settings.RawConfig;
        _textAtLoad = ConfigText;
        ShowConfigText = true;
        if (HasConfigText)
        {
            try
            {
                ShowConfigText = FrpNativeConfig.Parse(ConfigText).Secrets.Count == 0;
            }
            catch (FrpConfigurationException error)
            {
                ConfigTextFailed = true;
                ConfigTextMessage = string.Join("；", error.Problems);
                ShowConfigText = false;
            }
        }
    }

    private void LoadSavedDrafts(FrpSettings settings)
    {
        LoadFields(settings);
        LoadText(settings);
        SelectedSectionOption = SectionOptions.Single(option => option.Value == SectionOf(settings));
        MarkSavedMode();
    }

    private void MarkSavedMode()
    {
        _sectionAtLoad = SectionOf(_tunnel.Settings);
        _lastSavedFingerprint = FingerprintOf(_tunnel.Settings);
        OnPropertyChanged(nameof(SavedModeText));
        OnPropertyChanged(nameof(ConfigPathText));
        NotifyDraftState();
    }

    private void ApplySavedChanges()
    {
        var saved = _tunnel.Settings;
        var fingerprint = FingerprintOf(saved);
        if (string.Equals(fingerprint, _lastSavedFingerprint, StringComparison.Ordinal)) return;

        var visualDirty = HasUnsavedVisualEdits;
        var textDirty = HasUnsavedTextEdits;
        var modeDirty = SelectedSectionOption.Value != _sectionAtLoad;
        if (!visualDirty) LoadFields(saved);
        if (!textDirty) LoadText(saved);
        if (!modeDirty) SelectedSectionOption = SectionOptions.Single(option => option.Value == SectionOf(saved));
        MarkSavedMode();
        if (visualDirty || textDirty || modeDirty)
            OperationMessage = "保存的配置已在别处更新；当前有未保存的草稿，未自动覆盖。可继续编辑或重新读取。";
    }

    private static FrpConfigSection SectionOf(FrpSettings settings) => settings.ConfigMode == FrpConfigMode.Text
        ? FrpConfigSection.Text : FrpConfigSection.Visual;

    private void NotifyDraftState()
    {
        if (SelectedRoleOption is null || SelectedProxyKindOption is null || SelectedSectionOption is null) return;
        OnPropertyChanged(nameof(HasUnsavedVisualEdits));
        OnPropertyChanged(nameof(HasUnsavedTextEdits));
        OnPropertyChanged(nameof(HasUnsavedEdits));
        OnPropertyChanged(nameof(DraftHint));
    }

    /// <summary>
    /// 把表单拼成设置对象。端口一律要求能解析成合法数值：文本框里是"abc"就该当场报错，
    /// 而不是落回默认端口——那会让界面显示的端口与 frpc 实际使用的端口不一致。
    /// </summary>
    private FrpSettings? BuildSettings(out List<string> problems)
    {
        problems = [];
        var current = _tunnel.Settings;
        var client = current.Client with
        {
            ServerAddress = ServerAddress.Trim(),
            ServerPort = ParsePort(ServerPortText, "服务器端口", problems, current.Client.ServerPort),
            User = User.Trim(),
            ProxyName = ProxyName.Trim(),
            ProxyKind = SelectedProxyKindOption.Value,
            LocalAddress = LocalAddress.Trim(),
            LocalPort = ParsePort(LocalPortText, "本地服务端口", problems, current.Client.LocalPort),
            RemotePort = ParsePort(RemotePortText, "公网端口", problems, current.Client.RemotePort),
            CustomDomains = FrpSettingsStore.ParseDomains(CustomDomainsText),
            UseEncryption = UseEncryption,
            UseCompression = UseCompression,
            TransportTls = TransportTls,
            AdminPort = ParsePort(ClientAdminPortText, "本地状态端口", problems, current.Client.AdminPort),
        };

        var server = current.Server with
        {
            BindPort = ParsePort(BindPortText, "穿透端口", problems, current.Server.BindPort),
            VhostHttpPort = ParsePort(VhostHttpPortText, "HTTP 域名端口", problems, current.Server.VhostHttpPort, allowZero: true),
            SubdomainHost = SubdomainHost.Trim(),
            AdminPort = ParsePort(ServerAdminPortText, "本地状态端口", problems, current.Server.AdminPort),
        };

        var settings = new FrpSettings(
            SelectedRoleOption.Value,
            _tunnel.Settings.FollowService,
            client,
            server,
            current.InstalledVersion);
        problems.AddRange(settings.Validate());
        if (TokenIntent == FrpTokenEditIntent.Set && string.IsNullOrWhiteSpace(TokenInput))
        {
            problems.Add("新的 Token 不能为空；请填写 Token 或明确选择清除。");
        }
        return problems.Count == 0 ? settings : null;
    }

    // Serialize the complete draft rather than delimiting text: user text may itself contain '|'.
    // This private in-memory fingerprint is never logged or exposed to the UI.
    private string SettingsFormFingerprint() => System.Text.Json.JsonSerializer.Serialize(new object?[]
    {
        SelectedRoleOption.Value, SelectedProxyKindOption.Value,
        ServerAddress, ServerPortText, User, ProxyName, LocalAddress, LocalPortText, RemotePortText,
        CustomDomainsText, UseEncryption, UseCompression, TransportTls, ClientAdminPortText,
        BindPortText, VhostHttpPortText, SubdomainHost, ServerAdminPortText, TokenIntent, TokenInput,
    });

    private static string FingerprintOf(FrpSettings settings) => System.Text.Json.JsonSerializer.Serialize(settings);

    partial void OnTokenInputChanged(string? value)
    {
        if (!_isLoadingFields)
        {
            TokenIntent = string.IsNullOrWhiteSpace(value)
                ? (TokenIntent == FrpTokenEditIntent.Clear ? FrpTokenEditIntent.Clear : FrpTokenEditIntent.Keep)
                : FrpTokenEditIntent.Set;
        }
        OnPropertyChanged(nameof(TokenHint));
        OnPropertyChanged(nameof(CanClearToken));
    }

    partial void OnTokenIntentChanged(FrpTokenEditIntent value)
    {
        OnPropertyChanged(nameof(TokenHint));
        OnPropertyChanged(nameof(CanClearToken));
        OnPropertyChanged(nameof(HasTokenEdit));
    }

    partial void OnTokenConfiguredChanged(bool value)
    {
        OnPropertyChanged(nameof(TokenHint));
        OnPropertyChanged(nameof(CanClearToken));
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(SelectedRoleOption) or nameof(SelectedProxyKindOption)
            or nameof(ServerAddress) or nameof(ServerPortText) or nameof(User) or nameof(ProxyName) or nameof(LocalAddress)
            or nameof(LocalPortText) or nameof(RemotePortText) or nameof(CustomDomainsText)
            or nameof(UseEncryption) or nameof(UseCompression) or nameof(TransportTls) or nameof(ClientAdminPortText)
            or nameof(BindPortText) or nameof(VhostHttpPortText) or nameof(SubdomainHost) or nameof(ServerAdminPortText)
            or nameof(TokenInput) or nameof(TokenIntent))
        {
            ValidateFields();
            NotifyDraftState();
        }
    }

    private void ValidateFields()
    {
        if (_isLoadingFields || SelectedRoleOption is null || SelectedProxyKindOption is null) return;
        BuildSettings(out var problems);
        ShowProblems(problems);
    }

    private static int ParsePort(string text, string label, List<string> problems, int fallback, bool allowZero = false)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            problems.Add($"{label}不能为空");
            return fallback;
        }

        if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            problems.Add($"{label}必须是数字（当前：{trimmed}）");
            return fallback;
        }

        if (allowZero && port == 0)
        {
            return 0;
        }

        if (port is < FrpSettingsValidation.MinPort or > FrpSettingsValidation.MaxPort)
        {
            problems.Add($"{label}必须在 {FrpSettingsValidation.MinPort} 到 {FrpSettingsValidation.MaxPort} 之间（当前：{port}）");
            return fallback;
        }

        return port;
    }

    private void ShowProblems(IReadOnlyList<string> problems)
    {
        _problems.Clear();
        foreach (var problem in problems)
        {
            _problems.Add(problem);
        }

        ValidationText = problems.Count == 0 ? string.Empty : string.Join(Environment.NewLine, problems);
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(ShowVisualProblems));
    }

    private void ShowOperationFailure(string message)
    {
        OperationMessage = message;
        if (IsTextSection)
        {
            ConfigTextFailed = true;
            ConfigTextMessage = message;
        }
        else
        {
            ShowProblems([message]);
        }
    }

    private async Task RunAsync(Func<Task> operation)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await operation().ConfigureAwait(true);
        }
        catch (Exception error)
        {
            var failure = $"穿透配置操作失败：{error.GetType().Name}（0x{error.HResult:X8}）";
            _diagnostics.Record(failure + Environment.NewLine + error.StackTrace);
            ShowOperationFailure(failure);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
}
