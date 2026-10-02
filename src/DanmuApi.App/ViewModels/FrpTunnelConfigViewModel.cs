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

/// <summary>
/// 内网穿透的**配置**页：只有表单，不掺状态与日志。
///
/// 表单按"填什么"分区（服务器 / 代理 / 高级），并把校验结果与"将要生效的值"实时回显在底部——
/// 与配置工作台的约定一致：不让用户点保存才知道填错了。
/// 另外提供 frp 原生 JSON 的导入导出，便于把一份配置搬到另一台机器。
/// </summary>
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

    [ObservableProperty] private FrpRoleOption _selectedRoleOption = null!;
    [ObservableProperty] private FrpProxyKindOption _selectedProxyKindOption = null!;
    [ObservableProperty] private string _serverAddress = string.Empty;
    [ObservableProperty] private string _serverPortText = string.Empty;
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
    [ObservableProperty] private string _importWarningText = string.Empty;
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

        LoadFields(_tunnel.Settings);
        _tunnel.Changed += OnTunnelChanged;
        _ = LoadAsync();
    }

    public IReadOnlyList<FrpRoleOption> RoleOptions { get; }

    public IReadOnlyList<FrpProxyKindOption> ProxyKindOptions { get; }

    public IReadOnlyList<string> Problems => _problems;

    public bool HasProblems => _problems.Count > 0;

    public bool IsClientRole => SelectedRoleOption.Value == FrpRole.Client;

    public bool IsServerRole => !IsClientRole;

    public bool ShowTcpFields => IsClientRole && SelectedProxyKindOption.Value == FrpProxyKind.Tcp;

    public bool ShowDomainFields => IsClientRole && !ShowTcpFields;

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

    public bool HasImportWarning => ImportWarningText.Length > 0;

    [RelayCommand]
    private async Task ReloadAsync()
    {
        await RunAsync(async () =>
        {
            var result = await _tunnel.ReloadSettingsAsync().ConfigureAwait(true);
            LoadFields(_tunnel.Settings);
            OperationMessage = result.Succeeded ? "已重新读取保存的配置。" : result.Message;
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        await RunAsync(async () =>
        {
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
                return;
            }

            OperationMessage = result.Message;
            LoadFields(_tunnel.Settings);
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
                    "清除将暂存到表单，点「保存设置」后生效。客户端连接与服务端认证都将不再使用 Token；若远端仍要求 Token，连接会被拒绝。",
                    "暂存清除").ConfigureAwait(true))
            {
                return;
            }

            TokenInput = null;
            TokenIntent = FrpTokenEditIntent.Clear;
            OperationMessage = "已暂存清除 Token；点「保存设置」后生效。";
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

    /// <summary>
    /// 导出为 frp 原生 JSON（可直接改名为 frpc.json 交给官方 frpc 运行）。
    /// 弹窗既能看也能直接 Ctrl+A 复制——比"复制到剪贴板后只有一句提示"更不容易让人怀疑复制了什么。
    /// </summary>
    [RelayCommand]
    private async Task ExportJsonAsync()
    {
        var settings = BuildSettings(out var problems);
        if (settings is null)
        {
            ShowProblems(problems);
            return;
        }

        try
        {
            var token = TokenIntent switch
            {
                FrpTokenEditIntent.Set => TokenInput!.Trim(),
                FrpTokenEditIntent.Clear => string.Empty,
                _ => _tunnel.ReadAuthToken(),
            };
            var json = FrpConfigJson.Export(settings, token);
            await _dialogService.CopyTextAsync(json).ConfigureAwait(true);
            await _dialogService.PromptMultilineTextAsync(
                "穿透配置（JSON）",
                "已复制到剪贴板。这是 frp 原生格式：既能贴回本应用，也能改名 frpc.json 交给官方 frpc 运行。"
                + "注意：其中包含 frp 的 auth.token，请勿公开分享。",
                json,
                "关闭",
                readOnly: true).ConfigureAwait(true);
            OperationMessage = "配置已按 JSON 复制到剪贴板。";
        }
        catch (Exception error)
        {
            _diagnostics.Record("导出穿透配置失败", error);
            OperationMessage = $"导出失败：{error.Message}";
        }
    }

    /// <summary>
    /// 粘贴导入：严格解析；能认但不管理的字段会逐条列出来，绝不静默丢弃。
    /// 导入只填表单，仍然要点「保存设置」才会落盘——避免一次误粘贴直接改掉正在生效的配置。
    /// </summary>
    [RelayCommand]
    private async Task ImportJsonAsync()
    {
        try
        {
            var pasted = await _dialogService.PromptMultilineTextAsync(
                "粘贴穿透配置（JSON）",
                "支持 frp 原生 JSON（frpc 的 serverAddr/proxies 或 frps 的 bindPort 结构）。"
                + "导入只填入表单，确认无误后再点「保存设置」。",
                string.Empty,
                "导入到表单").ConfigureAwait(true);
            if (pasted is null)
            {
                return;
            }

            var current = _tunnel.Settings;
            var result = FrpConfigJson.Import(pasted, current with
            {
                Client = current.Client with { ProxyKind = SelectedProxyKindOption.Value },
            });
            if (!result.Succeeded || result.Settings is null)
            {
                ShowProblems(result.Problems);
                ImportWarningText = string.Empty;
                return;
            }

            // Whole-document replacement for its role; untouched opposite-role drafts remain in the form.
            // Do not mark an import as a saved baseline, or a later notification could erase it.
            LoadFields(result.Settings, importedRoleOnly: true);
            TokenInput = result.Token;
            TokenIntent = result.Token is { Length: > 0 } ? FrpTokenEditIntent.Set : FrpTokenEditIntent.Clear;
            ValidateFields();
            ImportWarningText = BuildImportSummary(result);
            OperationMessage = "JSON 已导入到表单；点「保存设置」后生效。";
        }
        catch (Exception error)
        {
            _diagnostics.Record("导入穿透配置失败", error);
            ShowProblems([$"导入失败：{error.Message}"]);
        }
    }

    /// <summary>
    /// 导入结果的说明：未支持的字段与我们补的默认值都要说清楚，哪怕"什么都没补"也要给一句回执。
    /// </summary>
    private static string BuildImportSummary(FrpConfigJsonImport result)
    {
        var lines = new List<string> { "已导入到表单。" };
        if (result.Unsupported.Count > 0)
        {
            lines.Add($"以下字段本应用不管理、未导入：{string.Join("、", result.Unsupported)}");
        }

        if (result.AppliedDefaults.Count > 0)
        {
            lines.Add($"以下字段配置里没有写，已按默认值填入表单：{string.Join("；", result.AppliedDefaults)}");
        }

        return string.Join(Environment.NewLine, lines);
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

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsReadOnly));

    partial void OnImportWarningTextChanged(string value) => OnPropertyChanged(nameof(HasImportWarning));

    private async Task LoadAsync()
    {
        try
        {
            var result = await _tunnel.ReloadSettingsAsync().ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            if (string.Equals(SettingsFormFingerprint(), _formFingerprintAtLoad, StringComparison.Ordinal))
            {
                LoadFields(_tunnel.Settings);
            }
            if (!result.Succeeded)
            {
                ShowProblems([result.Message]);
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

        // 只在"磁盘上的设置真的变过"时才回填，避免把用户正在编辑的输入冲掉；
        // 真有变化但表单里也有未保存内容时，保留表单并说清楚，不悄悄覆盖。
        var savedFingerprint = FingerprintOf(_tunnel.Settings);
        TokenConfigured = _tunnel.HasToken;
        if (string.Equals(savedFingerprint, _lastSavedFingerprint, StringComparison.Ordinal))
        {
            return;
        }

        var hadUnsavedEdits = !string.Equals(SettingsFormFingerprint(), _formFingerprintAtLoad, StringComparison.Ordinal);
        _lastSavedFingerprint = savedFingerprint;
        if (hadUnsavedEdits)
        {
            OperationMessage = "保存的配置已在别处更新；当前表单里有未保存的修改，未自动覆盖。点「重新读取」可放弃修改并加载最新配置。";
            return;
        }

        LoadFields(_tunnel.Settings);
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

    private void LoadFields(FrpSettings settings, bool importedRoleOnly = false)
    {
        _isLoadingFields = true;
        try
        {
            SelectedRoleOption = RoleOptions.First(option => option.Value == settings.Role);
            if (!importedRoleOnly || settings.Role == FrpRole.Client)
            {
                SelectedProxyKindOption = ProxyKindOptions.First(option => option.Value == settings.Client.ProxyKind);
                ServerAddress = settings.Client.ServerAddress;
                ServerPortText = Int(settings.Client.ServerPort);
                ProxyName = settings.Client.ProxyName;
                LocalAddress = settings.Client.LocalAddress;
                LocalPortText = Int(settings.Client.LocalPort);
                RemotePortText = Int(settings.Client.RemotePort);
                CustomDomainsText = string.Join(", ", settings.Client.CustomDomains);
                UseEncryption = settings.Client.UseEncryption;
                UseCompression = settings.Client.UseCompression;
                TransportTls = settings.Client.TransportTls;
                ClientAdminPortText = Int(settings.Client.AdminPort);
            }
            if (!importedRoleOnly || settings.Role == FrpRole.Server)
            {
                BindPortText = Int(settings.Server.BindPort);
                VhostHttpPortText = Int(settings.Server.VhostHttpPort);
                SubdomainHost = settings.Server.SubdomainHost;
                ServerAdminPortText = Int(settings.Server.AdminPort);
            }
            TokenConfigured = _tunnel.HasToken;
            if (!importedRoleOnly)
            {
                TokenInput = null;
                TokenIntent = FrpTokenEditIntent.Keep;
                ImportWarningText = string.Empty;
                _formFingerprintAtLoad = SettingsFormFingerprint();
                _lastSavedFingerprint = FingerprintOf(settings);
            }
        }
        finally
        {
            _isLoadingFields = false;
        }
        ValidateFields();
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
        ServerAddress, ServerPortText, ProxyName, LocalAddress, LocalPortText, RemotePortText,
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
            or nameof(ServerAddress) or nameof(ServerPortText) or nameof(ProxyName) or nameof(LocalAddress)
            or nameof(LocalPortText) or nameof(RemotePortText) or nameof(CustomDomainsText)
            or nameof(UseEncryption) or nameof(UseCompression) or nameof(TransportTls) or nameof(ClientAdminPortText)
            or nameof(BindPortText) or nameof(VhostHttpPortText) or nameof(SubdomainHost) or nameof(ServerAdminPortText)
            or nameof(TokenInput) or nameof(TokenIntent))
        {
            ValidateFields();
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
            _diagnostics.Record("穿透配置操作失败", error);
            ShowProblems([$"操作失败：{error.Message}"]);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
}
