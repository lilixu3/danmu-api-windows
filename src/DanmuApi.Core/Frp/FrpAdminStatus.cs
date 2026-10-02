using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace DanmuApi.Core.Frp;

/// <summary>frp 管理接口报告的单条代理状态（字段集取自实测 frp 0.71.0 的 <c>/api/status</c> 响应）。</summary>
public sealed record FrpProxyStatus(
    string Name,
    string Type,
    string Status,
    string Error,
    string LocalAddress,
    string RemoteAddress)
{
    public const string RunningStatus = "running";
    public const string StartErrorStatus = "start error";

    /// <summary>把 frp 的状态字符串映射成本应用概念；未知取值不猜，一律落到 <see cref="FrpProxyState.Unknown"/>。</summary>
    public FrpProxyState State => Status switch
    {
        RunningStatus => FrpProxyState.Running,
        StartErrorStatus => FrpProxyState.StartError,
        "new" or "waiting" => FrpProxyState.Pending,
        "closed" => FrpProxyState.Closed,
        _ => FrpProxyState.Unknown,
    };

    public bool IsRunning => State == FrpProxyState.Running;

    /// <summary>给用户看的一行说明：running 显示远端地址，出错显示 frp 给出的原因，其余原样显示状态词。</summary>
    public string Describe() => State switch
    {
        FrpProxyState.Running => string.IsNullOrWhiteSpace(RemoteAddress) ? "已连接" : $"已连接 · {RemoteAddress}",
        FrpProxyState.StartError => $"启动失败：{Error}",
        FrpProxyState.Pending => "等待服务器分配",
        FrpProxyState.Closed => "已关闭",
        _ => $"未知状态：{Status}",
    };
}

public enum FrpProxyState
{
    Running,
    StartError,
    Pending,
    Closed,
    Unknown,
}

/// <summary>frps 管理接口 <c>/api/serverinfo</c> 的关键字段。</summary>
public sealed record FrpServerInfo(
    string Version,
    int BindPort,
    int VhostHttpPort,
    int VhostHttpsPort,
    int ClientCounts);

/// <summary>
/// frp 管理接口响应的严格解析。
///
/// 严格到什么程度：字段缺失、类型不对、外层不是对象，一律抛异常并把原始响应带进消息，
/// 绝不"读不到就当空"。理由是这里的每个字段都会被当成"穿透到底通没通"的证据：
/// 把解析失败降级成空列表，界面就会显示成"没有代理"，与"隧道是通的"无法区分。
///
/// 允许未知的代理类型键（如未来的 stcp/sudp）：实测响应里只有已配置的代理类型才会出现，
/// 因此拒绝未知类型键会在 frp 新增类型时误伤；但每个条目仍必须齐备七个字符串字段。
/// </summary>
public static class FrpAdminStatusReader
{
    public const int MaxResponseBytes = 512 * 1024;

    private static readonly string[] RequiredProxyFields =
        ["name", "type", "status", "err", "local_addr", "plugin", "remote_addr"];

    /// <summary>解析 frpc <c>/api/status</c>。空对象 <c>{}</c> 是合法响应，表示当前没有任何代理在跑。</summary>
    public static IReadOnlyList<FrpProxyStatus> ParseClientStatus(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException error)
        {
            throw new IOException($"frp 状态响应不是合法 JSON：{error.Message}；原始响应：{Trim(json)}", error);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new IOException($"frp 状态响应不是 JSON 对象：{Trim(json)}");
            }

