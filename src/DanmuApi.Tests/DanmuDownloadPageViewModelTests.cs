using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
using DanmuApi.App.Views;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class DanmuDownloadPageViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"danmu-dlvm-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private const string SampleXml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<i><d p=\"1.5,1,16777215,[qq]\">hello</d></i>";

    private sealed class DownloadFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"danmu-dlvm-{Guid.NewGuid():N}");
        public RecordingDialogService Dialogs { get; } = new();
        public RecordingDiagnostics Diagnostics { get; } = new();
        public FakeDownloadClient Client { get; } = new();
        public StubEnvClient EnvClient { get; } = new();
        public DanmuDownloadStore Store { get; }
        public DanmuDownloadFileService Service { get; }
        public string SaveDirectory { get; }

        public DownloadFixture(string saveDirectory = "")
        {
            Directory.CreateDirectory(Root);
            SaveDirectory = string.IsNullOrEmpty(saveDirectory) ? Path.Combine(Root, "save") : saveDirectory;
            Store = new DanmuDownloadStore(Root);
            Store.SaveSettings(new DanmuDownloadSettings(SaveDirectory: SaveDirectory));
            Service = new DanmuDownloadFileService(Store, () => Client);
        }

        public DanmuDownloadPageViewModel CreateViewModel() =>
            new(CreateContext(), Client, EnvClient, Store, Service, Dialogs, Diagnostics, new PosterImageService());

        public RuntimeApiContext CreateContext()
        {
            var paths = new AppPaths(Root, Path.Combine(Root, "appdata"));
            return new RuntimeApiContext(paths, new StubRuntimeController(), new StubAdminSessionService(true, true, "admin-token"));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PickDirectoryPersistsSetting()
    {
        using var fixture = new DownloadFixture();
        var target = Path.Combine(fixture.Root, "chosen");
        fixture.Dialogs.PickedFolder = target;
        var viewModel = fixture.CreateViewModel();

        await viewModel.PickDirectoryCommand.ExecuteAsync(null);

        Assert.Equal(target, fixture.Store.Settings.SaveDirectory);
        Assert.Equal(target, viewModel.Settings.SaveDirectory);
        Assert.Contains("保存目录已更新", viewModel.OperationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchPopulatesRowsWithHistoryAndOpenLoadsEpisodes()
    {
        using var fixture = new DownloadFixture();
        fixture.Store.AppendRecord(new DanmuDownloadRecord(
            1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "测试番剧 from qq", "第1集", 11, 1, "qq", "xml",
            DownloadRecordStatus.Success.Key(), "a.xml", "测试番剧/a.xml", Path.Combine(fixture.SaveDirectory, "a.xml"), 5, 10, 1, 200, null, 9));
        var viewModel = fixture.CreateViewModel();
        viewModel.Keyword = "测试番剧";

        await viewModel.SearchCommand.ExecuteAsync(null);

        var row = Assert.Single(viewModel.AnimeRows);
        Assert.Equal(9, row.AnimeId);
        Assert.Contains("已下载", row.HistoryText, StringComparison.Ordinal);

        await viewModel.OpenAnimeCommand.ExecuteAsync(row);

        Assert.Equal(3, viewModel.EpisodeRows.Count);
        Assert.Equal(EpisodeDownloadState.Success, viewModel.EpisodeRows[0].State.State);
        Assert.Equal("全部来源", viewModel.SelectedSourceFilter);
    }

    [Fact]
    public async Task SearchWithoutServiceReportsExplicitError()
    {
        using var fixture = new DownloadFixture();
        var paths = new AppPaths(fixture.Root, Path.Combine(fixture.Root, "appdata"));
        var context = new RuntimeApiContext(paths, new StoppedRuntimeController(), new StubAdminSessionService());
        var viewModel = new DanmuDownloadPageViewModel(context, fixture.Client, fixture.EnvClient, fixture.Store, fixture.Service, fixture.Dialogs, fixture.Diagnostics);
        viewModel.Keyword = "测试番剧";

        await viewModel.SearchCommand.ExecuteAsync(null);

        Assert.Contains("服务未运行", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Client.SearchCalls);
    }

    [AvaloniaFact]
    public async Task StartDownloadEnqueuesRunsQueueAndRecordsSuccess()
    {
        using var fixture = new DownloadFixture();
        var viewModel = fixture.CreateViewModel();
        viewModel.Keyword = "测试番剧";
        await viewModel.SearchCommand.ExecuteAsync(null);
        await viewModel.OpenAnimeCommand.ExecuteAsync(viewModel.AnimeRows[0]);
        viewModel.EpisodeRows[0].IsSelected = true;
        var view = new DanmuDownloadView { DataContext = viewModel };
        var window = new Window { Width = 1280, Height = 800, Content = view };
        window.Show();
        window.Measure(new Size(1280, 800));
        window.Arrange(new Rect(0, 0, 1280, 800));
        var startButton = view.FindControl<Button>("StartDownloadButton");
        Assert.NotNull(startButton);
        Assert.True(startButton!.IsEnabled);

        Assert.NotNull(startButton.Command);
        Assert.True(startButton.Command!.CanExecute(startButton.CommandParameter));
        startButton.Command.Execute(startButton.CommandParameter);
        await WaitUntilAsync(() => !viewModel.IsDownloading);

        var task = Assert.Single(fixture.Store.QueueTasks);
        Assert.Equal(DownloadQueueStatus.Success, task.StatusEnum);
        Assert.Contains("已保存", task.LastDetail, StringComparison.Ordinal);
        Assert.Equal(EpisodeDownloadState.Success, viewModel.EpisodeRows[0].State.State);
        var record = Assert.Single(fixture.Store.Records);
        Assert.Equal(DownloadRecordStatus.Success, record.StatusEnum);
        Assert.True(File.Exists(record.FilePath));
        Assert.Contains("队列执行完成", viewModel.ProgressSummary, StringComparison.Ordinal);

        // 这一轮确实下载到了东西，界面应停到「记录与库」——用户下一件事就是核对结果。
        Assert.Equal(DanmuDownloadSection.Records, viewModel.SelectedSectionOption.Value);
    }

    /// <summary>
    /// 队列分组默认折叠：集多时全部展开要滚很久。展开态按番名记住，
    /// 因为每次进度刷新都会重建分组对象，状态只放对象上会被下一帧覆盖。
    /// </summary>
    [AvaloniaFact]
    public async Task QueueGroupsCollapseByDefaultAndRememberUserChoice()
    {
        using var fixture = new DownloadFixture();
        var viewModel = fixture.CreateViewModel();
        await SeedEpisodesAsync(viewModel);
        viewModel.EpisodeRows[0].IsSelected = true;
        viewModel.StartDownloadCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsDownloading);

        var group = Assert.Single(viewModel.QueueGroups);
        Assert.Equal("测试番剧", group.AnimeTitle);
        // 本轮已跑完（没有 Running 的集），默认折叠。
        Assert.False(group.IsExpanded);
        Assert.Contains("展开", group.ExpandToggleText, StringComparison.Ordinal);
        Assert.Contains("成功 1", group.EpisodesSummaryText, StringComparison.Ordinal);

        viewModel.ToggleQueueGroupCommand.Execute(group);
        Assert.True(group.IsExpanded);
        Assert.Equal("收起", group.ExpandToggleText);

        // 重建分组后（离开再回到队列分区各会重建一次），用户选的展开态必须还在。
        RebuildQueueGroups(viewModel);
        Assert.True(Assert.Single(viewModel.QueueGroups).IsExpanded);
        Assert.Equal("收起", Assert.Single(viewModel.QueueGroups).ExpandToggleText);

        // 再点一次回到折叠，并且这次选择同样被记住。
        viewModel.ToggleQueueGroupCommand.Execute(Assert.Single(viewModel.QueueGroups));
        RebuildQueueGroups(viewModel);
        Assert.False(Assert.Single(viewModel.QueueGroups).IsExpanded);
    }

    /// <summary>
    /// 逼队列分组重建：切到别的分区再切回队列分区。分组对象每次重建都是新实例，
    /// 只把状态改在对象上的实现在这里会现形。
    /// </summary>
    private static void RebuildQueueGroups(DanmuDownloadPageViewModel viewModel)
    {
        viewModel.SelectedSectionOption = viewModel.SectionOptions.Single(option => option.Value == DanmuDownloadSection.Search);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        viewModel.SelectedSectionOption = viewModel.SectionOptions.Single(option => option.Value == DanmuDownloadSection.Queue);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// 用户取消的那一轮不跳分区：下载中途取消还硬切到记录页会很突兀。
    /// </summary>
    [AvaloniaFact]
    public async Task CancelledRunDoesNotSwitchToRecords()
    {
        using var fixture = new DownloadFixture();
        var viewModel = fixture.CreateViewModel();
        await SeedEpisodesAsync(viewModel);
        viewModel.EpisodeRows[0].IsSelected = true;

        viewModel.PauseDownloadCommand.Execute(null);

        Assert.Equal(DanmuDownloadSection.Search, viewModel.SelectedSectionOption.Value);
    }

    [Fact]
    public async Task StartDownloadWithoutDirectoryReportsVisibleErrorAndDoesNotEnqueue()
    {
        using var fixture = new DownloadFixture();
        fixture.Store.SaveSettings(new DanmuDownloadSettings(SaveDirectory: string.Empty));
        var viewModel = fixture.CreateViewModel();
        await SeedEpisodesAsync(viewModel);
        viewModel.EpisodeRows[0].IsSelected = true;

        viewModel.StartDownloadCommand.Execute(null);

        Assert.Contains("选择保存目录", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(fixture.Store.QueueTasks);
        Assert.False(viewModel.IsDownloading);
    }

    [Fact]
    public async Task DownloadFailureMarksEpisodeFailedAndTaskFailed()
    {
        using var fixture = new DownloadFixture();
        fixture.Client.DownloadShouldFail = true;
        var viewModel = fixture.CreateViewModel();
        await SeedEpisodesAsync(viewModel);

        viewModel.EpisodeRows[0].IsSelected = true;
        viewModel.StartDownloadCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsDownloading);

        var task = Assert.Single(fixture.Store.QueueTasks);
        Assert.Equal(DownloadQueueStatus.Failed, task.StatusEnum);
        Assert.Contains("episode not found", task.LastDetail, StringComparison.Ordinal);
        Assert.Equal(EpisodeDownloadState.Failed, viewModel.EpisodeRows[0].State.State);
    }

    [Fact]
    public async Task RetryFailedQueueTasksResetsAndReruns()
    {
        using var fixture = new DownloadFixture();
        fixture.Client.DownloadShouldFail = true;
        var viewModel = fixture.CreateViewModel();
        await SeedEpisodesAsync(viewModel);
        viewModel.EpisodeRows[0].IsSelected = true;
        viewModel.StartDownloadCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsDownloading);
        Assert.Equal(DownloadQueueStatus.Failed, fixture.Store.QueueTasks[0].StatusEnum);

        fixture.Client.DownloadShouldFail = false;
        viewModel.RetryFailedQueueTasksCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsDownloading);

        Assert.Equal(DownloadQueueStatus.Success, fixture.Store.QueueTasks[0].StatusEnum);
    }

    [Fact]
    public async Task PauseDownloadWithoutRunnerReportsMessage()
    {
        using var fixture = new DownloadFixture();
        var viewModel = fixture.CreateViewModel();

        viewModel.PauseDownloadCommand.Execute(null);

        Assert.Contains("当前没有正在执行的下载", viewModel.OperationMessage, StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ClearAndClearCompletedQueueManipulateStore()
    {
        using var fixture = new DownloadFixture();
        fixture.Store.EnqueueTasks([NewInput(1), NewInput(2)]);
        var viewModel = fixture.CreateViewModel();
        fixture.Store.SetTaskStatus(fixture.Store.QueueTasks[0].TaskId, DownloadQueueStatus.Success, "done");

        viewModel.ClearCompletedQueueTasksCommand.Execute(null);
        Assert.Single(fixture.Store.QueueTasks);

        viewModel.ClearQueueTasksCommand.Execute(null);
        Assert.Empty(fixture.Store.QueueTasks);
        Assert.Contains("队列已清空", viewModel.OperationMessage, StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task DeleteSelectedRemovesRecordsAndFiles()
    {
        using var fixture = new DownloadFixture();
        Directory.CreateDirectory(fixture.SaveDirectory);
        var filePath = Path.Combine(fixture.SaveDirectory, "a.xml");
        await File.WriteAllTextAsync(filePath, SampleXml);
        fixture.Store.AppendRecord(new DanmuDownloadRecord(
            1, 1, "番剧", "第1集", 11, 1, "qq", "xml", "success", "a.xml", "番剧/a.xml", filePath, 1, 10, 1, 200, null, 9));
        var viewModel = fixture.CreateViewModel();
        await viewModel.StartupSyncTask;
        fixture.Dialogs.Confirmation = true;

        Assert.Single(viewModel.RecordGroups);
        var episodeGroup = Assert.Single(viewModel.RecordGroups[0].Episodes);
        Assert.True(episodeGroup.CanPreview);

        await viewModel.OpenPreviewCommand.ExecuteAsync(episodeGroup);
        Assert.True(viewModel.IsPreviewOpen);
        Assert.NotNull(viewModel.Preview);
        Assert.Single(viewModel.PreviewRows);

        episodeGroup.IsSelected = true;
        await viewModel.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Empty(fixture.Store.Records);
        Assert.False(File.Exists(filePath));
        Assert.Contains("已清理 1 条记录", viewModel.OperationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncDirectoryImportsExistingFiles()
    {
        using var fixture = new DownloadFixture();
        var viewModel = fixture.CreateViewModel();
        await viewModel.StartupSyncTask;

        var importDirectory = Path.Combine(fixture.Root, "already");
        Directory.CreateDirectory(Path.Combine(importDirectory, "番剧"));
        await File.WriteAllTextAsync(Path.Combine(importDirectory, "番剧", "E01.xml"), SampleXml);
        fixture.Store.SaveSettings(new DanmuDownloadSettings(SaveDirectory: importDirectory));

        await viewModel.SyncDirectoryCommand.ExecuteAsync(null);

        Assert.Single(fixture.Store.Records);
        Assert.Contains("新增 1 条记录", viewModel.OperationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThrottlePresetAndCustomConfigPersist()
    {
        using var fixture = new DownloadFixture();
        var viewModel = fixture.CreateViewModel();

        viewModel.SelectedThrottle = DownloadThrottlePresetKind.Fast;
        Assert.Equal("fast", fixture.Store.Settings.ThrottlePreset);

        viewModel.CustomBaseDelayMs = "700";
        viewModel.ApplyCustomThrottleCommand.Execute(null);

        Assert.Equal("custom", fixture.Store.Settings.ThrottlePreset);
        Assert.Equal(DownloadThrottlePresetKind.Custom, viewModel.SelectedThrottle);
        Assert.Equal(700, fixture.Store.Settings.CustomBaseDelayMs);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task FormatAndConflictSelectionPersist()
    {
        using var fixture = new DownloadFixture();
        var viewModel = fixture.CreateViewModel();

        viewModel.SelectedFormat = DanmuDownloadFormat.DplayerJson;
        viewModel.SelectedConflict = DownloadConflictPolicy.Overwrite;

        Assert.Equal("dplayer.json", fixture.Store.Settings.DefaultFormat);
        Assert.Equal("overwrite", fixture.Store.Settings.ConflictPolicy);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task SelectFailedAndUnfinishedPickExpectedRows()
    {
        using var fixture = new DownloadFixture();
        fixture.Store.EnqueueTasks([NewInput(1), NewInput(2), NewInput(3)]);
        var viewModel = fixture.CreateViewModel();
        fixture.Store.SetTaskStatus(fixture.Store.QueueTasks[0].TaskId, DownloadQueueStatus.Success, "done");
        fixture.Store.SetTaskStatus(fixture.Store.QueueTasks[1].TaskId, DownloadQueueStatus.Failed, "下载失败：HTTP 404");

        viewModel.Keyword = "测试番剧";
        await viewModel.SearchCommand.ExecuteAsync(null);
        await viewModel.OpenAnimeCommand.ExecuteAsync(viewModel.AnimeRows[0]);
        Assert.Equal(EpisodeDownloadState.Success, viewModel.EpisodeRows[0].State.State);
        Assert.Equal(EpisodeDownloadState.Failed, viewModel.EpisodeRows[1].State.State);

        viewModel.SelectFailedCommand.Execute(null);
        Assert.True(viewModel.EpisodeRows[1].IsSelected);
        Assert.False(viewModel.EpisodeRows[0].IsSelected);

        viewModel.SelectUnfinishedCommand.Execute(null);
        Assert.False(viewModel.EpisodeRows[0].IsSelected);
        Assert.True(viewModel.EpisodeRows[1].IsSelected);
        Assert.True(viewModel.EpisodeRows[2].IsSelected);
    }

    private static async Task SeedEpisodesAsync(DanmuDownloadPageViewModel viewModel)
    {
        viewModel.Keyword = "测试番剧";
        await viewModel.SearchCommand.ExecuteAsync(null);
        await viewModel.OpenAnimeCommand.ExecuteAsync(viewModel.AnimeRows[0]);
        Assert.Equal(3, viewModel.EpisodeRows.Count);
    }

    private static DanmuDownloadInput NewInput(long episodeId) => new(
        string.Empty, "测试番剧", $"第{episodeId}集", episodeId, (int)episodeId, "qq",
        DanmuDownloadFormat.Xml, DanmuDownloadDefaults.FileNameTemplate, DownloadConflictPolicy.Rename, 9);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (var attempt = 0; attempt < 200 && !predicate(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(predicate(), "下载队列未在测试窗口内完成");
    }

    [Fact]
    public async Task SelectingEpisodeEnablesStartDownloadImmediately()
    {
        using var fixture = new DownloadFixture();
        var viewModel = fixture.CreateViewModel();
        await SeedEpisodesAsync(viewModel);

        Assert.False(viewModel.CanStartDownload);
        Assert.False(viewModel.HasSelection);
        viewModel.EpisodeRows[0].IsSelected = true;

        Assert.True(viewModel.HasSelection);
        Assert.True(viewModel.CanStartDownload);
        viewModel.EpisodeRows[0].IsSelected = false;
        Assert.False(viewModel.CanStartDownload);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task OpenAnimeSwitchesToEpisodeStageAndBackRestoresList()
    {
        using var fixture = new DownloadFixture();
        var viewModel = fixture.CreateViewModel();
        Assert.Equal(DownloadSearchStage.AnimeList, viewModel.CurrentStage);
        await SeedEpisodesAsync(viewModel);

        Assert.Equal(DownloadSearchStage.EpisodeList, viewModel.CurrentStage);
        Assert.True(viewModel.HasEpisodeRows);

        viewModel.BackToAnimeListCommand.Execute(null);

        Assert.Equal(DownloadSearchStage.AnimeList, viewModel.CurrentStage);
        Assert.Empty(viewModel.EpisodeRows);
    }

    [Fact]
    public async Task SearchRowsExposeSourceAndIdWithoutFromSuffix()
    {
        using var fixture = new DownloadFixture();
        var viewModel = fixture.CreateViewModel();
        viewModel.Keyword = "测试番剧";
        await viewModel.SearchCommand.ExecuteAsync(null);

        var row = Assert.Single(viewModel.AnimeRows);
        Assert.Equal("测试番剧", row.DisplayTitle);
        Assert.Equal("来源：qq", row.SourceText);
        Assert.Equal("ID：9 · 3 集", row.MetaText);
        Assert.Equal("尚无下载记录", row.HistoryText);
        Assert.False(row.HasHistory);
    }

    [Fact]
    public async Task RecordLibraryUsesSeparateEpisodeStage()
    {
        using var fixture = new DownloadFixture();
        Directory.CreateDirectory(fixture.SaveDirectory);
        var filePath = Path.Combine(fixture.SaveDirectory, "a.xml");
        await File.WriteAllTextAsync(filePath, SampleXml);
        fixture.Store.AppendRecord(new DanmuDownloadRecord(
            1, 1, "番剧", "第1集", 11, 1, "qq", "xml", "success", "a.xml", "番剧/a.xml", filePath, 1, 10, 1, 200, null, 9));
        var viewModel = fixture.CreateViewModel();
        await viewModel.StartupSyncTask;

        Assert.Equal(DownloadRecordStage.AnimeList, viewModel.CurrentRecordStage);
        var group = Assert.Single(viewModel.RecordGroups);
        viewModel.OpenRecordGroupCommand.Execute(group);

        Assert.Equal(DownloadRecordStage.EpisodeList, viewModel.CurrentRecordStage);
        Assert.Same(group, viewModel.SelectedRecordGroup);
        Assert.True(viewModel.HasSelectedRecordGroup);

        viewModel.BackToRecordAnimeListCommand.Execute(null);
        Assert.Equal(DownloadRecordStage.AnimeList, viewModel.CurrentRecordStage);
        Assert.Null(viewModel.SelectedRecordGroup);
    }

    [Fact]
    public void FavoriteVisibleListReusesWrappers()
    {
        var item = new DanmuFavoriteItem("生万物_S01", "生万物", "qq", ["qq"], "", 12, 3, 1, 1, null);
        using var fixture = new DownloadFixture();
        var root = new DanmuTestPageViewModel(
            fixture.CreateContext(),
            new FakeDownloadClient(),
            fixture.Dialogs,
            fixture.Diagnostics);
        var favorites = new FavoritesPageViewModel(root, fixture.CreateContext(), new FakeDownloadClient(), fixture.Dialogs, fixture.Diagnostics);
        favorites.Favorites = [item];
        var first = Assert.Single(favorites.VisibleFavorites);
        favorites.SearchText = "不存在";
        Assert.Empty(favorites.VisibleFavorites);
        favorites.SearchText = "生万物";
        var second = Assert.Single(favorites.VisibleFavorites);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task SourceFilterShowsExplicitEmptyState()
    {
        using var fixture = new DownloadFixture();
        var viewModel = fixture.CreateViewModel();
        await SeedEpisodesAsync(viewModel);

        viewModel.SourceOptions.Add("不存在的来源");
        viewModel.SelectedSourceFilter = "不存在的来源";

        Assert.False(viewModel.HasEpisodeRows);
        Assert.True(viewModel.ShowEmptyEpisodes);
        Assert.DoesNotContain(viewModel.EpisodeRows, row => row.IsVisible);
    }

    [Fact]
    public void EpisodeStateCanEnterRunningAgainForExplicitRetry()
    {
        var row = new EpisodeRowViewModel(11, 1, "第1集", "qq", "", new EpisodeUiState(), () => { });

        row.UpdateState(new EpisodeUiState(EpisodeDownloadState.Failed, 1, "下载失败"));
        row.UpdateState(new EpisodeUiState(EpisodeDownloadState.Running, 0, "开始重试"));

        Assert.Equal(EpisodeDownloadState.Running, row.State.State);
        Assert.Equal("开始重试", row.Detail);
    }

    [AvaloniaFact]
    public void DanmuDownloadViewRendersSearchSectionWithRealViewModel()
    {
        using var fixture = new DownloadFixture();
        var viewModel = fixture.CreateViewModel();
        var view = new DanmuDownloadView { DataContext = viewModel };
        var window = new Window { Width = 1280, Height = 800, Content = view };
        window.Show();
        window.Measure(new Size(1280, 800));
        window.Arrange(new Rect(0, 0, 1280, 800));

        var search = view.FindControl<ScrollViewer>("SearchPanel");
        var queue = view.FindControl<ScrollViewer>("QueuePanel");
        var records = view.FindControl<ScrollViewer>("RecordsPanel");
        var settings = view.FindControl<ScrollViewer>("SettingsPanel");
        var animeListStage = view.FindControl<StackPanel>("AnimeListStage");
        var episodeStage = view.FindControl<StackPanel>("EpisodeStage");
        Assert.NotNull(search);
        Assert.NotNull(queue);
        Assert.NotNull(records);
        Assert.NotNull(settings);
        Assert.NotNull(animeListStage);
        Assert.NotNull(episodeStage);
        Assert.True(search!.IsVisible, "搜索分区应可见");
        Assert.False(queue!.IsVisible);
        Assert.False(records!.IsVisible);
        Assert.False(settings!.IsVisible);
        Assert.True(animeListStage!.IsVisible, "动漫列表阶段应可见");
        Assert.False(episodeStage!.IsVisible, "剧集阶段应隐藏");
        Assert.True(view.GetVisualDescendants().Count() > 20,
            "视图应渲染出可视子节点；实际 " + view.GetVisualDescendants().Count() +
            "；Content=" + view.Content?.GetType().FullName +
            "；子节点=" + string.Join(",", view.GetVisualDescendants().Select(item => item.GetType().Name).Take(15)));

        var sectionList = view.FindControl<ListBox>("SectionList");
        Assert.NotNull(sectionList);
        Assert.Equal(4, viewModel.SectionOptions.Count);
        Assert.NotNull(viewModel.SelectedSectionOption);

        viewModel.CurrentStage = DownloadSearchStage.EpisodeList;
        Assert.False(animeListStage.IsVisible, "切换后动漫列表阶段应隐藏");
        Assert.True(episodeStage.IsVisible, "切换后剧集阶段应可见");

        var recordAnimeStage = view.FindControl<StackPanel>("RecordAnimeStage");
        var recordEpisodeStage = view.FindControl<StackPanel>("RecordEpisodeStage");
        Assert.NotNull(recordAnimeStage);
        Assert.NotNull(recordEpisodeStage);
        viewModel.SelectedSectionOption = viewModel.SectionOptions[2];
        Assert.True(recordAnimeStage!.IsVisible, "记录剧列表阶段应可见");
        Assert.False(recordEpisodeStage!.IsVisible, "记录分集阶段应隐藏");
        viewModel.CurrentRecordStage = DownloadRecordStage.EpisodeList;
        Assert.False(recordAnimeStage.IsVisible, "切换后记录剧列表应隐藏");
        Assert.True(recordEpisodeStage.IsVisible, "切换后记录分集应可见");
    }

    /// <summary>
    /// 下载页重构后的视觉契约（不靠看图）：
    /// 分区选择用 rail-list、卡片统一 card、行分隔统一 rule，
    /// 并且四个分区的 ScrollViewer 始终互斥可见。
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void DanmuDownloadViewUsesSharedShellVocabulary(bool dark)
    {
        using var fixture = new DownloadFixture();
        var viewModel = fixture.CreateViewModel();
        var view = new DanmuDownloadView { DataContext = viewModel };
        var window = new Window
        {
            Width = 1280,
            Height = 800,
            Content = view,
            Padding = new Thickness(24),
            RequestedThemeVariant = dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light,
        };
        window.Show();
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var sectionList = view.FindControl<ListBox>("SectionList")!;
            Assert.Contains("rail-list", sectionList.Classes);
            Assert.Equal(4, sectionList.ItemCount);

            var headerTexts = view.GetVisualDescendants().OfType<TextBlock>()
                .Select(block => block.Text)
                .ToList();
            Assert.Contains("弹幕下载", headerTexts);

            // 旧词汇必须彻底退场：旧容器与旧按钮都是「同一元素两种外观」的来源。
            var borders = view.GetVisualDescendants().OfType<Border>().ToList();
            Assert.DoesNotContain(borders, border => border.Classes.Contains("dashboard-panel"));
            Assert.DoesNotContain(borders, border => border.Classes.Contains("muted-row"));
            Assert.Contains(borders, border => border.Classes.Contains("card"));

            var buttons = view.GetVisualDescendants().OfType<Button>().ToList();
            foreach (var legacy in new[] { "primary-action", "secondary-action", "ghost-action", "compact-action", "back-action", "danger-action" })
            {
                Assert.DoesNotContain(buttons, button => button.Classes.Contains(legacy));
            }

            // 四个分区互斥可见：任意时刻只能有一个 ScrollViewer 亮着。
            var panels = new[]
            {
                view.FindControl<ScrollViewer>("SearchPanel")!,
                view.FindControl<ScrollViewer>("QueuePanel")!,
                view.FindControl<ScrollViewer>("RecordsPanel")!,
                view.FindControl<ScrollViewer>("SettingsPanel")!,
            };
            Assert.Single(panels, panel => panel.IsVisible);

            foreach (var section in viewModel.SectionOptions)
            {
                viewModel.SelectedSectionOption = section;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var expected = viewModel.SelectedSectionOption.Value switch
                {
                    DanmuDownloadSection.Search => panels[0],
                    DanmuDownloadSection.Queue => panels[1],
                    DanmuDownloadSection.Records => panels[2],
                    _ => panels[3],
                };
                Assert.Same(expected, Assert.Single(panels, panel => panel.IsVisible));
                Assert.True(expected.Bounds.Width > 0, $"{section.Title} 分区没有实际宽度");
                Assert.True(expected.Bounds.Right <= window.Width, $"{section.Title} 分区横向溢出：{expected.Bounds}");
            }

            // 回到搜索分区并检查关键控件确实可点（宽度是实测出来的，不是声明值）。
            viewModel.SelectedSectionOption = viewModel.SectionOptions[0];
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var startDownload = view.FindControl<Button>("StartDownloadButton");
            Assert.NotNull(startDownload);
            Assert.Equal("开始下载", startDownload!.Content);
            Assert.Contains("primary", startDownload.Classes);
            // 该按钮只在「剧集阶段且确实有剧集行」时才被实例化（宿主 Border 绑 HasEpisodeRows），
            // 所以先走真实的搜索 → 打开动漫两步，再验证它真的被测出宽度。
            viewModel.Keyword = "测试番剧";
            viewModel.SearchCommand.Execute(null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var animeRow = Assert.Single(viewModel.AnimeRows);
            viewModel.OpenAnimeCommand.Execute(animeRow);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.Equal(DownloadSearchStage.EpisodeList, viewModel.CurrentStage);
            Assert.True(viewModel.HasEpisodeRows, "打开动漫后应加载出剧集行");
            Assert.True(startDownload.IsVisible, "剧集阶段应显示开始下载按钮");
            Assert.True(startDownload.Bounds.Width > 0, $"开始下载按钮必须实际测量出宽度，实际 {startDownload.Bounds}");
        }
        finally
        {
            window.Close();
        }
    }

    private sealed class StubRuntimeController : IRuntimeController
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

    private sealed class StoppedRuntimeController : IRuntimeController
    {
        public RuntimeSnapshot Snapshot { get; } = new(DesktopRuntimeState.Stopped);
        event EventHandler<RuntimeSnapshot>? IRuntimeController.SnapshotChanged { add { } remove { } }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AdoptionResult> AdoptAsync(CancellationToken cancellationToken = default) => Task.FromResult(AdoptionResult.Failure(AdoptionFailureKind.NotFound, Snapshot, "unused"));
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

    private sealed class StubEnvClient : ICoreEnvClient
    {
        public Task<CoreEnvDeleteResult> DeleteAsync(string host, int port, string? token, string? adminToken, string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(CoreEnvDeleteResult.Success());

        public Task<CoreEnvSetResult> SetAsync(string host, int port, string? token, string? adminToken, string key, string value, CancellationToken cancellationToken = default)
        {
            SetCalls.Add((key, value));
            return Task.FromResult(CoreEnvSetResult.Success());
        }

        public Task<CoreEnvValueResult> ReadConfigValueAsync(string host, int port, string? token, string? adminToken, string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(CoreEnvValueResult.Success("5"));

        public List<(string Key, string Value)> SetCalls { get; } = [];
    }

    private sealed class FakeDownloadClient : IDanmuApiClient
    {
        public int SearchCalls { get; private set; }
        public bool DownloadShouldFail { get; set; }

        public Task<DanmuDownloadPayload> DownloadCommentAsync(string host, int port, string? token, long episodeId, string formatValue, TimeSpan? requestTimeout = null, CancellationToken cancellationToken = default)
        {
            if (DownloadShouldFail)
            {
                throw new DanmuApiException(DanmuApiFailureKind.NotFound, "episode not found", statusCode: 404);
            }

            return Task.FromResult(new DanmuDownloadPayload(
                200,
                "application/xml",
                Encoding.UTF8.GetBytes(DanmuDownloadPageViewModelTests.SampleXml),
                formatValue,
                1));
        }

        public Task<DanmuRawApiResponse> SendRawAsync(string host, int port, string? token, string apiKey, IReadOnlyDictionary<string, string?> parameters, string? jsonBody = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DanmuSearchAnimeResult> SearchAnimeAsync(string host, int port, string? token, string keyword, CancellationToken cancellationToken = default)
        {
            SearchCalls++;
            var animes = new List<DanmuAnime>();
            var episodeCount = keyword == "测试番剧" ? 3 : 1;
            animes.Add(new DanmuAnime(9, "b", "测试番剧", "tv", "TV", "", "", episodeCount, 0, false, "qq", []));
            return Task.FromResult(new DanmuSearchAnimeResult(true, animes));
        }

        public Task<DanmuSearchEpisodesResult> SearchEpisodesAsync(string host, int port, string? token, string anime, string? episode = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DanmuMatchResult> MatchAsync(string host, int port, string? token, string fileName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DanmuBangumiResult> GetBangumiAsync(string host, int port, string? token, int animeId, CancellationToken cancellationToken = default)
        {
            var episodes = new List<DanmuEpisode>();
            for (var index = 1; index <= 3; index++)
            {
                episodes.Add(new DanmuEpisode("s1", 10 + index, $"【qq】第{index}集", index.ToString(System.Globalization.CultureInfo.InvariantCulture), "", $"https://example.invalid/{index}"));
            }

            return Task.FromResult(new DanmuBangumiResult(true, new DanmuBangumi(9, "b", "测试番剧", "", false, 0, false, 0, "tv", "TV", [], episodes)));
        }

        public Task<DanmuResult> GetCommentAsync(string host, int port, string? token, int commentId, bool includeDuration = true, string format = "json", CancellationToken cancellationToken = default, bool segmentFlag = false) =>
            throw new NotSupportedException();

        public Task<DanmuResult> GetCommentByUrlAsync(string host, int port, string? token, string videoUrl, bool includeDuration = true, string format = "json", CancellationToken cancellationToken = default, bool segmentFlag = false) =>
            throw new NotSupportedException();

        public Task<DanmuResult> GetSegmentCommentAsync(string host, int port, string? token, JsonElement segment, string format = "json", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DanmuFavoriteListResult> GetFavoritesAsync(string host, int port, string? token, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> AddFavoriteAsync(string host, int port, string? token, string? adminToken, string keyword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> RemoveFavoriteAsync(string host, int port, string? token, string? adminToken, string keyword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> RefreshFavoriteAsync(string host, int port, string? token, string? adminToken, string keyword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> SetFavoriteScheduleAsync(string host, int port, string? token, string? adminToken, string keyword, DanmuFavoriteSchedule? schedule, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
