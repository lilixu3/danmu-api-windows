namespace DanmuApi.Core.Frp;

/// <summary>本机在穿透链路里的角色：客户端把本机弹幕服务送到远端 frps；服务端则让本机自己充当 frps。</summary>
public enum FrpRole
{
    Client,
    Server,
}

/// <summary>frpc 代理类型。tcp 走 remotePort，http/https 走 customDomains（由 frps 的 vhost 端口承载）。</summary>
public enum FrpProxyKind
{
    Tcp,
    Http,
    Https,
}

/// <summary>穿透链路状态。Running 必须在 frpc 管理接口里看到代理 running 才成立，不看进程存活就报成功。</summary>
public enum FrpTunnelState
{
    Stopped,
    Starting,
    Running,
    Reconnecting,
    Stopping,
    Failed,
}

public static class FrpProxyKindExtensions
{
    public static string ToFrpText(this FrpProxyKind kind) => kind switch
    {
        FrpProxyKind.Tcp => "tcp",
        FrpProxyKind.Http => "http",
        FrpProxyKind.Https => "https",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知的 frp 代理类型"),
    };

    public static string ToLabel(this FrpProxyKind kind) => kind switch
    {
        FrpProxyKind.Tcp => "TCP 端口",
        FrpProxyKind.Http => "HTTP 域名",
        FrpProxyKind.Https => "HTTPS 域名",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知的 frp 代理类型"),
    };

    public static bool RequiresRemotePort(this FrpProxyKind kind) => kind == FrpProxyKind.Tcp;

    public static bool RequiresCustomDomains(this FrpProxyKind kind) => kind is FrpProxyKind.Http or FrpProxyKind.Https;
}

/// <summary>
/// frpc 侧配置。<see cref="AdminPort"/> 是本机回环上的 frpc 管理接口端口：状态判定、公网地址都来自它，
/// 因此它不是可选项（不配就没有任何权威的"穿透成功"证据）。
/// </summary>
public sealed record FrpClientSettings(
    string ServerAddress,
    int ServerPort,
    string ProxyName,
    FrpProxyKind ProxyKind,
    string LocalAddress,
    int LocalPort,
    int RemotePort,
    IReadOnlyList<string> CustomDomains,
    bool UseEncryption,
    bool UseCompression,
    bool TransportTls,
    int AdminPort)
{
    public const int DefaultServerPort = 7000;
    public const int DefaultAdminPort = 7400;
    public const string DefaultLocalAddress = "127.0.0.1";
}

/// <summary>frps 侧配置。<see cref="AdminPort"/> 同样是本机回环上的管理接口端口。</summary>
public sealed record FrpServerSettings(
    int BindPort,
    int VhostHttpPort,
    string SubdomainHost,
    int AdminPort)
{
    public const int DefaultBindPort = 7000;
    public const int DefaultAdminPort = 7500;
}

/// <summary>
/// 落盘在 settings.properties 的 FRP 设置（Token 不在这里：它单独走 DPAPI 保护的秘密存储）。
/// <see cref="InstalledVersion"/> 是已安装二进制目录对应的版本号，空串表示未安装。
/// <see cref="FollowService"/> 控制"弹幕服务起来时自动把穿透也拉起来"；**停止方向不受它控制**——
/// 服务一停，穿透必定跟着停（隧道指向一个已经关掉的本地端口没有任何意义，地址也是假的）。
/// </summary>
public sealed record FrpSettings(
    FrpRole Role,
    bool FollowService,
    FrpClientSettings Client,
    FrpServerSettings Server,
    string InstalledVersion)
{
    public const string DefaultProxyName = "danmu-api";

    public static FrpSettings Default(int localPort) => new(
        FrpRole.Client,
        FollowService: false,
        new FrpClientSettings(
            ServerAddress: string.Empty,
            ServerPort: FrpClientSettings.DefaultServerPort,
            ProxyName: DefaultProxyName,
            ProxyKind: FrpProxyKind.Tcp,
            LocalAddress: FrpClientSettings.DefaultLocalAddress,
            LocalPort: localPort,
            RemotePort: localPort,
            CustomDomains: [],
            UseEncryption: false,
            UseCompression: false,
            TransportTls: true,
            AdminPort: FrpClientSettings.DefaultAdminPort),
        new FrpServerSettings(
            BindPort: FrpServerSettings.DefaultBindPort,
            VhostHttpPort: 0,
            SubdomainHost: string.Empty,
            AdminPort: FrpServerSettings.DefaultAdminPort),
        InstalledVersion: string.Empty);

    /// <summary>当前角色下真正生效的那份配置需要校验的问题清单；空列表表示可以启动。</summary>
    public IReadOnlyList<string> Validate() => Role == FrpRole.Client
        ? FrpSettingsValidation.ValidateClient(Client)
        : FrpSettingsValidation.ValidateServer(Server);

    public void EnsureValid()
    {
        var problems = Validate();
        if (problems.Count > 0)
        {
            throw new FrpConfigurationException(problems);
        }
    }
}