            var proxies = new List<FrpProxyStatus>();
            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Array)
                {
                    throw new IOException(
                        $"frp 状态响应的 {property.Name} 不是数组：{Trim(property.Value.GetRawText())}");
                }

                foreach (var item in property.Value.EnumerateArray())
                {
                    proxies.Add(ParseProxy(item, property.Name, json));
                }
            }

            return proxies;
        }
    }

    public static FrpServerInfo ParseServerInfo(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException error)
        {
            throw new IOException($"frps 状态响应不是合法 JSON：{error.Message}；原始响应：{Trim(json)}", error);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new IOException($"frps 状态响应不是 JSON 对象：{Trim(json)}");
            }

            return new FrpServerInfo(
                RequireString(root, "version", json),
                RequireInt(root, "bindPort", json),
                RequireInt(root, "vhostHTTPPort", json),
                RequireInt(root, "vhostHTTPSPort", json),
                RequireInt(root, "clientCounts", json));
        }
    }

    private static FrpProxyStatus ParseProxy(JsonElement item, string typeKey, string payload)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new IOException($"frp 状态响应里 {typeKey} 的元素不是对象：{Trim(item.GetRawText())}");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in RequiredProxyFields)
        {
            if (!item.TryGetProperty(field, out var value))
            {
                throw new IOException($"frp 状态响应缺少字段 {field}（{typeKey}）：{Trim(payload)}");
            }

            // plugin 在实测响应里是空串而非 null；null 会直接判为格式异常，而不是当成空串。
            if (value.ValueKind != JsonValueKind.String)
            {
                throw new IOException(
                    $"frp 状态响应的字段 {field} 不是字符串（{typeKey}）：{Trim(value.GetRawText())}");
            }

            values[field] = value.GetString() ?? string.Empty;
        }

        if (values["name"].Length == 0)
        {
            throw new IOException($"frp 状态响应里有代理名为空：{Trim(payload)}");
        }

        if (values["status"].Length == 0)
        {
            throw new IOException($"frp 状态响应里代理 {values["name"]} 的 status 为空：{Trim(payload)}");
        }

        return new FrpProxyStatus(
            values["name"],
            values["type"],
            values["status"],
            values["err"],
            values["local_addr"],
            values["remote_addr"]);
    }

    private static string RequireString(JsonElement root, string name, string payload)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new IOException($"frps 状态响应缺少字符串字段 {name}：{Trim(payload)}");
        }

        return value.GetString() ?? string.Empty;
    }

    private static int RequireInt(JsonElement root, string name, string payload)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var number))
        {
            throw new IOException($"frps 状态响应缺少整数字段 {name}：{Trim(payload)}");
        }

        return number;
    }

    private static string Trim(string value) => value.Length <= 600 ? value : value[..600] + "…";
}

/// <summary>
/// frp 管理接口客户端。只连本机回环：管理接口由生成的配置固定为 <c>webServer.addr = 127.0.0.1</c>。
/// </summary>
public interface IFrpAdminClient
{
    Task<IReadOnlyList<FrpProxyStatus>> ReadClientStatusAsync(
        int port,
        string user,
        string password,
        CancellationToken cancellationToken = default);

    Task<FrpServerInfo> ReadServerInfoAsync(
        int port,
        string user,
        string password,
        CancellationToken cancellationToken = default);
}

public sealed class FrpAdminClient : IFrpAdminClient
{
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;

    public FrpAdminClient(HttpClient httpClient, TimeSpan? timeout = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
        if (_timeout <= TimeSpan.Zero || _timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    public Task<IReadOnlyList<FrpProxyStatus>> ReadClientStatusAsync(
        int port,
        string user,
        string password,
        CancellationToken cancellationToken = default) =>
        ReadAsync(port, user, password, "/api/status", FrpAdminStatusReader.ParseClientStatus, cancellationToken);

    public Task<FrpServerInfo> ReadServerInfoAsync(
        int port,
        string user,
        string password,
        CancellationToken cancellationToken = default) =>
        ReadAsync(port, user, password, "/api/serverinfo", FrpAdminStatusReader.ParseServerInfo, cancellationToken);

    private async Task<T> ReadAsync<T>(
        int port,
        string user,
        string password,
        string path,
        Func<string, T> parse,
        CancellationToken cancellationToken)
    {
        if (port is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "管理接口端口必须在 1 到 65535 之间");
        }

        var uri = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}{path}", UriKind.Absolute);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("DanmuApiWindows/1.0");
        if (user.Length > 0 || password.Length > 0)
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"frp 管理接口（127.0.0.1:{port}）超过 {_timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} 秒未响应", error);
        }
        catch (HttpRequestException error)
        {
            throw new IOException($"frp 管理接口（127.0.0.1:{port}）不可达：{error.Message}", error);
        }

        using (response)
        {
            var body = await ReadBodyAsync(response, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new IOException($"frp 管理接口（127.0.0.1:{port}）拒绝了凭据，本地管理密码可能已失效，请重新保存配置后重启穿透");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new IOException($"frp 管理接口 {path} 返回 HTTP {(int)response.StatusCode}：{Trim(body)}");
            }

            return parse(body);
        }
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[32 * 1024];
        using var memory = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (memory.Length + read > FrpAdminStatusReader.MaxResponseBytes)
            {
                throw new IOException(
                    $"frp 管理接口响应超过 {FrpAdminStatusReader.MaxResponseBytes.ToString(CultureInfo.InvariantCulture)} 字节上限");
            }

            memory.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static string Trim(string value) => value.Length <= 600 ? value : value[..600] + "…";
}
