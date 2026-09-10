using System.Net;
using DanmuApi.App.Services;

namespace DanmuApi.Tests;

public sealed class PosterImageServiceTests
{
    [Fact]
    public async Task HttpFailureRecordsSanitizedHostAndStatus()
    {
        var diagnostics = new RecordingDiagnostics();
        using var httpClient = new HttpClient(new StubHandler(
            new HttpResponseMessage(HttpStatusCode.NotFound)));
        var service = new PosterImageService(httpClient, diagnostics);

        var poster = await service.LoadAsync("https://poster.example.invalid/image.jpg?token=secret-value");

        Assert.Null(poster);
        Assert.True(
            diagnostics.LastDiagnostic?.Contains("HTTP 404", StringComparison.Ordinal) == true,
            $"diagnostic={diagnostics.LastDiagnostic}; error={diagnostics.LastErrorMessage}");
        Assert.Contains("poster.example.invalid", diagnostics.LastDiagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", diagnostics.LastDiagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("token=", diagnostics.LastDiagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidUrlRecordsExplicitDiagnostic()
    {
        var diagnostics = new RecordingDiagnostics();
        using var httpClient = new HttpClient(new StubHandler(
            new HttpResponseMessage(HttpStatusCode.OK)));
        var service = new PosterImageService(httpClient, diagnostics);

        var poster = await service.LoadAsync("file:///private/poster.jpg");

        Assert.Null(poster);
        Assert.Contains("不是 http/https", diagnostics.LastDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedLoadIsNotCached()
    {
        var diagnostics = new RecordingDiagnostics();
        var handler = new CountingHandler();
        using var httpClient = new HttpClient(handler);
        var service = new PosterImageService(httpClient, diagnostics);
        const string url = "https://poster.example.invalid/image.jpg";

        Assert.Null(await service.LoadAsync(url));
        Assert.Null(await service.LoadAsync(url));

        Assert.Equal(2, handler.RequestCount);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response);
    }

    private sealed class RecordingDiagnostics : IAppDiagnostics
    {
        public string? LastDiagnostic { get; private set; }
        public string? LastErrorMessage { get; private set; }

        public void Record(string message, Exception? error = null)
        {
            LastDiagnostic = error is null ? message : $"{message}: {error.GetType().Name}";
            LastErrorMessage = error?.Message;
        }
    }
}
