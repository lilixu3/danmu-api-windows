using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DanmuApi.Runtime;

/// <summary>核心缓存里的一条剧集链接（<c>links[]</c> 元素，也可能是被合并源自己的 links）。</summary>
public sealed record CacheAnimeLink(string Url, string Title);

/// <summary>
/// 被合并源（<c>mergedChildren[]</c> 元素）。<see cref="Links"/> 是核心回填过的完整链接表，
/// 用于把主源链接里的 <c>源名:源ID</c> 还原成可读的剧集标题。
/// </summary>
public sealed record CacheAnimeSource(
    string Source,
    string AnimeId,
    string AnimeTitle,
    string? ImageUrl,
    int Episodes,
    IReadOnlyList<CacheAnimeLink> Links);

/// <summary>缓存里的一个剧集条目（<c>data[]</c> 元素）。</summary>
public sealed record CacheAnimeEntry(
    string AnimeTitle,
    string Source,
    string? ImageUrl,
    int Episodes,
    IReadOnlyList<CacheAnimeLink> Links,
    IReadOnlyList<CacheAnimeSource> MergedChildren);

public enum CacheMappingStatus
{
    /// <summary>主源与副源在该链接上都有集数，视为已匹配。</summary>
    Matched,

    /// <summary>只有一侧有集数，视为落单。</summary>
    Lonely,
}

/// <summary>映射详情里的一行：主源一侧 ↔ 副源一侧，以及匹配状态。</summary>
public sealed record CacheMappingRow(
    CacheMappingStatus Status,
    string MainSide,
    string ChildSide,
    int? ChildEpisodeNumber,
    int OriginalIndex);

public sealed record CoreCacheAnimeResult(
    bool Succeeded,
    string Diagnostic,
    IReadOnlyList<CacheAnimeEntry> Items)
{
    public static CoreCacheAnimeResult Failure(string diagnostic) =>
        new(false, diagnostic, []);

    public static CoreCacheAnimeResult Success(string diagnostic, IReadOnlyList<CacheAnimeEntry> items) =>
        new(true, diagnostic, items);
}

