namespace DanmuApi.Core;

public sealed record GithubProxyOption(
    string Id,
    string Label,
    string BaseUrl,
    bool IsOriginal = false);

public static class GithubProxyCatalog
{
    public const string OriginalId = "original";
    public const string LatencyProbeUrl =
        "https://raw.githubusercontent.com/lilixu3/danmu_api/refs/heads/main/danmu_api/configs/globals.js";

    public static IReadOnlyList<GithubProxyOption> Options { get; } =
    [
        new(OriginalId, "GitHub 官方（直连）", string.Empty, true),
        new("gh_proxy_org", "GH-Proxy.org", "https://gh-proxy.org"),
        new("hk_gh_proxy", "HK GH-Proxy", "https://hk.gh-proxy.org"),
        new("cdn_gh_proxy", "CDN GH-Proxy", "https://cdn.gh-proxy.org"),
        new("edgeone_gh_proxy", "EdgeOne GH-Proxy", "https://edgeone.gh-proxy.org"),
    ];

    public static GithubProxyOption GetById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return Options[0];
        }

        return Options.FirstOrDefault(option => string.Equals(option.Id, id, StringComparison.Ordinal))
            ?? throw new ArgumentException($"未知 GitHub 下载线路：{id}", nameof(id));
    }

    public static IReadOnlyList<Uri> BuildDownloadCandidates(string? proxyId, Uri original)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (!string.Equals(original.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("GitHub 下载地址必须使用 HTTPS", nameof(original));
        }

        var option = GetById(proxyId);
        if (option.IsOriginal)
        {
            return [original];
        }

        return BuildProxyCandidates(option.BaseUrl, original);
    }

    public static IReadOnlyList<Uri> BuildProxyCandidates(string proxyBase, Uri original)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proxyBase);
        ArgumentNullException.ThrowIfNull(original);
        var baseUrl = proxyBase.Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedBase) ||
            !string.Equals(parsedBase.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("GitHub 代理地址必须是 HTTPS 绝对地址", nameof(proxyBase));
        }

        var source = original.AbsoluteUri;
        var withoutScheme = source.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? source["https://".Length..]
            : source;
        var candidates = new List<string>();
        if (baseUrl.Contains("{url}", StringComparison.Ordinal))
        {
            candidates.Add(baseUrl.Replace("{url}", source, StringComparison.Ordinal));
        }
        else if (baseUrl.Contains("%s", StringComparison.Ordinal))
        {
            candidates.Add(baseUrl.Replace("%s", source, StringComparison.Ordinal));
        }

        if (baseUrl.EndsWith('=') || baseUrl.Contains("url=", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(baseUrl + Uri.EscapeDataString(source));
        }

        candidates.Add($"{baseUrl}/{source}");
        candidates.Add($"{baseUrl}/{withoutScheme}");
        return candidates
            .Distinct(StringComparer.Ordinal)
            .Select(candidate => new Uri(candidate, UriKind.Absolute))
            .ToArray();
    }
}

public sealed record GithubDownloadProgress(
    long DownloadedBytes,
    long? TotalBytes,
    string RouteLabel,
    Uri RequestUri);

public interface IGithubFileDownloader
{
    Task DownloadAsync(
        Uri originalUri,
        string proxyId,
        string targetPath,
        IProgress<GithubDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
