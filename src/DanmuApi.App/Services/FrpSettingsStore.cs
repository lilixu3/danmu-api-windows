using System.Globalization;
using DanmuApi.Core.Frp;
using DanmuApi.Platform;

namespace DanmuApi.App.Services;

/// <summary>FRP 设置的落盘读取结果。<see cref="Problems"/> 非空时不得启动穿透。</summary>
public sealed record FrpSettingsReadResult(FrpSettings Settings, IReadOnlyList<string> Problems)
{
    public bool Succeeded => Problems.Count == 0;
}

/// <summary>
/// FRP 设置的读写。
///
/// 分流规则：<b>不是秘密</b>的字段（地址、端口、域名、角色）进 settings.properties；
/// <b>是秘密</b>的字段（auth token、本地管理接口密码）进 DPAPI 保护的单独文件，
/// 因为 settings.properties 是明文文本，任何人都能读。
///
/// 读取刻意<b>严格</b>：端口不是数字、角色不是 client/server 之类的情况会当场报出来，
/// 而不是"解析不出来就用默认值"——那会让界面上显示的配置与 frpc 真正跑起来的配置不一致。
/// </summary>
public sealed class FrpSettingsStore
{
    public const string RoleKey = "frp_role";
    public const string ConfigModeKey = "frp_config_mode";
    /// <summary>Integrity/transaction reference only; the source and its secrets never enter properties.</summary>
    public const string ConfigTextHashKey = "frp_config_text_sha256";
    /// <summary>随弹幕服务启动而启动穿透（旧键 <c>frp_autostart</c> 是"随应用启动"，语义已改，故换名）。</summary>
    public const string FollowServiceKey = "frp_follow_service";
    public const string VersionKey = "frp_version";
    public const string ServerAddressKey = "frp_server_address";
    public const string ServerPortKey = "frp_server_port";
    /// <summary>frp 的 <c>user</c>：服务商面板给的账号标识，frpc 用它把代理名登记为 <c>{user}.{proxy}</c>。</summary>
    public const string UserKey = "frp_user";
    public const string ProxyNameKey = "frp_proxy_name";
    public const string ProxyKindKey = "frp_proxy_kind";
    public const string LocalAddressKey = "frp_local_address";
    public const string LocalPortKey = "frp_local_port";
    public const string RemotePortKey = "frp_remote_port";
    public const string CustomDomainsKey = "frp_custom_domains";
    public const string UseEncryptionKey = "frp_use_encryption";
    public const string UseCompressionKey = "frp_use_compression";
    public const string TransportTlsKey = "frp_transport_tls";
    public const string ClientAdminPortKey = "frp_client_admin_port";
    public const string BindPortKey = "frp_bind_port";
    public const string VhostHttpPortKey = "frp_vhost_http_port";
    public const string SubdomainHostKey = "frp_subdomain_host";
    public const string ServerAdminPortKey = "frp_server_admin_port";

    private readonly ISettingsStore _settings;
    private readonly IProtectedStringStore _tokenStore;
    private readonly IProtectedStringStore _adminPasswordStore;
    private readonly IProtectedDocumentStore? _rawStore;

    public FrpSettingsStore(
        ISettingsStore settings,
        IProtectedStringStore tokenStore,
        IProtectedStringStore adminPasswordStore,
        IProtectedDocumentStore? rawStore = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _adminPasswordStore = adminPasswordStore ?? throw new ArgumentNullException(nameof(adminPasswordStore));
        _rawStore = rawStore;
    }

