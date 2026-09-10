using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DanmuApi.Core.ApplicationUpdates;

namespace DanmuApi.Tests;

public sealed class ApplicationUpdateServiceTests
{
    [Fact]
    public void SemVerOrdersOfficialPrereleaseSequenceAndIgnoresBuildMetadata()
    {
        string[] versions = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "2.0.0"];
        for (var i = 1; i < versions.Length; i++) Assert.True(SemanticVersion.Parse(versions[i - 1]).CompareTo(SemanticVersion.Parse(versions[i])) < 0);
        Assert.Equal(0, SemanticVersion.Parse("1.2.3+foo").CompareTo(SemanticVersion.Parse("1.2.3+bar")));
        Assert.True(SemanticVersion.Parse("999999999999999999999.0.0").CompareTo(SemanticVersion.Parse("2.0.0")) > 0);
    }

    [Theory]
    [InlineData("1.2")][InlineData("01.2.3")][InlineData("1.2.3-01")][InlineData("1.2.3-")][InlineData("1.2.3\n")]
    public void InvalidSemVerFails(string value) => Assert.Throws<FormatException>(() => SemanticVersion.Parse(value));

    [Fact]
    public async Task SelectsHighestNonDraftIncludingPrereleaseAndRevalidatesEtag()
    {
        using var fixture = new Fixture();
        var listCalls = 0;
        using var service = fixture.Service((request, _) =>
        {
            Assert.Null(request.Headers.Authorization);
            if (request.RequestUri!.Host == "api.github.com")
            {
                listCalls++;
                if (listCalls == 2)
                {
                    Assert.Equal("\"feed-v1\"", request.Headers.IfNoneMatch.Single().Tag);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
                }
                var response = JsonResponse(new[] { fixture.Release("v1.0.0"), fixture.Release("v9.0.0", draft: true), fixture.Release("v2.0.0-preview.2", prerelease: true), fixture.Release("v2.0.0-preview.1", prerelease: true) });
                response.Headers.ETag = new("\"feed-v1\"");
                return Task.FromResult(response);
            }
            return Task.FromResult(fixture.AssetResponse(request));
        });
        var update = Assert.IsType<ApplicationUpdate>(await service.CheckAsync("1.0.0"));
        Assert.Equal("2.0.0-preview.2", update.Manifest.Version);
        Assert.Equal("Release notes", update.ReleaseNotes);
        Assert.Equal(DateTimeOffset.Parse("2026-09-08T00:00:00Z"), update.PublishedAt);
        var bytes = update.ManifestBytes; bytes[0] ^= 1;
        var signature = update.SignatureBytes; signature[0] ^= 1;
        Assert.Equal(fixture.Manifest, update.ManifestBytes);
        Assert.Equal(fixture.Signature, update.SignatureBytes);
        service.VerifyManifest(update.ManifestBytes, update.SignatureBytes);
        Assert.NotNull(await service.CheckAsync("1.0.0"));
        Assert.Equal(2, listCalls);
    }

    [Fact]
    public async Task NoNewVersionDoesNotFetchManifest()
    {
        using var fixture = new Fixture();
        using var service = fixture.Service((request, _) => { Assert.Equal("api.github.com", request.RequestUri!.Host); return Task.FromResult(JsonResponse(new[] { fixture.Release("v1.0.0") })); });
        Assert.Null(await service.CheckAsync("1.0.0"));
    }

    [Fact]
    public async Task TamperedSignatureFailsWithoutSelectingOlderRelease()
    {
        using var fixture = new Fixture();
        fixture.Signature[0] ^= 1;
        using var service = fixture.Service();
        await Assert.ThrowsAsync<CryptographicException>(() => service.CheckAsync("1.0.0"));
    }

    [Theory]
    [InlineData("duplicate")][InlineData("unknown")][InlineData("channel")][InlineData("large-package")][InlineData("trailing")]
    public void SignedInvalidJsonOrSchemaFails(string change)
    {
        using var fixture = new Fixture();
        var text = Encoding.UTF8.GetString(fixture.Manifest);
        text = change switch
        {
            "duplicate" => text.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"),
            "unknown" => text.Replace("\"schemaVersion\":1", "\"extra\":1,\"schemaVersion\":1"),
            "channel" => text.Replace("\"channel\":\"preview\"", "\"channel\":\"stable\""),
            "large-package" => text.Replace("\"size\":7", "\"size\":536870913"),
            "trailing" => text[..^1] + ",}",
            _ => throw new InvalidOperationException()
        };
        var bytes = Encoding.UTF8.GetBytes(text);
        using var service = fixture.Service();
        Assert.ThrowsAny<Exception>(() => service.VerifyManifest(bytes, fixture.Sign(bytes)));
    }

    [Fact]
    public void OversizeManifestFailsBeforeParsing()
    {
        using var fixture = new Fixture();
        using var service = fixture.Service();
        Assert.Throws<InvalidDataException>(() => service.VerifyManifest(new byte[ApplicationUpdateService.MaximumManifestBytes + 1], []));
    }

    [Fact]
    public async Task DeadlineIsExplicitTimeoutAndCallerCancellationIsPreserved()
    {
        using var fixture = new Fixture();
        static async Task<HttpResponseMessage> Block(HttpRequestMessage _, CancellationToken ct) { await Task.Delay(Timeout.InfiniteTimeSpan, ct); throw new InvalidOperationException(); }
        using var service = fixture.Service(Block, TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<TimeoutException>(() => service.CheckAsync("1.0.0"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckAsync("1.0.0", cancellation.Token));
    }

    [Theory]
    [InlineData("http://github.com/unsafe")][InlineData("https://evil.example/unsafe")][InlineData("https://github.com:444/unsafe")][InlineData("https://user@github.com/unsafe")]
    public async Task RejectsUnsafeRedirectBeforeSendingIt(string target)
    {
        using var fixture = new Fixture();
        var calls = 0;
        using var service = fixture.Service((_, _) => { calls++; var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri(target); return Task.FromResult(response); });
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync("1.0.0"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SafeRedirectIsFollowedWithoutCredentials()
    {
        using var fixture = new Fixture();
        using var service = fixture.Service((request, _) =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            if (request.RequestUri!.Host == "api.github.com")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://github.com/list");
                return Task.FromResult(redirect);
            }
            if (request.RequestUri!.AbsolutePath == "/list") return Task.FromResult(JsonResponse(new[] { fixture.Release("v2.0.0-preview.2") }));
            return Task.FromResult(fixture.AssetResponse(request));
        });
        Assert.NotNull(await service.CheckAsync("1.0.0"));
    }

    [Theory]
    [InlineData("valid")][InlineData("hash")][InlineData("size")][InlineData("cancel")][InlineData("existing")]
    public async Task DownloadsAuthenticateAndCleanPartialFiles(string scenario)
    {
        using var fixture = new Fixture();
        using var service = fixture.Service();
        var update = (await service.CheckAsync("1.0.0"))!;
        if (scenario == "hash") fixture.Package = Encoding.UTF8.GetBytes("badload");
        if (scenario == "size") fixture.Package = [1, 2];
        var directory = Path.Combine(Path.GetTempPath(), "application-update-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "package.zip");
            using var cancellation = new CancellationTokenSource();
            var events = new List<ApplicationUpdateProgress>();
            var progress = new InlineProgress(p => { events.Add(p); if (scenario == "cancel") cancellation.Cancel(); });
            if (scenario == "existing") await File.WriteAllTextAsync(path, "existing");
            var task = service.DownloadAsync(update, "package.zip", path, progress, cancellation.Token);
            if (scenario == "valid")
            {
                Assert.Equal(path, await task);
                Assert.Equal(fixture.Package, await File.ReadAllBytesAsync(path));
                Assert.Equal(7, events.Last().BytesReceived);
                Assert.Equal(7, events.Last().TotalBytes);
            }
            else
            {
                if (scenario == "hash") await Assert.ThrowsAsync<CryptographicException>(() => task);
                if (scenario == "size") await Assert.ThrowsAsync<InvalidDataException>(() => task);
                if (scenario == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
                if (scenario == "existing") { await Assert.ThrowsAsync<IOException>(() => task); Assert.Equal("existing", await File.ReadAllTextAsync(path)); }
                else Assert.False(File.Exists(path));
            }
            Assert.Empty(Directory.GetFiles(directory, "*.part"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("truncated")][InlineData("oversize")][InlineData("timeout")]
    public async Task StreamingFailuresWithoutContentLengthCleanPartialFile(string scenario)
    {
        using var fixture = new Fixture();
        using var service = fixture.Service((request, _) =>
        {
            if (request.RequestUri!.Host == "api.github.com") return Task.FromResult(JsonResponse(new[] { fixture.Release("v2.0.0-preview.2") }));
            if (request.RequestUri.Segments.Last() != "package.zip") return Task.FromResult(fixture.AssetResponse(request));
            Stream input = scenario == "timeout" ? new BlockingStream() : new NonSeekableStream(scenario == "truncated" ? [1, 2] : new byte[8]);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(input) });
        }, TimeSpan.FromSeconds(1));
        var update = (await service.CheckAsync("1.0.0"))!;
        var directory = Path.Combine(Path.GetTempPath(), "application-update-stream-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "package.zip");
            if (scenario == "timeout") await Assert.ThrowsAsync<TimeoutException>(() => service.DownloadAsync(update, "package.zip", path));
            else await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(update, "package.zip", path));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ManifestVersionMismatchFailsExplicitly()
    {
        using var fixture = new Fixture();
        using var service = fixture.Service((request, _) => Task.FromResult(request.RequestUri!.Host == "api.github.com" ? JsonResponse(new[] { fixture.Release("v3.0.0") }) : fixture.AssetResponse(request)));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync("1.0.0"));
    }

    [Fact]
    public async Task NotModifiedWithoutCacheFailsExplicitly()
    {
        using var fixture = new Fixture();
        using var service = fixture.Service((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified)));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync("1.0.0"));
    }

    [Fact]
    public async Task SuccessfulResponseWithoutEtagClearsPreviousValidator()
    {
        using var fixture = new Fixture();
        var calls = 0;
        using var service = fixture.Service((request, _) =>
        {
            calls++;
            if (calls == 3) Assert.Empty(request.Headers.IfNoneMatch);
            var response = JsonResponse(Array.Empty<object>());
            if (calls == 1) response.Headers.ETag = new("\"old\"");
            return Task.FromResult(response);
        });
        for (var i = 0; i < 3; i++) Assert.Null(await service.CheckAsync("1.0.0"));
    }

    private class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
    private sealed class BlockingStream() : NonSeekableStream([])
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); throw new InvalidOperationException(); }
    }

    private sealed class InlineProgress(Action<ApplicationUpdateProgress> report) : IProgress<ApplicationUpdateProgress>
    { public void Report(ApplicationUpdateProgress value) => report(value); }
    private static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value)) };
    private sealed class FakeHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class Fixture : IDisposable
    {
        private readonly RSA rsa = RSA.Create(2048);
        public byte[] Package = Encoding.UTF8.GetBytes("payload");
        public byte[] Manifest { get; }
        public byte[] Signature { get; }
        public Fixture()
        {
            Manifest = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, product = "DanmuApi.Windows", version = "2.0.0-preview.2", channel = "preview", architecture = "win-x64", assets = new[] { new { name = "package.zip", size = 7, sha256 = Convert.ToHexString(SHA256.HashData(Package)), kind = "portable" } } });
            Signature = Sign(Manifest);
        }
        public byte[] Sign(byte[] bytes) => rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        public object Release(string tag, bool draft = false, bool prerelease = false) => new { tag_name = tag, draft, prerelease, body = "Release notes", published_at = "2026-09-08T00:00:00Z", assets = new[] { ApplicationUpdateService.ManifestFileName, ApplicationUpdateService.SignatureFileName, "package.zip" }.Select(name => new { name, browser_download_url = "https://github.com/lilixu3/danmu-api-windows/releases/download/" + tag + "/" + name }) };
        public HttpResponseMessage AssetResponse(HttpRequestMessage request)
        {
            var name = request.RequestUri!.Segments.Last();
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(name == ApplicationUpdateService.ManifestFileName ? Manifest : name == ApplicationUpdateService.SignatureFileName ? Signature : name == "package.zip" ? Package : throw new InvalidOperationException("Unexpected URL")) };
        }
        public ApplicationUpdateService Service(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? handler = null, TimeSpan? timeout = null) => new(rsa.ExportSubjectPublicKeyInfo(), new FakeHttpHandler(handler ?? ((request, _) => Task.FromResult(request.RequestUri!.Host == "api.github.com" ? JsonResponse(new[] { Release("v2.0.0-preview.2", prerelease: true) }) : AssetResponse(request)))), timeout);
        public void Dispose() => rsa.Dispose();
    }
}
