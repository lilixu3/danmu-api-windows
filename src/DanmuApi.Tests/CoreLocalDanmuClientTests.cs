using System.Net;
using System.Text;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

/// <summary>
/// 本地弹幕客户端契约：路径段鉴权、resourceKey 编码、multipart 字段名、错误分类。
/// 契约来自核心 1.21.0 的 <c>apis/local-danmu-api.js</c> 与 <c>worker.js</c>，改动前先复核核心。
/// </summary>
public sealed class CoreLocalDanmuClientTests
{
    private const string Token = "abcdefgh";
    private const string AdminToken = "admin-token-1";

    private static readonly string ListBody = """
        {"success":true,"resources":[
          {"resourceKey":"逐玉|2026|tv|5","videoId":"v-1","title":"逐玉","year":2026,"type":"tv","season":1,
           "episode":5,"filename":"逐玉_E05_第5集_腾讯.xml","size":2048,"format":"XML","status":"ready","count":42,
           "matchKeys":["逐玉"],"updatedAt":"2026-09-12T10:00:00.000Z"}
        ],"groups":[]}
        """;

    [Fact]
    public void BuildUrisUseTokenAsFirstPathSegment()
    {
        var list = CoreLocalDanmuClient.BuildListUri("127.0.0.1", 9321, Token);
        var upload = CoreLocalDanmuClient.BuildUploadUri("127.0.0.1", 9321, Token);

        Assert.Equal($"http://127.0.0.1:9321/{Token}/api/v2/local-danmu/list", list.AbsoluteUri);
        Assert.Equal($"http://127.0.0.1:9321/{Token}/api/v2/local-danmu/upload", upload.AbsoluteUri);
    }