    /// <summary>读取设置。<paramref name="defaultLocalPort"/> 仅在相关键完全缺失时用于默认值。</summary>
    public FrpSettingsReadResult Read(int defaultLocalPort)
    {
        var defaults = FrpSettings.Default(defaultLocalPort);
        var values = _settings.Read();
        var problems = new List<string>();
        var mode = FrpConfigMode.Visual;
        if (values.TryGetValue(ConfigModeKey, out var modeText))
        {
            mode = modeText switch
            {
                "visual" => FrpConfigMode.Visual,
                "text" => FrpConfigMode.Text,
                _ => AddProblem(problems, $"{ConfigModeKey} 取值无效（只能是 visual 或 text）", FrpConfigMode.Visual),
            };
        }
        var raw = ReadRaw(values, mode, problems);

        var role = defaults.Role;
        if (values.TryGetValue(RoleKey, out var roleText))
        {
            role = roleText.Trim().ToLowerInvariant() switch
            {
                "client" => FrpRole.Client,
                "server" => FrpRole.Server,
                _ => AddProblem(problems, $"{RoleKey} 取值无效：{roleText}（只能是 client 或 server）", defaults.Role),
            };
        }

        var proxyKind = defaults.Client.ProxyKind;
        if (values.TryGetValue(ProxyKindKey, out var kindText))
        {
            proxyKind = kindText.Trim().ToLowerInvariant() switch
            {
                "tcp" => FrpProxyKind.Tcp,
                "http" => FrpProxyKind.Http,
                "https" => FrpProxyKind.Https,
                _ => AddProblem(problems, $"{ProxyKindKey} 取值无效：{kindText}（只能是 tcp、http 或 https）", defaults.Client.ProxyKind),
            };
        }

        var client = defaults.Client with
        {
            ServerAddress = ReadString(values, ServerAddressKey, defaults.Client.ServerAddress),
            ServerPort = ReadPort(values, ServerPortKey, defaults.Client.ServerPort, problems),
            User = ReadString(values, UserKey, defaults.Client.User).Trim(),
            ProxyName = ReadString(values, ProxyNameKey, defaults.Client.ProxyName).Trim(),
            ProxyKind = proxyKind,
            LocalAddress = ReadString(values, LocalAddressKey, defaults.Client.LocalAddress).Trim(),
            LocalPort = ReadPort(values, LocalPortKey, defaults.Client.LocalPort, problems),
            RemotePort = ReadPort(values, RemotePortKey, defaults.Client.RemotePort, problems),
            CustomDomains = ParseDomains(ReadString(values, CustomDomainsKey, string.Empty)),
            UseEncryption = ReadBool(values, UseEncryptionKey, defaults.Client.UseEncryption, problems),
            UseCompression = ReadBool(values, UseCompressionKey, defaults.Client.UseCompression, problems),
            TransportTls = ReadBool(values, TransportTlsKey, defaults.Client.TransportTls, problems),
            AdminPort = ReadPort(values, ClientAdminPortKey, defaults.Client.AdminPort, problems),
        };

        var server = defaults.Server with
        {
            BindPort = ReadPort(values, BindPortKey, defaults.Server.BindPort, problems),
            VhostHttpPort = ReadPort(values, VhostHttpPortKey, defaults.Server.VhostHttpPort, problems, allowZero: true),
            SubdomainHost = ReadString(values, SubdomainHostKey, defaults.Server.SubdomainHost).Trim(),
            AdminPort = ReadPort(values, ServerAdminPortKey, defaults.Server.AdminPort, problems),
        };

        var settings = new FrpSettings(
            role,
            ReadBool(values, FollowServiceKey, defaults.FollowService, problems),
            client,
            server,
            ReadString(values, VersionKey, string.Empty).Trim())
        {
            ConfigMode = mode,
            RawConfig = raw,
        };
        if (mode == FrpConfigMode.Text && raw.Length > 0)
        {
            try { FrpNativeConfig.Parse(raw); }
            catch (FrpConfigurationException error) { problems.AddRange(error.Problems); }
        }

        return new FrpSettingsReadResult(settings, problems);
    }

