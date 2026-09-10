using System.Net;
using System.Text;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class CoreEnvClientTests
{
    [Fact]
    public async Task DeletePostsToAdminTokenPathAndParsesSuccess()
    {
        Uri? observedUri = null;
        string? body = null;
        using var handler = new StubHandler(async (request, _) =>
        {
            observedUri = request.RequestUri;
            body = await request.Content!.ReadAsStringAsync();
            return JsonResponse("{\"success\":true,\"message\":\"环境变量删除成功\"}");
        });
        var client = new CoreEnvClient(new HttpClient(handler), TimeSpan.FromSeconds(1));

        var result = await client.DeleteAsync(
            "127.0.0.1", 9321, "ordinary-token", "admin-token", "TEST_COUNT");

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal("http://127.0.0.1:9321/admin-token/api/env/del", observedUri?.AbsoluteUri);
        Assert.Equal("{\"key\":\"TEST_COUNT\"}", body);
    }

    [Fact]
    public async Task DeleteUsesOrdinaryTokenWhenAdminTokenIsMissing()
    {
        Uri? observedUri = null;
        using var handler = new StubHandler((request, _) =>
        {
            observedUri = request.RequestUri;
            return Task.FromResult(JsonResponse("{\"success\":true}"));
        });
        var client = new CoreEnvClient(new HttpClient(handler), TimeSpan.FromSeconds(1));

        var result = await client.DeleteAsync(
            "127.0.0.1", 9321, "ordinary-token", null, "TEST_COUNT");

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal("http://127.0.0.1:9321/ordinary-token/api/env/del", observedUri?.AbsoluteUri);
    }

    [Fact]
    public async Task DeleteRejectsFalseResponseWithoutReportingSuccess()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(
            JsonResponse("{\"success\":false,\"message\":\"删除失败\"}")));
        var client = new CoreEnvClient(new HttpClient(handler), TimeSpan.FromSeconds(1));

        var result = await client.DeleteAsync(
            "127.0.0.1", 9321, "ordinary-token", "admin-token", "TEST_COUNT");

        Assert.False(result.Succeeded);
        Assert.Contains("删除失败", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteRejectsMalformedResponseAndRedactsTokens()
    {
        const string token = "secret-token";
        using var handler = new StubHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent($"request used {token}", Encoding.UTF8, "text/plain"),
            }));
        var client = new CoreEnvClient(new HttpClient(handler), TimeSpan.FromSeconds(1));

        var result = await client.DeleteAsync(
            "127.0.0.1", 9321, token, "admin-token", "TEST_COUNT");

        Assert.False(result.Succeeded);
        Assert.Contains("HTTP 500", result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(token, result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDeleteUriBracketsIpv6AndEscapesToken()
    {
        var uri = CoreEnvClient.BuildDeleteUri("::1", 9321, "admin token");

        Assert.Equal("http://[::1]:9321/admin%20token/api/env/del", uri.AbsoluteUri);
    }

    [Fact]
    public async Task DeleteReportsConnectionFailure()
    {
        using var handler = new StubHandler((_, _) =>
            throw new HttpRequestException("connection refused"));
        var client = new CoreEnvClient(new HttpClient(handler), TimeSpan.FromSeconds(1));

        var result = await client.DeleteAsync(
            "127.0.0.1", 9321, "ordinary-token", "admin-token", "TEST_COUNT");

        Assert.False(result.Succeeded);
        Assert.Contains("请求失败", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("connection refused", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteReportsTimeout()
    {
        using var handler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse("{\"success\":true}");
        });
        var client = new CoreEnvClient(new HttpClient(handler), TimeSpan.FromMilliseconds(20));

        var result = await client.DeleteAsync(
            "127.0.0.1", 9321, "ordinary-token", "admin-token", "TEST_COUNT");

        Assert.False(result.Succeeded);
        Assert.Contains("超时", result.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"success\":\"true\"}")]
    public async Task DeleteRejectsResponseWithoutBooleanSuccess(string response)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(response)));
        var client = new CoreEnvClient(new HttpClient(handler), TimeSpan.FromSeconds(1));

        var result = await client.DeleteAsync(
            "127.0.0.1", 9321, "ordinary-token", "admin-token", "TEST_COUNT");

        Assert.False(result.Succeeded);
        Assert.Contains("布尔字段 success", result.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("中文变量")]
    [InlineData("TEST-VALUE")]
    [InlineData("9TEST")]
    [InlineData(" TEST")]
    public async Task DeleteUsesSameAsciiKeyRuleAsDotEnv(string key)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse("{\"success\":true}")));
        var client = new CoreEnvClient(new HttpClient(handler), TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<ArgumentException>(() => client.DeleteAsync(
            "127.0.0.1", 9321, "ordinary-token", "admin-token", key));
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responder(request, cancellationToken);
    }
}
