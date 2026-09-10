using System.Net;
using System.Net.Http;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class RuntimeHealthClientTests
{
    [Fact]
    public void ParsesHealthDocumentAndFractionalMtime()
    {
        const string json = "{\"ok\":true,\"pid\":42,\"uptimeSec\":12,\"envFileMtimeMs\":1787993369087.2434,\"ports\":{\"main\":9321,\"proxy\":5321},\"cwd\":\"C:\\\\data\",\"resolvedHome\":\"C:\\\\data\"}";

        var result = RuntimeHealthClient.Parse(System.Text.Encoding.UTF8.GetBytes(json));

        Assert.Equal(42, result.Pid);
        Assert.Equal(9321, result.MainPort);
        Assert.Equal(1787993369087.2434m, result.EnvFileMtimeMs);
    }

    [Fact]
    public void RejectsFalseHealthAndWrongTypes()
    {
        Assert.Throws<RuntimeHealthException>(() => RuntimeHealthClient.Parse(System.Text.Encoding.UTF8.GetBytes("{\"ok\":false}")));
        Assert.Throws<RuntimeHealthException>(() => RuntimeHealthClient.Parse(System.Text.Encoding.UTF8.GetBytes("{\"ok\":true,\"pid\":1.5}")));
        Assert.Throws<RuntimeHealthException>(() => RuntimeHealthClient.Parse(System.Text.Encoding.UTF8.GetBytes("{\"ok\":true,\"envFileMtimeMs\":-1}")));
    }

    [Fact]
    public void BuildsBracketedIpv6Uri()
    {
        var uri = RuntimeHealthClient.BuildHealthUri("::1", 9321);
        Assert.Equal("http://[::1]:9321/__health", uri.AbsoluteUri);
    }

    [Fact]
    public async Task DistinguishesHttpFailure()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)));
        var client = new RuntimeHealthClient(new HttpClient(handler), TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<RuntimeHealthException>(() => client.ReadAsync("127.0.0.1", 9321));

        Assert.Equal(HealthFailureKind.HttpStatus, error.Kind);
        Assert.Equal(409, error.StatusCode);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => responder(request, cancellationToken);
    }
}
