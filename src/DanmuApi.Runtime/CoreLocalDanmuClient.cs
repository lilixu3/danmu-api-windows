using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DanmuApi.Runtime;

/// <summary>本地弹幕操作的失败分类。分类用于界面给出可操作的下一步，而不是只显示一句话。</summary>
public enum LocalDanmuFailureKind
{
    None,
    /// <summary>401：路径里的 TOKEN 不被核心接受。</summary>
    Authentication,
    /// <summary>403：上传/删除需要 ADMIN_TOKEN，或需要开启 LOCAL_DANMU_NOT_REQUIRE_ADMIN。</summary>
    AdminRequired,
    /// <summary>404：单条资源不存在。</summary>
    NotFound,
    /// <summary>404 且发生在列表接口：核心版本过旧，没有本地弹幕路由。</summary>
    CoreUnsupported,
    /// <summary>413：单文件超过核心的 10 MB 上限。</summary>
    FileTooLarge,
    /// <summary>400：核心的字段校验失败（Diagnostic 是核心返回的原文）。</summary>
    Validation,
    Timeout,
    Cancelled,
    Connection,
    Http,
    /// <summary>响应不是预期的 JSON 形状，或核心返回了空响应（可能正在重启）。</summary>
    Protocol,
    ResponseTooLarge,
}

/// <summary>
/// 核心本地弹幕资源的元数据（<c>comments</c> 不入此模型：它是弹幕正文，只有预览时才需要，
/// 走既有的 comment 接口取）。字段名与核心 <c>apis/local-danmu-api.js</c> 的落盘结构一一对应。
/// </summary>
public sealed record CoreLocalDanmuResource(
    string ResourceKey,
    string? VideoId,
    string Title,
    int? Year,
    string Type,
    int Season,
    int? Episode,
    string Filename,
    long Size,
    string Format,
    string Status,
    int Count,
    string? UpdatedAt);

public sealed record CoreLocalDanmuListResult(
    bool Succeeded,
    IReadOnlyList<CoreLocalDanmuResource> Resources,
    string Diagnostic,
    LocalDanmuFailureKind FailureKind = LocalDanmuFailureKind.None)
{
    public static CoreLocalDanmuListResult Success(IReadOnlyList<CoreLocalDanmuResource> resources) =>
        new(true, resources, $"已读取 {resources.Count} 个本地弹幕资源");

    public static CoreLocalDanmuListResult Failure(string diagnostic, LocalDanmuFailureKind kind) =>
        new(false, [], diagnostic, kind);
}

public sealed record CoreLocalDanmuResourceResult(
    bool Succeeded,
    CoreLocalDanmuResource? Resource,
    string Diagnostic,
    LocalDanmuFailureKind FailureKind = LocalDanmuFailureKind.None)
{
    public static CoreLocalDanmuResourceResult Success(CoreLocalDanmuResource resource) =>
        new(true, resource, $"已读取「{resource.Title}」");

    public static CoreLocalDanmuResourceResult Failure(string diagnostic, LocalDanmuFailureKind kind) =>
        new(false, null, diagnostic, kind);
}

public sealed record CoreLocalDanmuOperationResult(
    bool Succeeded,
    string Diagnostic,
    LocalDanmuFailureKind FailureKind = LocalDanmuFailureKind.None)
{
    public static CoreLocalDanmuOperationResult Success(string diagnostic) => new(true, diagnostic);

    public static CoreLocalDanmuOperationResult Failure(string diagnostic, LocalDanmuFailureKind kind) =>
        new(false, diagnostic, kind);
}

/// <summary>上传用的表单内容。字段名与核心 <c>req.formData()</c> 读取的键一致，文件名必须真实
/// （核心按文件名后缀/内容嗅探判定弹幕格式）。
/// 注意：<see cref="Content"/> 由调用方创建，发送完成后会被释放（<c>HttpContent</c> 的标准行为），
/// 调用方不要在调用后再复用它。</summary>
public sealed record CoreLocalDanmuUploadRequest(
    string Title,
    int Year,
    string Type,
    int Season,
    int? Episode,
    string FileName,
    Stream Content,
    long ContentLength);