    public void Save(FrpSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Enum.IsDefined(settings.ConfigMode)) throw new ArgumentException("穿透配置模式无效", nameof(settings));
        var current = RequireReadable(settings.Client.LocalPort);
        if (settings.ConfigMode == FrpConfigMode.Text
            && !string.Equals(settings.RawConfig, current.RawConfig, StringComparison.Ordinal))
            throw new IOException("配置文本必须通过受保护文档事务保存，不能从可视化设置覆盖");
        _settings.Write(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ConfigModeKey] = settings.ConfigMode == FrpConfigMode.Text ? "text" : "visual",
            [RoleKey] = settings.Role == FrpRole.Client ? "client" : "server",
            [FollowServiceKey] = Bool(settings.FollowService),
            [VersionKey] = settings.InstalledVersion,
            [ServerAddressKey] = settings.Client.ServerAddress,
            [ServerPortKey] = Int(settings.Client.ServerPort),
            [UserKey] = settings.Client.User,
            [ProxyNameKey] = settings.Client.ProxyName,
            [ProxyKindKey] = settings.Client.ProxyKind.ToFrpText(),
            [LocalAddressKey] = settings.Client.LocalAddress,
            [LocalPortKey] = Int(settings.Client.LocalPort),
            [RemotePortKey] = Int(settings.Client.RemotePort),
            [CustomDomainsKey] = string.Join(",", settings.Client.CustomDomains),
            [UseEncryptionKey] = Bool(settings.Client.UseEncryption),
            [UseCompressionKey] = Bool(settings.Client.UseCompression),
            [TransportTlsKey] = Bool(settings.Client.TransportTls),
            [ClientAdminPortKey] = Int(settings.Client.AdminPort),
            [BindPortKey] = Int(settings.Server.BindPort),
            [VhostHttpPortKey] = Int(settings.Server.VhostHttpPort),
            [SubdomainHostKey] = settings.Server.SubdomainHost,
            [ServerAdminPortKey] = Int(settings.Server.AdminPort),
        });
    }

    /// <summary>
    /// Commit a protected source and its mode/integrity reference, without changing any visual field/token.
    /// A crash between files leaves a detectable hash mismatch, not an executable mixed snapshot.
    /// Failed commits restore both files; rollback failures are explicitly reported, never hidden.
    /// </summary>
    public FrpSettingsReadResult SaveText(string text, int defaultLocalPort) =>
        SaveTextAsync(text, defaultLocalPort).GetAwaiter().GetResult();

    public async Task<FrpSettingsReadResult> SaveTextAsync(string text, int defaultLocalPort,
        Func<string, CancellationToken, Task>? verifyBeforeActivation = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        var old = RequireReadable(defaultLocalPort);
        FrpNativeConfig.Parse(text);
        var rawStore = _rawStore ?? throw new IOException("未配置受保护配置文档存储，不能保存配置文本");
        var before = _settings.Read();
        before.TryGetValue(ConfigModeKey, out var oldMode);
        before.TryGetValue(ConfigTextHashKey, out var oldHash);
        var hadRaw = oldHash is not null;
        try
        {
            rawStore.Save(text);
            var persistedText = rawStore.Load();
            if (!string.Equals(persistedText, text, StringComparison.Ordinal))
                throw new IOException("受保护配置文档回读校验失败");
            FrpNativeConfig.Parse(persistedText!);
            if (verifyBeforeActivation is not null)
                await verifyBeforeActivation(persistedText!, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _settings.Write(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [ConfigModeKey] = "text",
                [ConfigTextHashKey] = DocumentHash(text),
            });
            var committed = Read(defaultLocalPort);
            if (!committed.Succeeded || committed.Settings.ConfigMode != FrpConfigMode.Text
                || !string.Equals(committed.Settings.RawConfig, text, StringComparison.Ordinal))
                throw new IOException("配置文本事务回读校验失败");
            return committed;
        }
        catch (Exception error)
        {
            try
            {
                if (hadRaw) rawStore.Save(old.RawConfig);
                else rawStore.Clear();
                _settings.Write(new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [ConfigModeKey] = oldMode,
                    [ConfigTextHashKey] = oldHash,
                });
                var restored = Read(defaultLocalPort);
                var restoredValues = _settings.Read();
                restoredValues.TryGetValue(ConfigModeKey, out var restoredMode);
                restoredValues.TryGetValue(ConfigTextHashKey, out var restoredHash);
                if (!restored.Succeeded || restoredMode != oldMode || restoredHash != oldHash
                    || !string.Equals(restored.Settings.RawConfig, old.RawConfig, StringComparison.Ordinal))
                    throw new IOException("配置文本事务回滚回读校验失败");
            }
            catch (Exception rollback)
            {
                throw new IOException($"配置文本保存失败：{error.Message}；回滚也失败：{rollback.Message}。请修复存储一致性后再启动。",
                    new AggregateException(error, rollback));
            }
            throw;
        }
    }

    private FrpSettings RequireReadable(int defaultLocalPort)
    {
        var read = Read(defaultLocalPort);
        if (!read.Succeeded) throw new IOException($"穿透设置读取失败：{string.Join("；", read.Problems)}");
        return read.Settings;
    }

    private string ReadRaw(IReadOnlyDictionary<string, string> values, FrpConfigMode mode, List<string> problems)
    {
        var hasHash = values.TryGetValue(ConfigTextHashKey, out var hash);
        if (_rawStore is null)
        {
            if (mode == FrpConfigMode.Text || hasHash) problems.Add("未配置受保护配置文档存储");
            return string.Empty;
        }
        string? raw;
        try { raw = _rawStore.Load(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            problems.Add($"读取受保护配置文档失败：{error.Message}");
            return string.Empty;
        }
        if (raw is null)
        {
            if (mode == FrpConfigMode.Text || hasHash) problems.Add("已保存的配置文本缺失；禁止改用可视化配置启动");
            return string.Empty;
        }
        if (!hasHash || hash!.Length != 64 || !hash.All(Uri.IsHexDigit)
            || !string.Equals(hash, DocumentHash(raw), StringComparison.Ordinal))
            problems.Add("配置文本与设置的完整性标记不一致；保存可能未完成，禁止启动");
        return raw;
    }

    private static string DocumentHash(string text)
    {
        var bytes = new System.Text.UTF8Encoding(false, true).GetBytes(text);
        try { return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>
    /// Strictly reread before patching the behavior key. Never serialize an old full settings snapshot
    /// from the monitor/settings shortcut: all unrelated and unknown keys must stay untouched.
    /// </summary>
    public FrpSettingsReadResult SetFollowService(bool follow, int defaultLocalPort)
    {
        var read = Read(defaultLocalPort);
        if (!read.Succeeded)
        {
            return read;
        }

        _settings.Write(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [FollowServiceKey] = Bool(follow),
        });
        return new FrpSettingsReadResult(read.Settings with { FollowService = follow }, []);
    }

    /// <summary>读取穿透 token；未保存过返回空串。</summary>
    public string ReadToken() => _tokenStore.Load() ?? string.Empty;

    public bool HasToken() => ReadToken().Length > 0;

    public void SaveToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        _tokenStore.Save(token.Trim());
    }

    public void ClearToken() => _tokenStore.Clear();

    /// <summary>
    /// 本地管理接口密码：随机生成一次后固定复用。它只用于 127.0.0.1 上的 frp 管理接口，
    /// 因此不需要用户输入，也不进 settings.properties。
    /// </summary>
    public string EnsureAdminPassword()
    {
        var existing = _adminPasswordStore.Load();
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        _adminPasswordStore.Save(password);
        return password;
    }

    public const string AdminUser = "admin";

    public static IReadOnlyList<string> ParseDomains(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split([',', ';', '\n', '\r', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ReadString(IReadOnlyDictionary<string, string> values, string key, string fallback) =>
        values.TryGetValue(key, out var value) ? value : fallback;

    private static int ReadPort(
        IReadOnlyDictionary<string, string> values,
        string key,
        int fallback,
        List<string> problems,
        bool allowZero = false)
    {
        if (!values.TryGetValue(key, out var text))
        {
            return fallback;
        }

        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            problems.Add($"{key} 不是整数：{text}");
            return fallback;
        }

        if (port == 0 && allowZero)
        {
            return 0;
        }

        if (port is < FrpSettingsValidation.MinPort or > FrpSettingsValidation.MaxPort)
        {
            problems.Add($"{key} 超出端口范围（{FrpSettingsValidation.MinPort}-{FrpSettingsValidation.MaxPort}）：{port}");
            return fallback;
        }

        return port;
    }

    private static bool ReadBool(
        IReadOnlyDictionary<string, string> values,
        string key,
        bool fallback,
        List<string> problems)
    {
        if (!values.TryGetValue(key, out var text))
        {
            return fallback;
        }

        return text.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => AddProblem(problems, $"{key} 不是布尔值：{text}", fallback),
        };
    }

    private static T AddProblem<T>(List<string> problems, string message, T fallback)
    {
        problems.Add(message);
        return fallback;
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
}
