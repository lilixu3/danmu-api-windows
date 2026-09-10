using System.Net;
using System.Text;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class CoreLogClientTests
{
    [Fact]
    public async Task ReadsCoreLogTextFromTokenPath()
    {
        Uri? observed = null;
        using var handler = new StubHandler((request, _) =>
        {
            observed = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[t1] info: [system] first\n[t2] warn: [cache] second\n",
                    Encoding.UTF8,
                    "text/plain"),
            });
        });
        var client = new CoreLogClient(new HttpClient(handler), TimeSpan.FromSeconds(1));

        var result = await client.ReadAsync("127.0.0.1", 9321, "custom token");

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal(2, result.Lines.Count);
        Assert.Equal("http://127.0.0.1:9321/custom%20token/api/logs", observed?.AbsoluteUri);
    }

    [Fact]
    public async Task HttpFailureDoesNotLeakToken()
    {
        const string token = "sensitive-admin-looking-value";
        using var handler = new StubHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(token),
            }));
        var client = new CoreLogClient(new HttpClient(handler), TimeSpan.FromSeconds(1));

        var result = await client.ReadAsync("127.0.0.1", 9321, token);

        Assert.False(result.Succeeded);
        Assert.Contains("HTTP 401", result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(token, result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildsBracketedIpv6Uri()
    {
        var uri = CoreLogClient.BuildLogsUri("::1", 9321, "abc");

        Assert.Equal("http://[::1]:9321/abc/api/logs", uri.AbsoluteUri);
    }

    [Fact]
    public async Task RejectsInvalidUtf8Explicitly()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0xC3, 0x28]),
            }));
        var client = new CoreLogClient(new HttpClient(handler), TimeSpan.FromSeconds(1));

        var result = await client.ReadAsync("127.0.0.1", 9321, "abc");

        Assert.False(result.Succeeded);
        Assert.Contains("不是有效 UTF-8", result.Diagnostic, StringComparison.Ordinal);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responder(request, cancellationToken);
    }
}
