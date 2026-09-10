using System.Net;
using System.Text;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class CoreCredentialClientTests
{
    [Fact]
    public async Task CookieVerificationUsesAdminPathAndDoesNotExposeCookie()
    {
        Uri? observed = null;
        string? payload = null;
        using var handler = new StubHandler(async (request, _) =>
        {
            observed = request.RequestUri;
            payload = await request.Content!.ReadAsStringAsync();
            return Json("{\"success\":true,\"data\":{\"isValid\":true,\"checked\":\"input\",\"uname\":\"tester\",\"expiresAt\":1900000000}}");
        });
        var client = new CoreCredentialClient(new HttpClient(handler), TimeSpan.FromSeconds(1));
        const string cookie = "SESSDATA=value; bili_jct=value";

        var result = await client.VerifyBilibiliCookieAsync(
            "127.0.0.1", 9321, "ordinary", "admin", cookie);

        Assert.True(result.RequestSucceeded, result.Diagnostic);
        Assert.True(result.IsValid);
        Assert.Equal("tester", result.UserName);
        Assert.Equal("http://127.0.0.1:9321/admin/api/cookie/verify", observed?.AbsoluteUri);
        Assert.Contains("SESSDATA", payload, StringComparison.Ordinal);
        Assert.DoesNotContain(cookie, result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CookieVerificationTreatsInvalidCookieAsCompletedCheck()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Json(
            "{\"success\":true,\"data\":{\"isValid\":false,\"checked\":\"input\",\"error\":\"Cookie 无效\"}}")));
        var client = new CoreCredentialClient(new HttpClient(handler), TimeSpan.FromSeconds(1));

        var result = await client.VerifyBilibiliCookieAsync(
            "127.0.0.1", 9321, "ordinary", "admin", "invalid-cookie");

        Assert.True(result.RequestSucceeded);
        Assert.False(result.IsValid);
        Assert.Contains("无效", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratesAndChecksQrProtocol()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            Json("{\"success\":true,\"data\":{\"url\":\"https://passport.example/qr\",\"qrcode_key\":\"key-1\"}}"),
            Json("{\"success\":true,\"data\":{\"code\":0,\"message\":\"\",\"cookie\":\"SESSDATA=value; bili_jct=value\"}}"),
        ]);
        using var handler = new StubHandler((_, _) => Task.FromResult(responses.Dequeue()));
        var client = new CoreCredentialClient(new HttpClient(handler), TimeSpan.FromSeconds(1));

        var generated = await client.GenerateBilibiliQrAsync(
            "127.0.0.1", 9321, "ordinary", "admin");
        var checkedResult = await client.CheckBilibiliQrAsync(
            "127.0.0.1", 9321, "ordinary", "admin", generated.QrCodeKey!);

        Assert.True(generated.Succeeded, generated.Diagnostic);
        Assert.Equal("key-1", generated.QrCodeKey);
        Assert.True(checkedResult.Succeeded, checkedResult.Diagnostic);
        Assert.Equal(0, checkedResult.Code);
        Assert.Contains("SESSDATA", checkedResult.Cookie, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsQrSuccessWithoutCookie()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Json(
            "{\"success\":true,\"data\":{\"code\":0,\"message\":\"success\"}}")));
        var client = new CoreCredentialClient(new HttpClient(handler), TimeSpan.FromSeconds(1));

        var result = await client.CheckBilibiliQrAsync(
            "127.0.0.1", 9321, "ordinary", "admin", "key-1");

        Assert.False(result.Succeeded);
        Assert.Contains("没有返回 Cookie", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AiVerificationUsesUnpersistedInputAndParsesOk()
    {
        string? payload = null;
        using var handler = new StubHandler(async (request, _) =>
        {
            payload = await request.Content!.ReadAsStringAsync();
            return Json("{\"success\":true,\"ok\":true,\"message\":\"AI 服务连通性测试成功\"}");
        });
        var client = new CoreCredentialClient(new HttpClient(handler), TimeSpan.FromSeconds(1));

        var result = await client.VerifyAiAsync(
            "127.0.0.1", 9321, "ordinary", "admin", "api-key-input");

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Contains("api-key-input", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("api-key-input", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpFailureRedactsAllSecrets()
    {
        const string token = "ordinary-secret";
        const string admin = "admin-secret";
        const string apiKey = "api-secret";
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                $"{{\"message\":\"{token} {admin} {apiKey}\"}}",
                Encoding.UTF8,
                "application/json"),
        }));
        var client = new CoreCredentialClient(new HttpClient(handler), TimeSpan.FromSeconds(1));

        var result = await client.VerifyAiAsync(
            "127.0.0.1", 9321, token, admin, apiKey);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(token, result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(admin, result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulHttpResponsesRedactSecretsFromProtocolMessages()
    {
        const string token = "ordinary-secret";
        const string admin = "admin-secret";
        const string apiKey = "api-secret";
        var responses = new Queue<HttpResponseMessage>(
        [
            Json($"{{\"success\":false,\"message\":\"{token} {admin}\"}}"),
            Json($"{{\"success\":false,\"message\":\"{admin}\"}}"),
            Json($"{{\"success\":false,\"message\":\"{admin} key-1\"}}"),
            Json($"{{\"success\":true,\"ok\":false,\"message\":\"{token} {admin} {apiKey}\"}}"),
        ]);
        using var handler = new StubHandler((_, _) => Task.FromResult(responses.Dequeue()));
        var client = new CoreCredentialClient(new HttpClient(handler), TimeSpan.FromSeconds(1));

        var cookieResult = await client.VerifyBilibiliCookieAsync(
            "127.0.0.1", 9321, token, admin, "cookie-secret");
        var generateResult = await client.GenerateBilibiliQrAsync(
            "127.0.0.1", 9321, token, admin);
        var checkResult = await client.CheckBilibiliQrAsync(
            "127.0.0.1", 9321, token, admin, "key-1");
        var aiResult = await client.VerifyAiAsync(
            "127.0.0.1", 9321, token, admin, apiKey);

        foreach (var diagnostic in new[]
                 {
                     cookieResult.Diagnostic,
                     generateResult.Diagnostic,
                     checkResult.Diagnostic,
                     aiResult.Diagnostic,
                 })
        {
            Assert.DoesNotContain(token, diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain(admin, diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain(apiKey, diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("key-1", diagnostic, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ReportsTimeout()
    {
        using var handler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Json("{\"success\":true,\"ok\":true}");
        });
        var client = new CoreCredentialClient(new HttpClient(handler), TimeSpan.FromMilliseconds(20));

        var result = await client.VerifyAiAsync(
            "127.0.0.1", 9321, "ordinary", "admin", "api-key");

        Assert.False(result.Succeeded);
        Assert.Contains("超时", result.Diagnostic, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json"),
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
