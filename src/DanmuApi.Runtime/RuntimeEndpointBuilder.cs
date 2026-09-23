using System.Globalization;
using System.Net;
using System.Text;

namespace DanmuApi.Runtime;

public static class RuntimeEndpointBuilder
{
    /// <summary>
    /// 令牌当作 URL 路径第一段时的编码。
    /// 核心拿**未解码**的路径段与 TOKEN / ADMIN_TOKEN 全等比较
    /// （<c>worker.js</c>：<c>path.split("/")[0] === globals.token</c>，pathname 来自
    /// <c>new URL(req.url).pathname</c>，不解码），所以这里必须按浏览器写路径的规则编码：
    /// 只转义路径里非转不可的字符（空格、控制符、<c>" &lt; &gt; ` { }</c> 与非 ASCII）。
    /// 用 <see cref="Uri.EscapeDataString"/> 会连 <c>! ' ( ) * &amp; = + , ; : $ @</c> 一起转义，
    /// 含这些字符的令牌在核心自家网页能用、在宿主这一侧会全线 401。
    /// </summary>
    public static string EncodeTokenSegment(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (token.Contains('/', StringComparison.Ordinal) ||
            token.Contains('#', StringComparison.Ordinal) ||
            token.Contains('?', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "令牌里的 / # ? 无法用作 API 路径前缀：它们会改变 URL 结构，而核心按未解码的路径段与令牌全等比较。" +
                "请把 TOKEN / ADMIN_TOKEN 改成不含这三个字符的值。",
                nameof(token));
        }

        var builder = new StringBuilder(token.Length);
        foreach (var rune in token.EnumerateRunes())
        {
            if (rune.Value is >= 0x20 and <= 0x7E && !PathLiteralNeedsEscaping((char)rune.Value))
            {
                builder.Append((char)rune.Value);
                continue;
            }

            foreach (var utf8 in Encoding.UTF8.GetBytes(rune.ToString()))
            {
                builder.Append('%').Append(utf8.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    /// <summary>路径段里保留原样、但浏览器仍会转义的那批 ASCII 字符。</summary>
    private static bool PathLiteralNeedsEscaping(char character) =>
        character is ' ' or '"' or '<' or '>' or '`' or '{' or '}';

    /// <summary>
    /// 令牌可能出现在诊断文本里的所有形态（原文 / 路径段编码 / 全量转义），长的排前面，
    /// 保证脱敏时不会因为「只替换了其中一种写法」而把密钥漏进日志或界面。
    /// </summary>
    public static IEnumerable<string> TokenTextForms(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var trimmed = token.Trim();
        var encoded = TryEncodeTokenSegment(trimmed);
        return new[] { token, trimmed, encoded, Uri.EscapeDataString(trimmed) }
            .Where(form => !string.IsNullOrEmpty(form))
            .Select(form => form!)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(form => form.Length);
    }

    private static string? TryEncodeTokenSegment(string token)
    {
        try
        {
            return EncodeTokenSegment(token);
        }
        catch (ArgumentException)
        {
            // 令牌含 / # ? 时根本没有合法的路径写法；脱敏仍然要把原文与全量转义挡掉。
            return null;
        }
    }

    public static string BuildApiAddress(string host, int port, string? token)
    {
        RuntimeValidation.ValidatePort(port);
        var authority = NormalizeHost(host);
        var effectiveToken = string.IsNullOrWhiteSpace(token)
            ? RuntimeDefaults.FallbackToken
            : token.Trim();
        var encodedToken = EncodeTokenSegment(effectiveToken);
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
