using System.Globalization;

namespace DanmuApi.Core.Frp;

/// <summary>
/// 把穿透状态拼成**可直接访问的 API 地址**（含核心的访问 TOKEN）。
///
/// 为什么要带 Token：局域网那张卡里的地址一直是 <c>http://ip:port/&lt;TOKEN&gt;</c> 的形状，
/// 外网入口如果只给一个 <c>host:port</c>，用户还得自己去别处复制 Token 再拼一遍——这正是用户报的"得加上 token"。
///
/// 主机名一律取 frp 自己返回的 <c>remote_addr</c>，本应用不猜公网 IP；
/// 取不到就不给地址（宁可不显示，也不给一个连不上的地址）。
/// </summary>
public static class FrpPublicAddress
{
    public static string Build(FrpPublicAddressInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.State != FrpTunnelState.Running)
        {
            return string.Empty;
        }

        // 服务端模式下"地址"取决于服务器自己的公网 IP 与端口映射，frp 也没有可用的 remote_addr，
        // 因此不给 URL，只由上层显示监听端口。
        if (input.Role == FrpRole.Server || string.IsNullOrWhiteSpace(input.RemoteAddress))
        {
            return string.Empty;
        }

        var scheme = input.ProxyKind == FrpProxyKind.Https ? "https" : "http";
        var host = NormalizeHost(input.RemoteAddress, scheme);
        var token = input.Token.Trim();
        return token.Length == 0
            ? $"{scheme}://{host}/"
            : $"{scheme}://{host}/{Uri.EscapeDataString(token)}";
    }

    /// <summary>
    /// frp 给的 remote_addr 形如 <c>1.2.3.4:19321</c>（tcp）或 <c>danmu.example.com:8080</c>（http）。
    /// 默认端口（http 80 / https 443）从地址里去掉，避免出现 <c>http://host:80/</c> 这种多余的写法。
    /// </summary>
    private static string NormalizeHost(string remoteAddress, string scheme)
    {
        var value = remoteAddress.Trim();
        var separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
        {
            return value;
        }

        var portText = value[(separator + 1)..];
        if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            // IPv6 字面量（形如 [::1]:19321 之外的裸地址）在这里解析不出端口，按原样使用。
            return value;
        }

        var defaultPort = scheme == "https" ? 443 : 80;
        return port == defaultPort ? value[..separator] : value;
    }
}

public sealed record FrpPublicAddressInput(
    FrpTunnelState State,
    FrpRole Role,
    FrpProxyKind ProxyKind,
    string? RemoteAddress,
    string Token);