public interface ICoreCacheAnimeClient
{
    Task<CoreCacheAnimeResult> FetchAsync(
        string host,
        int port,
        string? token,
        string? adminToken,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 最近数据面板的展示层派生逻辑。刻意与控件解耦，便于单测覆盖核心前端那套"清洗标题 / 映射详情"语义。
/// </summary>
public static class CacheAnimePresentation
{
    private static readonly Regex FromSuffixPattern = new(
        @"\s*from\s+.*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ZeroWidthPattern = new(
        @"[\u200B-\u200F\uFEFF]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex YearBracketPattern = new(
        @"\s*[（(〔\[]\s*[0-9０-９]{4}\s*年?\s*[）)〕\]]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TypeSuffixPattern = new(
        @"(.+?)\s*【[^】]+】$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LeadingTypePattern = new(
        @"^【.*?】\s*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FirstNumberPattern = new(
        @"\d+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// 列表标题清洗：去掉核心在 animeTitle 末尾追加的 <c>from 源名</c>。
    /// 对应核心前端 <c>cleanTitleStr</c>。
    /// </summary>
    public static string CleanTitle(string? rawTitle) =>
        FromSuffixPattern.Replace(rawTitle ?? string.Empty, string.Empty).Trim();

    /// <summary>
    /// 一键写入 DANMU_OFFSET 时的剧名清洗：去零宽字符、去年份括号、去末尾 <c>【类型】</c>。
    /// 对应核心前端 <c>fillOffsetEntity</c>。
    /// </summary>
    public static string CleanOffsetTitle(string? rawTitle)
    {
        var value = ZeroWidthPattern.Replace(rawTitle ?? string.Empty, string.Empty);
        value = YearBracketPattern.Replace(value, string.Empty);
        var match = TypeSuffixPattern.Match(value);
        if (match.Success)
        {
            value = match.Groups[1].Value;
        }

        return value.Trim();
    }

    /// <summary>剧集标题去掉前缀 <c>【类型】</c>；空值回退成"未知剧集"（与核心前端一致）。</summary>
    public static string CleanEpisodeTitle(string? rawTitle)
    {
        var value = LeadingTypePattern.Replace(rawTitle ?? string.Empty, string.Empty).Trim();
        return value.Length == 0 ? "未知剧集" : value;
    }

    /// <summary>
    /// 构建某个被合并源的映射详情行。逐条主源链接判断两侧是否都有集数：
    /// <list type="bullet">
    /// <item>主源侧：链接里带 <c>主源:</c> 或不含 <c>:</c> 即算命中，否则是"主源越界"。</item>
    /// <item>副源侧：链接里带 <c>副源:</c> 才算命中；用其中的源 ID 去被合并源的 links 里换回可读标题，换不到就显示 <c>(源ID: xxx)</c>。</item>
    /// </list>
    /// 排序：已匹配的按副源集数升序（集数解析不到时保持缓存原序），落单的排在后面且保持原序。
    /// 核心前端用 JS 的比较器实现，对"集数解析不到"的行会退回原序；这里改成集数未知统一排在最后，
    /// 结果更稳定（同样的输入必得同样的顺序）。
    /// </summary>
    public static IReadOnlyList<CacheMappingRow> BuildMappingRows(
        CacheAnimeEntry parent,
        CacheAnimeSource child)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(child);

        var rows = new List<CacheMappingRow>();
        for (var index = 0; index < parent.Links.Count; index++)
        {
            var link = parent.Links[index];
            var url = link.Url ?? string.Empty;
            var hasMain = url.Contains(parent.Source + ":", StringComparison.Ordinal) ||
                          !url.Contains(':', StringComparison.Ordinal);
            var hasChild = url.Contains(child.Source + ":", StringComparison.Ordinal);
            if (!hasMain && !hasChild)
            {
                continue;
            }

            var mainSide = hasMain
                ? $"【{parent.Source}】{CleanEpisodeTitle(link.Title)}"
                : "(主源越界)";

            string childSide;
            int? childEpisode;
            if (!hasChild)
            {
                childSide = "(副源缺失)";
                childEpisode = null;
            }
            else
            {
                var childTitle = ResolveChildTitle(child, url, link);
                childSide = $"【{child.Source}】{childTitle}";
                var number = FirstNumberPattern.Match(childTitle);
                childEpisode = number.Success && int.TryParse(number.Value, out var parsed)
                    ? parsed
                    : null;
            }

            rows.Add(new CacheMappingRow(
                hasMain && hasChild ? CacheMappingStatus.Matched : CacheMappingStatus.Lonely,
                mainSide,
                childSide,
                childEpisode,
                index));
        }

        var matched = rows
            .Where(row => row.Status == CacheMappingStatus.Matched)
            .OrderBy(row => row.ChildEpisodeNumber ?? int.MaxValue)
            .ThenBy(row => row.OriginalIndex);
        var lonely = rows
            .Where(row => row.Status == CacheMappingStatus.Lonely)
            .OrderBy(row => row.OriginalIndex);
        return matched.Concat(lonely).ToArray();
    }

    private static string ResolveChildTitle(
        CacheAnimeSource child,
        string url,
        CacheAnimeLink link)
    {
        var match = Regex.Match(
            url,
            Regex.Escape(child.Source) + @":([^$]+)",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return CleanEpisodeTitle(link.Title);
        }

        var childId = match.Groups[1].Value;
        var resolved = child.Links.FirstOrDefault(candidate =>
            string.Equals(candidate.Url, childId, StringComparison.Ordinal));
        return resolved?.Title is { Length: > 0 } title
            ? CleanEpisodeTitle(title)
            : $"(源ID: {childId})";
    }
}

/// <summary>
/// 读取核心 <c>GET /api/cache/animes</c>（最近数据面板的数据源）。
/// 解析刻意严格：契约字段缺失或类型不符一律报失败，不返回"空列表"掩盖问题。
/// </summary>
public sealed class CoreCacheAnimeClient : ICoreCacheAnimeClient
{
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _requestTimeout;

    public CoreCacheAnimeClient(HttpClient? httpClient = null, TimeSpan? requestTimeout = null)
    {
        _httpClient = httpClient ?? CreateDefaultClient();
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10);
        if (_requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), "核心缓存请求超时必须大于零");
        }
    }

    public async Task<CoreCacheAnimeResult> FetchAsync(
        string host,
        int port,
        string? token,
        string? adminToken,
        CancellationToken cancellationToken = default)
    {
        RuntimeValidation.ValidateHost(host);
        RuntimeValidation.ValidatePort(port);

        var effectiveToken = string.IsNullOrWhiteSpace(token)
            ? RuntimeDefaults.FallbackToken
            : token.Trim();
        var effectiveAdminToken = string.IsNullOrWhiteSpace(adminToken) ? null : adminToken.Trim();
        var endpoint = CoreCacheClient.BuildCacheUri(host, port, effectiveToken, effectiveAdminToken, "animes");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CoreCacheAnimeResult.Failure("最近数据请求已取消");
        }
        catch (OperationCanceledException)
        {
            return CoreCacheAnimeResult.Failure("最近数据请求超时");
        }
        catch (HttpRequestException error)
        {
            return CoreCacheAnimeResult.Failure(
                $"最近数据请求失败: {DescribeSensitive(error.Message, effectiveToken, effectiveAdminToken)}");
        }

        using (response)
        {
            byte[] body;
            try
            {
                body = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return CoreCacheAnimeResult.Failure("最近数据响应读取超时");
            }
            catch (Exception error) when (error is IOException or HttpRequestException)
            {
                return CoreCacheAnimeResult.Failure(
                    $"最近数据响应读取失败: {DescribeSensitive(error.Message, effectiveToken, effectiveAdminToken)}");
            }

            var result = ParseResponse(body);
            if ((int)response.StatusCode is < 200 or > 299 && result.Succeeded)
            {
                return CoreCacheAnimeResult.Failure($"核心缓存接口返回 HTTP {(int)response.StatusCode}");
            }

            return result with
            {
                Diagnostic = DescribeSensitive(result.Diagnostic, effectiveToken, effectiveAdminToken),
            };
        }
    }

    public static CoreCacheAnimeResult ParseResponse(ReadOnlySpan<byte> body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 24,
            });
        }
        catch (JsonException error)
        {
            return CoreCacheAnimeResult.Failure($"最近数据响应 JSON 无效：{Describe(error.Message)}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return CoreCacheAnimeResult.Failure("最近数据响应不是 JSON 对象");
            }

            if (!root.TryGetProperty("success", out var success) ||
                success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return CoreCacheAnimeResult.Failure("最近数据响应缺少布尔字段 success");
            }

            if (!success.GetBoolean())
            {
                var message = root.TryGetProperty("message", out var failed) &&
                              failed.ValueKind == JsonValueKind.String
                    ? failed.GetString()
                    : null;
                return CoreCacheAnimeResult.Failure(
                    string.IsNullOrWhiteSpace(message)
                        ? "核心缓存接口报告失败"
                        : $"核心缓存接口报告失败：{Describe(message)}");
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return CoreCacheAnimeResult.Failure("最近数据响应缺少数组字段 data");
            }

            var items = new List<CacheAnimeEntry>();
            var index = 0;
            foreach (var element in data.EnumerateArray())
            {
                if (!TryReadEntry(element, index, out var entry, out var failure))
                {
                    return CoreCacheAnimeResult.Failure(failure);
                }

                items.Add(entry);
                index++;
            }

            return CoreCacheAnimeResult.Success($"已读取 {items.Count} 条缓存剧集", items);
        }
    }

    private static bool TryReadEntry(
        JsonElement element,
        int index,
        out CacheAnimeEntry entry,
        out string failure)
    {
        entry = null!;
        failure = string.Empty;
        if (element.ValueKind != JsonValueKind.Object)
        {
            failure = $"最近数据第 {index + 1} 条不是 JSON 对象";
            return false;
        }

        if (!TryReadRequiredString(element, "animeTitle", index, out var animeTitle, out failure) ||
            !TryReadRequiredString(element, "source", index, out var source, out failure))
        {
            return false;
        }

        if (!element.TryGetProperty("episodes", out var episodes) ||
            episodes.ValueKind != JsonValueKind.Number ||
            !episodes.TryGetInt32(out var episodeCount))
        {
            failure = $"最近数据第 {index + 1} 条（{animeTitle}）缺少数字字段 episodes";
            return false;
        }

        if (!TryReadLinks(element, index, animeTitle, out var links, out failure))
        {
            return false;
        }

        if (!TryReadChildren(element, index, animeTitle, out var children, out failure))
        {
            return false;
        }

        entry = new CacheAnimeEntry(
            animeTitle,
            source,
            ReadOptionalString(element, "imageUrl"),
            episodeCount,
            links,
            children);
        return true;
    }

    private static bool TryReadChildren(
        JsonElement element,
        int index,
        string animeTitle,
        out IReadOnlyList<CacheAnimeSource> children,
        out string failure)
    {
        children = [];
        failure = string.Empty;
        if (!element.TryGetProperty("mergedChildren", out var merged) ||
            merged.ValueKind == JsonValueKind.Null ||
            merged.ValueKind == JsonValueKind.Undefined)
        {
            return true;
        }

        if (merged.ValueKind != JsonValueKind.Array)
        {
            failure = $"最近数据第 {index + 1} 条（{animeTitle}）的 mergedChildren 不是数组";
            return false;
        }

        var list = new List<CacheAnimeSource>();
        var childIndex = 0;
        foreach (var child in merged.EnumerateArray())
        {
            if (child.ValueKind != JsonValueKind.Object)
            {
                failure = $"最近数据第 {index + 1} 条（{animeTitle}）的第 {childIndex + 1} 个被合并源不是 JSON 对象";
                return false;
            }

            var label = $"{animeTitle} 的第 {childIndex + 1} 个被合并源";
            if (!TryReadRequiredString(child, "source", index, out var childSource, out failure) ||
                !TryReadRequiredString(child, "animeTitle", index, out var childTitle, out failure))
            {
                return false;
            }

            var childEpisodes = 0;
            if (child.TryGetProperty("episodes", out var childEp) &&
                childEp.ValueKind == JsonValueKind.Number &&
                childEp.TryGetInt32(out var parsedChildEp))
            {
                childEpisodes = parsedChildEp;
            }

            if (!TryReadLinks(child, index, label, out var childLinks, out failure))
            {
                return false;
            }

            list.Add(new CacheAnimeSource(
                childSource,
                ReadOptionalString(child, "animeId") ?? string.Empty,
                childTitle,
                ReadOptionalString(child, "imageUrl"),
                childEpisodes,
                childLinks));
            childIndex++;
        }

        children = list;
        return true;
    }

    private static bool TryReadLinks(
        JsonElement element,
        int index,
        string label,
        out IReadOnlyList<CacheAnimeLink> links,
        out string failure)
    {
        links = [];
        failure = string.Empty;
        if (!element.TryGetProperty("links", out var raw) ||
            raw.ValueKind == JsonValueKind.Null ||
            raw.ValueKind == JsonValueKind.Undefined)
        {
            return true;
        }

        if (raw.ValueKind != JsonValueKind.Array)
        {
            failure = $"最近数据第 {index + 1} 条（{label}）的 links 不是数组";
            return false;
        }

        var list = new List<CacheAnimeLink>();
        foreach (var link in raw.EnumerateArray())
        {
            if (link.ValueKind != JsonValueKind.Object)
            {
                failure = $"最近数据第 {index + 1} 条（{label}）的 links 含非对象元素";
                return false;
            }

            list.Add(new CacheAnimeLink(
                ReadOptionalString(link, "url") ?? string.Empty,
                ReadOptionalString(link, "title") ?? string.Empty));
        }

        links = list;
        return true;
    }

    private static bool TryReadRequiredString(
        JsonElement element,
        string property,
        int index,
        out string value,
        out string failure)
    {
        value = string.Empty;
        failure = string.Empty;
        if (!element.TryGetProperty(property, out var raw) ||
            raw.ValueKind != JsonValueKind.String)
        {
            failure = $"最近数据第 {index + 1} 条缺少字符串字段 {property}";
            return false;
        }

        value = raw.GetString() ?? string.Empty;
        if (value.Length == 0)
        {
            failure = $"最近数据第 {index + 1} 条的 {property} 为空";
            return false;
        }

        return true;
    }

    private static string? ReadOptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var raw) && raw.ValueKind == JsonValueKind.String
            ? raw.GetString()
            : null;

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new IOException("最近数据响应超过 4MB");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new IOException("最近数据响应超过 4MB");
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private static HttpClient CreateDefaultClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(3),
        UseCookies = false,
    });

    private static string DescribeSensitive(string value, string token, string? adminToken)
    {
        var normalized = Describe(value);
        foreach (var secret in new[] { token, adminToken }
                     .Where(secret => !string.IsNullOrWhiteSpace(secret))
                     .Distinct(StringComparer.Ordinal))
        {
            normalized = normalized.Replace(secret!, "***", StringComparison.Ordinal);
            normalized = normalized.Replace(
                Uri.EscapeDataString(secret!),
                "***",
                StringComparison.OrdinalIgnoreCase);
        }

        return normalized;
    }

    private static string Describe(string value)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 300 ? normalized : normalized[..300] + "...";
    }
}
