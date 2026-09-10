using System.Text;
using System.Text.Json;
using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class ApiDebugPageViewModelTests
{
    [Fact]
    public async Task EndpointSelectionBuildsDynamicParametersAndUsesDefaults()
    {
        await using var fixture = new Fixture();
        var viewModel = fixture.CreateViewModel();
        viewModel.SelectedApi = viewModel.Definitions.Single(item => item.Key == "getCommentByUrl");

        Assert.False(viewModel.HasRawBody);
        Assert.Equal("json", Parameter(viewModel, "format").Value);
        Assert.Equal("true", Parameter(viewModel, "duration").Value);
        Assert.Equal(string.Empty, Parameter(viewModel, "segmentflag").Value);

        Parameter(viewModel, "url").Value = "https://example.invalid/video";
        Parameter(viewModel, "segmentflag").Value = "true";
        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal("getCommentByUrl", fixture.Client.LastApiKey);
        Assert.Equal("https://example.invalid/video", fixture.Client.LastParameters!["url"]);
        Assert.Equal("true", fixture.Client.LastParameters["segmentflag"]);
        Assert.Contains("HTTP 200", viewModel.ResponseMeta, StringComparison.Ordinal);
        Assert.Contains("42 ms", viewModel.ResponseMeta, StringComparison.Ordinal);
        Assert.DoesNotContain("example.invalid", viewModel.RequestPreview, StringComparison.Ordinal);
        Assert.DoesNotContain("example.invalid", viewModel.CurlText, StringComparison.Ordinal);
        Assert.Contains("url=***", viewModel.RequestPreview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SegmentCurlAndResponseRedactSensitiveFields()
    {
        await using var fixture = new Fixture();
        fixture.Client.ResponseBody = "{\"success\":true,\"token\":\"response-secret\",\"comments\":[]}";
        var viewModel = fixture.CreateViewModel();
        viewModel.SelectedApi = viewModel.Definitions.Single(item => item.Key == "getSegmentComment");
        viewModel.JsonBody = "{\"type\":\"qq\",\"segment_start\":0,\"segment_end\":30000,\"url\":\"https://example.invalid/segment\",\"_m_h5_tk\":\"request-secret\"}";

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasRawBody);
        Assert.DoesNotContain("request-secret", viewModel.CurlText, StringComparison.Ordinal);
        Assert.DoesNotContain("response-secret", viewModel.ResponseText, StringComparison.Ordinal);
        Assert.Contains("***", viewModel.CurlText, StringComparison.Ordinal);
        Assert.Contains("***", viewModel.ResponseText, StringComparison.Ordinal);
        Assert.Contains("<TOKEN>", viewModel.CurlText, StringComparison.Ordinal);
        Assert.DoesNotContain("ordinary-secret", viewModel.CurlText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fongmiGet", "name", ApiParameterKind.Query, false)]
    [InlineData("fongmiPost", "name", ApiParameterKind.Json, true)]
    [InlineData("danmakuGet", "name", ApiParameterKind.Query, false)]
    [InlineData("danmakuPost", "name", ApiParameterKind.Json, true)]
    public async Task CompatibilityEndpointsExposeCoreContract(string key, string requiredName, ApiParameterKind kind, bool hasBody)
    {
        await using var fixture = new Fixture();
        var viewModel = fixture.CreateViewModel();

        viewModel.SelectedApi = viewModel.Definitions.Single(item => item.Key == key);

        Assert.Equal(hasBody, viewModel.SelectedApi.HasBody);
        Assert.Collection(viewModel.SelectedApi.Parameters,
            name =>
            {
                Assert.Equal(requiredName, name.Name);
                Assert.True(name.Required);
                Assert.Equal(kind, name.Kind);
            },
            episode =>
            {
                Assert.Equal("episode", episode.Name);
                Assert.False(episode.Required);
                Assert.Equal(kind, episode.Kind);
            });
    }

    [Theory]
    [InlineData("fongmiPost")]
    [InlineData("danmakuPost")]
    public async Task CompatibilityPostBuildsNameAndOptionalEpisodeJson(string key)
    {
        await using var fixture = new Fixture();
        var viewModel = fixture.CreateViewModel();
        viewModel.SelectedApi = viewModel.Definitions.Single(item => item.Key == key);
        Parameter(viewModel, "name").Value = "测试番剧";
        Parameter(viewModel, "episode").Value = "12";

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        using var body = JsonDocument.Parse(fixture.Client.LastJsonBody!);
        Assert.Equal("测试番剧", body.RootElement.GetProperty("name").GetString());
        Assert.Equal("12", body.RootElement.GetProperty("episode").GetString());
        Assert.False(body.RootElement.TryGetProperty("fileName", out _));
    }

    [Fact]
    public async Task LongResponseUsesPreviewAndCanExpandWithoutLosingBody()
    {
        await using var fixture = new Fixture();
        fixture.Client.ResponseBody = JsonSerializer.Serialize(new { payload = new string('x', 5000) });
        var viewModel = fixture.CreateViewModel();
        Parameter(viewModel, "keyword").Value = "test";

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.True(viewModel.ResponseText.Length > 4000);
        Assert.Equal(4000, viewModel.ResponsePreview.Length);
        Assert.Equal(viewModel.ResponsePreview, viewModel.ResponseDisplay);

        viewModel.ToggleResponseCommand.Execute(null);

        Assert.Equal(viewModel.ResponseText, viewModel.ResponseDisplay);
    }

    [Fact]
    public async Task SwitchingEndpointCancelsAndIgnoresPreviousResponse()
    {
        await using var fixture = new Fixture(blockFirstRequest: true);
        var viewModel = fixture.CreateViewModel();
        Parameter(viewModel, "keyword").Value = "first";
        var first = viewModel.ExecuteCommand.ExecuteAsync(null);
        await fixture.Client.FirstRequestEntered.Task;

        viewModel.SelectedApi = viewModel.Definitions.Single(item => item.Key == "getBangumi");
        Parameter(viewModel, "animeId").Value = "7";
        await viewModel.ExecuteCommand.ExecuteAsync(null);
        fixture.Client.ReleaseFirstRequest();
        await first;

        Assert.Contains("/api/v2/bangumi/7", viewModel.RequestPreview, StringComparison.Ordinal);
        Assert.DoesNotContain("first response", viewModel.ResponseText, StringComparison.Ordinal);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task InvalidJsonResponseDoesNotExposeUnstructuredBody()
    {
        await using var fixture = new Fixture();
        fixture.Client.ResponseBody = "invalid-json private-unstructured-secret";
        var viewModel = fixture.CreateViewModel();
        Parameter(viewModel, "keyword").Value = "test";

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.Contains("JSON", viewModel.ResponseText, StringComparison.Ordinal);
        Assert.Contains("未显示原文", viewModel.ResponseText, StringComparison.Ordinal);
        Assert.DoesNotContain("private-unstructured-secret", viewModel.ResponseText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonJsonResponseRedactsRuntimeToken()
    {
        await using var fixture = new Fixture();
        fixture.Client.ResponseBody = $"response {fixture.RuntimeToken}";
        fixture.Client.ResponseContentType = "text/plain";
        var viewModel = fixture.CreateViewModel();
        Parameter(viewModel, "keyword").Value = "test";

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.DoesNotContain(fixture.RuntimeToken, viewModel.ResponseText, StringComparison.Ordinal);
        Assert.Contains("***", viewModel.ResponseText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawResponseBufferIsPrivateAndClearedWithVisibleResponse()
    {
        await using var fixture = new Fixture();
        fixture.Client.ResponseBody = "{\"token\":\"private-raw-value\",\"success\":true}";
        var viewModel = fixture.CreateViewModel();
        Parameter(viewModel, "keyword").Value = "test";

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        Assert.True(viewModel.RawResponseByteCount > 0);
        Assert.DoesNotContain("private-raw-value", viewModel.ResponseText, StringComparison.Ordinal);
        Assert.Contains("脱敏响应副本", viewModel.Diagnostic, StringComparison.Ordinal);

        viewModel.ClearResponseCommand.Execute(null);

        Assert.Equal(0, viewModel.RawResponseByteCount);
        Assert.False(viewModel.HasResponse);
    }

    [Fact]
    public async Task SwitchingEndpointClearsPrivateRawResponseBuffer()
    {
        await using var fixture = new Fixture();
        var viewModel = fixture.CreateViewModel();
        Parameter(viewModel, "keyword").Value = "test";
        await viewModel.ExecuteCommand.ExecuteAsync(null);
        Assert.True(viewModel.RawResponseByteCount > 0);

        viewModel.SelectedApi = viewModel.Definitions.Single(item => item.Key == "getBangumi");

        Assert.Equal(0, viewModel.RawResponseByteCount);
        Assert.False(viewModel.HasResponse);
    }

    private static ApiParameterValueViewModel Parameter(ApiDebugPageViewModel viewModel, string name) =>
        viewModel.ParameterValues.Single(item => item.Name == name);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        private readonly RuntimeApiContext _context;
        private readonly RecordingDialogService _dialogs = new();
        private readonly RecordingDiagnostics _diagnostics = new();

        public Fixture(bool blockFirstRequest = false)
        {
            var paths = new AppPaths(_directory.Path, Path.Combine(_directory.Path, "appdata"));
            _context = new RuntimeApiContext(paths, new TestRuntimeController(), new StubAdminSessionService());
            Client = new FakeClient(blockFirstRequest);
        }

        public FakeClient Client { get; }
        public string RuntimeToken => _context.Token;

        public ApiDebugPageViewModel CreateViewModel() => new(_context, Client, _dialogs, _diagnostics);

        public ValueTask DisposeAsync()
        {
            _directory.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeClient(bool blockFirstRequest) : IDanmuApiClient
    {
        private readonly TaskCompletionSource _releaseFirstRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;
        public TaskCompletionSource FirstRequestEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ResponseBody { get; set; } = "{\"success\":true}";
        public string ResponseContentType { get; set; } = "application/json";
        public string? LastApiKey { get; private set; }
        public IReadOnlyDictionary<string, string?>? LastParameters { get; private set; }
        public string? LastJsonBody { get; private set; }

        public void ReleaseFirstRequest() => _releaseFirstRequest.TrySetResult();

        public async Task<DanmuRawApiResponse> SendRawAsync(string host, int port, string? token, string apiKey, IReadOnlyDictionary<string, string?> parameters, string? jsonBody = null, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (blockFirstRequest && call == 1)
            {
                FirstRequestEntered.TrySetResult();
                try
                {
                    await _releaseFirstRequest.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException error)
                {
                    throw new DanmuApiException(DanmuApiFailureKind.Cancelled, "cancelled", error);
                }
            }

            LastApiKey = apiKey;
            LastParameters = new Dictionary<string, string?>(parameters);
            LastJsonBody = jsonBody;
            var body = call == 1 && blockFirstRequest ? "{\"message\":\"first response\"}" : ResponseBody;
            var path = apiKey switch
            {
                "getBangumi" => $"/api/v2/bangumi/{parameters["animeId"]}",
                "getCommentByUrl" => "/api/v2/comment?url=https%3A%2F%2Fexample.invalid%2Fvideo&format=json&duration=true&segmentflag=true",
                "getSegmentComment" => "/api/v2/segmentcomment?format=json",
                _ => "/api/v2/search/anime?keyword=test",
            };
            return new DanmuRawApiResponse(200, ResponseContentType, Encoding.UTF8.GetBytes(body), body, path, TimeSpan.FromMilliseconds(42));
        }

        public Task<DanmuDownloadPayload> DownloadCommentAsync(string host, int port, string? token, long episodeId, string formatValue, TimeSpan? requestTimeout = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DanmuSearchAnimeResult> SearchAnimeAsync(string host, int port, string? token, string keyword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DanmuSearchEpisodesResult> SearchEpisodesAsync(string host, int port, string? token, string anime, string? episode = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DanmuMatchResult> MatchAsync(string host, int port, string? token, string fileName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DanmuBangumiResult> GetBangumiAsync(string host, int port, string? token, int animeId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DanmuResult> GetCommentAsync(string host, int port, string? token, int commentId, bool includeDuration = true, string format = "json", CancellationToken cancellationToken = default, bool segmentFlag = false) => throw new NotSupportedException();
        public Task<DanmuResult> GetCommentByUrlAsync(string host, int port, string? token, string videoUrl, bool includeDuration = true, string format = "json", CancellationToken cancellationToken = default, bool segmentFlag = false) => throw new NotSupportedException();
        public Task<DanmuResult> GetSegmentCommentAsync(string host, int port, string? token, JsonElement segment, string format = "json", CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DanmuFavoriteListResult> GetFavoritesAsync(string host, int port, string? token, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> AddFavoriteAsync(string host, int port, string? token, string? adminToken, string keyword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> RemoveFavoriteAsync(string host, int port, string? token, string? adminToken, string keyword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> RefreshFavoriteAsync(string host, int port, string? token, string? adminToken, string keyword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> SetFavoriteScheduleAsync(string host, int port, string? token, string? adminToken, string keyword, DanmuFavoriteSchedule? schedule, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TestRuntimeController : IRuntimeController
    {
        public RuntimeSnapshot Snapshot { get; } = new(DesktopRuntimeState.Running, 9321, 1, "test");
        event EventHandler<RuntimeSnapshot>? IRuntimeController.SnapshotChanged { add { } remove { } }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AdoptionResult> AdoptAsync(CancellationToken cancellationToken = default) => Task.FromResult(AdoptionResult.Success(Snapshot));
        public string? ReconcileLiveness() => null;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RestartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingDiagnostics : IAppDiagnostics
    {
        public string? LastDiagnostic { get; private set; }
        public void Record(string message, Exception? error = null) => LastDiagnostic = message;
    }
}
