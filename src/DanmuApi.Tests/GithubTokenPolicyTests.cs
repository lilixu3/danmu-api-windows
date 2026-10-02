using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DanmuApi.Core;
using DanmuApi.Core.ApplicationUpdates;

namespace DanmuApi.Tests;

/// <summary>
/// 用户实测报过：超出匿名限额后保存了 Token、核心检查的额度也刷新了，但软件更新检查仍报超限。
/// 根因是软件更新检查完全不使用凭据（只吃 60/小时 的匿名额度，与 Token 的 5000/小时 是两个桶）。
/// 这里钉住修复后的行为：**已配置 Token 时，所有走 api.github.com 的请求都必须带上它**，
/// 且 Token 绝不能出现在代理/资产主机或任何重定向后的请求上。
/// </summary>
public sealed class GithubTokenPolicyTests
{
    [Theory]
    [InlineData("https://api.github.com/repos/x/y/releases")]
    [InlineData("https://api.github.com/repos/x/y/zipball/abcdef")]
    public void ApiHostOverHttpsAllowsToken(string url) =>
        Assert.True(GithubTokenPolicy.CanAttach(new Uri(url)));

    [Theory]
    // 第三方代理：把凭据交给它等于泄漏。
    [InlineData("https://gh-proxy.org/https://api.github.com/x")]
    [InlineData("https://hk.gh-proxy.org/api.github.com/x")]
    // 资产与原始内容 CDN：不消耗 API 配额、不需要认证。
    [InlineData("https://github.com/x/y/releases/download/v1/a.zip")]
    [InlineData("https://objects.githubusercontent.com/x")]
    [InlineData("https://release-assets.githubusercontent.com/x")]
    [InlineData("https://raw.githubusercontent.com/x/y/main/z.js")]
    // 非 HTTPS / 带凭据的 URL / 端口不对。
    [InlineData("http://api.github.com/x")]
    [InlineData("https://user:pass@api.github.com/x")]
    [InlineData("https://api.github.com:8443/x")]
    public void EverythingElseRejectsToken(string url) =>
        Assert.False(GithubTokenPolicy.CanAttach(new Uri(url)));

    [Fact]
    public void NormalizeStripsPastedPrefixes()
    {
        Assert.Equal("abc", GithubTokenPolicy.Normalize("abc"));
        Assert.Equal("abc", GithubTokenPolicy.Normalize("Bearer abc"));
        Assert.Equal("abc", GithubTokenPolicy.Normalize("bearer   abc  "));
        Assert.Equal("abc", GithubTokenPolicy.Normalize("token abc"));
        Assert.Equal(string.Empty, GithubTokenPolicy.Normalize(null));
        Assert.Equal(string.Empty, GithubTokenPolicy.Normalize("   "));
    }

    [Fact]
    public async Task AppUpdateCheckSendsTokenToApiHostOnly()
    {
        var fixture = new Fixture();
        var requests = new List<(string Host, string? Authorization)>();
        using var service = fixture.Service(tokenProvider: new FixedToken("secret-token"),
            handler: (request, _) =>
            {
                requests.Add((request.RequestUri!.IdnHost, request.Headers.Authorization?.ToString()));
                // 列表走 API 主机；清单与签名走 github.com 资产地址；包走 CDN。
                return Task.FromResult(request.RequestUri!.Host == "api.github.com"
                    ? JsonResponse(new[] { fixture.Release("v2.0.0") })
                    : request.RequestUri.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal)
                        ? BytesResponse(fixture.Signature)
                        : request.RequestUri.AbsolutePath.EndsWith(".json", StringComparison.Ordinal)
                            ? BytesResponse(fixture.Manifest)
                            : BytesResponse(fixture.Package));
            });

        var update = await service.CheckAsync("1.0.0");

