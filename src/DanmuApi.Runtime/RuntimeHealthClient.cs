using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DanmuApi.Runtime;

public sealed class RuntimeHealthClient : IRuntimeHealthClient
{
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _requestTimeout;

    public RuntimeHealthClient(HttpClient? httpClient = null, TimeSpan? requestTimeout = null)
    {
        _httpClient = httpClient ?? CreateDefaultClient();
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(3);
        if (_requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), "健康检查超时必须大于零");
        }
    }

    public async Task<RuntimeHealthSnapshot> ReadAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        RuntimeValidation.ValidateHost(host);
        RuntimeValidation.ValidatePort(port);
        var endpoint = BuildHealthUri(host, port);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RuntimeHealthException(HealthFailureKind.Connection, $"健康接口连接超时: {endpoint}", error);
        }
        catch (HttpRequestException error)
        {
            throw new RuntimeHealthException(HealthFailureKind.Connection, $"健康接口连接失败: {endpoint}", error);
        }

        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new RuntimeHealthException(
                    HealthFailureKind.HttpStatus,
                    $"健康接口返回 HTTP {(int)response.StatusCode}: {endpoint}",
                    statusCode: (int)response.StatusCode);
            }

            if (response.Content.Headers.ContentLength is > RuntimeDefaults.MaxHealthBodyBytes)
            {
                throw new RuntimeHealthException(HealthFailureKind.InvalidDocument, "健康响应超过 1MB");
            }

            byte[] body;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                while (true)
                {
                    var read = await stream.ReadAsync(chunk.AsMemory(), timeout.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    if (buffer.Length + read > RuntimeDefaults.MaxHealthBodyBytes)
                    {
                        throw new RuntimeHealthException(HealthFailureKind.InvalidDocument, "健康响应超过 1MB");
                    }

                    buffer.Write(chunk, 0, read);
                }

                body = buffer.ToArray();
            }
            catch (RuntimeHealthException)
            {
                throw;
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                throw new RuntimeHealthException(HealthFailureKind.Connection, $"健康接口读取超时: {endpoint}", error);
            }
            catch (HttpRequestException error)
            {
                throw new RuntimeHealthException(HealthFailureKind.Connection, $"健康接口读取失败: {endpoint}", error);
            }

            try
            {
                return Parse(body);
            }
            catch (RuntimeHealthException)
            {
                throw;
            }
            catch (JsonException error)
            {
                throw new RuntimeHealthException(HealthFailureKind.InvalidDocument, "健康接口返回的 JSON 无效", error);
            }
        }
    }

    public static RuntimeHealthSnapshot Parse(ReadOnlySpan<byte> body)
    {
        using var document = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
        });
        var root = document.RootElement;
        RequireKind(root, JsonValueKind.Object, "root");
        if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
        {
            if (root.TryGetProperty("ok", out var present) && present.ValueKind == JsonValueKind.False)
            {
                throw new RuntimeHealthException(HealthFailureKind.InvalidDocument, "健康接口报告 ok=false");
            }

            throw new RuntimeHealthException(HealthFailureKind.InvalidDocument, "健康响应缺少布尔字段 ok=true");
        }

        var ports = OptionalObject(root, "ports");
        var accessControl = OptionalObject(root, "accessControl") is { } access
            ? new AccessControlSummary(
                OptionalString(access, "mode"),
                OptionalInt(access, "whitelistCount"),
                OptionalInt(access, "blacklistCount"),
                OptionalLong(access, "blockedRequests"))
            : null;

        return new RuntimeHealthSnapshot(
            OptionalLong(root, "pid"),
            OptionalString(root, "node"),
            OptionalLong(root, "uptimeSec"),
            OptionalString(root, "host"),
            ports is null ? null : OptionalInt(ports.Value, "main"),
            ports is null ? null : OptionalInt(ports.Value, "proxy"),
            OptionalString(root, "cwd"),
            OptionalString(root, "envHome"),
            OptionalString(root, "resolvedHome"),
            OptionalString(root, "cacheProbeDir"),
            OptionalBool(root, "cacheProbeWritable"),
            OptionalString(root, "variant"),
            OptionalString(root, "variantLabel"),
            OptionalString(root, "runtimeIdentity"),
            OptionalLong(root, "requestCount"),
            OptionalLong(root, "lastRequestAt"),
            OptionalString(root, "lastRequestPath"),
            OptionalString(root, "lastClientIp"),
            OptionalTimestamp(root, "envFileMtimeMs"),
            OptionalString(root, "logFile"),
            OptionalString(root, "logLevel"),
            accessControl);
    }

    public static Uri BuildHealthUri(string host, int port)
    {
        var authority = host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[')
            ? $"[{host}]"
            : host;
        return new Uri($"http://{authority}:{port}/__health", UriKind.Absolute);
    }

    private static HttpClient CreateDefaultClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(2),
        UseCookies = false,
    });

    private static JsonElement? OptionalObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        RequireKind(value, JsonValueKind.Object, name);
        return value;
    }

    private static string? OptionalString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        RequireKind(value, JsonValueKind.String, name);
        return value.GetString();
    }

    private static bool? OptionalBool(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new RuntimeHealthException(HealthFailureKind.InvalidDocument, $"字段 {name} 必须是布尔值或 null");
        }

        return value.GetBoolean();
    }

    private static int? OptionalInt(JsonElement parent, string name)
    {
        var value = OptionalLong(parent, name);
        if (value is null)
        {
            return null;
        }

        if (value.Value is < int.MinValue or > int.MaxValue)
        {
            throw new RuntimeHealthException(HealthFailureKind.InvalidDocument, $"字段 {name} 超出 32 位整数范围");
        }

        return (int)value.Value;
    }

    private static long? OptionalLong(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
        {
            throw new RuntimeHealthException(HealthFailureKind.InvalidDocument, $"字段 {name} 必须是 64 位整数或 null");
        }

        return result;
    }

    private static decimal? OptionalTimestamp(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var result) || result < 0)
        {
            throw new RuntimeHealthException(HealthFailureKind.InvalidDocument, $"字段 {name} 必须是非负数字或 null");
        }

        return result;
    }

    private static void RequireKind(JsonElement value, JsonValueKind expected, string name)
    {
        if (value.ValueKind != expected)
        {
            throw new RuntimeHealthException(HealthFailureKind.InvalidDocument, $"字段 {name} 必须是 {expected}");
        }
    }
}
