using System.ComponentModel;
using System.Text;
using System.Text.Json;
using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class DanmuPageViewModelTests
{
    [Fact]
    public async Task ManualFlowUsesSeparatePagesAndPopsOnePageAtATime()
    {
        await using var fixture = new Fixture();
        var root = fixture.CreateViewModel();
        root.SelectedTabOption = root.TabOptions.Single(item => item.Value == DanmuTestTab.Manual);
        var manual = Assert.IsType<ManualSearchPageViewModel>(root.CurrentPage);
        manual.Keyword = "测试番剧";

        await manual.SearchCommand.ExecuteAsync(null);
        var results = Assert.IsType<SearchResultsPageViewModel>(root.CurrentPage);
        Assert.Equal(DanmuPageRoute.SearchResults, root.Route);

        results.SelectAnimeCommand.Execute(results.Results[0]);
        var details = Assert.IsType<BangumiDetailsPageViewModel>(root.CurrentPage);
        Assert.Equal(DanmuPageRoute.BangumiDetails, root.Route);
        await details.LoadCommand.ExecuteAsync(null);
        Assert.Single(details.Episodes);

        details.SelectEpisodeCommand.Execute(details.Episodes[0]);
        var danmu = Assert.IsType<DanmuDetailsPageViewModel>(root.CurrentPage);
        Assert.Equal(DanmuPageRoute.DanmuDetails, root.Route);
        await WaitUntilAsync(() => danmu.AllComments.Count == 501);

        danmu.BackCommand.Execute(null);
        Assert.Same(details, root.CurrentPage);
        Assert.Equal(DanmuPageRoute.BangumiDetails, root.Route);
        danmu = null!;

        ((BangumiDetailsPageViewModel)root.CurrentPage).BackCommand.Execute(null);
        Assert.Same(results, root.CurrentPage);
        Assert.Equal(DanmuPageRoute.SearchResults, root.Route);

        ((SearchResultsPageViewModel)root.CurrentPage).BackCommand.Execute(null);
        Assert.Same(manual, root.CurrentPage);
        Assert.Equal(DanmuPageRoute.Entry, root.Route);
    }

    [Fact]
    public async Task AutoFlowReturnsToAutoEntryInsteadOfManualSearch()
    {
        await using var fixture = new Fixture();
        var root = fixture.CreateViewModel();
        var auto = Assert.IsType<AutoMatchPageViewModel>(root.CurrentPage);
        auto.FileName = "测试番剧 S01E01";

        await auto.MatchCommand.ExecuteAsync(null);
        var details = Assert.IsType<DanmuDetailsPageViewModel>(root.CurrentPage);
        Assert.Equal(DanmuReturnTarget.Auto, details.ReturnTarget);

        details.BackCommand.Execute(null);
        Assert.Same(auto, root.CurrentPage);
        Assert.Equal(DanmuTestTab.Auto, root.SelectedTabOption.Value);
    }

    [Fact]
    public async Task CommentDetailsExposeLoadingStateWhileRequestIsPending()
    {
        await using var fixture = new Fixture(blockComments: true);
        var root = fixture.CreateViewModel();
        var auto = Assert.IsType<AutoMatchPageViewModel>(root.CurrentPage);
        auto.FileName = "测试番剧 S01E01";
        await auto.MatchCommand.ExecuteAsync(null);
        var details = Assert.IsType<DanmuDetailsPageViewModel>(root.CurrentPage);
        await fixture.CommentsEntered.Task;

        Assert.True(details.IsBusy);
        Assert.False(details.CanRetry);
        Assert.Contains("拉取弹幕", details.LoadingText, StringComparison.Ordinal);

        fixture.ReleaseComments();
        await WaitUntilAsync(() => !details.IsBusy);
    }

    [Fact]
    public async Task EmptySuccessfulResponseIsNotShownAsRetryableFailure()
    {
        await using var fixture = new Fixture(emptyComments: true);
        var root = fixture.CreateViewModel();
        var auto = Assert.IsType<AutoMatchPageViewModel>(root.CurrentPage);
        auto.FileName = "测试番剧 S01E01";
        await auto.MatchCommand.ExecuteAsync(null);
        var details = Assert.IsType<DanmuDetailsPageViewModel>(root.CurrentPage);
        await WaitUntilAsync(() => !details.IsBusy);

        Assert.True(details.HasLoadedComments);
        Assert.False(details.CanRetry);
        Assert.Contains("暂无弹幕", details.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommentListShowsFiveHundredThenLoadsNextBatch()
    {
        await using var fixture = new Fixture();
        var root = fixture.CreateViewModel();
        var auto = Assert.IsType<AutoMatchPageViewModel>(root.CurrentPage);
        auto.FileName = "测试番剧 S01E01";
        await auto.MatchCommand.ExecuteAsync(null);
        var details = Assert.IsType<DanmuDetailsPageViewModel>(root.CurrentPage);
        await WaitUntilAsync(() => details.AllComments.Count == 501);

        Assert.Equal(501, details.FilteredComments.Count);
        Assert.Equal(500, details.DisplayedComments.Count);
        Assert.True(details.CanLoadMore);

        details.LoadMoreCommand.Execute(null);

        Assert.Equal(501, details.DisplayedComments.Count);
        Assert.False(details.CanLoadMore);

        details.SearchQuery = "comment 500";
        Assert.Single(details.FilteredComments);
        Assert.Single(details.DisplayedComments);
        Assert.False(details.CanLoadMore);
    }

    [Fact]
    public async Task EmptySearchFilterShowsExplicitState()
    {
        await using var fixture = new Fixture();
        var root = fixture.CreateViewModel();
        var auto = Assert.IsType<AutoMatchPageViewModel>(root.CurrentPage);
        auto.FileName = "测试番剧 S01E01";
        await auto.MatchCommand.ExecuteAsync(null);
        var details = Assert.IsType<DanmuDetailsPageViewModel>(root.CurrentPage);
        await WaitUntilAsync(() => details.HasLoadedComments);

        details.SearchQuery = "not-present-in-comments";

        Assert.Empty(details.DisplayedComments);
        Assert.True(details.ShowEmptyFilterState);
        Assert.False(details.HasDisplayedComments);
        Assert.False(details.CanLoadMore);
    }

    [Fact]
    public void CommentFiltersFollowCoreModeSemantics()
    {
        var comments = new[]
        {
            new DanmuComment("scroll", 1, 1, 0, null),
            new DanmuComment("top", 2, 5, 0, null),
            new DanmuComment("bottom", 3, 4, 0, null),
        };
        Assert.Equal(3, comments.Length);
        Assert.Equal(1, comments.Count(item => item.Mode == 5));
        Assert.Equal(1, comments.Count(item => item.Mode == 4));
        Assert.Equal(1, comments.Count(item => item.Mode is not (4 or 5)));
    }

    [Fact]
    public async Task SearchResultsBindFavoriteStateAndRefreshAfterToggle()
    {
        await using var fixture = new Fixture(adminMode: true);
        fixture.Client.FavoriteSupported = true;
        fixture.Client.Favorites.Add(new DanmuFavoriteItem(
            "测试番剧_S01", "测试番剧", "test", ["test"], "", 1, 1, 1, 2, null));
        var root = fixture.CreateViewModel();
        root.SelectedTabOption = root.TabOptions.Single(item => item.Value == DanmuTestTab.Manual);
        var manual = Assert.IsType<ManualSearchPageViewModel>(root.CurrentPage);
        manual.Keyword = "测试番剧";

        await manual.SearchCommand.ExecuteAsync(null);
        var results = Assert.IsType<SearchResultsPageViewModel>(root.CurrentPage);
        await WaitUntilAsync(() => results.HasFavoriteCapability);
        Assert.True(results.IsFavorite);
        Assert.Equal("取消收藏快照", results.FavoriteActionText);

        await results.ToggleFavoriteCommand.ExecuteAsync(null);

        Assert.Equal(1, fixture.Client.RemoveFavoriteCalls);
        Assert.Equal("测试番剧_S01", fixture.Client.LastRemovedFavoriteKeyword);
        await WaitUntilAsync(() => !results.IsFavorite);
        Assert.Equal("收藏当前搜索快照", results.FavoriteActionText);
    }

    [Fact]
    public async Task AutoMatchAcceptsDirectUrlAndUsesUrlEndpoint()
    {
        await using var fixture = new Fixture();
        var root = fixture.CreateViewModel();
        var auto = Assert.IsType<AutoMatchPageViewModel>(root.CurrentPage);
        auto.FileName = "https://example.invalid/video";

        await auto.MatchCommand.ExecuteAsync(null);
        var details = Assert.IsType<DanmuDetailsPageViewModel>(root.CurrentPage);
        await WaitUntilAsync(() => !details.IsBusy);

        Assert.Contains("https://example.invalid/video", fixture.Client.CommentUrls);
        Assert.Equal(0, fixture.Client.CommentIdCalls);
        Assert.False(details.CanExport);

        await details.ExportCommand.ExecuteAsync(null);

        Assert.Empty(fixture.Client.DownloadFormats);
        Assert.Contains("剧集 ID", details.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualSearchAcceptsDirectUrlWithoutAnimeSearch()
    {
        await using var fixture = new Fixture();
        var root = fixture.CreateViewModel();
        root.SelectedTabOption = root.TabOptions.Single(item => item.Value == DanmuTestTab.Manual);
        var manual = Assert.IsType<ManualSearchPageViewModel>(root.CurrentPage);
        manual.Keyword = "https://example.invalid/manual-video";

        await manual.SearchCommand.ExecuteAsync(null);
        var details = Assert.IsType<DanmuDetailsPageViewModel>(root.CurrentPage);
        await WaitUntilAsync(() => !details.IsBusy);

        Assert.Equal(DanmuReturnTarget.ManualSearch, details.ReturnTarget);
        Assert.Contains("https://example.invalid/manual-video", fixture.Client.CommentUrls);
        Assert.Equal(0, fixture.Client.SearchAnimeCalls);
        Assert.Equal(0, fixture.Client.CommentIdCalls);
    }

    [Fact]
    public async Task CommentDetailsFallsBackToSourceUrlAfterEpisodeRequestFailure()
    {
        await using var fixture = new Fixture();
        fixture.Client.FailCommentById = true;
        var root = fixture.CreateViewModel();
        var auto = Assert.IsType<AutoMatchPageViewModel>(root.CurrentPage);
        auto.FileName = "测试番剧 S01E01";

        await auto.MatchCommand.ExecuteAsync(null);
        var details = Assert.IsType<DanmuDetailsPageViewModel>(root.CurrentPage);
        await WaitUntilAsync(() => !details.IsBusy);

        Assert.Contains("https://example.invalid/episode", fixture.Client.CommentUrls);
        Assert.True(details.HasLoadedComments);
        Assert.Contains("来源 URL", details.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EpisodeNumberLocationFiltersTargetWithoutOpeningDanmu()
    {
        await using var fixture = new Fixture();
        fixture.Client.BangumiEpisodes =
        [
            new DanmuEpisode("s1", 1, "开端", "1", "", "https://example.invalid/1"),
            new DanmuEpisode("s1", 2, "继续", "2", "", "https://example.invalid/2"),
        ];
        var root = fixture.CreateViewModel();
        root.SelectedTabOption = root.TabOptions.Single(item => item.Value == DanmuTestTab.Manual);
        var manual = Assert.IsType<ManualSearchPageViewModel>(root.CurrentPage);
        manual.Keyword = "测试番剧";
        await manual.SearchCommand.ExecuteAsync(null);
        var results = Assert.IsType<SearchResultsPageViewModel>(root.CurrentPage);
        results.SelectAnimeCommand.Execute(results.Results[0]);
        var details = Assert.IsType<BangumiDetailsPageViewModel>(root.CurrentPage);
        await details.LoadCommand.ExecuteAsync(null);

        details.EpisodeQuery = "2";
        details.JumpToEpisodeCommand.Execute(null);

        Assert.Same(details, root.CurrentPage);
        var episode = Assert.Single(details.VisibleEpisodes);
        Assert.Equal(2, episode.EpisodeId);
        Assert.Contains("已定位第 2 集", details.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Client.CommentIdCalls);
    }

    [Fact]
    public async Task SourceUrlFailurePreservesOriginalFailureClassification()
    {
        await using var fixture = new Fixture();
        fixture.Client.FailCommentById = true;
        fixture.Client.CommentByUrlFailure = new DanmuApiException(
            DanmuApiFailureKind.Authentication,
            "URL authentication failed",
            statusCode: 403);
        var root = fixture.CreateViewModel();
        var auto = Assert.IsType<AutoMatchPageViewModel>(root.CurrentPage);
        auto.FileName = "测试番剧 S01E01";

        await auto.MatchCommand.ExecuteAsync(null);
        var details = Assert.IsType<DanmuDetailsPageViewModel>(root.CurrentPage);
        await WaitUntilAsync(() => !details.IsBusy);

        Assert.Contains("URL authentication failed", details.Diagnostic, StringComparison.Ordinal);
        var dialog = Assert.Single(fixture.Dialogs.Messages);
        Assert.Contains("URL authentication failed", dialog.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("连接失败", dialog.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceUrlOpenFailureBecomesVisibleDiagnostic()
    {
        await using var fixture = new Fixture();
        fixture.Dialogs.OpenUrlFailure = new IOException("browser unavailable");
        var root = fixture.CreateViewModel();
        var auto = Assert.IsType<AutoMatchPageViewModel>(root.CurrentPage);
        auto.FileName = "测试番剧 S01E01";
        await auto.MatchCommand.ExecuteAsync(null);
        var details = Assert.IsType<DanmuDetailsPageViewModel>(root.CurrentPage);
        await WaitUntilAsync(() => !details.IsBusy);

        await details.OpenSourceUrlCommand.ExecuteAsync(null);

        Assert.Contains("打开来源地址失败", details.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("browser unavailable", details.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("打开弹幕来源地址失败", fixture.Diagnostics.LastDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommentDetailsExposeStatisticsMetadataSourceAndExports()
    {
        await using var fixture = new Fixture();
        fixture.Client.CommentResult = new DanmuResult(
            3,
            [
                new DanmuComment("white", 1, 1, 0xFFFFFF, "1,1,16777215,[qq]", Source: "qq", Cid: 10, Like: 2, ColorV2: "1-2"),
                new DanmuComment("red", 31, 5, 0xFF0000, "31,5,16711680,[bilibili]", Source: "bilibili"),
                new DanmuComment("green", 35, 4, 0x00FF00, "35,4,65280,[qq]", Source: "qq"),
            ],
            60,
            "application/json");
        var root = fixture.CreateViewModel();
        var auto = Assert.IsType<AutoMatchPageViewModel>(root.CurrentPage);
        auto.FileName = "测试番剧 S01E01";
        await auto.MatchCommand.ExecuteAsync(null);
        var details = Assert.IsType<DanmuDetailsPageViewModel>(root.CurrentPage);
        await WaitUntilAsync(() => details.HasLoadedComments);
        Assert.True(details.CanExport);

        Assert.NotNull(details.Statistics);
        Assert.Equal(3, details.Statistics!.Count);
        Assert.Equal(3.0, details.Statistics.AveragePerMinute, precision: 3);
        Assert.Equal(30, details.Statistics.HotStartSeconds);
        Assert.Equal(2, details.Statistics.HotCount);
        Assert.Equal(2.0 / 3.0, details.Statistics.ColoredRatio, precision: 3);
        Assert.InRange(details.HeatmapBuckets.Count, 20, 60);
        Assert.InRange(details.HighMoments.Count, 1, 5);
        Assert.All(
            details.HighMoments.Zip(details.HighMoments.Skip(1)),
            pair => Assert.True(Math.Abs(pair.First.Index - pair.Second.Index) > 1));
        var highMoment = details.HighMoments[0];
        details.SelectHeatmapBucketCommand.Execute(highMoment);
        Assert.Equal(highMoment.Index, details.SelectedHeatmapIndex);
        Assert.Equal(highMoment.RangeText, details.SelectedHeatmapRangeText);
        Assert.Equal(highMoment.CountText, details.SelectedHeatmapCountText);
        Assert.Equal("[qq]", details.AllComments[0].SourceLabel);
        Assert.Equal("cid：10", details.AllComments[0].CidText);
        Assert.Equal("like：2", details.AllComments[0].LikeText);
        Assert.Equal("color_v2：1-2", details.AllComments[0].ColorV2Text);

        await details.CopySourceUrlCommand.ExecuteAsync(null);
        await details.OpenSourceUrlCommand.ExecuteAsync(null);
        await details.ExportJsonCommand.ExecuteAsync(null);
        await details.ExportXmlCommand.ExecuteAsync(null);

        details.SelectedExportFormatOption = details.ExportFormatOptions.Single(item => item.Value == DanmuDownloadFormat.DanuniBinPb);
        await details.ExportCommand.ExecuteAsync(null);

        Assert.Equal("https://example.invalid/episode", fixture.Dialogs.CopiedText);
        Assert.Contains("https://example.invalid/episode", fixture.Dialogs.OpenedUrls);
        Assert.Equal(
            new[]
            {
                "json", "xml", "ddplay.json", "dplayer.json", "artplayer.json",
                "vod.json", "baha.json", "bili.xml", "danuni.json", "danuni.binpb",
            },
            details.ExportFormatOptions.Select(option => option.Value.Value()).ToArray());
        Assert.Equal(["json", "xml", "danuni.binpb"], fixture.Client.DownloadFormats.ToArray());
        Assert.Collection(
            fixture.Dialogs.SavedByteFiles,
            json =>
            {
                Assert.Equal("测试番剧 S01E01.json", json.FileName);
                Assert.Contains("\"cid\": 10", Encoding.UTF8.GetString(json.Content), StringComparison.Ordinal);
            },
            xml =>
            {
                Assert.Equal("测试番剧 S01E01.xml", xml.FileName);
                Assert.Contains("[qq]", Encoding.UTF8.GetString(xml.Content), StringComparison.Ordinal);
            },
            binary =>
            {
                Assert.Equal("测试番剧 S01E01.danuni.binpb", binary.FileName);
                Assert.Equal(new byte[] { 0, 1, 2, 3 }, binary.Content);
            });
        var exports = fixture.RequestRecords.Snapshot();
        Assert.Equal(3, exports.Count);
        Assert.Collection(
            exports.OrderBy(record => record.Id),
            json =>
            {
                Assert.True(json.Success);
                Assert.Equal("local://danmu/export?format=***", json.Interface);
                Assert.Empty(json.Parameters);
                Assert.Empty(json.ResponseSummary);
            },
            xml =>
            {
                Assert.True(xml.Success);
                Assert.Equal("local://danmu/export?format=***", xml.Interface);
            },
            binary =>
            {
                Assert.True(binary.Success);
                Assert.Equal("local://danmu/export?format=***", binary.Interface);
            });
        var recordedText = JsonSerializer.Serialize(exports);
        Assert.DoesNotContain("white", recordedText, StringComparison.Ordinal);
        Assert.DoesNotContain("example.invalid", recordedText, StringComparison.Ordinal);
        Assert.DoesNotContain("danmu_1", recordedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportCancelCreatesExplicitRecordWithoutErrorDiagnostic()
    {
        await using var fixture = new Fixture();
        fixture.Dialogs.CancelSaveTextFile = true;
        var root = fixture.CreateViewModel();
        var auto = Assert.IsType<AutoMatchPageViewModel>(root.CurrentPage);
        auto.FileName = "测试番剧 S01E01";
        await auto.MatchCommand.ExecuteAsync(null);
        var details = Assert.IsType<DanmuDetailsPageViewModel>(root.CurrentPage);
        await WaitUntilAsync(() => details.HasLoadedComments);

        await details.ExportJsonCommand.ExecuteAsync(null);

        var record = Assert.Single(fixture.RequestRecords.Snapshot());
        Assert.False(record.Success);
        Assert.Equal("用户取消导出", record.ErrorMessage);
        Assert.Empty(fixture.Dialogs.Messages);
        Assert.False(details.HasDiagnostic);
    }

    [Fact]
    public async Task ExportFailureCreatesDiagnosticAndSafeFailedRecord()
    {
        await using var fixture = new Fixture();
        fixture.Client.FailDownload = true;
        fixture.Dialogs.SaveTextFileFailure = new IOException("blocked private-path");
        var root = fixture.CreateViewModel();
        var auto = Assert.IsType<AutoMatchPageViewModel>(root.CurrentPage);
        auto.FileName = "测试番剧 S01E01";
        await auto.MatchCommand.ExecuteAsync(null);
        var details = Assert.IsType<DanmuDetailsPageViewModel>(root.CurrentPage);
        await WaitUntilAsync(() => details.HasLoadedComments);

        await details.ExportXmlCommand.ExecuteAsync(null);

        Assert.Contains("导出 XML 失败", details.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("blocked private-path", details.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("导出弹幕 XML 失败", fixture.Diagnostics.LastDiagnostic, StringComparison.Ordinal);
        var record = Assert.Single(fixture.RequestRecords.Snapshot());
        Assert.False(record.Success);
        Assert.Equal("导出保存失败", record.ErrorMessage);
        Assert.DoesNotContain("private-path", JsonSerializer.Serialize(record), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommentDetailsRaiseHasLoadedCommentsForExportButton()
    {
        await using var fixture = new Fixture();
        var root = fixture.CreateViewModel();
        var auto = Assert.IsType<AutoMatchPageViewModel>(root.CurrentPage);
        auto.FileName = "测试番剧 S01E01";

        var notifications = new List<string?>();
        root.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DanmuDetailsPageViewModel.HasLoadedComments))
            {
                notifications.Add(args.PropertyName);
            }
        };
        // 只观测当前页与后续导航出的详情页；详情页在导航后替换 CurrentPage。
        void WatchCurrentPage(object? sender, PropertyChangedEventArgs args)
        {
            if (sender is DanmuTestPageViewModel vm && vm.CurrentPage is DanmuDetailsPageViewModel details)
            {
                details.PropertyChanged -= WatchCurrentPage;
                details.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(DanmuDetailsPageViewModel.HasLoadedComments))
                    {
                        notifications.Add(e.PropertyName);
                    }
                };
            }
        }

        root.PropertyChanged += WatchCurrentPage;
        await auto.MatchCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => notifications.Count > 0);

        Assert.Contains(nameof(DanmuDetailsPageViewModel.HasLoadedComments), notifications);
    }

    [Fact]
    public void FavoriteCardBuildsCoreFrontendEquivalentMeta()
    {
        var schedule = new DanmuFavoriteSchedule(
            "daily", "09:00", null, "Asia/Shanghai",
            1_800_000_000_000, null, 1_700_000_000_000, "success", null);
        var item = new DanmuFavoriteItem(
            "生万物_S01", "生万物", "qq", ["qq"], "", 12, 3,
            1_760_000_000_000, 1_770_000_000_000, schedule);

        var card = new FavoriteItemViewModel(item, null);
        var searchRow = new SearchResultRowViewModel(
            new DanmuAnime(9, "b", "测试番剧 from qq", "tv", "TV", "", "", 12, 0, false, "qq", []),
            null);

        Assert.Equal("测试番剧", searchRow.DisplayTitle);
        Assert.Equal("来源：qq", searchRow.SourceText);
        Assert.Equal("ID：9 · 12 集", searchRow.MetaText);
        Assert.Equal("测", searchRow.PosterFallbackText);
        Assert.Equal("生万物", card.Title);
        Assert.Equal("生", card.PosterFallbackText);
        Assert.True(card.ShowPosterPlaceholder);
        Assert.False(card.HasPoster);
        Assert.Contains("来源：qq", card.MetaLine, StringComparison.Ordinal);
        Assert.Contains("12 集", card.MetaLine, StringComparison.Ordinal);
        Assert.Contains("3 个搜索结果", card.MetaLine, StringComparison.Ordinal);
        Assert.Equal("定时刷新：每天 09:00 (Asia/Shanghai)", card.ScheduleSummaryText);
        Assert.Contains("下次", card.ScheduleDetailText, StringComparison.Ordinal);
        Assert.Contains("success", card.ScheduleDetailText, StringComparison.Ordinal);
        Assert.True(card.HasSchedule);

        var noSchedule = new FavoriteItemViewModel(item with { RefreshSchedule = null, AnimeTitle = "" }, null);
        Assert.Equal("生万物_S01", noSchedule.Title);
        Assert.Equal("定时刷新：未设置", noSchedule.ScheduleSummaryText);
        Assert.Empty(noSchedule.ScheduleDetailText);
        Assert.False(noSchedule.HasSchedule);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (var attempt = 0; attempt < 100 && !predicate(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(predicate(), "异步页面请求未在测试窗口内完成");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        private readonly RuntimeApiContext _context;
        private readonly FakeClient _client;
        private readonly RecordingDialogService _dialogs = new();
        private readonly RecordingDiagnostics _diagnostics = new();
        private readonly LocalRequestRecordStore _requestRecords = new();

        public Fixture(bool blockComments = false, bool emptyComments = false, bool adminMode = false)
        {
            var paths = new AppPaths(_directory.Path, Path.Combine(_directory.Path, "appdata"));
            var controller = new TestRuntimeController();
            _context = new RuntimeApiContext(paths, controller, new StubAdminSessionService(adminMode, adminMode, adminMode ? "admin-token" : null));
            _client = new FakeClient(blockComments, emptyComments);
        }

        public RecordingDialogService Dialogs => _dialogs;
        public RecordingDiagnostics Diagnostics => _diagnostics;
        public LocalRequestRecordStore RequestRecords => _requestRecords;
        public FakeClient Client => _client;
        public TaskCompletionSource CommentsEntered => _client.CommentsEntered;
        public void ReleaseComments() => _client.ReleaseComments();

        public DanmuTestPageViewModel CreateViewModel() =>
            new(_context, _client, _dialogs, _diagnostics, _requestRecords);

        public ValueTask DisposeAsync()
        {
            _directory.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeClient : IDanmuApiClient
    {
        private readonly bool _blockComments;
        private readonly bool _emptyComments;
        public TaskCompletionSource CommentsEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseComments = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FavoriteSupported { get; set; }
        public bool FailCommentById { get; set; }
        public bool FailDownload { get; set; }
        public DanmuApiException? CommentByUrlFailure { get; set; }
        public DanmuResult? CommentResult { get; set; }
        public IReadOnlyList<DanmuEpisode> BangumiEpisodes { get; set; } =
            [new DanmuEpisode("s1", 1, "S01E01", "1", "", "https://example.invalid/episode")];
        public int SearchAnimeCalls { get; private set; }
        public int CommentIdCalls { get; private set; }
        public int AddFavoriteCalls { get; private set; }
        public int RemoveFavoriteCalls { get; private set; }
        public string? LastRemovedFavoriteKeyword { get; private set; }
        public List<string> CommentUrls { get; } = [];
        public List<string> DownloadFormats { get; } = [];
        public List<DanmuFavoriteItem> Favorites { get; } = [];

        public FakeClient(bool blockComments, bool emptyComments)
        {
            _blockComments = blockComments;
            _emptyComments = emptyComments;
        }

        public void ReleaseComments() => _releaseComments.TrySetResult();

        public Task<DanmuRawApiResponse> SendRawAsync(string host, int port, string? token, string apiKey, IReadOnlyDictionary<string, string?> parameters, string? jsonBody = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DanmuRawApiResponse(200, "application/json", Encoding.UTF8.GetBytes("{}"), "{}", "/", TimeSpan.Zero));

        public Task<DanmuDownloadPayload> DownloadCommentAsync(string host, int port, string? token, long episodeId, string formatValue, TimeSpan? requestTimeout = null, CancellationToken cancellationToken = default)
        {
            DownloadFormats.Add(formatValue);
            if (FailDownload)
            {
                throw new IOException("blocked private-path");
            }

            if (formatValue == "danuni.binpb")
            {
                return Task.FromResult(new DanmuDownloadPayload(200, "application/octet-stream", [0, 1, 2, 3], formatValue, 1));
            }

            if (formatValue is "xml" or "bili.xml")
            {
                return Task.FromResult(new DanmuDownloadPayload(200, "application/xml", Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><i><d p=\"1,1,16777215,[qq]\">white</d></i>"), formatValue, 1));
            }

            var json = "{\"comments\":[{\"p\":\"1,1,16777215,[qq]\",\"m\":\"white\",\"t\":1,\"cid\":10,\"like\":2,\"color_v2\":\"1-2\"}],\"count\":1}";
            return Task.FromResult(new DanmuDownloadPayload(200, "application/json", Encoding.UTF8.GetBytes(json), formatValue, 1));
        }

        public Task<DanmuSearchAnimeResult> SearchAnimeAsync(string host, int port, string? token, string keyword, CancellationToken cancellationToken = default)
        {
            SearchAnimeCalls++;
            return Task.FromResult(new DanmuSearchAnimeResult(true, [new DanmuAnime(1, "b", "测试番剧", "tv", "TV", "", "", 1, 0, false, "source", [])]));
        }

        public Task<DanmuSearchEpisodesResult> SearchEpisodesAsync(string host, int port, string? token, string anime, string? episode = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DanmuSearchEpisodesResult(true, [], null));

        public Task<DanmuMatchResult> MatchAsync(string host, int port, string? token, string fileName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DanmuMatchResult(true, [new DanmuAnimeMatch(1, 1, "测试番剧", "S01E01", "tv", "TV", 0, "", "https://example.invalid/episode")]));

        public Task<DanmuBangumiResult> GetBangumiAsync(string host, int port, string? token, int animeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DanmuBangumiResult(true, new DanmuBangumi(1, "b", "测试番剧", "", false, 0, false, 0, "tv", "TV", [], BangumiEpisodes)));

        public async Task<DanmuResult> GetCommentAsync(string host, int port, string? token, int commentId, bool includeDuration = true, string format = "json", CancellationToken cancellationToken = default, bool segmentFlag = false)
        {
            CommentIdCalls++;
            CommentsEntered.TrySetResult();
            if (FailCommentById)
            {
                throw new DanmuApiException(DanmuApiFailureKind.NotFound, "episode not found");
            }
            if (_blockComments)
            {
                await _releaseComments.Task.WaitAsync(cancellationToken);
            }

            if (CommentResult is not null)
            {
                return CommentResult;
            }

            if (_emptyComments)
            {
                return new DanmuResult(0, [], 120, "application/json");
            }

            return new DanmuResult(501, Enumerable.Range(0, 501).Select(index => new DanmuComment($"comment {index}", index + 0.5, index % 6 == 4 ? 4 : index % 6 == 5 ? 5 : 1, 0, null)).ToArray(), 120, "application/json");
        }

        public Task<DanmuResult> GetCommentByUrlAsync(string host, int port, string? token, string videoUrl, bool includeDuration = true, string format = "json", CancellationToken cancellationToken = default, bool segmentFlag = false)
        {
            CommentUrls.Add(videoUrl);
            if (CommentByUrlFailure is not null)
            {
                throw CommentByUrlFailure;
            }

            return Task.FromResult(new DanmuResult(1, [new DanmuComment("url comment", 1.5, 1, 0, "1.5,1,0,[qq]")], 120, "application/json"));
        }

        public Task<DanmuResult> GetSegmentCommentAsync(string host, int port, string? token, JsonElement segment, string format = "json", CancellationToken cancellationToken = default) =>
            Task.FromResult(new DanmuResult(0, [], null, "application/json"));

        public Task<DanmuFavoriteListResult> GetFavoritesAsync(string host, int port, string? token, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DanmuFavoriteListResult(true, new(FavoriteSupported, false, null), Favorites.ToArray(), null));

        public Task<string> AddFavoriteAsync(string host, int port, string? token, string? adminToken, string keyword, CancellationToken cancellationToken = default)
        {
            AddFavoriteCalls++;
            return Task.FromResult("ok");
        }

        public Task<string> RemoveFavoriteAsync(string host, int port, string? token, string? adminToken, string keyword, CancellationToken cancellationToken = default)
        {
            RemoveFavoriteCalls++;
            LastRemovedFavoriteKeyword = keyword;
            Favorites.RemoveAll(item => string.Equals(item.Keyword, keyword, StringComparison.OrdinalIgnoreCase) || string.Equals(item.AnimeTitle, keyword, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult("ok");
        }
        public Task<string> RefreshFavoriteAsync(string host, int port, string? token, string? adminToken, string keyword, CancellationToken cancellationToken = default) => Task.FromResult("ok");
        public Task<string> SetFavoriteScheduleAsync(string host, int port, string? token, string? adminToken, string keyword, DanmuFavoriteSchedule? schedule, CancellationToken cancellationToken = default) => Task.FromResult("ok");
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
