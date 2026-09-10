using System.Net;
using System.Text;
using System.Text.Json;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class DanmuApiClientTests
{
    [Fact]
    public void BuildApiUriEscapesTokenAndFormatsIpv6Authority()
    {
        var uri = DanmuApiClient.BuildApiUri(
            "2001:4860:4860::8888",
            9321,
            "token/with space",
            "/api/v2/search/anime?keyword=test");

        Assert.Equal(
            "http://[2001:4860:4860::8888]:9321/token%2Fwith%20space/api/v2/search/anime?keyword=test",
            uri.AbsoluteUri);
    }

    [Fact]
    public async Task UsesFallbackTokenWhenTokenIsMissing()
    {
        Uri? observed = null;
        using var handler = new RecordingHandler((request, _) =>
        {
            observed = request.RequestUri;
            return Task.FromResult(Json("{\"success\":true,\"animes\":[]}"));
        });
        var client = new DanmuApiClient(new HttpClient(handler));

        var result = await client.SearchAnimeAsync("127.0.0.1", 9321, null, "test");

        Assert.True(result.Success);
        Assert.Equal("/87654321/api/v2/search/anime?keyword=test", observed?.PathAndQuery);
    }

    [Fact]
    public async Task DoesNotSendRequestWhenRuntimeIsNotRunning()
    {
        var controller = new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped, 9321));
        using var handler = new RecordingHandler((_, _) =>
            Task.FromResult(Json("{\"success\":true,\"animes\":[]}")));
        var client = new DanmuApiClient(new HttpClient(handler), runtimeController: controller);

        var error = await Assert.ThrowsAsync<DanmuApiException>(() =>
            client.SearchAnimeAsync("127.0.0.1", 9321, "token", "test"));

        Assert.Equal(DanmuApiFailureKind.ServiceNotRunning, error.Kind);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, DanmuApiFailureKind.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, DanmuApiFailureKind.Authentication)]
    [InlineData(HttpStatusCode.NotFound, DanmuApiFailureKind.NotFound)]
    [InlineData(HttpStatusCode.BadRequest, DanmuApiFailureKind.Http)]
    public async Task MapsHttpFailures(HttpStatusCode status, DanmuApiFailureKind expected)
    {
        using var handler = new RecordingHandler((_, _) =>
            Task.FromResult(Json("{\"message\":\"ordinary-secret\"}", status)));
        var client = new DanmuApiClient(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<DanmuApiException>(() =>
            client.GetBangumiAsync("127.0.0.1", 9321, "ordinary-secret", 12));

        Assert.Equal(expected, error.Kind);
        Assert.Equal((int)status, error.StatusCode);
        Assert.DoesNotContain("ordinary-secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MapsConnectionFailureAndRedactsToken()
    {
        using var handler = new RecordingHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("socket failed ordinary-secret")));
        var client = new DanmuApiClient(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<DanmuApiException>(() =>
            client.SearchAnimeAsync("127.0.0.1", 9321, "ordinary-secret", "test"));

        Assert.Equal(DanmuApiFailureKind.Connection, error.Kind);
        Assert.DoesNotContain("ordinary-secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DistinguishesTimeoutFromCallerCancellation()
    {
        using var timeoutHandler = new RecordingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Json("{\"success\":true,\"animes\":[]}");
        });
        var timeoutClient = new DanmuApiClient(
            new HttpClient(timeoutHandler),
            TimeSpan.FromMilliseconds(25));

        var timeout = await Assert.ThrowsAsync<DanmuApiException>(() =>
            timeoutClient.SearchAnimeAsync("127.0.0.1", 9321, "token", "test"));
        Assert.Equal(DanmuApiFailureKind.Timeout, timeout.Kind);

        using var cancellationHandler = new RecordingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Json("{\"success\":true,\"animes\":[]}");
        });
        var cancellationClient = new DanmuApiClient(
            new HttpClient(cancellationHandler),
            TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var request = cancellationClient.SearchAnimeAsync(
            "127.0.0.1", 9321, "token", "test", cancellation.Token);
        await cancellationHandler.Entered.Task;
        cancellation.Cancel();

        var cancelled = await Assert.ThrowsAsync<DanmuApiException>(() => request);
        Assert.Equal(DanmuApiFailureKind.Cancelled, cancelled.Kind);
    }

    [Fact]
    public async Task RejectsResponseLargerThanEightMegabytes()
    {
        using var handler = new RecordingHandler((_, _) =>
            Task.FromResult(Json(
                "{\"success\":true,\"animes\":[]}" + new string('x', 8 * 1024 * 1024))));
        var client = new DanmuApiClient(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<DanmuApiException>(() =>
            client.SearchAnimeAsync("127.0.0.1", 9321, "token", "test"));

        Assert.Equal(DanmuApiFailureKind.ResponseTooLarge, error.Kind);
    }

    [Fact]
    public void ParsesSearchEpisodesWrapperWithLightweightEpisodes()
    {
        var result = DanmuApiClient.ParseSearchEpisodes(Encoding.UTF8.GetBytes(
            "{\"errorCode\":0,\"success\":true,\"errorMessage\":\"\",\"animes\":[{\"animeId\":123,\"animeTitle\":\"生万物\",\"type\":\"tv\",\"typeDescription\":\"电视剧\",\"episodes\":[{\"episodeId\":1,\"episodeTitle\":\"第1集\",\"url\":\"https://example.invalid/1\"}]}]}"));

        var anime = Assert.Single(result.Animes);
        var episode = Assert.Single(anime.Episodes);
        Assert.Equal(123, anime.AnimeId);
        Assert.Equal(1, episode.EpisodeId);
        Assert.Equal("第1集", episode.EpisodeTitle);
    }

    [Fact]
    public void ParsesFavoriteNumericTimestampsAndScheduleFields()
    {
        var result = DanmuApiClient.ParseFavorites(Encoding.UTF8.GetBytes(
            "{\"success\":true,\"favoriteSupported\":true,\"scheduledRefreshSupported\":true,\"favorites\":[{\"keyword\":\"生万物\",\"animeTitle\":\"生万物\",\"source\":\"qq\",\"sources\":[\"qq\"],\"imageUrl\":\"\",\"episodeCount\":40,\"resultsCount\":1,\"timestamp\":1700000000000,\"lastRefreshAt\":1700000001000,\"refreshSchedule\":{\"frequency\":\"daily\",\"time\":\"09:00\",\"timezone\":\"Asia/Shanghai\",\"nextRunAt\":1700003600000,\"retryAt\":null,\"lastRunAt\":1700000001000,\"lastStatus\":\"success\",\"lastError\":\"\"}}]}"));

        var favorite = Assert.Single(result.Favorites);
        Assert.Equal(1700000000000L, favorite.Timestamp);
        Assert.Equal(1700000001000L, favorite.LastRefreshAt);
        Assert.NotNull(favorite.RefreshSchedule);
        Assert.Equal(1700003600000L, favorite.RefreshSchedule!.NextRunAt);
        Assert.Null(favorite.RefreshSchedule.RetryAt);
        Assert.Equal("Asia/Shanghai", favorite.RefreshSchedule.Timezone);
    }

    [Fact]
    public async Task DisablingFavoriteScheduleWritesExplicitNull()
    {
        string? payload = null;
        using var handler = new RecordingHandler(async (request, _) =>
        {
            payload = await request.Content!.ReadAsStringAsync();
            return Json("{\"success\":true,\"message\":\"disabled\"}");
        });
        var client = new DanmuApiClient(new HttpClient(handler));

        await client.SetFavoriteScheduleAsync("127.0.0.1", 9321, "token", "admin", "生万物", null);

        using var document = JsonDocument.Parse(payload!);
        Assert.True(document.RootElement.TryGetProperty("schedule", out var schedule));
        Assert.Equal(JsonValueKind.Null, schedule.ValueKind);
    }

    [Fact]
    public void ParsesSegmentCommentAsUnifiedDanmuResponse()
    {
        var result = DanmuApiClient.ParseSegmentCommentJson(Encoding.UTF8.GetBytes(
            "{\"success\":true,\"count\":1,\"comments\":[{\"p\":\"1.5,1,16711680,[qq]\",\"m\":\"hello\",\"cid\":9,\"like\":2,\"color_v2\":\"1-2\"}]}"));

        var comment = Assert.Single(result.Comments);
        Assert.Equal(9L, comment.Cid);
        Assert.Equal(2L, comment.Like);
        Assert.Equal("qq", comment.Source);
        Assert.Equal("1-2", comment.ColorV2);
    }

    [Fact]
    public void ParsesCommentPositionAsFloatingPointAndPreservesMetadata()
    {
        var result = DanmuApiClient.ParseCommentXml(Encoding.UTF8.GetBytes(
            "<i><d p=\"12.5,1,16711680,[tencent]\">hello</d></i>"));

        var comment = Assert.Single(result.Comments);
        Assert.Equal(12.5, comment.TimeSeconds);
        Assert.Equal(1, comment.Mode);
        Assert.Equal(16_711_680, comment.Color);
        Assert.Equal("tencent", comment.Source);
        Assert.Equal("[tencent]", comment.SourceLabel);
        Assert.Equal("p：12.5,1,16711680,[tencent]", comment.RawPositionText);
        Assert.Equal("hello", comment.Text);
    }

    [Fact]
    public void ParsesJsonCommentWithCoreFourFieldPosition()
    {
        var result = DanmuApiClient.ParseCommentJson(Encoding.UTF8.GetBytes(
            "{\"count\":1,\"comments\":[{\"p\":\"12.5,1,16711680,[tencent]\",\"m\":\"hello\"}]}"));

        var comment = Assert.Single(result.Comments);
        Assert.Equal(12.5, comment.TimeSeconds);
        Assert.Equal(1, comment.Mode);
        Assert.Equal(16_711_680, comment.Color);
    }

    [Fact]
    public void ParsesStandardEightFieldPositionColor()
    {
        var result = DanmuApiClient.ParseCommentXml(Encoding.UTF8.GetBytes(
            "<i><d p=\"12.5,1,25,16711680,0,0,hash,1\">hello</d></i>"));

        Assert.Equal(16_711_680, Assert.Single(result.Comments).Color);
    }

    [Fact]
    public void RejectsCommentColorOutsideRgbRange()
    {
        var error = Assert.Throws<DanmuApiException>(() =>
            DanmuApiClient.ParseCommentJson(Encoding.UTF8.GetBytes(
                "{\"count\":1,\"comments\":[{\"p\":\"1,1,16777216,[qq]\",\"m\":\"bad\"}]}")));

        Assert.Equal(DanmuApiFailureKind.Protocol, error.Kind);
        Assert.Contains("16777215", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsInvalidUtf8Xml()
    {
        var error = Assert.Throws<DanmuApiException>(() =>
            DanmuApiClient.ParseCommentXml([0x3C, 0x69, 0x3E, 0xFF, 0x3C, 0x2F, 0x69, 0x3E]));

        Assert.Equal(DanmuApiFailureKind.Encoding, error.Kind);
    }

    [Fact]
    public void StrictParsersRejectMissingOrContradictoryFields()
    {
        var missing = Assert.Throws<DanmuApiException>(() =>
            DanmuApiClient.ParseSearchAnime(Encoding.UTF8.GetBytes("{\"animes\":[]}")));
        Assert.Equal(DanmuApiFailureKind.Protocol, missing.Kind);

        var contradictory = Assert.Throws<DanmuApiException>(() =>
            DanmuApiClient.ParseMatch(Encoding.UTF8.GetBytes(
                "{\"isMatched\":false,\"matches\":[{\"episodeId\":1,\"animeId\":2,\"animeTitle\":\"A\",\"episodeTitle\":\"E\"}]}")));
        Assert.Equal(DanmuApiFailureKind.Protocol, contradictory.Kind);
    }

    [Fact]
    public async Task FavoriteMutationUsesAdminTokenInPathAndKeywordBody()
    {
        Uri? observed = null;
        string? payload = null;
        using var handler = new RecordingHandler(async (request, _) =>
        {
            observed = request.RequestUri;
            payload = await request.Content!.ReadAsStringAsync();
            return Json("{\"success\":true,\"message\":\"saved\"}");
        });
        var client = new DanmuApiClient(new HttpClient(handler));

        var message = await client.AddFavoriteAsync(
            "127.0.0.1", 9321, "ordinary", "admin", "  番剧  ");

        Assert.Equal("saved", message);
        Assert.Equal("/admin/api/v2/favorite/add", observed?.AbsolutePath);
        Assert.DoesNotContain("Authorization", handler.LastRequest!.Headers.Select(item => item.Key));
        using var document = JsonDocument.Parse(payload!);
        Assert.Equal("番剧", document.RootElement.GetProperty("keyword").GetString());
    }

    [Fact]
    public async Task FavoriteRefreshConflictPreservesCoreMessageStatusAndSafeRecord()
    {
        const string token = "ordinary-conflict-token";
        const string admin = "admin-conflict-token";
        using var handler = new RecordingHandler((_, _) =>
            Task.FromResult(Json(
                "{\"success\":false,\"message\":\"该收藏正在刷新，请稍后再试\"}",
                HttpStatusCode.Conflict)));
        var records = new LocalRequestRecordStore();
        var client = new DanmuApiClient(new HttpClient(handler), requestRecords: records);

        var error = await Assert.ThrowsAsync<DanmuApiException>(() =>
            client.RefreshFavoriteAsync("127.0.0.1", 9321, token, admin, "private-favorite"));

        Assert.Equal(DanmuApiFailureKind.Http, error.Kind);
        Assert.Equal(409, error.StatusCode);
        Assert.Contains("该收藏正在刷新", error.Message, StringComparison.Ordinal);
        var record = Assert.Single(records.Snapshot());
        Assert.False(record.Success);
        Assert.Equal(409, record.StatusCode);
        Assert.Equal("收藏/刷新", record.Scene);
        Assert.Equal("HTTP 409", record.ErrorMessage);
        var serialized = JsonSerializer.Serialize(record);
        Assert.DoesNotContain(token, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(admin, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("private-favorite", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("该收藏正在刷新", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SegmentMutationSendsSegmentObjectWithoutWrapper()
    {
        string? payload = null;
        using var handler = new RecordingHandler(async (request, _) =>
        {
            payload = await request.Content!.ReadAsStringAsync();
            return Json("{\"success\":true,\"count\":0,\"comments\":[]}");
        });
        var client = new DanmuApiClient(new HttpClient(handler));
        using var document = JsonDocument.Parse(
            "{\"type\":\"xml\",\"segment_start\":0,\"segment_end\":60,\"url\":\"https://example.invalid/a\"}");

        var result = await client.GetSegmentCommentAsync(
            "127.0.0.1", 9321, "token", document.RootElement);

        Assert.Empty(result.Comments);
        Assert.DoesNotContain("\"segment\"", payload, StringComparison.Ordinal);
        Assert.Contains("segment_start", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawMatchRequestPutsFileNameOnlyInJsonBody()
    {
        string? payload = null;
        Uri? observed = null;
        using var handler = new RecordingHandler(async (request, _) =>
        {
            observed = request.RequestUri;
            payload = await request.Content!.ReadAsStringAsync();
            return Json("{\"success\":true}");
        });
        var client = new DanmuApiClient(new HttpClient(handler));

        var response = await client.SendRawAsync(
            "127.0.0.1",
            9321,
            "ordinary-token",
            "matchAnime",
            new Dictionary<string, string?> { ["fileName"] = "生万物 S02E08" },
            "{\"fileName\":\"生万物 S02E08\"}");

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("/ordinary-token/api/v2/match", observed?.AbsolutePath);
        Assert.Equal("", observed?.Query);
        Assert.Equal("{\"fileName\":\"生万物 S02E08\"}", payload);
    }

    [Fact]
    public async Task RawCommentUrlRequestIncludesAllQueryOptions()
    {
        Uri? observed = null;
        using var handler = new RecordingHandler((request, _) =>
        {
            observed = request.RequestUri;
            return Task.FromResult(Json("{\"count\":0,\"comments\":[]}"));
        });
        var client = new DanmuApiClient(new HttpClient(handler));

        await client.SendRawAsync(
            "127.0.0.1",
            9321,
            "token",
            "getCommentByUrl",
            new Dictionary<string, string?>
            {
                ["url"] = "https://example.invalid/video?a=1&b=2",
                ["format"] = "xml",
                ["duration"] = "false",
                ["segmentflag"] = "true",
            });

        Assert.Equal(
            "/token/api/v2/comment?url=https%3A%2F%2Fexample.invalid%2Fvideo%3Fa%3D1%26b%3D2&format=xml&duration=false&segmentflag=true",
            observed?.PathAndQuery);
    }

    [Fact]
    public async Task RawRequestPreservesNonSuccessBodyAndStatus()
    {
        using var handler = new RecordingHandler((_, _) =>
            Task.FromResult(Json("{\"success\":false,\"message\":\"bad input\"}", HttpStatusCode.BadRequest)));
        var client = new DanmuApiClient(new HttpClient(handler));

        var response = await client.SendRawAsync(
            "127.0.0.1",
            9321,
            "token",
            "searchAnime",
            new Dictionary<string, string?> { ["keyword"] = "bad" });

        Assert.Equal(400, response.StatusCode);
        Assert.False(response.IsSuccessStatusCode);
        Assert.Contains("bad input", response.BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulRequestRecordMasksTokenQueryBodyAndResponseValues()
    {
        const string token = "ordinary-record-secret";
        const string keyword = "private-keyword";
        using var handler = new RecordingHandler((_, _) =>
            Task.FromResult(Json("{\"success\":true,\"animes\":[{\"animeId\":9,\"animeTitle\":\"private-title\"}]}")));
        var records = new LocalRequestRecordStore();
        var client = new DanmuApiClient(new HttpClient(handler), requestRecords: records);

        await client.SearchAnimeAsync("127.0.0.1", 9321, token, keyword);

        var record = Assert.Single(records.Snapshot());
        Assert.True(record.Success);
        Assert.Equal(200, record.StatusCode);
        Assert.Equal("searchAnime", record.Scene);
        Assert.Equal("/api/v2/search/anime?keyword=***", record.Interface);
        Assert.Contains("animeTitle", record.ResponseSummary, StringComparison.Ordinal);
        var serialized = JsonSerializer.Serialize(record);
        Assert.DoesNotContain(token, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(keyword, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("private-title", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProtocolFailureReplacesTransportSuccessWithSingleFailedRecord()
    {
        using var handler = new RecordingHandler((_, _) =>
            Task.FromResult(Json("{\"success\":true,\"message\":\"private-response\"}")));
        var records = new LocalRequestRecordStore();
        var client = new DanmuApiClient(new HttpClient(handler), requestRecords: records);

        var error = await Assert.ThrowsAsync<DanmuApiException>(() =>
            client.SearchAnimeAsync("127.0.0.1", 9321, "token", "keyword"));

        Assert.Equal(DanmuApiFailureKind.Protocol, error.Kind);
        var record = Assert.Single(records.Snapshot());
        Assert.False(record.Success);
        Assert.Equal(200, record.StatusCode);
        Assert.Equal("响应协议无效", record.ErrorMessage);
        Assert.DoesNotContain("private-response", record.ResponseSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpFailureCreatesSingleClassifiedRecordWithoutResponseSecrets()
    {
        const string token = "http-record-token";
        using var handler = new RecordingHandler((_, _) =>
            Task.FromResult(Json("{\"message\":\"http-record-token private-error\"}", HttpStatusCode.BadRequest)));
        var records = new LocalRequestRecordStore();
        var client = new DanmuApiClient(new HttpClient(handler), requestRecords: records);

        var error = await Assert.ThrowsAsync<DanmuApiException>(() =>
            client.GetBangumiAsync("127.0.0.1", 9321, token, 12));

        Assert.Equal(DanmuApiFailureKind.Http, error.Kind);
        var record = Assert.Single(records.Snapshot());
        Assert.False(record.Success);
        Assert.Equal(400, record.StatusCode);
        Assert.Equal("HTTP 400", record.ErrorMessage);
        var serialized = JsonSerializer.Serialize(record);
        Assert.DoesNotContain(token, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("private-error", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SegmentRecordMasksAllBodyLeavesIncludingProviderTokens()
    {
        const string h5Token = "segment-h5-secret";
        const string videoUrl = "https://example.invalid/private-video";
        using var handler = new RecordingHandler((_, _) =>
            Task.FromResult(Json("{\"count\":0,\"comments\":[]}")));
        var records = new LocalRequestRecordStore();
        var client = new DanmuApiClient(new HttpClient(handler), requestRecords: records);
        using var document = JsonDocument.Parse($$"""
            {
              "type": "youku",
              "url": "{{videoUrl}}",
              "_m_h5_tk": "{{h5Token}}",
              "nested": { "cookie": "nested-cookie-secret" }
            }
            """);

        await client.GetSegmentCommentAsync("127.0.0.1", 9321, "token", document.RootElement);

        var record = Assert.Single(records.Snapshot());
        Assert.Contains("_m_h5_tk", record.Parameters, StringComparison.Ordinal);
        Assert.Contains("cookie", record.Parameters, StringComparison.Ordinal);
        Assert.DoesNotContain(h5Token, record.Parameters, StringComparison.Ordinal);
        Assert.DoesNotContain(videoUrl, record.Parameters, StringComparison.Ordinal);
        Assert.DoesNotContain("nested-cookie-secret", record.Parameters, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalRequestStoreReplacesSameIdAndKeepsNewestTwoHundred()
    {
        var records = new LocalRequestRecordStore();
        var first = NewLocalRecord(1, success: true);
        records.Add(first);
        records.Add(first with { Success = false, ErrorMessage = "响应协议无效" });
        for (var id = 2; id <= 205; id++)
        {
            records.Add(NewLocalRecord(id, success: true));
        }

        var snapshot = records.Snapshot();
        Assert.Equal(LocalRequestRecordStore.MaximumRecords, snapshot.Count);
        Assert.DoesNotContain(snapshot, record => record.Id <= 5);
        Assert.Equal(205, snapshot[0].Id);
    }

    [Theory]
    [InlineData("http://127.0.0.1:9321/private-token/danmaku?name=private", "/danmaku?name=***")]
    [InlineData("/private-token/api/v2/comment?url=private", "/api/v2/comment?url=***")]
    [InlineData("local://danmu/export?format=json", "local://danmu/export?format=***")]
    public void RequestRecordInterfaceRemovesTokenPathsAndMasksQueryValues(string input, string expected)
    {
        Assert.Equal(expected, RequestRecordRedactor.MaskInterface(input));
    }

    [Fact]
    public async Task SceneContextOverridesDefaultTraceScene()
    {
        using var handler = new RecordingHandler((_, _) =>
            Task.FromResult(Json("{\"success\":true,\"animes\":[]}")));
        var records = new LocalRequestRecordStore();
        var client = new DanmuApiClient(new HttpClient(handler), requestRecords: records);

        using (DanmuRequestSceneContext.Push("弹幕测试/手动搜索动漫"))
        {
            await client.SearchAnimeAsync("127.0.0.1", 9321, "token", "private-search-input");
        }

        var record = Assert.Single(records.Snapshot());
        Assert.Equal("弹幕测试/手动搜索动漫", record.Scene);
        Assert.Equal("/api/v2/search/anime?keyword=***", record.Interface);
        var serialized = JsonSerializer.Serialize(record);
        Assert.DoesNotContain("private-search-input", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalRequestStoreAllocationAdvancesPastExplicitIds()
    {
        var records = new LocalRequestRecordStore();
        records.Add(NewLocalRecord(41, success: true));

        var allocated = records.AllocateId();
        records.Add(NewLocalRecord(allocated, success: false));

        Assert.Equal(42, allocated);
        Assert.Equal(2, records.Snapshot().Count);
    }

    private static LocalRequestRecord NewLocalRecord(long id, bool success) => new(
        id,
        DateTimeOffset.UnixEpoch.AddSeconds(id),
        "test",
        "GET",
        "/api/v2/test",
        string.Empty,
        200,
        1,
        success,
        null,
        string.Empty);

    private static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            Entered.TrySetResult();
            return await responder(request, cancellationToken);
        }
    }

    private sealed class RecordingRuntimeController(RuntimeSnapshot snapshot) : IRuntimeController
    {
        public RuntimeSnapshot Snapshot { get; } = snapshot;
        event EventHandler<RuntimeSnapshot>? IRuntimeController.SnapshotChanged
        {
            add { }
            remove { }
        }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AdoptionResult> AdoptAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(AdoptionResult.Failure(AdoptionFailureKind.NotFound, Snapshot, "not used"));
        public string? ReconcileLiveness() => null;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RestartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
