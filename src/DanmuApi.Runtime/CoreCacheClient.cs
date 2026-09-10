using System.Net;
using System.Text;
using System.Text.Json;

namespace DanmuApi.Runtime;

public sealed record CoreCacheItem(
    string Key,
    string Title,
    string Description);

public static class CoreCacheCatalog
{
    public static IReadOnlyList<CoreCacheItem> Items { get; } =
    [
        new("searchCache", "搜索结果缓存", "清除搜索接口保留的搜索结果。"),
        new("commentCache", "弹幕内容缓存", "清除视频弹幕内容缓存。"),
        new("requestHistory", "请求历史记录", "清除请求记录并重置今日请求次数。"),
        new("animes", "动漫搜索缓存", "清除最近的动漫搜索结果。"),
        new("bangumiData", "动画元数据缓存", "清除 Bangumi Data 内存与磁盘缓存。"),
        new("episodeIds", "剧集 ID 缓存", "清除剧集 ID 缓存。"),
        new("episodeNum", "剧集编号缓存", "将剧集编号恢复为核心初始值。"),
        new("lastSelectMap", "最后选择映射缓存", "清除最近一次手动选择的映射。"),
    ];

    private static readonly IReadOnlySet<string> Keys =
        Items.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);

    public static bool Contains(string key) => Keys.Contains(key);
}

public sealed record CoreCacheClearResult(
    bool Succeeded,
    string Diagnostic,
    IReadOnlyDictionary<string, int> ClearedItems)
{
    public static CoreCacheClearResult Success(
        string diagnostic,
        IReadOnlyDictionary<string, int>? clearedItems = null) =>
        new(true, diagnostic, clearedItems ?? new Dictionary<string, int>(StringComparer.Ordinal));

    public static CoreCacheClearResult Failure(string diagnostic) =>
        new(false, diagnostic, new Dictionary<string, int>(StringComparer.Ordinal));
}

public interface ICoreCacheClient
{
    Task<CoreCacheClearResult> ClearAsync(
        string host,
        int port,
        string? token,
        string? adminToken,
        IEnumerable<string> items,
        CancellationToken cancellationToken = default);
}

public sealed class CoreCacheClient : ICoreCacheClient
{
    private const int MaxResponseBytes = 1_048_576;
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _requestTimeout;

