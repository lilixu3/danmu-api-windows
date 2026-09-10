using System.Net;
using Avalonia.Controls;
using System.Text.Json;
using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class CoreRequestRecordsClientTests
{
    [Fact]
    public async Task ReadUsesTokenPathAndParsesCoreResponseStrictly()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler((request, _) =>
        {
            captured = CloneRequest(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "records": [
                        {
                          "interface": "/api/v2/search/anime?keyword=***",
                          "params": { "name": "***" },
                          "timestamp": "2026-09-02T01:02:03.000Z",
                          "method": "POST",
                          "clientIp": "**.*.*.**"
                        }
                      ],
                      "todayReqNum": 7
                    }
                    """),
            });
        });
        var client = new CoreRequestRecordsClient(new HttpClient(handler), TimeSpan.FromSeconds(1));

        var result = await client.ReadAsync("127.0.0.1", 9321, "custom token");

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal(7, result.TodayRequestCount);
        var record = Assert.Single(result.Records);
        Assert.Equal("POST", record.Method);
        Assert.Equal("/api/v2/search/anime?keyword=***", record.Interface);
        Assert.NotNull(record.ParametersJson);
        using (var parameters = System.Text.Json.JsonDocument.Parse(record.ParametersJson!))
        {
            Assert.Equal("***", parameters.RootElement.GetProperty("name").GetString());
        }
        Assert.NotNull(captured);
        Assert.Equal("/custom%20token/api/reqrecords", captured!.RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData("{}", "records")]
    [InlineData("{\"records\":{},\"todayReqNum\":0}", "records")]
    [InlineData("{\"records\":[],\"todayReqNum\":-1}", "todayReqNum")]
    [InlineData("{\"records\":[{\"interface\":42,\"method\":\"GET\"}],\"todayReqNum\":1}", "interface")]
    public void InvalidProtocolFailsExplicitly(string json, string expected)
    {
        var result = CoreRequestRecordsClient.ParseResponse(System.Text.Encoding.UTF8.GetBytes(json));

        Assert.False(result.Succeeded);
        Assert.Contains(expected, result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NetworkFailureRedactsToken()
    {
        const string token = "request-record-secret";
        var client = new CoreRequestRecordsClient(
            new HttpClient(new StubHandler((_, _) => throw new HttpRequestException($"failed {token}"))),
            TimeSpan.FromSeconds(1));

        var result = await client.ReadAsync("127.0.0.1", 9321, token);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(token, result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("***", result.Diagnostic, StringComparison.Ordinal);
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request) =>
        new(request.Method, request.RequestUri);

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responder(request, cancellationToken);
    }
}

public sealed class RequestRecordsPageViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"danmu-requests-{Guid.NewGuid():N}");

    [Fact]
    public async Task RefreshUsesRuntimeTokenAndMapsRecords()
    {
        var paths = CreateRuntime("access-token");
        var client = new RecordingClient(CoreRequestRecordsReadResult.Success(
            [new CoreRequestRecord("/api/v2/search/anime", "{\"name\":\"***\"}", "2026-09-02T01:02:03Z", "POST", "**.*.*.**")],
            12));
        await using var viewModel = new RequestRecordsPageViewModel(
            paths,
            client,
            new LocalRequestRecordStore(),
            new StubRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 9)),
            new RecordingDialogService(),
            new RecordingDiagnostics());

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("access-token", client.Token);
        Assert.Equal(12, viewModel.TodayRequestCount);
        Assert.Contains("12", viewModel.TodayRequestCountText, StringComparison.Ordinal);
        var item = Assert.Single(viewModel.Records);
        Assert.Equal("POST", item.Method);
        Assert.Contains("name", item.ParametersDisplay, StringComparison.Ordinal);
        Assert.False(viewModel.HasDiagnostic);
    }

    [Fact]
    public async Task StoppedServiceReportsReasonWithoutCallingApi()
    {
        var paths = CreateRuntime("unused");
        var client = new RecordingClient(CoreRequestRecordsReadResult.Success([], 0));
        await using var viewModel = new RequestRecordsPageViewModel(
            paths,
            client,
            new LocalRequestRecordStore(),
            new StubRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped)),
            new RecordingDialogService(),
            new RecordingDiagnostics());

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(0, client.Calls);
        Assert.Contains("服务未运行", viewModel.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientFailureRemainsVisibleAndRecordsDiagnostic()
    {
        var paths = CreateRuntime("access-token");
        var diagnostics = new RecordingDiagnostics();
        await using var viewModel = new RequestRecordsPageViewModel(
            paths,
            new RecordingClient(CoreRequestRecordsReadResult.Failure("核心请求记录接口返回 HTTP 500")),
            new LocalRequestRecordStore(),
            new StubRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 9)),
            new RecordingDialogService(),
            diagnostics);

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Contains("HTTP 500", viewModel.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("HTTP 500", diagnostics.LastDiagnostic ?? string.Empty, StringComparison.Ordinal);
        Assert.True(viewModel.HasDiagnostic);
    }

    [Fact]
    public async Task StoppedServiceStillDisplaysLocalFailures()
    {
        var paths = CreateRuntime("unused");
        var local = new LocalRequestRecordStore();
        local.Add(new LocalRequestRecord(
            1,
            DateTimeOffset.UtcNow,
            "弹幕测试/手动搜索",
            "GET",
            "/api/v2/search/anime?keyword=***",
            string.Empty,
            null,
            15,
            false,
            "连接失败",
            string.Empty));
        var client = new RecordingClient(CoreRequestRecordsReadResult.Success([], 0));
        await using var viewModel = new RequestRecordsPageViewModel(
            paths,
            client,
            local,
            new StubRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped)),
            new RecordingDialogService(),
            new RecordingDiagnostics());

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(0, client.Calls);
        var item = Assert.Single(viewModel.Records);
        Assert.Equal("连接失败", item.ErrorMessage);
        Assert.Equal("弹幕测试/手动搜索", item.Source);
        Assert.Contains("服务未运行", viewModel.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void MergeDeduplicatesNearbyLocalAndCoreRecordsAndMasksRemoteValues()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var local = new LocalRequestRecord(
            1,
            timestamp,
            "弹幕测试/手动搜索",
            "GET",
            "/api/v2/search/anime?keyword=***",
            string.Empty,
            200,
            4,
            true,
            null,
            "{\"success\":\"***\"}");
        var remote = new CoreRequestRecord(
            "/api/v2/search/anime?keyword=private-keyword",
            "{\"fileName\":\"private-file\"}",
            timestamp.AddSeconds(1).ToString("O"),
            "GET",
            "192.168.1.21");

        var merged = RequestRecordsPageViewModel.MergeRecords([remote], [local]);

        var item = Assert.Single(merged);
        Assert.Equal("弹幕测试/手动搜索", item.Source);
        var text = JsonSerializer.Serialize(item);
        Assert.DoesNotContain("private-keyword", text, StringComparison.Ordinal);
        Assert.DoesNotContain("private-file", text, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.1.21", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MergeRetainsUnmatchedCoreRequestWithDefenseInDepthRedaction()
    {
        var remote = new CoreRequestRecord(
            "/api/v2/comment?url=https%3A%2F%2Fexample.invalid%2Fprivate",
            "{\"token\":\"private-token\",\"segment\":17}",
            "2026-09-02T01:02:03Z",
            "POST",
            "2001:db8::9");

        var item = Assert.Single(RequestRecordsPageViewModel.MergeRecords([remote], []));

        Assert.Equal("/api/v2/comment?url=***", item.Interface);
        Assert.Contains("token", item.Parameters, StringComparison.Ordinal);
        var text = JsonSerializer.Serialize(item);
        Assert.DoesNotContain("example.invalid", text, StringComparison.Ordinal);
        Assert.DoesNotContain("private-token", text, StringComparison.Ordinal);
        Assert.DoesNotContain("2001:db8::9", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestRecordFiltersCombineQueryOutcomeMethodAndTime()
    {
        var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var records = new[]
        {
            NewListItem("/api/v2/search/anime?keyword=***", "GET", now.AddMinutes(-20), true, 120),
            NewListItem("/api/v2/comment/42", "GET", now.AddMinutes(-10), false, 2_300, "协议失败"),
            NewListItem("/api/v2/favorite/add", "POST", now.AddHours(-2), true, 80),
            NewListItem("/api/v2/remote", "GET", now.AddMinutes(-5), null, null),
        };

        var failed = RequestRecordAnalysis.Filter(
            records,
            "协议失败",
            RequestOutcomeFilter.Failure,
            "GET",
            RequestRecordTimeRange.LastHour,
            now);
        var successful = RequestRecordAnalysis.Filter(
            records,
            string.Empty,
            RequestOutcomeFilter.Success,
            null,
            RequestRecordTimeRange.LastHour,
            now);

        Assert.Single(failed);
        Assert.Equal("/api/v2/comment/42", failed[0].Interface);
        Assert.Single(successful);
        Assert.Equal("/api/v2/search/anime?keyword=***", successful[0].Interface);
        Assert.DoesNotContain(successful, record => record.Success is null);
    }

    [Fact]
    public void RequestRecordInsightsUseOnlyKnownOutcomesAndAvailableDurations()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var records = new[]
        {
            NewListItem("/api/v2/search/anime", "GET", timestamp, true, 100, clientOrdinal: 0),
            NewListItem("/api/v2/comment/1", "GET", timestamp, false, 2_000, "failed", 0),
            NewListItem("/api/v2/comment/2", "GET", timestamp, false, 3_000, "failed", 1),
            NewListItem("/api/v2/remote", "POST", timestamp, null, null, clientOrdinal: 1),
        };

        var insights = RequestRecordAnalysis.Summarize(records);

        Assert.Equal(4, insights.Total);
        Assert.Equal(3, insights.KnownOutcomeCount);
        Assert.Equal(66, insights.FailureRatePercent);
        Assert.Equal(3_000, insights.P95DurationMilliseconds);
        Assert.Equal(2, insights.UniqueClients);
        Assert.Equal(2, insights.SlowCount);
        Assert.Equal("/api/v2/comment/1", insights.TopFailureEndpoint);
        Assert.Equal(1, insights.TopFailureCount);
    }

    [Fact]
    public async Task CopyUrlUsesOnlyRedactedInterface()
    {
        var paths = CreateRuntime("unused");
        var dialogs = new RecordingDialogService();
        await using var viewModel = new RequestRecordsPageViewModel(
            paths,
            new RecordingClient(CoreRequestRecordsReadResult.Success([], 0)),
            new LocalRequestRecordStore(),
            new StubRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped)),
            dialogs,
            new RecordingDiagnostics());
        var record = NewListItem("/api/v2/comment?url=***", "GET", DateTimeOffset.UtcNow, false, 1, "failed");

        await viewModel.CopyUrlCommand.ExecuteAsync(record);

        Assert.Equal("/api/v2/comment?url=***", dialogs.CopiedText);
    }

    [Avalonia.Headless.XUnit.AvaloniaTheory]
    [InlineData(1100, 800, false)]
    [InlineData(1100, 800, true)]
    [InlineData(640, 740, false)]
    [InlineData(640, 740, true)]
    public async Task RequestWorkspaceAdaptsAndKeepsUnknownDetails(int width, int height, bool dark)
    {
        var paths = CreateRuntime("unused");
        var client = new RecordingClient(CoreRequestRecordsReadResult.Success(
            [new CoreRequestRecord("/api/v2/search/anime?keyword=private", "{\"token\":\"private\",\"keyword\":\"private\"}", "2026-09-08T10:20:30Z", "GET", "192.168.1.2")], 24));
        await using var model = new RequestRecordsPageViewModel(paths, client, new LocalRequestRecordStore(),
            new StubRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 9)), new RecordingDialogService(), new RecordingDiagnostics());
        await model.RefreshCommand.ExecuteAsync(null);
        model.SelectedOutcomeFilter = model.OutcomeFilters.Single(option => option.Value == RequestOutcomeFilter.Unknown);
        model.SelectedRecord = Assert.Single(model.Records);
        Assert.Null(model.SelectedRecord.Success);
        Assert.Equal("结果未提供", model.SelectedRecord.OutcomeDisplay);
        Assert.Equal("未提供", model.FailureRateText);
        Assert.DoesNotContain("private", model.SelectedRecord.ParametersDisplay);
        var view = new DanmuApi.App.Views.RequestRecordsView { DataContext = model };
        var window = new Avalonia.Controls.Window
        {
            Width = width, Height = height, Padding = new Avalonia.Thickness(20), Content = view,
            RequestedThemeVariant = dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light,
        };
        window.Show();
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var details = view.FindControl<Avalonia.Controls.Border>("RecordDetailsPanel")!;
            var table = view.FindControl<Avalonia.Controls.Border>("RecordTablePanel")!;
            Assert.Equal(width < 860 ? 1 : 0, Avalonia.Controls.Grid.GetRow(details));
            Assert.Equal(width < 860 ? 0 : 1, Avalonia.Controls.Grid.GetColumn(details));
            Assert.True(details.Bounds.Width > 0 && table.Bounds.Width > 0);
            Assert.True(details.Bounds.Right <= view.Bounds.Width);
            var directory = Environment.GetEnvironmentVariable("DANMU_TEST_RENDER_DIRECTORY");
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(width, height));
                bitmap.Render(window);
                bitmap.Save(Path.Combine(directory, $"requests-{width}-{(dark ? "dark" : "light")}.png"));
            }
            await model.RefreshCommand.ExecuteAsync(null);
            Assert.True(model.HasSelectedRecord);
            model.SelectedOutcomeFilter = model.OutcomeFilters.Single(option => option.Value == RequestOutcomeFilter.Success);
            Assert.Empty(model.Records);
            Assert.False(model.HasSelectedRecord);
        }
        finally { window.Close(); }
    }

    private static RequestRecordListItem NewListItem(
        string interfacePath,
        string method,
        DateTimeOffset timestamp,
        bool? success,
        long? duration,
        string error = "",
        int? clientOrdinal = null) => new(
            interfacePath,
            method,
            timestamp,
            clientOrdinal is null ? "本地" : "核心请求 · ***.***.***.***",
            string.Empty,
            success is null ? null : success == true ? 200 : 500,
            duration,
            success,
            error,
            string.Empty,
            clientOrdinal);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AppPaths CreateRuntime(string token)
    {
        var paths = new AppPaths(_root, Path.Combine(_root, "appdata"));
        Directory.CreateDirectory(Path.Combine(paths.NodeProjectDirectory, "config"));
        File.WriteAllText(
            Path.Combine(paths.NodeProjectDirectory, "config", ".env"),
            $"TOKEN={token}{Environment.NewLine}");
        return paths;
    }

    private sealed class RecordingClient(CoreRequestRecordsReadResult result) : ICoreRequestRecordsClient
    {
        public int Calls { get; private set; }
        public string? Token { get; private set; }

        public Task<CoreRequestRecordsReadResult> ReadAsync(
            string host,
            int port,
            string? token,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Token = token;
            return Task.FromResult(result);
        }
    }

    private sealed class StubRuntimeController(RuntimeSnapshot snapshot) : IRuntimeController
    {
        public RuntimeSnapshot Snapshot { get; } = snapshot;
        event EventHandler<RuntimeSnapshot>? IRuntimeController.SnapshotChanged
        {
            add { }
            remove { }
        }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AdoptionResult> AdoptAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(AdoptionResult.Failure(AdoptionFailureKind.NotFound, Snapshot, "unused"));
        public string? ReconcileLiveness() => null;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RestartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingDiagnostics : IAppDiagnostics
    {
        public string? LastDiagnostic { get; private set; }
        public void Record(string message, Exception? error = null) =>
            LastDiagnostic = error is null ? message : $"{message}: {error.Message}";
    }
}
