using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DanmuApi.Runtime;

public sealed record CoreEnvDeleteResult(bool Succeeded, string Diagnostic)
{
    public static CoreEnvDeleteResult Success(string diagnostic = "核心已删除环境变量") => new(true, diagnostic);

    public static CoreEnvDeleteResult Failure(string diagnostic) => new(false, diagnostic);
}

public sealed record CoreEnvSetResult(bool Succeeded, string Diagnostic)
{
    public static CoreEnvSetResult Success(string diagnostic = "核心已写入环境变量") => new(true, diagnostic);

    public static CoreEnvSetResult Failure(string diagnostic) => new(false, diagnostic);
}

public sealed record CoreEnvValueResult(bool Succeeded, string? Value, string Diagnostic)
{
    public static CoreEnvValueResult Success(string? value) => new(true, value, string.Empty);

    public static CoreEnvValueResult Failure(string diagnostic) => new(false, null, diagnostic);
}

public interface ICoreEnvClient
{
    Task<CoreEnvDeleteResult> DeleteAsync(
        string host,
        int port,
        string? token,
        string? adminToken,
        string key,
        CancellationToken cancellationToken = default);

    Task<CoreEnvSetResult> SetAsync(
        string host,
        int port,
        string? token,
        string? adminToken,
        string key,
        string value,
        CancellationToken cancellationToken = default);

    Task<CoreEnvValueResult> ReadConfigValueAsync(
        string host,
        int port,
        string? token,
        string? adminToken,
        string key,
        CancellationToken cancellationToken = default);
}