public sealed class FrpConfigurationException : Exception
{
    public FrpConfigurationException(IReadOnlyList<string> problems)
        : base(string.Join("；", problems))
    {
        Problems = problems ?? throw new ArgumentNullException(nameof(problems));
    }

    public IReadOnlyList<string> Problems { get; }
}

/// <summary>
/// 配置校验。<b>不提供任何"猜一个默认值继续跑"的分支</b>：缺字段就把缺的字段报出来，
/// 因为 frp 拿到一份半成品配置只会以进程退出或代理 start error 收场，让用户去看日志猜原因。
/// </summary>
public static class FrpSettingsValidation
{
    public const int MinPort = 1;
    public const int MaxPort = 65_535;

    public static IReadOnlyList<string> ValidateClient(FrpClientSettings client)
    {
        ArgumentNullException.ThrowIfNull(client);
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(client.ServerAddress))
        {
            problems.Add("服务器地址不能为空");
        }
        else if (client.ServerAddress.Any(char.IsWhiteSpace)
                 || client.ServerAddress.Contains('/', StringComparison.Ordinal)
                 || client.ServerAddress.Contains(':', StringComparison.Ordinal))
        {
            problems.Add("服务器地址只能是域名或 IP，不要带端口、协议或路径");
        }

        ValidatePort(client.ServerPort, "服务器端口", problems);
        ValidatePort(client.AdminPort, "本地状态端口", problems);
        ValidateProxyName(client.ProxyName, problems);
        if (string.IsNullOrWhiteSpace(client.LocalAddress))
        {
            problems.Add("本地服务地址不能为空");
        }

        ValidatePort(client.LocalPort, "本地服务端口", problems);
        if (client.ProxyKind.RequiresRemotePort())
        {
            ValidatePort(client.RemotePort, "公网端口", problems);
            if (client.RemotePort == client.AdminPort || client.RemotePort == client.ServerPort)
            {
                problems.Add("公网端口不能与服务器端口或本地状态端口相同");
            }
        }
        else if (client.CustomDomains.Count == 0)
        {
            problems.Add($"{client.ProxyKind.ToLabel()} 至少需要一个域名");
        }
        else
        {
            var invalid = client.CustomDomains
                .Where(domain => string.IsNullOrWhiteSpace(domain)
                                 || domain.Any(char.IsWhiteSpace)
                                 || domain.Contains('/', StringComparison.Ordinal)
                                 || domain.Contains(':', StringComparison.Ordinal))
                .ToArray();
            if (invalid.Length > 0)
            {
                problems.Add($"域名格式无效：{string.Join("、", invalid)}（只写域名本身，不带协议、端口或路径）");
            }
        }

        if (client.AdminPort == client.LocalPort)
        {
            problems.Add("本地状态端口不能与被穿透的本地服务端口相同");
        }

        return problems;
    }

    public static IReadOnlyList<string> ValidateServer(FrpServerSettings server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var problems = new List<string>();
        ValidatePort(server.BindPort, "穿透端口", problems);
        ValidatePort(server.AdminPort, "本地状态端口", problems);
        if (server.BindPort == server.AdminPort)
        {
            problems.Add("穿透端口不能与本地状态端口相同");
        }

        if (server.VhostHttpPort != 0)
        {
            ValidatePort(server.VhostHttpPort, "HTTP 域名端口", problems);
            if (server.VhostHttpPort == server.BindPort || server.VhostHttpPort == server.AdminPort)
            {
                problems.Add("HTTP 域名端口不能与穿透端口或本地状态端口相同");
            }
        }

        if (server.SubdomainHost.Length > 0
            && (server.SubdomainHost.Any(char.IsWhiteSpace)
                || server.SubdomainHost.Contains('/', StringComparison.Ordinal)
                || server.SubdomainHost.Contains(':', StringComparison.Ordinal)))
        {
            problems.Add("泛域名只能写域名本身，不带协议、端口或路径");
        }

        return problems;
    }

    private static void ValidatePort(int port, string label, List<string> problems)
    {
        if (port is < MinPort or > MaxPort)
        {
            problems.Add($"{label}必须在 {MinPort} 到 {MaxPort} 之间（当前 {port}）");
        }
    }

    private static void ValidateProxyName(string name, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            problems.Add("代理名称不能为空");
            return;
        }

        if (!name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
        {
            problems.Add("代理名称只能包含字母、数字、连字符、下划线和点");
        }
    }
}
