using System.Net;
using DanmuApi.Core;

namespace DanmuApi.Tests;

/// <summary>
/// 核心 zipball 也走 api.github.com（`/zipball/{sha}`），同样吃每小时配额。
/// 已配置 Token 时必须带上，否则装核心会先撞匿名 60/小时；代理线路与资产 CDN 一律不带。
/// </summary>
public sealed class GithubFileDownloaderTokenTests
{
    [Fact]
    public async Task ZipballFromApiHostCarriesTheToken()
    {
        var handler = new RecordingHandler();
        var downloader = new GithubFileDownloader(
            new HttpClient(handler), routePreferences: new ConfirmedRoute(GithubProxyCatalog.OriginalId),
            tokenProvider: new FixedToken("secret-token"));
        var target = TempFile();

        try
        {
            await downloader.DownloadAsync(
                new Uri("https://api.github.com/repos/o/r/zipball/abcdef"), GithubProxyCatalog.OriginalId, target);

            var call = Assert.Single(handler.Requests);
            Assert.Equal("api.github.com", call.Host);
            Assert.Equal("Bearer secret-token", call.Authorization);
        }
        finally
        {
            Delete(target);
        }
    }

    [Fact]
    public async Task ProxyRouteNeverCarriesTheToken()
    {
        // 走第三方代理时，凭据绝不能交给它。
        var handler = new RecordingHandler();
        var downloader = new GithubFileDownloader(
            new HttpClient(handler), routePreferences: new ConfirmedRoute("gh_proxy_org"),
            tokenProvider: new FixedToken("secret-token"));
        var target = TempFile();

        try
        {
            await downloader.DownloadAsync(
                new Uri("https://api.github.com/repos/o/r/zipball/abcdef"), "gh_proxy_org", target);

            Assert.NotEmpty(handler.Requests);
            Assert.All(handler.Requests, call => Assert.Null(call.Authorization));
        }
        finally
        {
            Delete(target);
        }
    }

    [Fact]
    public async Task NoTokenConfiguredSendsAnonymousRequest()
    {
        var handler = new RecordingHandler();
        var downloader = new GithubFileDownloader(
            new HttpClient(handler), routePreferences: new ConfirmedRoute(GithubProxyCatalog.OriginalId),
            tokenProvider: new FixedToken(null));
        var target = TempFile();

        try
        {
            await downloader.DownloadAsync(
                new Uri("https://api.github.com/repos/o/r/zipball/abcdef"), GithubProxyCatalog.OriginalId, target);

            Assert.All(handler.Requests, call => Assert.Null(call.Authorization));
        }
        finally
        {
            Delete(target);
        }
    }

    private static string TempFile() => Path.Combine(Path.GetTempPath(), "danmu-dl-" + Guid.NewGuid().ToString("N") + ".zip");

    private static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private sealed class FixedToken(string? token) : IGithubTokenProvider
    {
        public bool IsConfigured => token is not null;
        public string? GetToken() => token;
    }

    private sealed class ConfirmedRoute(string proxyId) : IGithubRoutePreferenceStore
    {
        public GithubRoutePreference Read() => new(proxyId, true);
        public void Confirm(string id) { }
        public void Invalidate() { }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Host, string? Authorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.IdnHost, request.Headers.Authorization?.ToString()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("payload"u8.ToArray()),
            });
        }
    }
}
