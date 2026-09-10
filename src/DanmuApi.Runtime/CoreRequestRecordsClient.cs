using System.Net;
using System.Text;
using System.Text.Json;

namespace DanmuApi.Runtime;

public sealed record CoreRequestRecord(
    string Interface,
    string? ParametersJson,
    string? Timestamp,
    string Method,
    string? ClientIp);

public sealed record CoreRequestRecordsReadResult(
    bool Succeeded,
    IReadOnlyList<CoreRequestRecord> Records,
    int TodayRequestCount,
    string Diagnostic)
{
    public static CoreRequestRecordsReadResult Success(
        IReadOnlyList<CoreRequestRecord> records,
        int todayRequestCount) =>
        new(true, records, todayRequestCount, $"已读取 {records.Count} 条请求记录");

    public static CoreRequestRecordsReadResult Failure(string diagnostic) =>
        new(false, [], 0, diagnostic);
}

public interface ICoreRequestRecordsClient
{
    Task<CoreRequestRecordsReadResult> ReadAsync(
        string host,
        int port,
        string? token,
        CancellationToken cancellationToken = default);
}

/// <summary>读取核心真实的 <c>GET /{TOKEN}/api/reqrecords</c> 请求记录接口。</summary>
public sealed class CoreRequestRecordsClient : ICoreRequestRecordsClient
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _requestTimeout;

    public CoreRequestRecordsClient(HttpClient? httpClient = null, TimeSpan? requestTimeout = null)
    {
        _httpClient = httpClient ?? CreateDefaultClient();
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(5);
        if (_requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), "请求记录请求超时必须大于零");
        }
    }

    public async Task<CoreRequestRecordsReadResult> ReadAsync(
        string host,
        int port,
        string? token,
        CancellationToken cancellationToken = default)
    {
        RuntimeValidation.ValidateHost(host);
        RuntimeValidation.ValidatePort(port);
        var effectiveToken = string.IsNullOrWhiteSpace(token)
            ? RuntimeDefaults.FallbackToken
            : token.Trim();
        var endpoint = BuildRecordsUri(host, port, effectiveToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(
                endpoint,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CoreRequestRecordsReadResult.Failure("核心请求记录请求已取消");
        }
        catch (OperationCanceledException)
        {
            return CoreRequestRecordsReadResult.Failure("核心请求记录请求超时");
        }
        catch (HttpRequestException error)
        {
            return CoreRequestRecordsReadResult.Failure($"核心请求记录请求失败：{Describe(error.Message, effectiveToken)}");
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
                return CoreRequestRecordsReadResult.Failure("核心请求记录响应读取已取消");
            }
            catch (OperationCanceledException)
            {
                return CoreRequestRecordsReadResult.Failure("核心请求记录响应读取超时");
            }
            catch (Exception error) when (error is IOException or HttpRequestException)
            {
                return CoreRequestRecordsReadResult.Failure($"核心请求记录响应读取失败：{Describe(error.Message, effectiveToken)}");
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return CoreRequestRecordsReadResult.Failure($"核心请求记录接口返回 HTTP {(int)response.StatusCode}");
            }

            try
            {
                return ParseResponse(body);
            }
            catch (JsonException error)
            {
                return CoreRequestRecordsReadResult.Failure($"核心请求记录响应 JSON 无效：{Describe(error.Message, effectiveToken)}");
            }
            catch (InvalidDataException error)
            {
                return CoreRequestRecordsReadResult.Failure($"核心请求记录响应格式无效：{Describe(error.Message, effectiveToken)}");
            }
        }
    }

    public static Uri BuildRecordsUri(string host, int port, string token)
    {
        RuntimeValidation.ValidateHost(host);
        RuntimeValidation.ValidatePort(port);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var authority = host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[')
            ? $"[{host}]"
            : host;
        return new Uri(
            $"http://{authority}:{port}/{Uri.EscapeDataString(token.Trim())}/api/reqrecords",
            UriKind.Absolute);
    }

    public static CoreRequestRecordsReadResult ParseResponse(ReadOnlySpan<byte> body)
    {
        try
        {
            return ParseResponseStrict(body);
        }
        catch (JsonException error)
        {
            return CoreRequestRecordsReadResult.Failure($"核心请求记录响应 JSON 无效：{error.Message}");
        }
        catch (InvalidDataException error)
        {
            return CoreRequestRecordsReadResult.Failure($"核心请求记录响应格式无效：{error.Message}");
        }
    }

    private static CoreRequestRecordsReadResult ParseResponseStrict(ReadOnlySpan<byte> body)
    {
        using var document = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
        });
        var root = RequireObject(document.RootElement, "根对象");
        var recordsElement = RequireProperty(root, "records");
        if (recordsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("records 必须是数组");
        }

        var todayElement = RequireProperty(root, "todayReqNum");
        if (todayElement.ValueKind != JsonValueKind.Number || !todayElement.TryGetInt32(out var todayRequestCount) || todayRequestCount < 0)
        {
            throw new InvalidDataException("todayReqNum 必须是非负整数");
        }

        var records = recordsElement.EnumerateArray().Select(ParseRecord).ToArray();
        return CoreRequestRecordsReadResult.Success(records, todayRequestCount);
    }

    private static CoreRequestRecord ParseRecord(JsonElement element)
    {
        var root = RequireObject(element, "请求记录");
        var interfacePath = RequireString(root, "interface");
        var method = RequireString(root, "method");
        string? timestamp = OptionalString(root, "timestamp");
        string? clientIp = OptionalString(root, "clientIp");
        string? parametersJson = null;
        if (root.TryGetProperty("params", out var parameters) && parameters.ValueKind != JsonValueKind.Null)
        {
            parametersJson = parameters.GetRawText();
        }

        return new CoreRequestRecord(interfacePath, parametersJson, timestamp, method, clientIp);
    }

    private static JsonElement RequireObject(JsonElement element, string label) =>
        element.ValueKind == JsonValueKind.Object
            ? element
            : throw new InvalidDataException($"{label} 必须是对象");

    private static JsonElement RequireProperty(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
            ? value
            : throw new InvalidDataException($"响应缺少字段 {name}");

    private static string RequireString(JsonElement root, string name)
    {
        var value = RequireProperty(root, name);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"字段 {name} 必须是非空字符串");
        }

        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"字段 {name} 必须是字符串或 null");
        }

        return value.GetString();
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new IOException("核心请求记录响应超过 4MB");
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

            if (buffer.Length + read > MaximumResponseBytes)
            {
                throw new IOException("核心请求记录响应超过 4MB");
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

    private static string Describe(string message, string token)
    {
        var normalized = string.IsNullOrWhiteSpace(message)
            ? "未提供失败详情"
            : message.Replace('\r', ' ').Replace('\n', ' ');
        return normalized
            .Replace(token, "***", StringComparison.Ordinal)
            .Replace(Uri.EscapeDataString(token), "***", StringComparison.Ordinal);
    }
}