    public CoreCacheClient(HttpClient? httpClient = null, TimeSpan? requestTimeout = null)
    {
        _httpClient = httpClient ?? CreateDefaultClient();
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10);
        if (_requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), "核心缓存请求超时必须大于零");
        }
    }

    public async Task<CoreCacheClearResult> ClearAsync(
        string host,
        int port,
        string? token,
        string? adminToken,
        IEnumerable<string> items,
        CancellationToken cancellationToken = default)
    {
        RuntimeValidation.ValidateHost(host);
        RuntimeValidation.ValidatePort(port);
        ArgumentNullException.ThrowIfNull(items);

        var selectedItems = items
            .Where(item => item is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (selectedItems.Length == 0)
        {
            return CoreCacheClearResult.Failure("至少选择一项核心缓存");
        }

        if (selectedItems.Any(item => !CoreCacheCatalog.Contains(item)))
        {
            var invalid = selectedItems.First(item => !CoreCacheCatalog.Contains(item));
            return CoreCacheClearResult.Failure($"未知核心缓存项，拒绝请求: {invalid}");
        }

        var effectiveToken = string.IsNullOrWhiteSpace(token)
            ? RuntimeDefaults.FallbackToken
            : token.Trim();
        var effectiveAdminToken = string.IsNullOrWhiteSpace(adminToken)
            ? null
            : adminToken.Trim();
        var endpoint = BuildClearUri(host, port, effectiveToken, effectiveAdminToken);
        var payload = SerializeRequest(selectedItems);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

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
            return CoreCacheClearResult.Failure("核心缓存请求已取消");
        }
        catch (OperationCanceledException)
        {
            return CoreCacheClearResult.Failure("核心缓存请求超时");
        }
        catch (HttpRequestException error)
        {
            return CoreCacheClearResult.Failure(
                $"核心缓存请求失败: {DescribeSensitive(error.Message, effectiveToken, effectiveAdminToken)}");
        }

        using (response)
        {
            byte[] body;
            try
            {
                body = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CoreCacheClearResult.Failure("核心缓存响应读取已取消");
            }
            catch (OperationCanceledException)
            {
                return CoreCacheClearResult.Failure("核心缓存响应读取超时");
            }
            catch (Exception error) when (error is IOException or HttpRequestException)
            {
                return CoreCacheClearResult.Failure(
                    $"核心缓存响应读取失败: {DescribeSensitive(error.Message, effectiveToken, effectiveAdminToken)}");
            }

            if ((int)response.StatusCode is < 200 or > 299)
            {
                return CoreCacheClearResult.Failure(
                    $"核心缓存接口返回 HTTP {(int)response.StatusCode}: " +
                    DescribeSensitive(ExtractMessage(body), effectiveToken, effectiveAdminToken));
            }

            return RedactResult(ParseResponse(body), effectiveToken, effectiveAdminToken);
        }
    }

    public static Uri BuildClearUri(
        string host,
        int port,
        string token,
        string? adminToken)
    {
        RuntimeValidation.ValidateHost(host);
        RuntimeValidation.ValidatePort(port);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var pathToken = string.IsNullOrWhiteSpace(adminToken) ? token.Trim() : adminToken.Trim();
        var authority = host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[')
            ? $"[{host}]"
            : host;
        return new Uri(
            $"http://{authority}:{port}/{Uri.EscapeDataString(pathToken)}/api/cache/clear",
            UriKind.Absolute);
    }

    public static CoreCacheClearResult ParseResponse(ReadOnlySpan<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("success", out var success) ||
                success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return CoreCacheClearResult.Failure("核心缓存接口响应缺少布尔字段 success");
            }

            if (!success.GetBoolean())
            {
                var failureMessage = root.TryGetProperty("message", out var failedMessage) &&
                                      failedMessage.ValueKind == JsonValueKind.String
                    ? failedMessage.GetString()
                    : null;
                return CoreCacheClearResult.Failure(
                    string.IsNullOrWhiteSpace(failureMessage)
                        ? "核心缓存接口报告清理失败"
                        : $"核心缓存接口报告清理失败: {Describe(failureMessage)}");
            }

            if (!root.TryGetProperty("clearedItems", out var cleared) ||
                cleared.ValueKind != JsonValueKind.Object)
            {
                return CoreCacheClearResult.Failure("核心缓存接口响应缺少 clearedItems 对象");
            }

            var values = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var property in cleared.EnumerateObject())
            {
                if (!CoreCacheCatalog.Contains(property.Name) &&
                    property.Name is not ("reqRecords" or "todayReqNum"))
                {
                    return CoreCacheClearResult.Failure($"核心缓存接口返回未知清理结果: {property.Name}");
                }

                if (property.Value.ValueKind != JsonValueKind.Number ||
                    !property.Value.TryGetInt32(out var count))
                {
                    return CoreCacheClearResult.Failure($"核心缓存接口返回的清理数量无效: {property.Name}");
                }

                values[property.Name] = count;
            }

            var message = root.TryGetProperty("message", out var successMessage) &&
                          successMessage.ValueKind == JsonValueKind.String
                ? successMessage.GetString()
                : null;
            return CoreCacheClearResult.Success(
                string.IsNullOrWhiteSpace(message) ? "核心缓存清理完成" : Describe(message),
                values);
        }
        catch (JsonException error)
        {
            return CoreCacheClearResult.Failure($"核心缓存接口响应 JSON 无效: {Describe(error.Message)}");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new IOException("核心缓存响应超过 1MB");
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
                throw new IOException("核心缓存响应超过 1MB");
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private static string ExtractMessage(ReadOnlySpan<byte> body)
    {
        if (body.Length == 0)
        {
            return "未提供响应详情";
        }

        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            var root = document.RootElement;
            foreach (var name in new[] { "message", "errorMessage" })
            {
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty(name, out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    return Describe(value.GetString()!);
                }
            }
        }
        catch (JsonException error)
        {
            return $"响应正文不是有效 JSON: {Describe(error.Message)}";
        }

        return "响应 JSON 未包含 message 或 errorMessage";
    }

    private static HttpClient CreateDefaultClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(3),
        UseCookies = false,
    });

    private static string SerializeRequest(IReadOnlyList<string> items)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("items");
            writer.WriteStartArray();
            foreach (var item in items)
            {
                writer.WriteStringValue(item);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static CoreCacheClearResult RedactResult(
        CoreCacheClearResult result,
        string token,
        string? adminToken) =>
        result with { Diagnostic = DescribeSensitive(result.Diagnostic, token, adminToken) };

    private static string DescribeSensitive(
        string value,
        string token,
        string? adminToken)
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
        return normalized.Length <= 500 ? normalized : normalized[..500] + "...";
    }
}