/// <summary>调用核心真实的 <c>POST /{TOKEN}/api/env/del</c> 删除接口。</summary>
public sealed class CoreEnvClient : ICoreEnvClient
{
    private const int MaximumResponseBytes = 1_048_576;
    private static readonly Regex EnvironmentKeyPattern = new(
        @"^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _requestTimeout;

    public CoreEnvClient(HttpClient? httpClient = null, TimeSpan? requestTimeout = null)
    {
        _httpClient = httpClient ?? CreateDefaultClient();
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10);
        if (_requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), "核心环境变量请求超时必须大于零");
        }
    }

    public async Task<CoreEnvDeleteResult> DeleteAsync(
        string host,
        int port,
        string? token,
        string? adminToken,
        string key,
        CancellationToken cancellationToken = default)
    {
        RuntimeValidation.ValidateHost(host);
        RuntimeValidation.ValidatePort(port);
        ValidateKey(key);

        var effectiveToken = string.IsNullOrWhiteSpace(token)
            ? RuntimeDefaults.FallbackToken
            : token.Trim();
        var effectiveAdminToken = string.IsNullOrWhiteSpace(adminToken)
            ? null
            : adminToken.Trim();
        var pathToken = effectiveAdminToken ?? effectiveToken;
        var endpoint = BuildDeleteUri(host, port, pathToken);
        var payload = CreateStringPayload("key", key);
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
            return CoreEnvDeleteResult.Failure("核心环境变量删除请求已取消");
        }
        catch (OperationCanceledException)
        {
            return CoreEnvDeleteResult.Failure("核心环境变量删除请求超时");
        }
        catch (HttpRequestException error)
        {
            return CoreEnvDeleteResult.Failure($"核心环境变量删除请求失败：{Describe(error.Message, effectiveToken, effectiveAdminToken)}");
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
                return CoreEnvDeleteResult.Failure("核心环境变量删除响应读取已取消");
            }
            catch (OperationCanceledException)
            {
                return CoreEnvDeleteResult.Failure("核心环境变量删除响应读取超时");
            }
            catch (Exception error) when (error is IOException or HttpRequestException)
            {
                return CoreEnvDeleteResult.Failure($"核心环境变量删除响应读取失败：{Describe(error.Message, effectiveToken, effectiveAdminToken)}");
            }

            if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
            {
                return CoreEnvDeleteResult.Failure(
                    $"核心环境变量删除接口返回 HTTP {(int)response.StatusCode}：{DescribeMessage(body, effectiveToken, effectiveAdminToken)}");
            }

            return ParseResponse(body, effectiveToken, effectiveAdminToken);
        }
    }

    public static Uri BuildDeleteUri(string host, int port, string pathToken)
    {
        RuntimeValidation.ValidateHost(host);
        RuntimeValidation.ValidatePort(port);
        ArgumentException.ThrowIfNullOrWhiteSpace(pathToken);
        var authority = host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[')
            ? $"[{host}]"
            : host;
        return new Uri(
            $"http://{authority}:{port}/{Uri.EscapeDataString(pathToken.Trim())}/api/env/del",
            UriKind.Absolute);
    }

    public static CoreEnvDeleteResult ParseResponse(ReadOnlySpan<byte> body) => ParseResponse(body, null, null);

    private static CoreEnvDeleteResult ParseResponse(
        ReadOnlySpan<byte> body,
        string? token,
        string? adminToken)
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
                return CoreEnvDeleteResult.Failure("核心环境变量删除响应缺少布尔字段 success");
            }

            var message = root.TryGetProperty("message", out var messageElement) &&
                          messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString()
                : null;
            var diagnostic = string.IsNullOrWhiteSpace(message)
                ? success.GetBoolean() ? "核心已删除环境变量" : "核心环境变量删除失败"
                : Describe(message!, token, adminToken);
            return success.GetBoolean()
                ? CoreEnvDeleteResult.Success(diagnostic)
                : CoreEnvDeleteResult.Failure($"核心环境变量删除失败：{diagnostic}");
        }
        catch (JsonException error)
        {
            return CoreEnvDeleteResult.Failure($"核心环境变量删除响应 JSON 无效：{Describe(error.Message, token, adminToken)}");
        }
    }

    public async Task<CoreEnvSetResult> SetAsync(
        string host,
        int port,
        string? token,
        string? adminToken,
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        RuntimeValidation.ValidateHost(host);
        RuntimeValidation.ValidatePort(port);
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(value);

        var effectiveToken = string.IsNullOrWhiteSpace(token) ? RuntimeDefaults.FallbackToken : token.Trim();
        var effectiveAdminToken = string.IsNullOrWhiteSpace(adminToken) ? null : adminToken.Trim();
        var pathToken = effectiveAdminToken ?? effectiveToken;
        var endpoint = BuildApiUri(host, port, pathToken, "env/set");
        var payload = CreateKeyValuePayload(key, value);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CoreEnvSetResult.Failure($"写入 {key} 请求已取消");
        }
        catch (OperationCanceledException)
        {
            return CoreEnvSetResult.Failure($"写入 {key} 请求超时");
        }
        catch (HttpRequestException error)
        {
            return CoreEnvSetResult.Failure($"写入 {key} 请求失败：{Describe(error.Message, effectiveToken, effectiveAdminToken)}");
        }

        using (response)
        {
            byte[] body;
            try
            {
                body = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException)
            {
                return CoreEnvSetResult.Failure($"写入 {key} 响应读取失败：{Describe(error.Message, effectiveToken, effectiveAdminToken)}");
            }

            if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
            {
                return CoreEnvSetResult.Failure(
                    $"写入 {key} 接口返回 HTTP {(int)response.StatusCode}：{DescribeMessage(body, effectiveToken, effectiveAdminToken)}");
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("success", out var success) &&
                    success.ValueKind is JsonValueKind.False or JsonValueKind.True)
                {
                    if (!success.GetBoolean())
                    {
                        var message = root.TryGetProperty("message", out var messageElement) &&
                                      messageElement.ValueKind == JsonValueKind.String
                            ? messageElement.GetString()
                            : null;
                        var diagnostic = string.IsNullOrWhiteSpace(message)
                            ? $"写入 {key} 失败"
                            : Describe(message!, effectiveToken, effectiveAdminToken);
                        return CoreEnvSetResult.Failure($"核心拒绝写入 {key}：{diagnostic}");
                    }

                    return CoreEnvSetResult.Success($"核心已写入 {key}，等待热加载");
                }

                return CoreEnvSetResult.Success($"核心已接受 {key} 写入请求");
            }
            catch (JsonException)
            {
                return CoreEnvSetResult.Success($"核心已接受 {key} 写入请求");
            }
        }
    }

    public async Task<CoreEnvValueResult> ReadConfigValueAsync(
        string host,
        int port,
        string? token,
        string? adminToken,
        string key,
        CancellationToken cancellationToken = default)
    {
        RuntimeValidation.ValidateHost(host);
        RuntimeValidation.ValidatePort(port);
        ValidateKey(key);

        var effectiveToken = string.IsNullOrWhiteSpace(token) ? RuntimeDefaults.FallbackToken : token.Trim();
        var effectiveAdminToken = string.IsNullOrWhiteSpace(adminToken) ? null : adminToken.Trim();
        var pathToken = effectiveAdminToken ?? effectiveToken;
        var endpoint = BuildApiUri(host, port, pathToken, "config");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CoreEnvValueResult.Failure($"读取 {key} 请求已取消");
        }
        catch (OperationCanceledException)
        {
            return CoreEnvValueResult.Failure($"读取 {key} 请求超时");
        }
        catch (HttpRequestException error)
        {
            return CoreEnvValueResult.Failure($"读取 {key} 请求失败：{Describe(error.Message, effectiveToken, effectiveAdminToken)}");
        }

        using (response)
        {
            byte[] body;
            try
            {
                body = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException)
            {
                return CoreEnvValueResult.Failure($"读取 {key} 响应读取失败：{Describe(error.Message, effectiveToken, effectiveAdminToken)}");
            }

            if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
            {
                return CoreEnvValueResult.Failure(
                    $"读取 {key} 接口返回 HTTP {(int)response.StatusCode}：{DescribeMessage(body, effectiveToken, effectiveAdminToken)}");
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return CoreEnvValueResult.Failure("核心配置响应不是 JSON 对象");
                }

                foreach (var container in new[] { "originalEnvVars", "envs" })
                {
                    if (!root.TryGetProperty(container, out var section) || section.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (!section.TryGetProperty(key, out var value))
                    {
                        continue;
                    }

                    var extracted = ExtractConfigValue(value);
                    if (extracted is not null)
                    {
                        return CoreEnvValueResult.Success(extracted);
                    }
                }

                return CoreEnvValueResult.Failure($"核心配置中不存在 {key}");
            }
            catch (JsonException error)
            {
                return CoreEnvValueResult.Failure($"核心配置响应 JSON 无效：{Describe(error.Message, effectiveToken, effectiveAdminToken)}");
            }
        }
    }

    private static string? ExtractConfigValue(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            return value.TryGetProperty("value", out var inner) && inner.ValueKind is not JsonValueKind.Null
                ? inner.ToString()
                : null;
        }

        return value.ToString();
    }

    private static Uri BuildApiUri(string host, int port, string pathToken, string relative)
    {
        var authority = host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[')
            ? $"[{host}]"
            : host;
        return new Uri(
            $"http://{authority}:{port}/{Uri.EscapeDataString(pathToken.Trim())}/api/{relative.TrimStart('/')}",
            UriKind.Absolute);
    }

    private static string CreateKeyValuePayload(string key, string value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("key", key);
            writer.WriteString("value", value);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string CreateStringPayload(string name, string value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(name, value);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new IOException("核心环境变量删除响应超过 1MB");
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
                throw new IOException("核心环境变量删除响应超过 1MB");
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

    private static string DescribeMessage(ReadOnlySpan<byte> body, string token, string? adminToken)
    {
        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "message", "errorMessage" })
                {
                    if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        return Describe(value.GetString() ?? string.Empty, token, adminToken);
                    }
                }
            }
        }
        catch (JsonException error)
        {
            return $"响应正文不是有效 JSON：{Describe(error.Message, token, adminToken)}";
        }

        return "响应未提供失败详情";
    }

    private static string Describe(string message, string? token, string? adminToken)
    {
        var normalized = string.IsNullOrWhiteSpace(message)
            ? "未提供失败详情"
            : message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        foreach (var secret in new[] { token, adminToken }
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.Ordinal))
        {
            normalized = normalized.Replace(secret!, "***", StringComparison.Ordinal);
            normalized = normalized.Replace(Uri.EscapeDataString(secret!), "***", StringComparison.OrdinalIgnoreCase);
        }

        return normalized.Length <= 500 ? normalized : normalized[..500] + "...";
    }

    private static void ValidateKey(string key)
    {
        if (key is null || !EnvironmentKeyPattern.IsMatch(key))
        {
            throw new ArgumentException($"环境变量名非法：{key}", nameof(key));
        }
    }
}
