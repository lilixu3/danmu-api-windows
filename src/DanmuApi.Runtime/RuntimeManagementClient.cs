using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DanmuApi.Runtime;

public sealed record AccessDevice(string Ip, long TotalRequests, long AllowedRequests, long BlockedRequests,
    long LastSeenAtMs, bool InBlacklist, bool EffectiveBlocked);
public sealed record AccessControlSnapshot(string Mode, IReadOnlyList<string> Blacklist,
    IReadOnlyList<AccessDevice> Devices, long TotalAllowedRequests, long TotalBlockedRequests);

public interface IRuntimeManagementClient
{
    Task<AccessControlSnapshot> ReadAccessAsync(string host, int port, string token, CancellationToken cancellationToken = default);
    Task<AccessControlSnapshot> SaveAccessAsync(string host, int port, string token, string mode,
        IReadOnlyList<string> blacklist, bool clearDevices = false, CancellationToken cancellationToken = default);
    Task ClearLogsAsync(string host, int port, string token, string adminToken, CancellationToken cancellationToken = default);
}

/// <summary>设备接口属于随包 android-server.js 宿主；日志清理属于核心 worker.js。</summary>
public sealed class RuntimeManagementClient : IRuntimeManagementClient
{
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public RuntimeManagementClient(HttpClient? httpClient = null, TimeSpan? requestTimeout = null)
    {
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(3),
        });
        _timeout = requestTimeout ?? TimeSpan.FromSeconds(10);
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
    }

    public async Task<AccessControlSnapshot> ReadAccessAsync(string host, int port, string token, CancellationToken cancellationToken = default)
    {
        using var json = await SendAsync(host, port, token, null, "/__access-control", HttpMethod.Get, null, cancellationToken).ConfigureAwait(false);
        return ParseAccess(json.RootElement);
    }

    public async Task<AccessControlSnapshot> SaveAccessAsync(string host, int port, string token, string mode,
        IReadOnlyList<string> blacklist, bool clearDevices = false, CancellationToken cancellationToken = default)
    {
        if (mode is not ("off" or "blacklist")) throw new ArgumentException("访问模式仅支持 off / blacklist");
        ArgumentNullException.ThrowIfNull(blacklist);
        var ips = blacklist.Select(ValidateIp).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var body = JsonSerializer.Serialize(new { mode, blacklist = ips, clearDevices });
        using var json = await SendAsync(host, port, token, null, "/__access-control", HttpMethod.Post, body, cancellationToken).ConfigureAwait(false);
        var snapshot = ParseAccess(json.RootElement);
        if (snapshot.Mode != mode || !snapshot.Blacklist.Order(StringComparer.Ordinal).SequenceEqual(ips) ||
            (clearDevices && snapshot.Devices.Count != 0))
            throw new FormatException("设备管理返回结果与提交不一致；服务端可能已部分修改，请刷新核对");
        return snapshot;
    }

    public async Task ClearLogsAsync(string host, int port, string token, string adminToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adminToken);
        using var json = await SendAsync(host, port, token, adminToken,
            "/" + Uri.EscapeDataString(adminToken.Trim()) + "/api/logs/clear", HttpMethod.Post, "{}", cancellationToken).ConfigureAwait(false);
        if (Text(json.RootElement, "message") != "Logs cleared")
            throw new FormatException("日志清理响应缺少已确认的 Logs cleared 消息；结果未确认");
    }

    private async Task<JsonDocument> SendAsync(string host, int port, string token, string? adminToken,
        string path, HttpMethod method, string? body, CancellationToken cancellationToken)
    {
        RuntimeValidation.ValidateHost(host);
        RuntimeValidation.ValidatePort(port);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        // 管理客户端仅供本机控制台使用，避免凭据被误发送到外部主机。
        if (!IPAddress.TryParse(host, out var ip) || !IPAddress.IsLoopback(ip))
            throw new ArgumentException("管理接口仅允许本机回环地址");
        var uri = new UriBuilder("http", host, port) { Path = path }.Uri;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var detail = response.StatusCode == HttpStatusCode.NotFound && path == "/__access-control"
                    ? "；当前宿主未提供设备控制接口，请更新宿主并重启服务" : "";
                throw new HttpRequestException($"管理接口返回 HTTP {(int)response.StatusCode}{detail}", null, response.StatusCode);
            }
            const int limit = 1024 * 1024;
            if (response.Content.Headers.ContentLength > limit) throw new IOException("管理接口响应超过 1MB");
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, cts.Token).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + read > limit) throw new IOException("管理接口响应超过 1MB");
                buffer.Write(chunk, 0, read);
            }
            var json = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 24 });
            try
            {
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new FormatException("管理接口响应必须是 JSON 对象");
                RejectDuplicates(root);
                if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
                    throw new FormatException("管理接口未返回 success: true，操作失败或结果未确认");
                return json;
            }
            catch { json.Dispose(); throw; }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("管理接口请求超时；写操作可能已执行，请刷新核对");
        }
        catch (HttpRequestException error)
        {
            throw new HttpRequestException(Redact(error.Message, token, adminToken), null, error.StatusCode);
        }
        catch (JsonException)
        {
            throw new FormatException("管理接口响应不是有效 JSON；操作结果未确认");
        }
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new FormatException("管理接口响应包含重复字段");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }

    private static AccessControlSnapshot ParseAccess(JsonElement root)
    {
        var config = Field(root, "config", JsonValueKind.Object);
        var mode = Text(config, "mode");
        if (mode is not ("off" or "blacklist")) throw new FormatException("设备接口返回未知访问模式");
        var blacklist = Field(config, "blacklist", JsonValueKind.Array).EnumerateArray().Select(x =>
            x.ValueKind == JsonValueKind.String ? ValidateIp(x.GetString()!) : throw new FormatException("黑名单必须为 IP 字符串数组")).ToArray();
        var devices = Field(root, "devices", JsonValueKind.Array).EnumerateArray().Select(x => new AccessDevice(
            ValidateIp(Text(x, "ip")), Number(x, "totalRequests"), Number(x, "allowedRequests"), Number(x, "blockedRequests"),
            Number(x, "lastSeenAtMs"), Boolean(x, "inBlacklist"), Boolean(x, "effectiveBlocked"))).ToArray();
        var stats = Field(root, "stats", JsonValueKind.Object);
        return new(mode, blacklist, devices, Number(stats, "totalAllowedRequests"), Number(stats, "totalBlockedRequests"));
    }

    private static JsonElement Field(JsonElement root, string name, JsonValueKind kind)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value) || value.ValueKind != kind)
            throw new FormatException($"管理接口响应字段 {name} 缺失或类型错误");
        return value;
    }
    private static string Text(JsonElement root, string name) => Field(root, name, JsonValueKind.String).GetString()!;
    private static long Number(JsonElement root, string name)
    {
        if (!Field(root, name, JsonValueKind.Number).TryGetInt64(out var value) || value < 0)
            throw new FormatException($"管理接口响应字段 {name} 必须是非负整数");
        return value;
    }
    private static bool Boolean(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new FormatException($"管理接口响应字段 {name} 必须是布尔值");
        return value.GetBoolean();
    }
    public static string ValidateIp(string value)
    {
        // 排除 .NET 接受的简写 IPv4、整数、zone id，避免服务端静默丢弃规则。
        var text = value.Trim();
        if (text.Contains('%') || text.Contains('/') || !IPAddress.TryParse(text, out var ip) ||
            (!text.Contains(':') && (text.Split('.').Length != 4 || text.Split('.').Any(x => x.Length == 0 || x.Any(c => c is < '0' or > '9')))))
            throw new FormatException("黑名单包含无效 IP；仅支持完整 IPv4 / IPv6，不支持 CIDR 或正则");
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString();
    }
    public static string Redact(string message, params string?[] secrets)
    {
        foreach (var secret in secrets.Where(x => !string.IsNullOrWhiteSpace(x)).OrderByDescending(x => x!.Length))
        {
            message = message.Replace(secret!, "***", StringComparison.Ordinal);
            message = message.Replace(Uri.EscapeDataString(secret!), "***", StringComparison.OrdinalIgnoreCase);
        }
        return message.Replace('\r', ' ').Replace('\n', ' ');
    }
}
