using System.Net;

namespace DanmuApi.Runtime;

public static class RuntimeEndpointBuilder
{
    public static string BuildApiAddress(string host, int port, string? token)
    {
        RuntimeValidation.ValidatePort(port);
        var authority = NormalizeHost(host);
        var effectiveToken = string.IsNullOrWhiteSpace(token)
            ? RuntimeDefaults.FallbackToken
            : token.Trim();
        var encodedToken = Uri.EscapeDataString(effectiveToken);
        return $"http://{authority}:{port}/{encodedToken}";
    }

    private static string NormalizeHost(string host)
    {
        RuntimeValidation.ValidateHost(host);

        var startsWithBracket = host[0] == '[';
        var endsWithBracket = host[^1] == ']';
        if (startsWithBracket || endsWithBracket)
        {
            if (!startsWithBracket || !endsWithBracket || host.Length < 3)
            {
                throw new ArgumentException("主机地址的 IPv6 方括号必须成对出现", nameof(host));
            }

            var literal = host[1..^1];
            if (!IPAddress.TryParse(literal, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                throw new ArgumentException("方括号内必须是有效的 IPv6 地址", nameof(host));
            }

            return host;
        }

        if (host.Contains('[', StringComparison.Ordinal) || host.Contains(']', StringComparison.Ordinal))
        {
            throw new ArgumentException("主机地址包含非法的 IPv6 方括号", nameof(host));
        }

        if (host.Contains(':', StringComparison.Ordinal))
        {
            if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                throw new ArgumentException("主机地址不是有效的 IPv6 地址", nameof(host));
            }

            return $"[{host}]";
        }

        if (IPAddress.TryParse(host, out var ipv4) && ipv4.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new ArgumentException("主机地址不是有效的 IPv4 地址", nameof(host));
        }

        if (IPAddress.TryParse(host, out _))
        {
            return host;
        }

        if (Uri.CheckHostName(host) != UriHostNameType.Dns)
        {
            throw new ArgumentException("主机地址不是有效的主机名或 IP 地址", nameof(host));
        }

        return host;
    }
}
