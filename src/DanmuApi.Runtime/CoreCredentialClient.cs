using System.Net;
using System.Text;
using System.Text.Json;

namespace DanmuApi.Runtime;

public sealed record CoreCookieVerificationResult(
    bool RequestSucceeded,
    bool IsValid,
    string Diagnostic,
    string? UserName = null,
    long? ExpiresAt = null);

public sealed record CoreQrGenerateResult(
    bool Succeeded,
    string Diagnostic,
    string? Url = null,
    string? QrCodeKey = null);

public sealed record CoreQrCheckResult(
    bool Succeeded,
    string Diagnostic,
    int? Code = null,
    string? Cookie = null);

public sealed record CoreAiVerificationResult(bool Succeeded, string Diagnostic);

public interface ICoreCredentialClient
{
    Task<CoreCookieVerificationResult> VerifyBilibiliCookieAsync(
        string host,
        int port,
        string token,
        string adminToken,
        string? cookie,
        CancellationToken cancellationToken = default);

    Task<CoreQrGenerateResult> GenerateBilibiliQrAsync(
        string host,
        int port,
        string token,
        string adminToken,
        CancellationToken cancellationToken = default);

    Task<CoreQrCheckResult> CheckBilibiliQrAsync(
        string host,
        int port,
        string token,
        string adminToken,
        string qrCodeKey,
        CancellationToken cancellationToken = default);

    Task<CoreAiVerificationResult> VerifyAiAsync(
        string host,
        int port,
        string token,
        string adminToken,
        string? apiKey,
        CancellationToken cancellationToken = default);
}

public sealed class CoreCredentialClient : ICoreCredentialClient
{
    private const int MaximumResponseBytes = 1_048_576;
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _requestTimeout;