        Assert.NotNull(update);
        var apiCalls = requests.Where(r => r.Host == "api.github.com").ToArray();
        Assert.NotEmpty(apiCalls);
        Assert.All(apiCalls, call => Assert.Equal("Bearer secret-token", call.Authorization));
        // 资产主机一条都不能带凭据。
        Assert.All(requests.Where(r => r.Host != "api.github.com"),
            call => Assert.Null(call.Authorization));
    }

    [Fact]
    public async Task AppUpdateCheckStaysAnonymousWhenNoTokenIsConfigured()
    {
        var fixture = new Fixture();
        var authorizations = new List<string?>();
        using var service = fixture.Service(tokenProvider: new FixedToken(null),
            handler: (request, _) =>
            {
                if (request.RequestUri!.Host == "api.github.com") authorizations.Add(request.Headers.Authorization?.ToString());
                return Task.FromResult(request.RequestUri.Host == "api.github.com"
                    ? JsonResponse(new[] { fixture.Release("v2.0.0") })
                    : request.RequestUri.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal)
                        ? BytesResponse(fixture.Signature)
                        : request.RequestUri.AbsolutePath.EndsWith(".json", StringComparison.Ordinal)
                            ? BytesResponse(fixture.Manifest)
                            : BytesResponse(fixture.Package));
            });

        await service.CheckAsync("1.0.0");

        Assert.NotEmpty(authorizations);
        Assert.All(authorizations, value => Assert.Null(value));
    }

    [Fact]
    public async Task SavingATokenTakesEffectWithoutRecreatingTheService()
    {
        // 用户的实际操作顺序是"先超限、再存 Token、立刻重试"：Token 必须在下次调用就被读到。
        var fixture = new Fixture();
        var provider = new MutableToken();
        var seen = new List<string?>();
        using var service = fixture.Service(tokenProvider: provider,
            handler: (request, _) =>
            {
                if (request.RequestUri!.Host == "api.github.com") seen.Add(request.Headers.Authorization?.ToString());
                return Task.FromResult(request.RequestUri.Host == "api.github.com"
                    ? JsonResponse(new[] { fixture.Release("v2.0.0") })
                    : request.RequestUri.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal)
                        ? BytesResponse(fixture.Signature)
                        : request.RequestUri.AbsolutePath.EndsWith(".json", StringComparison.Ordinal)
                            ? BytesResponse(fixture.Manifest)
                            : BytesResponse(fixture.Package));
            });

        await service.CheckAsync("1.0.0");
        Assert.All(seen, value => Assert.Null(value));

        provider.Save("fresh-token");
        await service.CheckAsync("1.0.0");
        Assert.Contains("Bearer fresh-token", seen);
    }

    [Fact]
    public async Task PackageDownloadNeverCarriesTheToken()
    {
        var fixture = new Fixture();
        var assetCalls = new List<(string Host, string? Authorization)>();
        using var service = fixture.Service(tokenProvider: new FixedToken("secret-token"),
            handler: (request, _) =>
            {
                if (request.RequestUri!.Host != "api.github.com")
                    assetCalls.Add((request.RequestUri.IdnHost, request.Headers.Authorization?.ToString()));
                return Task.FromResult(request.RequestUri.Host == "api.github.com"
                    ? JsonResponse(new[] { fixture.Release("v2.0.0") })
                    : request.RequestUri.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal)
                        ? BytesResponse(fixture.Signature)
                        : request.RequestUri.AbsolutePath.EndsWith(".json", StringComparison.Ordinal)
                            ? BytesResponse(fixture.Manifest)
                            : BytesResponse(fixture.Package));
            });

        var update = await service.CheckAsync("1.0.0");
        var target = Path.Combine(Path.GetTempPath(), "danmu-token-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await service.DownloadAsync(update!, "package.zip", target);
            Assert.NotEmpty(assetCalls);
            Assert.All(assetCalls, call => Assert.Null(call.Authorization));
        }
        finally
        {
            if (File.Exists(target)) File.Delete(target);
        }
    }

    private static HttpResponseMessage JsonResponse(object value) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value)) };

    private static HttpResponseMessage BytesResponse(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private sealed class FixedToken(string? token) : IGithubTokenProvider
    {
        public bool IsConfigured => token is not null;
        public string? GetToken() => token;
    }

    private sealed class MutableToken : IGithubTokenProvider
    {
        private string? _token;
        public bool IsConfigured => _token is not null;
        public string? GetToken() => _token;
        public void Save(string token) => _token = token;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly RSA _rsa = RSA.Create(2048);
        public byte[] Package { get; } = Encoding.UTF8.GetBytes("payload");
        public byte[] Manifest { get; }
        public byte[] Signature { get; }

        public Fixture()
        {
            Manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                product = "DanmuApi.Windows",
                version = "2.0.0",
                channel = "preview",
                architecture = ApplicationArchitecture.Current,
                assets = new[] { new { name = "package.zip", size = Package.Length, sha256 = Convert.ToHexString(SHA256.HashData(Package)).ToLowerInvariant(), kind = "portable" } },
            });
            Signature = _rsa.SignData(Manifest, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        public object Release(string tag) => new
        {
            tag_name = tag,
            draft = false,
            prerelease = false,
            body = "notes",
            published_at = "2026-09-08T00:00:00Z",
            assets = new[] { "update-manifest.json", "update-manifest.json.sig", "package.zip", "asset.zip" }
                .Select(name => new { name, browser_download_url = "https://github.com/lilixu3/danmu-api-windows/releases/download/" + tag + "/" + name }),
        };

        public ApplicationUpdateService Service(
            IGithubTokenProvider? tokenProvider = null,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? handler = null) =>
            new(_rsa.ExportSubjectPublicKeyInfo(),
                new FakeHandler(handler ?? ((request, _) => Task.FromResult(BytesResponse(Manifest)))),
                tokenProvider: tokenProvider);

        public void Dispose() => _rsa.Dispose();
    }
}
