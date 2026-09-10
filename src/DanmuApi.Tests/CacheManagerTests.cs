using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class CoreCacheClientTests
{
    [Fact]
    public void CatalogMatchesCoreCacheProtocol()
    {
        Assert.Equal(
            [
                "searchCache",
                "commentCache",
                "requestHistory",
                "animes",
                "bangumiData",
                "episodeIds",
                "episodeNum",
                "lastSelectMap",
            ],
            CoreCacheCatalog.Items.Select(item => item.Key).ToArray());
    }

    [Fact]
    public async Task PostsItemsToAdminTokenEndpointAndParsesCounts()
    {
        using var sentinel = new TemporaryDirectory();
        var sentinelPath = Path.Combine(sentinel.Path, "node-runtime-sentinel.txt");
        File.WriteAllText(sentinelPath, "keep");
        HttpMethod? capturedMethod = null;
        Uri? capturedUri = null;
        string? capturedContentType = null;
        string? capturedBody = null;
        using var handler = new StubHandler(async (request, _) =>
        {
            capturedMethod = request.Method;
            capturedUri = request.RequestUri;
            capturedContentType = request.Content?.Headers.ContentType?.MediaType;
            capturedBody = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            return JsonResponse(
                "{\"success\":true,\"message\":\"Cache cleared successfully\",\"clearedItems\":{\"searchCache\":3,\"requestHistory\":0}}");
        });
        var client = new CoreCacheClient(new HttpClient(handler), TimeSpan.FromSeconds(1));

        var result = await client.ClearAsync(
            "127.0.0.1",
            9321,
            "runtime-token",
            "admin-token",
            ["searchCache", "requestHistory"]);

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal(3, result.ClearedItems["searchCache"]);
        Assert.Equal(0, result.ClearedItems["requestHistory"]);
        Assert.Equal(HttpMethod.Post, capturedMethod);
        Assert.Equal(
            "http://127.0.0.1:9321/admin-token/api/cache/clear",
            capturedUri!.AbsoluteUri);
        Assert.Equal("application/json", capturedContentType);
        using var document = JsonDocument.Parse(capturedBody!);
        Assert.True(document.RootElement.TryGetProperty("items", out var items));
        Assert.False(document.RootElement.TryGetProperty("Items", out _));
        Assert.Equal(["searchCache", "requestHistory"], items.EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.True(File.Exists(sentinelPath));
    }

    [Fact]
    public async Task UsesRegularTokenWhenAdminTokenIsAbsent()
    {
        Uri? capturedUri = null;
        using var handler = new StubHandler((request, _) =>
        {
            capturedUri = request.RequestUri;
            return Task.FromResult(JsonResponse("{\"success\":true,\"clearedItems\":{\"episodeNum\":10001}}"));
        });
        var client = new CoreCacheClient(new HttpClient(handler));

        var result = await client.ClearAsync(
            "localhost",
            9321,
            "regular-token",
            null,
            ["episodeNum"]);

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal("http://localhost:9321/regular-token/api/cache/clear", capturedUri!.AbsoluteUri);
    }

    [Fact]
    public async Task RejectsUnknownItemsWithoutSendingRequest()
    {
        var sent = false;
        using var handler = new StubHandler((_, _) =>
        {
            sent = true;
            return Task.FromResult(JsonResponse("{}"));
        });
        var client = new CoreCacheClient(new HttpClient(handler));

        var result = await client.ClearAsync("127.0.0.1", 9321, "token", null, [".cache"]);

        Assert.False(result.Succeeded);
        Assert.Contains("未知核心缓存项", result.Diagnostic, StringComparison.Ordinal);
        Assert.False(sent);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "HTTP 401")]
    [InlineData(HttpStatusCode.BadGateway, "HTTP 502")]
    public async Task ReportsHttpFailure(HttpStatusCode statusCode, string expected)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)));
        var client = new CoreCacheClient(new HttpClient(handler));

        var result = await client.ClearAsync("127.0.0.1", 9321, "token", null, ["animes"]);

        Assert.False(result.Succeeded);
        Assert.Contains(expected, result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsCoreFailureAndInvalidJson()
    {
        using var failureHandler = new StubHandler((_, _) => Task.FromResult(JsonResponse(
            "{\"success\":false,\"message\":\"not allowed\"}")));
        var failure = await new CoreCacheClient(new HttpClient(failureHandler))
            .ClearAsync("127.0.0.1", 9321, "token", null, ["animes"]);
        Assert.False(failure.Succeeded);
        Assert.Contains("not allowed", failure.Diagnostic, StringComparison.Ordinal);

        using var invalidHandler = new StubHandler((_, _) => Task.FromResult(JsonResponse("not-json")));
        var invalid = await new CoreCacheClient(new HttpClient(invalidHandler))
            .ClearAsync("127.0.0.1", 9321, "token", null, ["animes"]);
        Assert.False(invalid.Succeeded);
        Assert.Contains("JSON 无效", invalid.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedactsTokensFromNetworkDiagnostics()
    {
        const string token = "regular-secret";
        const string adminToken = "admin-secret";
        using var handler = new StubHandler((_, _) => throw new HttpRequestException(
            $"request http://127.0.0.1:9321/{adminToken}/api/cache/clear failed for {token}"));
        var client = new CoreCacheClient(new HttpClient(handler));

        var result = await client.ClearAsync(
            "127.0.0.1",
            9321,
            token,
            adminToken,
            ["animes"]);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(token, result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(adminToken, result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("***", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsCallerCancellation()
    {
        using var handler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse("{}");
        });
        var client = new CoreCacheClient(new HttpClient(handler), TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await client.ClearAsync("127.0.0.1", 9321, "token", null, ["animes"], cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Contains("已取消", result.Diagnostic, StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responder(request, cancellationToken);
    }
}