    public CoreCredentialClient(HttpClient? httpClient = null, TimeSpan? requestTimeout = null)
    {
        _httpClient = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            UseCookies = false,
        });
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(15);
        if (_requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), "核心凭据请求超时必须大于零");
        }
    }

    public async Task<CoreCookieVerificationResult> VerifyBilibiliCookieAsync(
        string host,
        int port,
        string token,
        string adminToken,
        string? cookie,
        CancellationToken cancellationToken = default)
    {
        var payload = string.IsNullOrWhiteSpace(cookie)
            ? "{}"
            : CreateStringPayload("cookie", cookie);
        var response = await SendAsync(
            host,
            port,
            token,
            adminToken,
            "api/cookie/verify",
            payload,
            cancellationToken,
            cookie).ConfigureAwait(false);
        if (!response.Succeeded)
        {
            return new CoreCookieVerificationResult(false, false, response.Diagnostic);
        }

        try
        {
            using var document = ParseDocument(response.Body);
            var root = document.RootElement;
            if (!ReadRequiredBoolean(root, "success", out var success))
            {
                return new CoreCookieVerificationResult(false, false, "Bilibili Cookie 验证响应缺少布尔字段 success");
            }

            if (!success)
            {
                return new CoreCookieVerificationResult(
                    false,
                    false,
                    Redact(ReadMessage(root, "Bilibili Cookie 验证失败"), [token, adminToken, cookie]));
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !ReadRequiredBoolean(data, "isValid", out var isValid))
            {
                return new CoreCookieVerificationResult(false, false, "Bilibili Cookie 验证响应缺少 data.isValid 布尔字段");
            }

            var userName = ReadOptionalString(data, "uname");
            long? expiresAt = data.TryGetProperty("expiresAt", out var expires) && expires.TryGetInt64(out var timestamp)
                ? timestamp
                : null;
            var diagnostic = isValid
                ? string.IsNullOrWhiteSpace(userName) ? "Bilibili Cookie 有效" : $"Bilibili Cookie 有效，账号：{userName}"
                : ReadOptionalString(data, "error") ?? "Bilibili Cookie 无效或已失效";
            return new CoreCookieVerificationResult(
                true,
                isValid,
                Redact(diagnostic, [token, adminToken, cookie]),
                userName,
                expiresAt);
        }
        catch (JsonException error)
        {
            return new CoreCookieVerificationResult(false, false, $"Bilibili Cookie 验证响应 JSON 无效：{Describe(error.Message)}");
        }
    }

    public async Task<CoreQrGenerateResult> GenerateBilibiliQrAsync(
        string host,
        int port,
        string token,
        string adminToken,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            host,
            port,
            token,
            adminToken,
            "api/cookie/qr/generate",
            "{}",
            cancellationToken).ConfigureAwait(false);
        if (!response.Succeeded)
        {
            return CoreQrGenerateResultFailure(response.Diagnostic);
        }

        try
        {
            using var document = ParseDocument(response.Body);
            var root = document.RootElement;
            if (!ReadRequiredBoolean(root, "success", out var success))
            {
                return CoreQrGenerateResultFailure("Bilibili 二维码响应缺少布尔字段 success");
            }

            if (!success)
            {
                return CoreQrGenerateResultFailure(
                    Redact(ReadMessage(root, "Bilibili 二维码生成失败"), [token, adminToken]));
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return CoreQrGenerateResultFailure("Bilibili 二维码响应缺少 data 对象");
            }

            var url = ReadOptionalString(data, "url");
            var key = ReadOptionalString(data, "qrcode_key");
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
                parsed.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(key))
            {
                return CoreQrGenerateResultFailure("Bilibili 二维码响应缺少有效 url 或 qrcode_key");
            }

            return new CoreQrGenerateResult(true, "Bilibili 二维码已生成", url, key);
        }
        catch (JsonException error)
        {
            return CoreQrGenerateResultFailure($"Bilibili 二维码响应 JSON 无效：{Describe(error.Message)}");
        }
    }

    public async Task<CoreQrCheckResult> CheckBilibiliQrAsync(
        string host,
        int port,
        string token,
        string adminToken,
        string qrCodeKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qrCodeKey);
        var response = await SendAsync(
            host,
            port,
            token,
            adminToken,
            "api/cookie/qr/check",
            CreateStringPayload("qrcode_key", qrCodeKey),
            cancellationToken,
            qrCodeKey).ConfigureAwait(false);
        if (!response.Succeeded)
        {
            return CoreQrCheckResultFailure(response.Diagnostic);
        }

        try
        {
            using var document = ParseDocument(response.Body);
            var root = document.RootElement;
            if (!ReadRequiredBoolean(root, "success", out var success))
            {
                return CoreQrCheckResultFailure("Bilibili 扫码状态响应缺少布尔字段 success");
            }

            if (!success)
            {
                return CoreQrCheckResultFailure(
                    Redact(ReadMessage(root, "Bilibili 扫码状态检查失败"), [token, adminToken, qrCodeKey]));
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("code", out var codeElement) || !codeElement.TryGetInt32(out var code))
            {
                return CoreQrCheckResultFailure("Bilibili 扫码状态响应缺少整数 data.code");
            }

            var cookie = ReadOptionalString(data, "cookie");
            if (code == 0 && string.IsNullOrWhiteSpace(cookie))
            {
                return CoreQrCheckResultFailure("Bilibili 扫码登录成功，但核心响应没有返回 Cookie");
            }

            var diagnostic = code switch
            {
                86101 => "等待扫描二维码",
                86090 => "已扫描，请在 Bilibili 客户端确认",
                86038 => "二维码已过期",
                0 => "Bilibili 扫码登录成功，请保存配置",
                _ => ReadOptionalString(data, "message") ?? $"Bilibili 返回未知扫码状态：{code}",
            };
            return new CoreQrCheckResult(
                true,
                Redact(diagnostic, [token, adminToken, qrCodeKey, cookie]),
                code,
                cookie);
        }
        catch (JsonException error)
        {
            return CoreQrCheckResultFailure($"Bilibili 扫码状态响应 JSON 无效：{Describe(error.Message)}");
        }
    }

    public async Task<CoreAiVerificationResult> VerifyAiAsync(
        string host,
        int port,
        string token,
        string adminToken,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        var payload = string.IsNullOrWhiteSpace(apiKey)
            ? "{}"
            : CreateStringPayload("aiApiKey", apiKey);
        var response = await SendAsync(
            host,
            port,
            token,
            adminToken,
            "api/ai/verify",
            payload,
            cancellationToken,
            apiKey).ConfigureAwait(false);
        if (!response.Succeeded)
        {
            return new CoreAiVerificationResult(false, response.Diagnostic);
        }

        try
        {
            using var document = ParseDocument(response.Body);
            var root = document.RootElement;
            if (!ReadRequiredBoolean(root, "ok", out var ok))
            {
                return new CoreAiVerificationResult(false, "AI 验证响应缺少布尔字段 ok");
            }

            var diagnostic = Redact(
                ReadMessage(root, ok ? "AI 服务连通性测试成功" : "AI 服务连通性测试失败"),
                [token, adminToken, apiKey]);
            return new CoreAiVerificationResult(ok, diagnostic);
        }
        catch (JsonException error)
        {
            return new CoreAiVerificationResult(false, $"AI 验证响应 JSON 无效：{Describe(error.Message)}");
        }
    }

    private async Task<CoreHttpResult> SendAsync(
        string host,
        int port,
        string token,
        string adminToken,
        string path,
        string payload,
        CancellationToken cancellationToken,
        params string?[] additionalSecrets)
    {
        RuntimeValidation.ValidateHost(host);
        RuntimeValidation.ValidatePort(port);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var endpoint = BuildUri(host, port, adminToken, path);
        var secrets = new[] { token, adminToken }.Concat(additionalSecrets).ToArray();
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
            return CoreHttpResult.Failure("核心凭据请求已取消");
        }
        catch (OperationCanceledException)
        {
            return CoreHttpResult.Failure("核心凭据请求超时");
        }
        catch (HttpRequestException error)
        {
            return CoreHttpResult.Failure($"核心凭据请求失败：{Redact(error.Message, secrets)}");
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
                return CoreHttpResult.Failure("核心凭据响应读取已取消");
            }
            catch (OperationCanceledException)
            {
                return CoreHttpResult.Failure("核心凭据响应读取超时");
            }
            catch (Exception error) when (error is IOException or HttpRequestException)
            {
                return CoreHttpResult.Failure($"核心凭据响应读取失败：{Redact(error.Message, secrets)}");
            }

            if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
            {
                var diagnostic = ExtractMessage(body, secrets);
                return new CoreHttpResult(false, $"核心凭据接口返回 HTTP {(int)response.StatusCode}：{diagnostic}", body);
            }

            return new CoreHttpResult(true, "核心凭据请求成功", body);
        }
    }

    public static Uri BuildUri(string host, int port, string pathToken, string path)
    {
        RuntimeValidation.ValidateHost(host);
        RuntimeValidation.ValidatePort(port);
        ArgumentException.ThrowIfNullOrWhiteSpace(pathToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var authority = host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[')
            ? $"[{host}]"
            : host;
        return new Uri(
            $"http://{authority}:{port}/{Uri.EscapeDataString(pathToken.Trim())}/{path.TrimStart('/')}",
            UriKind.Absolute);
    }

    private static JsonDocument ParseDocument(byte[] body) => JsonDocument.Parse(body, new JsonDocumentOptions
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16,
    });

    private static bool ReadRequiredBoolean(JsonElement parent, string name, out bool value)
    {
        if (parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(name, out var property) &&
            property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        value = false;
        return false;
    }

    private static string? ReadOptionalString(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ReadMessage(JsonElement root, string fallback) =>
        ReadOptionalString(root, "message") is { Length: > 0 } message
            ? Describe(message)
            : fallback;

    private static string ExtractMessage(byte[] body, IReadOnlyList<string?> secrets)
    {
        try
        {
            using var document = ParseDocument(body);
            var root = document.RootElement;
            foreach (var name in new[] { "message", "errorMessage" })
            {
                var message = ReadOptionalString(root, name);
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return Redact(message, secrets);
                }
            }

            return "响应未提供失败详情";
        }
        catch (JsonException error)
        {
            return $"响应正文不是有效 JSON：{Redact(error.Message, secrets)}";
        }
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
            throw new IOException("核心凭据响应超过 1MB");
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
                throw new IOException("核心凭据响应超过 1MB");
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private static string Redact(string message, IEnumerable<string?> secrets)
    {
        var value = Describe(message);
        foreach (var secret in secrets
                     .Where(secret => !string.IsNullOrWhiteSpace(secret))
                     .Distinct(StringComparer.Ordinal))
        {
            value = value.Replace(secret!, "***", StringComparison.Ordinal);
            value = value.Replace(Uri.EscapeDataString(secret!), "***", StringComparison.OrdinalIgnoreCase);
        }

        return value;
    }

    private static string Describe(string message)
    {
        var value = string.IsNullOrWhiteSpace(message)
            ? "未提供失败详情"
            : message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 500 ? value : value[..500] + "...";
    }

    private static CoreQrGenerateResult CoreQrGenerateResultFailure(string diagnostic) =>
        new(false, diagnostic);

    private static CoreQrCheckResult CoreQrCheckResultFailure(string diagnostic) =>
        new(false, diagnostic);

    private sealed record CoreHttpResult(bool Succeeded, string Diagnostic, byte[] Body)
    {
        public static CoreHttpResult Failure(string diagnostic) => new(false, diagnostic, []);
    }
}
