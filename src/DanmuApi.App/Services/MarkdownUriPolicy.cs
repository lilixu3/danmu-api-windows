namespace DanmuApi.App.Services;

/// <summary>
/// Markdown 里链接与图片的放行规则，与移动端 <c>MarkdownUriPolicy</c> 逐条对应：
/// 链接只允许带主机名的 http/https 与有内容的 mailto；图片只允许 https。
/// 其余（javascript:、file:、content:、站内相对路径等）一律不放行。
/// </summary>
public static class MarkdownUriPolicy
{
    public static bool CanOpenLink(string? value)
    {
        var uri = ParseAbsolute(value);
        if (uri is null)
        {
            return false;
        }

        return uri.Scheme.ToLowerInvariant() switch
        {
            "http" or "https" => !string.IsNullOrWhiteSpace(uri.Host),
            "mailto" => !string.IsNullOrWhiteSpace(SchemeSpecificPart(value!)),
            _ => false,
        };
    }

    public static bool CanLoadImage(string? value)
    {
        var uri = ParseAbsolute(value);
        return uri is not null &&
               string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(uri.Host);
    }

    /// <summary>冒号之后的部分（不含协议名），用于判断 mailto: 后面是否真有地址。</summary>
    private static string? SchemeSpecificPart(string value)
    {
        var separator = value.IndexOf(':');
        return separator < 0 ? null : value[(separator + 1)..].Trim();
    }

    private static Uri? ParseAbsolute(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Any(char.IsControl))
        {
            return null;
        }

        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ? uri : null;
    }
}
