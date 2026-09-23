using System.Net;
using System.Net.Http;
using DanmuApi.App.Services;

namespace DanmuApi.Tests;

/// <summary>
/// Markdown 图片加载：只允许 https，限额、超时、失败都要留诊断。
/// 关键断言是"被拒绝的地址根本不发请求" —— 只看返回值无法区分"没请求"和"请求失败"。
/// </summary>
public sealed class MarkdownImageLoaderTests
{
    [Theory]
    [InlineData("http://example.com/a.png")]
    [InlineData("file:///C:/a.png")]
    [InlineData("//example.com/a.png")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("")]
    public async Task RejectedUrlsNeverHitTheNetwork(string url)
    {
        var handler = new RecordingHandler();
        using var loader = new MarkdownImageLoader(new HttpClient(handler));

        var bitmap = await loader.LoadAsync(url);

        Assert.Null(bitmap);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task HttpsImageIsFetchedAndFailureIsDiagnosed()
    {
        var handler = new RecordingHandler { Response = _ => new HttpResponseMessage(HttpStatusCode.NotFound) };
        var diagnostics = new List<string>();
        using var loader = new MarkdownImageLoader(new HttpClient(handler), diagnostics.Add);

        var bitmap = await loader.LoadAsync("https://example.com/broken.png");

        Assert.Null(bitmap);
        Assert.Equal("https://example.com/broken.png", Assert.Single(handler.Requests));
        // 失败必须留痕，不能静默返回 null。
        Assert.Contains(diagnostics, message => message.Contains("Markdown 图片加载失败", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NonHttpsIsReportedInDiagnosticsAsRejected()
    {
        var handler = new RecordingHandler();
        var diagnostics = new List<string>();
        using var loader = new MarkdownImageLoader(new HttpClient(handler), diagnostics.Add);

        await loader.LoadAsync("http://example.com/a.png");

        Assert.Empty(handler.Requests);
        Assert.Contains(diagnostics, message => message.Contains("只允许 https", StringComparison.Ordinal));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage>? Response { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(Response?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