public interface ICoreLocalDanmuClient
{
    Task<CoreLocalDanmuListResult> ListAsync(
        string host,
        int port,
        string? token,
        CancellationToken cancellationToken = default);

    Task<CoreLocalDanmuResourceResult> GetAsync(
        string host,
        int port,
        string? token,
        string resourceKey,
        CancellationToken cancellationToken = default);

    Task<CoreLocalDanmuResourceResult> UploadAsync(
        string host,
        int port,
        string? token,
        string? adminToken,
        CoreLocalDanmuUploadRequest request,
        IProgress<long>? uploadProgress = null,
        CancellationToken cancellationToken = default);

    Task<CoreLocalDanmuOperationResult> DeleteAsync(
        string host,
        int port,
        string? token,
        string? adminToken,
        string resourceKey,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 核心本地弹幕接口客户端（<c>/{TOKEN}/api/v2/local-danmu/...</c>）。
///
/// 契约要点（全部来自核心 1.21.0 的实现，改动前请复核）：
/// - 鉴权沿用「token 作路径第一段」的既有约定，没有 Authorization 头分支；
///   上传/删除要求 ADMIN_TOKEN，除非核心侧 LOCAL_DANMU_NOT_REQUIRE_ADMIN=true。
/// - 单文件上限 10 MB（超限核心返回 413），单文件最多解析 200000 行，不支持压缩包。
/// - 列表接口无分页，一次返回全部元数据。
/// - DELETE 幂等：核心对不存在的资源也返回 200，因此 DELETE 收到 404 只可能是路由不存在
///   （核心版本过旧），不能当成功处理。
/// </summary>
public sealed class CoreLocalDanmuClient : ICoreLocalDanmuClient
{
    /// <summary>列表/详情这类元数据响应上限（只有元数据，不含弹幕正文）。</summary>
    private const int MaximumMetadataBytes = 16 * 1024 * 1024;

    private const string RoutePrefix = "api/v2/local-danmu";

    /// <summary>核心重启窗口里会返回 200 + 空响应，这种情况要给出「可能正在重启」而不是「格式无效」。</summary>
    private const string EmptyResponse = "核心返回了空响应，可能正在重启，请稍后重试";

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _uploadTimeout;

    public CoreLocalDanmuClient(
        HttpClient? httpClient = null,
        TimeSpan? requestTimeout = null,
        TimeSpan? uploadTimeout = null)
    {
        _httpClient = httpClient ?? CreateDefaultClient();
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(25);
        _uploadTimeout = uploadTimeout ?? TimeSpan.FromMinutes(10);
        if (_requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), "本地弹幕请求超时必须大于零");
        }

        if (_uploadTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(uploadTimeout), "本地弹幕上传超时必须大于零");
        }
    }

    public async Task<CoreLocalDanmuListResult> ListAsync(
        string host,
        int port,
        string? token,
        CancellationToken cancellationToken = default)
    {
        var effectiveToken = EffectiveToken(token);
        var endpoint = BuildListUri(host, port, effectiveToken);
        var (status, body, failure) = await SendAsync(
            HttpMethod.Get,
            endpoint,
            content: null,
            effectiveToken,
            adminToken: null,
            _requestTimeout,
            MaximumMetadataBytes,
            cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return CoreLocalDanmuListResult.Failure(failure.Value.Diagnostic, failure.Value.Kind);
        }

        if (status != HttpStatusCode.OK)
        {
            var mapped = MapFailure(status, TryReadErrorMessage(body), isListRoute: true);
            return CoreLocalDanmuListResult.Failure(mapped.Diagnostic, mapped.Kind);
        }

        if (body.Length == 0)
        {
            return CoreLocalDanmuListResult.Failure(EmptyResponse, LocalDanmuFailureKind.Protocol);
        }

        try
        {
            return CoreLocalDanmuListResult.Success(ParseListResponse(body));
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            return CoreLocalDanmuListResult.Failure(
                $"核心本地弹幕列表响应格式无效：{Describe(error.Message, effectiveToken, null)}",
                LocalDanmuFailureKind.Protocol);
        }
    }

    public async Task<CoreLocalDanmuResourceResult> GetAsync(
        string host,
        int port,
        string? token,
        string resourceKey,
        CancellationToken cancellationToken = default)
    {
        var effectiveToken = EffectiveToken(token);
        var endpoint = BuildResourceUri(host, port, effectiveToken, resourceKey);
        var (status, body, failure) = await SendAsync(
            HttpMethod.Get,
            endpoint,
            content: null,
            effectiveToken,
            adminToken: null,
            _requestTimeout,
            MaximumMetadataBytes,
            cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return CoreLocalDanmuResourceResult.Failure(failure.Value.Diagnostic, failure.Value.Kind);
        }

        if (status != HttpStatusCode.OK)
        {
            var mapped = MapFailure(status, TryReadErrorMessage(body), isListRoute: false);
            return CoreLocalDanmuResourceResult.Failure(mapped.Diagnostic, mapped.Kind);
        }

        if (body.Length == 0)
        {
            return CoreLocalDanmuResourceResult.Failure(EmptyResponse, LocalDanmuFailureKind.Protocol);
        }

        try
        {
            return CoreLocalDanmuResourceResult.Success(ParseResourceResponse(body));
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            return CoreLocalDanmuResourceResult.Failure(
                $"核心本地弹幕详情响应格式无效：{Describe(error.Message, effectiveToken, null)}",
                LocalDanmuFailureKind.Protocol);
        }
    }

    public async Task<CoreLocalDanmuResourceResult> UploadAsync(
        string host,
        int port,
        string? token,
        string? adminToken,
        CoreLocalDanmuUploadRequest request,
        IProgress<long>? uploadProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var effectiveToken = EffectiveToken(token);
        var writeToken = EffectiveWriteToken(effectiveToken, adminToken);
        var endpoint = BuildUploadUri(host, port, writeToken);

        // multipart：file 字段必须带真实文件名，核心按文件名后缀判定弹幕格式。
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(new UploadProgressStream(request.Content, uploadProgress));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", SanitizeFileName(request.FileName));
        content.Add(new StringContent(request.Title.Trim(), Encoding.UTF8), "title");
        content.Add(new StringContent(request.Year.ToString(CultureInfo.InvariantCulture), Encoding.UTF8), "year");
        content.Add(new StringContent(request.Type.Trim(), Encoding.UTF8), "type");
        if (request.Season > 0)
        {
            content.Add(new StringContent(request.Season.ToString(CultureInfo.InvariantCulture), Encoding.UTF8), "season");
        }

        if (request.Episode is int episode)
        {
            content.Add(new StringContent(episode.ToString(CultureInfo.InvariantCulture), Encoding.UTF8), "episode");
        }

        var (status, body, failure) = await SendAsync(
            HttpMethod.Post,
            endpoint,
            content,
            writeToken,
            adminToken,
            _uploadTimeout,
            MaximumMetadataBytes,
            cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return CoreLocalDanmuResourceResult.Failure(failure.Value.Diagnostic, failure.Value.Kind);
        }

        if (status != HttpStatusCode.OK)
        {
            var mapped = MapFailure(status, TryReadErrorMessage(body), isListRoute: false);
            return CoreLocalDanmuResourceResult.Failure(mapped.Diagnostic, mapped.Kind);
        }

        if (body.Length == 0)
        {
            return CoreLocalDanmuResourceResult.Failure(EmptyResponse, LocalDanmuFailureKind.Protocol);
        }

        try
        {
            return CoreLocalDanmuResourceResult.Success(ParseResourceResponse(body));
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            return CoreLocalDanmuResourceResult.Failure(
                $"核心上传响应格式无效：{Describe(error.Message, writeToken, adminToken)}",
                LocalDanmuFailureKind.Protocol);
        }
    }

    public async Task<CoreLocalDanmuOperationResult> DeleteAsync(
        string host,
        int port,
        string? token,
        string? adminToken,
        string resourceKey,
        CancellationToken cancellationToken = default)
    {
        var effectiveToken = EffectiveToken(token);
        var writeToken = EffectiveWriteToken(effectiveToken, adminToken);
        var endpoint = BuildResourceUri(host, port, writeToken, resourceKey);
        var (status, body, failure) = await SendAsync(
            HttpMethod.Delete,
            endpoint,
            content: null,
            writeToken,
            adminToken,
            _requestTimeout,
            MaximumMetadataBytes,
            cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return CoreLocalDanmuOperationResult.Failure(failure.Value.Diagnostic, failure.Value.Kind);
        }

        if (status == HttpStatusCode.OK)
        {
            return CoreLocalDanmuOperationResult.Success("已删除本地弹幕文件");
        }

        // 核心的删除是幂等的（资源不存在也返回 200），所以 404 只可能是路由不存在。
        var mapped = MapFailure(status, TryReadErrorMessage(body), isListRoute: false);
        return CoreLocalDanmuOperationResult.Failure(mapped.Diagnostic, mapped.Kind);
    }

    public static Uri BuildListUri(string host, int port, string token) =>
        BuildApiUri(host, port, token, "list");

    public static Uri BuildUploadUri(string host, int port, string token) =>
        BuildApiUri(host, port, token, "upload");

    public static Uri BuildResourceUri(string host, int port, string token, string resourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);
        return BuildApiUri(host, port, token, Uri.EscapeDataString(resourceKey.Trim()));
    }

    /// <summary>列表响应：<c>{success:true, resources:[...]}</c>。分组由界面侧按同一规则重建。</summary>
    public static IReadOnlyList<CoreLocalDanmuResource> ParseListResponse(ReadOnlySpan<byte> body)
    {
        using var document = JsonDocument.Parse(body.ToArray());
        var root = RequireObject(document.RootElement);
        EnsureSuccess(root, "本地弹幕列表");
        var resources = RequireProperty(root, "resources");
        if (resources.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("字段 resources 必须是数组");
        }

        var items = new List<CoreLocalDanmuResource>();
        foreach (var element in resources.EnumerateArray())
        {
            items.Add(ParseResource(element));
        }

        return items;
    }

    /// <summary>详情/上传响应：<c>{success:true, resource:{...}}</c>。</summary>
    public static CoreLocalDanmuResource ParseResourceResponse(ReadOnlySpan<byte> body)
    {
        using var document = JsonDocument.Parse(body.ToArray());
        var root = RequireObject(document.RootElement);
        EnsureSuccess(root, "本地弹幕资源");
        return ParseResource(RequireProperty(root, "resource"));
    }

    /// <summary>单个资源对象 → 元数据。字段类型不符即视为协议错误；仅展示用字段允许缺省。</summary>
    public static CoreLocalDanmuResource ParseResource(JsonElement element)
    {
        var root = RequireObject(element);
        var season = OptionalInt(root, "season") ?? 1;
        return new CoreLocalDanmuResource(
            RequireString(root, "resourceKey"),
            OptionalString(root, "videoId"),
            RequireString(root, "title"),
            OptionalInt(root, "year"),
            RequireString(root, "type"),
            season > 0 ? season : 1,
            OptionalInt(root, "episode"),
            OptionalString(root, "filename") ?? string.Empty,
            OptionalLong(root, "size") ?? 0,
            OptionalString(root, "format") ?? string.Empty,
            OptionalString(root, "status") ?? "ready",
            OptionalInt(root, "count") ?? 0,
            OptionalString(root, "updatedAt"));
    }

    /// <summary>核心的错误响应有两套形状：401/403 带 errorCode，校验类只有 errorMessage。</summary>
    public static string? ParseErrorMessage(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var message = OptionalString(document.RootElement, "errorMessage");
            if (!string.IsNullOrWhiteSpace(message))
            {
                return message.Trim();
            }

            var errorCode = OptionalInt(document.RootElement, "errorCode");
            return errorCode is int code ? $"核心返回错误码 {code}" : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>上传文件名只保留核心需要的信息：去掉换行/NUL、trim、截断，空则退回 danmu.txt。</summary>
    public static string SanitizeFileName(string? fileName)
    {
        var value = (fileName ?? string.Empty)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Trim();
        if (value.Length == 0)
        {
            return "danmu.txt";
        }

        return value.Length <= 200 ? value : value[..200];
    }

    /// <summary>写操作用管理员令牌（已配置并登录时），否则退回普通令牌，由核心按策略判定 403。</summary>
    public static string EffectiveWriteToken(string? token, string? adminToken) =>
        string.IsNullOrWhiteSpace(adminToken) ? EffectiveToken(token) : adminToken.Trim();

    private static Uri BuildApiUri(string host, int port, string token, string suffix)
    {
        RuntimeValidation.ValidateHost(host);
        RuntimeValidation.ValidatePort(port);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var authority = host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[')
            ? $"[{host}]"
            : host;
        return new Uri(
            $"http://{authority}:{port}/{Uri.EscapeDataString(token.Trim())}/{RoutePrefix}/{suffix}",
            UriKind.Absolute);
    }

    private static string EffectiveToken(string? token) =>
        string.IsNullOrWhiteSpace(token) ? RuntimeDefaults.FallbackToken : token.Trim();

    private async Task<(HttpStatusCode Status, byte[] Body, (string Diagnostic, LocalDanmuFailureKind Kind)? Failure)> SendAsync(
        HttpMethod method,
        Uri endpoint,
        HttpContent? content,
        string token,
        string? adminToken,
        TimeSpan timeout,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var request = new HttpRequestMessage(method, endpoint) { Content = content };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ((HttpStatusCode)0, [], ("本地弹幕请求已取消", LocalDanmuFailureKind.Cancelled));
        }
        catch (OperationCanceledException)
        {
            return ((HttpStatusCode)0, [], ($"本地弹幕请求超时（{timeout.TotalSeconds:0} 秒）", LocalDanmuFailureKind.Timeout));
        }
        catch (HttpRequestException error)
        {
            return ((HttpStatusCode)0, [], (
                $"无法连接本地核心服务，请确认服务已启动：{Describe(error.Message, token, adminToken)}",
                LocalDanmuFailureKind.Connection));
        }

        using (response)
        {
            byte[] body;
            try
            {
                body = await ReadBoundedAsync(response.Content, maximumBytes, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return ((HttpStatusCode)0, [], ("本地弹幕响应读取已取消", LocalDanmuFailureKind.Cancelled));
            }
            catch (OperationCanceledException)
            {
                return ((HttpStatusCode)0, [], ($"本地弹幕响应读取超时（{timeout.TotalSeconds:0} 秒）", LocalDanmuFailureKind.Timeout));
            }
            catch (IOException error)
            {
                var kind = error.Message.Contains("超过", StringComparison.Ordinal)
                    ? LocalDanmuFailureKind.ResponseTooLarge
                    : LocalDanmuFailureKind.Connection;
                return ((HttpStatusCode)0, [], ($"本地弹幕响应读取失败：{Describe(error.Message, token, adminToken)}", kind));
            }

            return (response.StatusCode, body, null);
        }
    }

    /// <summary>按状态码给出可操作的失败分类；Diagnostic 优先使用核心返回的原文。</summary>
    private static (string Diagnostic, LocalDanmuFailureKind Kind) MapFailure(
        HttpStatusCode status,
        string? coreMessage,
        bool isListRoute) =>
        status switch
        {
            HttpStatusCode.Unauthorized => (
                coreMessage is null
                    ? "令牌校验失败，请检查 TOKEN/ADMIN_TOKEN 配置"
                    : $"令牌校验失败：{coreMessage}",
                LocalDanmuFailureKind.Authentication),
            HttpStatusCode.Forbidden => (
                coreMessage is null
                    ? "需要管理员权限：请开启管理员模式，或将 LOCAL_DANMU_NOT_REQUIRE_ADMIN 设为 true"
                    : $"需要管理员权限：{coreMessage}",
                LocalDanmuFailureKind.AdminRequired),
            HttpStatusCode.NotFound when isListRoute => (
                "当前核心版本不支持本地弹幕，请先到「核心」页更新核心",
                LocalDanmuFailureKind.CoreUnsupported),
            HttpStatusCode.NotFound => (
                coreMessage is null
                    ? "资源不存在，列表可能已变化；也可能是核心版本过旧不支持该接口"
                    : $"资源不存在：{coreMessage}",
                LocalDanmuFailureKind.NotFound),
            HttpStatusCode.RequestEntityTooLarge => (
                coreMessage is null ? "单文件不能超过核心 10 MB 上限" : $"单文件不能超过核心 10 MB 上限：{coreMessage}",
                LocalDanmuFailureKind.FileTooLarge),
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => (
                coreMessage is null ? "请求参数或弹幕文件无效" : $"请求参数或弹幕文件无效：{coreMessage}",
                LocalDanmuFailureKind.Validation),
            _ => (
                coreMessage is null
                    ? $"核心本地弹幕接口返回 HTTP {(int)status}"
                    : $"核心本地弹幕接口返回 HTTP {(int)status}：{coreMessage}",
                LocalDanmuFailureKind.Http),
        };

    private static string? TryReadErrorMessage(byte[] body) => ParseErrorMessage(body);

    private static void EnsureSuccess(JsonElement root, string what)
    {
        if (root.TryGetProperty("success", out var success) &&
            success.ValueKind is JsonValueKind.False)
        {
            var message = OptionalString(root, "errorMessage");
            throw new InvalidDataException(message is null ? $"{what}失败" : $"{what}失败：{message}");
        }
    }

    private static JsonElement RequireObject(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
            ? element
            : throw new InvalidDataException("响应的 JSON 根节点必须是对象");

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

    private static int? OptionalInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            throw new InvalidDataException($"字段 {name} 必须是整数或 null");
        }

        return number;
    }

    private static long? OptionalLong(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
        {
            throw new InvalidDataException($"字段 {name} 必须是整数或 null");
        }

        return number;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 and var declared && declared > maximumBytes)
        {
            throw new IOException($"本地弹幕响应超过 {maximumBytes / 1024 / 1024} MB");
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

            if (buffer.Length + read > maximumBytes)
            {
                throw new IOException($"本地弹幕响应超过 {maximumBytes / 1024 / 1024} MB");
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

    private static string Describe(string message, string? token, string? adminToken)
    {
        var normalized = string.IsNullOrWhiteSpace(message)
            ? "未提供失败详情"
            : message.Replace('\r', ' ').Replace('\n', ' ');
        return RuntimeManagementClient.Redact(normalized, token, adminToken);
    }

    /// <summary>上传进度按「已读字节」上报（StreamContent 会边读边发）。</summary>
    private sealed class UploadProgressStream : Stream
    {
        private readonly Stream _inner;
        private readonly IProgress<long>? _progress;
        private long _sent;

        public UploadProgressStream(Stream inner, IProgress<long>? progress)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _progress = progress;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.CanSeek ? _inner.Length : throw new NotSupportedException();
        public override long Position
        {
            get => _inner.CanSeek ? _inner.Position : _sent;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            Report(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Report(read);
            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Report(int read)
        {
            if (read <= 0 || _progress is null)
            {
                return;
            }

            _sent += read;
            _progress.Report(_sent);
        }
    }
}