    [Fact]
    public void ResourceUriPercentEncodesChineseAndPipe()
    {
        var uri = CoreLocalDanmuClient.BuildResourceUri("127.0.0.1", 9321, Token, "逐玉|2026|tv|5");

        Assert.Contains("%E9%80%90%E7%8E%89", uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("%7C", uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain("逐玉", uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteTokenPrefersAdminTokenAndFallsBackToToken()
    {
        Assert.Equal(AdminToken, CoreLocalDanmuClient.EffectiveWriteToken(Token, AdminToken));
        Assert.Equal(Token, CoreLocalDanmuClient.EffectiveWriteToken(Token, null));
        Assert.Equal(Token, CoreLocalDanmuClient.EffectiveWriteToken(Token, "   "));
    }

    [Fact]
    public void SanitizeFileNameStripsControlCharactersAndFallsBack()
    {
        Assert.Equal("逐玉_E05.xml", CoreLocalDanmuClient.SanitizeFileName(" 逐玉_E05.xml\r\n"));
        Assert.Equal("a\0b.xml".Replace("\0", string.Empty), CoreLocalDanmuClient.SanitizeFileName("a\0b.xml"));
        Assert.Equal("danmu.txt", CoreLocalDanmuClient.SanitizeFileName(null));
        Assert.Equal("danmu.txt", CoreLocalDanmuClient.SanitizeFileName("   "));
        Assert.Equal(200, CoreLocalDanmuClient.SanitizeFileName(new string('x', 500)).Length);
    }

    [Fact]
    public void ParseListResponseMapsEveryCoreField()
    {
        var resources = CoreLocalDanmuClient.ParseListResponse(Encoding.UTF8.GetBytes(ListBody));

        var resource = Assert.Single(resources);
        Assert.Equal("逐玉|2026|tv|5", resource.ResourceKey);
        Assert.Equal("v-1", resource.VideoId);
        Assert.Equal("逐玉", resource.Title);
        Assert.Equal(2026, resource.Year);
        Assert.Equal("tv", resource.Type);
        Assert.Equal(1, resource.Season);
        Assert.Equal(5, resource.Episode);
        Assert.Equal("逐玉_E05_第5集_腾讯.xml", resource.Filename);
        Assert.Equal(2048, resource.Size);
        Assert.Equal("XML", resource.Format);
        Assert.Equal("ready", resource.Status);
        Assert.Equal(42, resource.Count);
        Assert.Equal("2026-09-12T10:00:00.000Z", resource.UpdatedAt);
    }

    [Fact]
    public void ParseListResponseRejectsMissingIdentityFields()
    {
        var body = Encoding.UTF8.GetBytes("""{"success":true,"resources":[{"title":"逐玉","type":"tv"}]}""");

        Assert.Throws<InvalidDataException>(() => CoreLocalDanmuClient.ParseListResponse(body));
    }

    [Fact]
    public void ParseListResponseRejectsWrongFieldType()
    {
        var body = Encoding.UTF8.GetBytes(
            """{"success":true,"resources":[{"resourceKey":"k","title":"t","type":"tv","count":"42"}]}""");

        Assert.Throws<InvalidDataException>(() => CoreLocalDanmuClient.ParseListResponse(body));
    }

    [Fact]
    public void ParseErrorMessageHandlesBothCoreShapes()
    {
        Assert.Equal(
            "标题为必填项",
            CoreLocalDanmuClient.ParseErrorMessage(Encoding.UTF8.GetBytes("""{"success":false,"status":"failed","errorMessage":"标题为必填项"}""")));
        Assert.Equal(
            "Local danmu upload and deletion require ADMIN_TOKEN or LOCAL_DANMU_NOT_REQUIRE_ADMIN=true",
            CoreLocalDanmuClient.ParseErrorMessage(Encoding.UTF8.GetBytes(
                """{"errorCode":403,"success":false,"errorMessage":"Local danmu upload and deletion require ADMIN_TOKEN or LOCAL_DANMU_NOT_REQUIRE_ADMIN=true"}""")));
        Assert.Equal("核心返回错误码 401", CoreLocalDanmuClient.ParseErrorMessage(Encoding.UTF8.GetBytes("""{"errorCode":401,"success":false}""")));
        Assert.Null(CoreLocalDanmuClient.ParseErrorMessage(ReadOnlySpan<byte>.Empty));
        Assert.Null(CoreLocalDanmuClient.ParseErrorMessage(Encoding.UTF8.GetBytes("not json")));
    }

    [Fact]
    public async Task ListMapsResourcesAndUsesReadToken()
    {
        var requests = new List<HttpRequestMessage>();
        var client = new CoreLocalDanmuClient(new HttpClient(new StubHandler((request, _) =>
        {
            requests.Add(Clone(request));
            return Task.FromResult(Json(ListBody));
        })));

        var result = await client.ListAsync("127.0.0.1", 9321, Token);

        Assert.True(result.Succeeded);
        Assert.Single(result.Resources);
        Assert.Contains($"/{Token}/api/v2/local-danmu/list", requests[0].RequestUri!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListOnNotFoundReportsCoreUnsupported()
    {
        var client = new CoreLocalDanmuClient(new HttpClient(
            new StubHandler((_, _) => Task.FromResult(Json("{\"errorMessage\":\"not found\"}", HttpStatusCode.NotFound)))));

        var result = await client.ListAsync("127.0.0.1", 9321, Token);

        Assert.False(result.Succeeded);
        Assert.Equal(LocalDanmuFailureKind.CoreUnsupported, result.FailureKind);
        Assert.Contains("请先到「核心」页更新核心", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForbiddenUploadReportsAdminRequiredWithCoreMessage()
    {
        var client = new CoreLocalDanmuClient(new HttpClient(new StubHandler((_, _) => Task.FromResult(Json(
            "{\"errorCode\":403,\"success\":false,\"errorMessage\":\"Local danmu upload and deletion require ADMIN_TOKEN or LOCAL_DANMU_NOT_REQUIRE_ADMIN=true\"}",
            HttpStatusCode.Forbidden)))));
        using var payload = new MemoryStream(Encoding.UTF8.GetBytes("<i></i>"));
        var request = new CoreLocalDanmuUploadRequest("逐玉", 2026, "tv", 1, 5, "逐玉_E05.xml", payload, payload.Length);

        var result = await client.UploadAsync("127.0.0.1", 9321, Token, adminToken: null, request);

        Assert.False(result.Succeeded);
        Assert.Equal(LocalDanmuFailureKind.AdminRequired, result.FailureKind);
        Assert.Contains("LOCAL_DANMU_NOT_REQUIRE_ADMIN", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TooLargeUploadReportsFileTooLarge()
    {
        var client = new CoreLocalDanmuClient(new HttpClient(new StubHandler((_, _) => Task.FromResult(Json(
            "{\"success\":false,\"errorMessage\":\"文件大小不能超过 10 MB\"}",
            HttpStatusCode.RequestEntityTooLarge)))));
        using var payload = new MemoryStream(Encoding.UTF8.GetBytes("<i></i>"));
        var request = new CoreLocalDanmuUploadRequest("逐玉", 2026, "tv", 1, 5, "逐玉_E05.xml", payload, payload.Length);

        var result = await client.UploadAsync("127.0.0.1", 9321, Token, AdminToken, request);

        Assert.Equal(LocalDanmuFailureKind.FileTooLarge, result.FailureKind);
        Assert.Contains("10 MB", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptySuccessBodyReportsRestartHint()
    {
        var client = new CoreLocalDanmuClient(new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) }))));

        var result = await client.ListAsync("127.0.0.1", 9321, Token);

        Assert.False(result.Succeeded);
        Assert.Equal(LocalDanmuFailureKind.Protocol, result.FailureKind);
        Assert.Contains("可能正在重启", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadSendsCoreFormFieldsAndRealFileName()
    {
        string? body = null;
        string? path = null;
        var client = new CoreLocalDanmuClient(new HttpClient(new StubHandler(async (request, token) =>
        {
            path = request.RequestUri!.AbsoluteUri;
            body = await request.Content!.ReadAsStringAsync(token);
            return Json("""
                {"success":true,"resource":{"resourceKey":"逐玉|2026|tv|5","title":"逐玉","year":2026,"type":"tv",
                 "season":1,"episode":5,"filename":"逐玉_E05_第5集_腾讯.xml","size":12,"format":"XML",
                 "status":"ready","count":3,"updatedAt":"2026-09-12T10:00:00.000Z"}}
                """);
        })));
        using var payload = new MemoryStream(Encoding.UTF8.GetBytes("<i><d p=\"1,1,16777215\">hi</d></i>"));
        var request = new CoreLocalDanmuUploadRequest("逐玉", 2026, "tv", 1, 5, "逐玉_E05_第5集_腾讯.xml", payload, payload.Length);

        var result = await client.UploadAsync("127.0.0.1", 9321, Token, AdminToken, request);

        Assert.True(result.Succeeded);
        Assert.Equal("逐玉|2026|tv|5", result.Resource!.ResourceKey);
        // 写操作走管理员令牌的路径段。
        Assert.Contains($"/{AdminToken}/api/v2/local-danmu/upload", path, StringComparison.Ordinal);
        Assert.NotNull(body);
        Assert.Contains("name=file", body, StringComparison.Ordinal);
        Assert.Contains("name=title", body, StringComparison.Ordinal);
        Assert.Contains("逐玉", body, StringComparison.Ordinal);
        Assert.Contains("name=year", body, StringComparison.Ordinal);
        Assert.Contains("2026", body, StringComparison.Ordinal);
        Assert.Contains("name=type", body, StringComparison.Ordinal);
        Assert.Contains("name=season", body, StringComparison.Ordinal);
        Assert.Contains("name=episode", body, StringComparison.Ordinal);
        // 文件名必须是真实的——核心按文件名后缀/内容嗅探判定弹幕格式。
        // .NET 对非 ASCII 文件名会写成 RFC 5987 的 filename*（UTF-8 百分号编码），
        // Node 的 undici（Fetch 标准解析）会按 filename* 还原，两种形式都算合格。
        var ascii = body.Contains("逐玉_E05_第5集_腾讯.xml", StringComparison.Ordinal);
        var encoded = body.Contains(Uri.EscapeDataString("逐玉_E05_第5集_腾讯.xml"), StringComparison.Ordinal);
        Assert.True(ascii || encoded, $"multipart 里既没有原始文件名也没有 filename* 编码形式：{body[..Math.Min(400, body.Length)]}");
    }

    [Fact]
    public async Task UploadOmitsEpisodeForMoviesWithoutOne()
    {
        string? body = null;
        var client = new CoreLocalDanmuClient(new HttpClient(new StubHandler(async (request, token) =>
        {
            body = await request.Content!.ReadAsStringAsync(token);
            return Json("""
                {"success":true,"resource":{"resourceKey":"movie|2024|movie|all","title":"movie","year":2024,
                 "type":"movie","season":1,"episode":null,"filename":"movie.xml","size":1,"format":"XML",
                 "status":"ready","count":1,"updatedAt":"2026-09-12T10:00:00.000Z"}}
                """);
        })));
        using var payload = new MemoryStream(Encoding.UTF8.GetBytes("<i></i>"));
        var request = new CoreLocalDanmuUploadRequest("movie", 2024, "movie", 1, null, "movie.xml", payload, payload.Length);

        await client.UploadAsync("127.0.0.1", 9321, Token, AdminToken, request);

        Assert.NotNull(body);
        Assert.DoesNotContain("name=episode", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadReportsSentBytes()
    {
        var client = new CoreLocalDanmuClient(new HttpClient(new StubHandler(async (request, token) =>
        {
            _ = await request.Content!.ReadAsByteArrayAsync(token);
            return Json("""
                {"success":true,"resource":{"resourceKey":"k|2026|tv|1","title":"k","year":2026,"type":"tv",
                 "season":1,"episode":1,"filename":"k.xml","size":4,"format":"XML","status":"ready","count":1,
                 "updatedAt":"2026-09-12T10:00:00.000Z"}}
                """);
        })));
        var bytes = Encoding.UTF8.GetBytes("<i></i>");
        using var payload = new MemoryStream(bytes);
        var expectedLength = payload.Length;
        var sent = new List<long>();
        var request = new CoreLocalDanmuUploadRequest("k", 2026, "tv", 1, 1, "k.xml", payload, payload.Length);

        var result = await client.UploadAsync(
            "127.0.0.1",
            9321,
            Token,
            AdminToken,
            request,
            new InlineProgress<long>(sent.Add));

        Assert.True(result.Succeeded);
        Assert.NotEmpty(sent);
        // 客户端发完会释放传入的流（HttpContent 的标准行为），所以长度必须在调用前取。
        Assert.Equal(expectedLength, sent[^1]);
    }

    [Fact]
    public async Task ConnectionFailureIsClassified()
    {
        var client = new CoreLocalDanmuClient(new HttpClient(
            new StubHandler((_, _) => throw new HttpRequestException($"连接被拒绝（token={Token}）"))));

        var result = await client.ListAsync("127.0.0.1", 9321, Token);

        Assert.False(result.Succeeded);
        Assert.Equal(LocalDanmuFailureKind.Connection, result.FailureKind);
        // 诊断里不能出现明文 token。
        Assert.DoesNotContain(Token, result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("***", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteTreatsNotFoundAsUnsupportedBecauseCoreDeleteIsIdempotent()
    {
        // 核心的删除对不存在的资源也返回 200，所以 404 只可能是路由不存在（核心版本过旧）。
        var client = new CoreLocalDanmuClient(new HttpClient(
            new StubHandler((_, _) => Task.FromResult(Json("{\"errorMessage\":\"not found\"}", HttpStatusCode.NotFound)))));

        var result = await client.DeleteAsync("127.0.0.1", 9321, Token, AdminToken, "逐玉|2026|tv|5");

        Assert.False(result.Succeeded);
        Assert.Equal(LocalDanmuFailureKind.NotFound, result.FailureKind);
    }

    [Fact]
    public async Task DeleteSucceedsOnOk()
    {
        var client = new CoreLocalDanmuClient(new HttpClient(
            new StubHandler((_, _) => Task.FromResult(Json("{\"success\":true}")))));

        var result = await client.DeleteAsync("127.0.0.1", 9321, Token, AdminToken, "逐玉|2026|tv|5");

        Assert.True(result.Succeeded);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpRequestMessage Clone(HttpRequestMessage request) => new(request.Method, request.RequestUri);

    /// <summary>同步上报的进度实现：<see cref="Progress{T}"/> 会把回调异步投递到上下文，
    /// 在测试里断言会跑在回调之前（实测踩过），所以这里同步调用。</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responder(request, cancellationToken);
    }
}
