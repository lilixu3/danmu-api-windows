using System.Text;
using Avalonia.Controls;
using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
using DanmuApi.Core;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

/// <summary>
/// 本地弹幕页（工具页新分类）的行为契约：数据来源优先级（缓存快路径 → 核心接口，失败必须显式）、
/// 分组与筛选、写权限三态、删除（单集/整季/整部）、上传校验与批量导入。
/// </summary>
public sealed partial class LocalDanmuPageViewModelTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    /// <summary>
    /// 回归（用户实测反馈）：批量导入期间**不能**每个文件弹一次窗——进度要显示在面板的进度条里；
    /// 全部成功后要给一条结果提示并直接回到列表，而不是停在导入界面等用户点取消。
    /// </summary>
    [Fact]
    public async Task BatchImportUsesInlineProgressAndReturnsToTheListOnSuccess()
    {
        var folder = Path.Combine(_directory.Path, "batch-ok");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "逐玉 (2026) E05.xml"), "<i></i>");
        File.WriteAllText(Path.Combine(folder, "逐玉 (2026) E06.xml"), "<i></i>");
        var dialogs = new RecordingDialogService { PickedFolderWithTitle = folder };
        var client = new StubClient
        {
            UploadResult = CoreLocalDanmuResourceResult.Success(Resource("逐玉|2026|tv|5", "逐玉")),
        };
        await using var viewModel = Create(
            new StubCacheReader(LocalDanmuCacheReadResult.Success([])),
            client,
            dialogs,
            adminSession: new StubAdminSessionService(adminMode: true, configured: true, sessionToken: "admin-token"));
        await viewModel.LoadAsync(force: true);

        await viewModel.ImportFolderCommand.ExecuteAsync(null);
        await viewModel.SubmitBatchCommand.ExecuteAsync(null);

        Assert.Equal(2, client.UploadCalls);
        // 批量不上任何弹窗：既没有进度窗，也没有每个文件的成功窗。
        Assert.Empty(dialogs.ProgressTitles);
        var message = Assert.Single(dialogs.Messages);
        Assert.False(message.IsError);
        Assert.Contains("导入完成：成功 2 个", message.Message, StringComparison.Ordinal);
        // 进度条走满，面板自动关闭并回到列表。
        Assert.Equal(1d, viewModel.BatchProgress, 3);
        Assert.False(viewModel.IsBatchPanelOpen);
        Assert.Empty(viewModel.PendingItems);
        Assert.Equal("返回列表", viewModel.BatchActionText);
    }

    [Fact]
    public async Task BatchImportKeepsThePanelForRetryWhenSomeFilesFailed()
    {
        var folder = Path.Combine(_directory.Path, "batch-partial");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "逐玉 (2026) E05.xml"), "<i></i>");
        File.WriteAllText(Path.Combine(folder, "逐玉 (2026) E06.xml"), "<i></i>");
        var dialogs = new RecordingDialogService { PickedFolderWithTitle = folder };
        var client = new StubClient
        {
            UploadResult = CoreLocalDanmuResourceResult.Failure("解析失败：文件中没有有效弹幕", LocalDanmuFailureKind.Validation),
        };
        await using var viewModel = Create(
            new StubCacheReader(LocalDanmuCacheReadResult.Success([])),
            client,
            dialogs,
            adminSession: new StubAdminSessionService(adminMode: true, configured: true, sessionToken: "admin-token"));
        await viewModel.LoadAsync(force: true);

        await viewModel.ImportFolderCommand.ExecuteAsync(null);
        await viewModel.SubmitBatchCommand.ExecuteAsync(null);

        Assert.Empty(dialogs.ProgressTitles);
        var message = Assert.Single(dialogs.Messages);
        Assert.True(message.IsError);
        Assert.Contains("失败 2 个", message.Message, StringComparison.Ordinal);
        // 失败原因要看得见（不许只标个「失败」）。
        Assert.Contains("文件中没有有效弹幕", message.Message, StringComparison.Ordinal);
        // 有失败时留在面板里，便于「重试失败」。
        Assert.True(viewModel.IsBatchPanelOpen);
        Assert.True(viewModel.HasFailedItems);
        Assert.All(viewModel.PendingItems, item => Assert.Equal("失败", item.StatusText));
    }

    /// <summary>
    /// 回归（用户实测反馈）：导入后统计带里「文件 / 弹幕 / 占用」必须跟着刷新。
    /// 它们是从分组派生的属性，重建列表时如果只 raise 了 StatsText（右侧那句汇总），
    /// 就会出现「资源=1、文件=0、弹幕=0、占用=0，右侧汇总却正常」。
    /// 这里断言的是**通知**而不是取值：取值每次都会重新计算，只断言取值抓不到这个 bug。
    /// </summary>
    [Fact]
    public async Task RebuildNotifiesTheDerivedStatProperties()
    {
        var cache = new StubCacheReader(LocalDanmuCacheReadResult.Success([
            Resource("逐玉|2026|tv|1", "逐玉", count: 7),
            Resource("逐玉|2026|tv|2", "逐玉", episode: 2, count: 3),
        ]));
        await using var viewModel = Create(cache, new StubClient());
        var notified = new List<string>();
        viewModel.PropertyChanged += (_, args) => notified.Add(args.PropertyName ?? string.Empty);

        await viewModel.LoadAsync(force: true);

        Assert.Contains(nameof(LocalDanmuPageViewModel.FileCount), notified);
        Assert.Contains(nameof(LocalDanmuPageViewModel.CommentCount), notified);
        Assert.Contains(nameof(LocalDanmuPageViewModel.SizeText), notified);
        Assert.Equal(2, viewModel.FileCount);
        Assert.Equal(10, viewModel.CommentCount);
        Assert.Equal("4.0 KB", viewModel.SizeText);
    }

    /// <summary>扫描目录要有加载中状态（文件多时用户才不会以为卡死），扫描期间不能重复提交。</summary>
    [Fact]
    public async Task FolderScanExposesAScanningStateAndBlocksSubmit()
    {
        var folder = Path.Combine(_directory.Path, "slow-scan");
        Directory.CreateDirectory(folder);
        for (var index = 1; index <= 40; index++)
        {
            File.WriteAllText(Path.Combine(folder, $"逐玉 (2026) E{index:00}.xml"), "<i></i>");
        }

        var dialogs = new RecordingDialogService { PickedFolderWithTitle = folder };
        await using var viewModel = Create(
            new StubCacheReader(LocalDanmuCacheReadResult.Success([])),
            new StubClient(),
            dialogs,
            adminSession: new StubAdminSessionService(adminMode: true, configured: true, sessionToken: "admin-token"));
        await viewModel.LoadAsync(force: true);
        var scanningStates = new List<bool>();
        var submitStates = new List<bool>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(LocalDanmuPageViewModel.IsScanningImport))
            {
                scanningStates.Add(viewModel.IsScanningImport);
            }

            if (args.PropertyName == nameof(LocalDanmuPageViewModel.CanSubmitBatch))
            {
                submitStates.Add(viewModel.CanSubmitBatch);
            }
        };

        await viewModel.ImportFolderCommand.ExecuteAsync(null);

        Assert.Contains(true, scanningStates);
        Assert.False(viewModel.IsScanningImport);
        Assert.Equal(40, viewModel.PendingItems.Count);
        Assert.Contains(true, submitStates);
        Assert.True(viewModel.CanSubmitBatch);
        Assert.Contains("共 40 个文件", viewModel.BatchSummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FolderImportParsesRealTitleWithoutPlatformSuffix()
    {
        var folder = Path.Combine(_directory.Path, "core-anime-title");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "凡人修仙传 第1季(2026)【TV】from 腾讯_E05_第5集_腾讯.xml"), "<i></i>");
        var dialogs = new RecordingDialogService { PickedFolderWithTitle = folder };
        await using var viewModel = Create(
            new StubCacheReader(LocalDanmuCacheReadResult.Success([])),
            new StubClient(),
            dialogs,
            adminSession: new StubAdminSessionService(adminMode: true, configured: true, sessionToken: "admin-token"));
        await viewModel.LoadAsync(force: true);

        await viewModel.ImportFolderCommand.ExecuteAsync(null);

        var item = Assert.Single(viewModel.PendingItems);
        Assert.Equal("凡人修仙传", item.Title);
        Assert.Equal(2026, item.Year);
        Assert.Equal(5, item.Episode);
        Assert.Null(item.Validate(DateTimeOffset.Now.Year));
    }

    [Fact]
    public async Task LoadPrefersCacheFastPathAndGroupsResources()
    {
        var cache = new StubCacheReader(LocalDanmuCacheReadResult.Success([
            Resource("逐玉|2026|tv|2", "逐玉", episode: 2, count: 10),
            Resource("逐玉|2026|tv|1", "逐玉", episode: 1, count: 30, updatedAt: "2026-09-11T10:00:00.000Z"),
            Resource("逐玉|2026|tv|s2|1", "逐玉", season: 2, episode: 1, count: 5),
        ]));
        var client = new StubClient();
        var diagnostics = new RecordingDiagnostics();
        await using var viewModel = Create(cache, client, diagnostics: diagnostics);

        await viewModel.LoadAsync(force: true);

        Assert.Empty(diagnostics.Messages);
        Assert.Equal(0, client.ListCalls);
        // 第 1 季两集 + 第 2 季一集 = 两个组
        Assert.Equal(2, viewModel.Groups.Count);
        var seasonOne = viewModel.Groups.Single(group => group.Season == 1);
        Assert.Equal(2, seasonOne.Episodes.Count);
        // 组内按集号升序
        Assert.Equal([1, 2], seasonOne.Episodes.Select(row => row.Resource.Episode));
        Assert.Equal(3, viewModel.FileCount);
        Assert.Equal(45, viewModel.CommentCount);
        Assert.Equal("2 个资源 · 3 个文件 · 6.0 KB", viewModel.StatsText);
    }

    [Fact]
    public async Task LoadFallsBackToCoreApiAndKeepsTheReasonInDiagnostics()
    {
        var cache = new StubCacheReader(LocalDanmuCacheReadResult.Unavailable("字段顺序可能已变化"));
        var client = new StubClient
        {
            ListResult = CoreLocalDanmuListResult.Success([Resource("甲|2026|tv|1", "甲")]),
        };
        var diagnostics = new RecordingDiagnostics();
        await using var viewModel = Create(cache, client, diagnostics: diagnostics);

        await viewModel.LoadAsync(force: true);

        Assert.Equal(1, client.ListCalls);
        Assert.Single(viewModel.Groups);
        Assert.Contains(diagnostics.Messages, message => message.Contains("快路径未命中", StringComparison.Ordinal));
        Assert.Equal(string.Empty, viewModel.Diagnostic);
    }

    [Fact]
    public async Task LoadSurfacesApiFailureInsteadOfShowingStaleData()
    {
        var client = new StubClient
        {
            ListResult = CoreLocalDanmuListResult.Failure(
                "当前核心版本不支持本地弹幕，请先到「核心」页更新核心",
                LocalDanmuFailureKind.CoreUnsupported),
        };
        await using var viewModel = Create(new StubCacheReader(LocalDanmuCacheReadResult.Unavailable("核心落盘字段顺序变化")), client);

        await viewModel.LoadAsync(force: true);

        Assert.Empty(viewModel.Groups);
        Assert.True(viewModel.HasDiagnostic);
        Assert.Contains("请先到「核心」页更新核心", viewModel.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAndTypeFilterNarrowTheVisibleList()
    {
        var cache = new StubCacheReader(LocalDanmuCacheReadResult.Success([
            Resource("逐玉|2026|tv|1", "逐玉"),
            Resource("别的剧|2025|tv|1", "别的剧"),
            Resource("某电影|2024|movie|all", "某电影", type: LocalDanmuTypes.Movie, episode: null),
        ]));
        await using var viewModel = Create(cache, new StubClient());
        await viewModel.LoadAsync(force: true);
        Assert.Equal(3, viewModel.Groups.Count);

        viewModel.SearchText = "逐玉";
        Assert.Single(viewModel.Groups);

        viewModel.SearchText = string.Empty;
        viewModel.SelectFilterCommand.Execute(viewModel.FilterOptions.Single(chip => chip.Value == LocalDanmuTypeFilter.Movie));
        Assert.Single(viewModel.Groups);
        Assert.Equal(LocalDanmuTypes.Movie, viewModel.Groups[0].Type);
        // 选中态要回写到筛选按钮上（自动化按它回读）
        Assert.True(viewModel.FilterOptions.Single(chip => chip.Value == LocalDanmuTypeFilter.Movie).IsActive);
        Assert.False(viewModel.FilterOptions.Single(chip => chip.Value == LocalDanmuTypeFilter.All).IsActive);
    }

    [Theory]
    [InlineData(true, true, false, LocalDanmuWriteAccess.Writable)]
    [InlineData(false, true, true, LocalDanmuWriteAccess.Writable)]
    [InlineData(false, true, false, LocalDanmuWriteAccess.AdminRequired)]
    [InlineData(false, false, false, LocalDanmuWriteAccess.ReadOnly)]
    public async Task WriteAccessFollowsAdminModeAndRelaxedFlag(
        bool adminMode,
        bool configured,
        bool relaxedFlag,
        LocalDanmuWriteAccess expected)
    {
        await using var viewModel = Create(
            new StubCacheReader(LocalDanmuCacheReadResult.Success([])),
            new StubClient(),
            adminSession: new StubAdminSessionService(adminMode: adminMode, configured: configured),
            relaxedFlag: relaxedFlag);

        await viewModel.LoadAsync(force: true);

        Assert.Equal(expected, viewModel.WriteAccess);
    }

    [Fact]
    public async Task WriteAccessAsksForAdminModeWhenTokenConfiguredButNotEntered()
    {
        await using var viewModel = Create(
            new StubCacheReader(LocalDanmuCacheReadResult.Success([])),
            new StubClient(),
            adminSession: new StubAdminSessionService(adminMode: false, configured: true));

        await viewModel.LoadAsync(force: true);

        Assert.Equal(LocalDanmuWriteAccess.AdminRequired, viewModel.WriteAccess);
        Assert.True(viewModel.NeedsAdminMode);
        Assert.False(viewModel.CanWrite);
    }

    [Fact]
    public async Task DeleteWholeTitleDeletesEverySeasonOfThatTitle()
    {
        var cache = new StubCacheReader(LocalDanmuCacheReadResult.Success([
            Resource("逐玉|2026|tv|1", "逐玉", episode: 1),
            Resource("逐玉|2026|tv|s2|1", "逐玉", season: 2, episode: 1),
            Resource("别的剧|2025|tv|1", "别的剧"),
        ]));
        var client = new StubClient();
        var dialogs = new RecordingDialogService { Confirmation = true };
        await using var viewModel = Create(
            cache,
            client,
            dialogs,
            adminSession: new StubAdminSessionService(adminMode: true, configured: true, sessionToken: "admin-token"));
        await viewModel.LoadAsync(force: true);
        // 「逐玉」在缓存里是两个组（第 1 季与第 2 季），整部删除要覆盖到另一个季。
        var group = viewModel.Groups.Single(item => item.Title == "逐玉" && item.Season == 1);

        viewModel.DeleteTitleCommand.Execute(group);
        await WaitForDeletesAsync(client);

        Assert.Equal(2, client.DeletedKeys.Count);
        Assert.Contains("逐玉|2026|tv|1", client.DeletedKeys);
        Assert.Contains("逐玉|2026|tv|s2|1", client.DeletedKeys);
        Assert.DoesNotContain("别的剧|2025|tv|1", client.DeletedKeys);
        Assert.Contains(
            dialogs.ConfirmationMessages,
            entry => entry.Title == "确认批量删除" && entry.Message.Contains("全部 2 个文件", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UploadValidatesTheSameFieldsAsTheCoreBeforeCallingIt()
    {
        var client = new StubClient();
        var dialogs = new RecordingDialogService();
        await using var viewModel = Create(
            new StubCacheReader(LocalDanmuCacheReadResult.Success([])),
            client,
            dialogs,
            adminSession: new StubAdminSessionService(adminMode: true, configured: true, sessionToken: "admin-token"));
        await viewModel.LoadAsync(force: true);
        viewModel.ToggleUploadPanelCommand.Execute(null);

        // 没选文件
        await viewModel.SubmitUploadCommand.ExecuteAsync(null);
        Assert.Equal(0, client.UploadCalls);
        Assert.Contains(dialogs.Messages, entry => entry.Message.Contains("请选择要上传的弹幕文件", StringComparison.Ordinal));

        // 选了文件但标题为空
        var file = WriteTempFile("逐玉_E05_第5集_腾讯.xml", "<i></i>");
        dialogs.PickedFiles.Add(file);
        await viewModel.PickUploadFileCommand.ExecuteAsync(null);
        Assert.Equal("逐玉", viewModel.UploadTitle);
        Assert.Contains("未识别到年份", viewModel.UploadNotes, StringComparison.Ordinal);

        viewModel.UploadTitle = "   ";
        await viewModel.SubmitUploadCommand.ExecuteAsync(null);
        Assert.Equal(0, client.UploadCalls);
        Assert.Contains(dialogs.Messages, entry => entry.Message.Contains("标题为必填项", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UploadSendsParsedMetadataAndRefreshesTheList()
    {
        var client = new StubClient
        {
            UploadResult = CoreLocalDanmuResourceResult.Success(Resource("逐玉|2026|tv|5", "逐玉")),
        };
        var dialogs = new RecordingDialogService();
        await using var viewModel = Create(
            new StubCacheReader(LocalDanmuCacheReadResult.Success([])),
            client,
            dialogs,
            adminSession: new StubAdminSessionService(adminMode: true, configured: true, sessionToken: "admin-token"));
        await viewModel.LoadAsync(force: true);
        viewModel.ToggleUploadPanelCommand.Execute(null);
        dialogs.PickedFiles.Add(WriteTempFile("逐玉 (2026) S01E05.mkv", "<i></i>"));
        await viewModel.PickUploadFileCommand.ExecuteAsync(null);
        Assert.Equal(2026, viewModel.UploadYear);

        await viewModel.SubmitUploadCommand.ExecuteAsync(null);

        Assert.Equal(1, client.UploadCalls);
        var upload = client.LastUpload!;
        Assert.Equal("逐玉", upload.Title);
        Assert.Equal(2026, upload.Year);
        Assert.Equal(LocalDanmuTypes.Tv, upload.Type);
        Assert.Equal(5, upload.Episode);
        Assert.Equal("逐玉 (2026) S01E05.mkv", upload.FileName);
        Assert.Contains(dialogs.Messages, entry => entry.Message.Contains("上传成功", StringComparison.Ordinal));
        // 单文件上传仍然走进度弹窗（只有批量那条路径不能弹）。
        Assert.Contains("上传本地弹幕", dialogs.ProgressTitles);
        Assert.False(viewModel.IsUploadPanelOpen);
    }

    [Fact]
    public async Task ImportFolderBuildsPendingItemsFromSupportedFiles()
    {
        var folder = Path.Combine(_directory.Path, "danmu");
        Directory.CreateDirectory(folder);
        // 文件名里要有年份，否则和核心一样会被必填校验拦下（那正是另一条用例在测的事）。
        File.WriteAllText(Path.Combine(folder, "逐玉 (2026) E05.xml"), "<i></i>");
        File.WriteAllText(Path.Combine(folder, "逐玉 (2026) E06.xml"), "<i></i>");
        File.WriteAllText(Path.Combine(folder, "readme.md"), "not a danmu file");
        var dialogs = new RecordingDialogService { PickedFolderWithTitle = folder };
        var client = new StubClient
        {
            UploadResult = CoreLocalDanmuResourceResult.Success(Resource("逐玉|2026|tv|5", "逐玉")),
        };
        await using var viewModel = Create(
            new StubCacheReader(LocalDanmuCacheReadResult.Success([])),
            client,
            dialogs,
            adminSession: new StubAdminSessionService(adminMode: true, configured: true, sessionToken: "admin-token"));
        await viewModel.LoadAsync(force: true);

        await viewModel.ImportFolderCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsBatchPanelOpen);
        Assert.Equal(2, viewModel.PendingItems.Count);
        Assert.DoesNotContain(viewModel.PendingItems, item => item.FileName == "readme.md");

        await viewModel.SubmitBatchCommand.ExecuteAsync(null);

        Assert.Equal(2, client.UploadCalls);
        // 全部成功后面板关闭并回到列表（结果提示与进度见专门的两条用例）。
        Assert.False(viewModel.IsBatchPanelOpen);
        Assert.Contains(dialogs.Messages, entry => entry.Message.Contains("成功 2 个", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadIsSkippedWhenTheSnapshotIsFresh()
    {
        var cache = new StubCacheReader(LocalDanmuCacheReadResult.Success([Resource("甲|2026|tv|1", "甲")]));
        await using var viewModel = Create(cache, new StubClient());

        await viewModel.LoadAsync(force: true);
        await viewModel.LoadAsync(force: false);
        await viewModel.LoadAsync(force: false);

        Assert.Equal(1, cache.ReadCalls);
    }

    // ── 渲染契约：列表独占整列、不再有详情卡与两栏工作台 ─────────────────
    [Avalonia.Headless.XUnit.AvaloniaTheory]
    [InlineData(1440)]
    [InlineData(720)]
    public async Task LocalDanmuViewKeepsAFullWidthListWithoutDetailCard(int width)
    {
        var cache = new StubCacheReader(LocalDanmuCacheReadResult.Success([
            Resource("逐玉|2026|tv|1", "逐玉", episode: 1),
            Resource("逐玉|2026|tv|2", "逐玉", episode: 2),
        ]));
        await using var viewModel = Create(cache, new StubClient());
        await viewModel.LoadAsync(force: true);
        viewModel.Groups[0].IsExpanded = true;

        var view = new DanmuApi.App.Views.LocalDanmuView { DataContext = viewModel };
        var window = new Avalonia.Controls.Window { Width = width, Height = 700, Content = view };
        window.Show();
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.NotNull(view.FindControl<Avalonia.Controls.TextBox>("LocalDanmuSearchBox"));
            var listPanel = view.FindControl<Avalonia.Controls.Border>("LocalDanmuListPanel")!;
            var groupScroller = view.FindControl<Avalonia.Controls.ScrollViewer>("LocalDanmuGroupScroller")!;
            // 详情改成弹窗后，页面上不再有详情卡或两栏工作台（窄屏那套堆叠问题从根上消失）。
            Assert.Null(view.FindControl<Avalonia.Controls.Border>("LocalDanmuDetailPanel"));
            Assert.Null(view.FindControl<Avalonia.Controls.Grid>("LocalDanmuWorkspace"));
            // 列表吃满整行宽度，内容自己滚动——窄屏也一样，不会再被压到只看得见两三集。
            Assert.True(listPanel.Bounds.Width > width - 80, $"列表没有铺满整行：{listPanel.Bounds.Width} / 窗口 {width}");
            Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, groupScroller.VerticalScrollBarVisibility);
            Assert.True(groupScroller.Bounds.Height > 100, $"列表可视高度过小：{groupScroller.Bounds.Height}");
            // 每一集都有「详情」入口（详情只在弹窗里看）。行模板在 DataTemplate 的独立名字作用域里，
            // FindControl 找不到，所以按内容在可视树里找。
            // 关键：命令必须真的绑到页面 VM 上——集行嵌在**内层** ItemsControl 里，
            // 用 $parent[ItemsControl] 会取到内层那个（DataContext 是分组行），绑定失败后按钮会变灰不可点。
            var detailButtons = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view)
                .OfType<Avalonia.Controls.Button>()
                .Where(button => Equals(button.Content, "详情"))
                .ToArray();
            Assert.Equal(viewModel.Groups[0].Episodes.Count, detailButtons.Length);
            Assert.All(detailButtons, button =>
            {
                Assert.NotNull(button.Command);
                Assert.Same(viewModel.OpenDetailCommand, button.Command);
                Assert.True(button.IsEffectivelyEnabled, "集行的「详情」按钮不可点（命令没绑上）");
            });

            // 分组行的按钮同样要绑到页面 VM（它们在外层模板里，祖先绑定改法不能把它们弄坏）。
            foreach (var content in new[] { "收起", "删除本季", "删除整部" })
            {
                var button = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view)
                    .OfType<Avalonia.Controls.Button>()
                    .FirstOrDefault(candidate => Equals(candidate.Content, content));
                Assert.NotNull(button);
                Assert.NotNull(button!.Command);
                Assert.True(button.IsEffectivelyEnabled, $"分组行的「{content}」按钮不可点");
            }

            var directory = Environment.GetEnvironmentVariable("DANMU_TEST_RENDER_DIRECTORY");
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
                    new Avalonia.PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                bitmap.Render(window);
                bitmap.Save(Path.Combine(directory, $"local-danmu-{width}.png"));
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>批量面板必须自带内联进度条，且闲时那颗按钮是「返回列表」（而不是让人以为只能「取消」）。</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task BatchPanelRendersInlineProgressAndReturnButton()
    {
        var folder = Path.Combine(_directory.Path, "batch-panel");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "逐玉 (2026) E05.xml"), "<i></i>");
        var dialogs = new RecordingDialogService { PickedFolderWithTitle = folder };
        await using var viewModel = Create(
            new StubCacheReader(LocalDanmuCacheReadResult.Success([])),
            new StubClient(),
            dialogs,
            adminSession: new StubAdminSessionService(adminMode: true, configured: true, sessionToken: "admin-token"));
        await viewModel.LoadAsync(force: true);
        await viewModel.ImportFolderCommand.ExecuteAsync(null);

        var view = new DanmuApi.App.Views.LocalDanmuView { DataContext = viewModel };
        var window = new Avalonia.Controls.Window { Width = 1280, Height = 900, Content = view };
        window.Show();
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var progress = view.FindControl<Avalonia.Controls.ProgressBar>("LocalDanmuBatchProgress");
            Assert.NotNull(progress);
            Assert.Equal(0d, progress!.Minimum);
            Assert.Equal(1d, progress.Maximum);
            var close = view.FindControl<Avalonia.Controls.Button>("LocalDanmuCloseBatchButton");
            Assert.NotNull(close);
            Assert.Equal("返回列表", close!.Content);

            var directory = Environment.GetEnvironmentVariable("DANMU_TEST_RENDER_DIRECTORY");
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
                    new Avalonia.PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                bitmap.Render(window);
                bitmap.Save(Path.Combine(directory, "local-danmu-batch-panel.png"));
            }
        }
        finally
        {
            window.Close();
        }
    }


    /// <summary>
    /// 详情改成弹窗（用户实测反馈：窄屏叠成上下两栏时详情显示不全也没法滑动）：
    /// 点「详情」必须打开详情弹窗，装的是当前这一集的字段；列表不再有右侧详情卡。
    /// </summary>
    [Fact]
    public async Task OpenDetailShowsTheDetailDialogForThatEpisode()
    {
        var cache = new StubCacheReader(LocalDanmuCacheReadResult.Success([
            Resource("逐玉|2026|tv|1", "逐玉", episode: 1, count: 12),
        ]));
        var dialogs = new RecordingDialogService();
        await using var viewModel = Create(cache, new StubClient(), dialogs);
        await viewModel.LoadAsync(force: true);
        var row = viewModel.Groups[0].Episodes[0];

        await viewModel.OpenDetailCommand.ExecuteAsync(row);

        var dialog = Assert.Single(dialogs.LocalDanmuDetailDialogs);
        Assert.Equal("逐玉", dialog.Title);
        Assert.Equal("第 1 集", dialog.EpisodeLabel);
        Assert.Equal(row.FileName, dialog.FileName);
        Assert.Equal(row.ResourceKey, dialog.ResourceKey);
        Assert.Contains("12 条", dialog.MetaText, StringComparison.Ordinal);
        Assert.False(dialog.IsPreviewOpen);
    }

    /// <summary>弹窗里的「预览弹幕」走核心既有 comment 接口（url=local:&lt;resourceKey&gt;）。</summary>
    [Fact]
    public async Task DetailDialogPreviewLoadsCommentsFromTheCoreCommentEndpoint()
    {
        var cache = new StubCacheReader(LocalDanmuCacheReadResult.Success([
            Resource("逐玉|2026|tv|1", "逐玉", episode: 1),
        ]));
        var danmu = new PreviewDanmuClient();
        var dialogs = new RecordingDialogService();
        await using var viewModel = Create(cache, new StubClient(), dialogs, danmuClient: danmu);
        await viewModel.LoadAsync(force: true);
        await viewModel.OpenDetailCommand.ExecuteAsync(viewModel.Groups[0].Episodes[0]);

        await viewModel.DetailDialog.TogglePreviewCommand.ExecuteAsync(null);

        Assert.Equal("local:逐玉|2026|tv|1", danmu.LastUrl);
        Assert.True(viewModel.DetailDialog.IsPreviewOpen);
        Assert.Equal(2, viewModel.DetailDialog.PreviewLines.Count);
        Assert.Equal("00:03", viewModel.DetailDialog.PreviewLines[1].TimeText);
        Assert.Contains("共 2 条", viewModel.DetailDialog.PreviewSummaryText, StringComparison.Ordinal);
    }

    /// <summary>弹窗里删除成功后要关闭弹窗并刷新列表。</summary>
    [Fact]
    public async Task DetailDialogDeleteClosesTheDialog()
    {
        var cache = new StubCacheReader(LocalDanmuCacheReadResult.Success([
            Resource("逐玉|2026|tv|1", "逐玉", episode: 1),
        ]));
        var client = new StubClient();
        var dialogs = new RecordingDialogService { Confirmation = true };
        await using var viewModel = Create(
            cache,
            client,
            dialogs,
            adminSession: new StubAdminSessionService(adminMode: true, configured: true, sessionToken: "admin-token"));
        await viewModel.LoadAsync(force: true);
        await viewModel.OpenDetailCommand.ExecuteAsync(viewModel.Groups[0].Episodes[0]);
        var closed = 0;
        viewModel.DetailDialog.CloseRequested += (_, _) => closed++;

        await viewModel.DetailDialog.DeleteCommand.ExecuteAsync(null);

        Assert.Equal(1, closed);
        Assert.Contains("逐玉|2026|tv|1", client.DeletedKeys);
        Assert.Contains(
            dialogs.ConfirmationMessages,
            entry => entry.Title == "确认删除本地弹幕" && entry.Message.Contains("第 1 集", StringComparison.Ordinal));
    }

    // ── 夹具 ────────────────────────────────────────────────────────────
    private LocalDanmuPageViewModel Create(
        ILocalDanmuCacheReader cache,
        ICoreLocalDanmuClient client,
        RecordingDialogService? dialogs = null,
        StubAdminSessionService? adminSession = null,
        RecordingDiagnostics? diagnostics = null,
        bool relaxedFlag = false,
        IDanmuApiClient? danmuClient = null)
    {
        var paths = new AppPaths(_directory.Path, Path.Combine(_directory.Path, "appdata"));
        var configDirectory = Path.Combine(paths.NodeProjectDirectory, "config");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(
            Path.Combine(configDirectory, ".env"),
            $"DANMU_API_PORT=9321{Environment.NewLine}DANMU_API_VARIANT=stable{Environment.NewLine}TOKEN=test-token{Environment.NewLine}"
            + (relaxedFlag ? $"LOCAL_DANMU_NOT_REQUIRE_ADMIN=true{Environment.NewLine}" : string.Empty),
            new UTF8Encoding(false));
        var controller = new LocalStubRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 42));
        var session = adminSession ?? new StubAdminSessionService();
        var context = new RuntimeApiContext(paths, controller, session);
        return new LocalDanmuPageViewModel(
            context,
            client,
            cache,
            danmuClient ?? new LocalStubDanmuClient(),
            new StubEnvClient(),
            dialogs ?? new RecordingDialogService(),
            new AlwaysAllowWriteGate(),
            session,
            diagnostics ?? new RecordingDiagnostics(),
            new ShellNavigationAccessor(),
            paths);
    }

    private string WriteTempFile(string name, string content)
    {
        var path = Path.Combine(_directory.Path, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static async Task WaitForDeletesAsync(StubClient client)
    {
        for (var attempt = 0; attempt < 100 && client.DeletedKeys.Count == 0; attempt++)
        {
            await Task.Delay(10);
        }
    }

    private static CoreLocalDanmuResource Resource(
        string key,
        string title,
        string type = LocalDanmuTypes.Tv,
        int season = 1,
        int? episode = 1,
        int count = 20,
        long size = 2048,
        string updatedAt = "2026-09-12T10:00:00.000Z") =>
        new(key, "v-1", title, 2026, type, season, episode, $"{title}.xml", size, "XML", "ready", count, updatedAt);

    private sealed class StubCacheReader(LocalDanmuCacheReadResult result) : ILocalDanmuCacheReader
    {
        public int ReadCalls { get; private set; }

        public LocalDanmuCacheReadResult Read(string nodeProjectDirectory)
        {
            ReadCalls++;
            return result;
        }
    }

    private sealed class StubClient : ICoreLocalDanmuClient
    {
        public CoreLocalDanmuListResult ListResult { get; set; } = CoreLocalDanmuListResult.Success([]);
        public CoreLocalDanmuResourceResult UploadResult { get; set; } =
            CoreLocalDanmuResourceResult.Failure("未设置", LocalDanmuFailureKind.Protocol);
        public int ListCalls { get; private set; }
        public int UploadCalls { get; private set; }
        public CoreLocalDanmuUploadRequest? LastUpload { get; private set; }
        public List<string> DeletedKeys { get; } = [];

        public Task<CoreLocalDanmuListResult> ListAsync(string host, int port, string? token, CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult(ListResult);
        }

        public Task<CoreLocalDanmuResourceResult> GetAsync(string host, int port, string? token, string resourceKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(CoreLocalDanmuResourceResult.Failure("未设置", LocalDanmuFailureKind.NotFound));

        public Task<CoreLocalDanmuResourceResult> UploadAsync(
            string host,
            int port,
            string? token,
            string? adminToken,
            CoreLocalDanmuUploadRequest request,
            IProgress<long>? uploadProgress = null,
            CancellationToken cancellationToken = default)
        {
            UploadCalls++;
            LastUpload = request;
            return Task.FromResult(UploadResult);
        }

        public Task<CoreLocalDanmuOperationResult> DeleteAsync(
            string host,
            int port,
            string? token,
            string? adminToken,
            string resourceKey,
            CancellationToken cancellationToken = default)
        {
            DeletedKeys.Add(resourceKey);
            return Task.FromResult(CoreLocalDanmuOperationResult.Success("已删除"));
        }
    }

    private sealed class StubEnvClient : ICoreEnvClient
    {
        public string? SourceOrder { get; set; } = "douban,360";

        public Task<CoreEnvDeleteResult> DeleteAsync(string host, int port, string? token, string? adminToken, string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(CoreEnvDeleteResult.Success());

        public Task<CoreEnvSetResult> SetAsync(string host, int port, string? token, string? adminToken, string key, string value, CancellationToken cancellationToken = default)
        {
            if (key == "SOURCE_ORDER")
            {
                SourceOrder = value;
            }

            return Task.FromResult(CoreEnvSetResult.Success());
        }

        public Task<CoreEnvValueResult> ReadConfigValueAsync(string host, int port, string? token, string? adminToken, string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(key == "SOURCE_ORDER"
                ? CoreEnvValueResult.Success(SourceOrder)
                : CoreEnvValueResult.Failure($"未设置 {key}"));
    }

    private sealed class LocalStubRuntimeController(RuntimeSnapshot snapshot) : IRuntimeController
    {
        public RuntimeSnapshot Snapshot { get; private set; } = snapshot;
        event EventHandler<RuntimeSnapshot>? IRuntimeController.SnapshotChanged
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AdoptionResult> AdoptAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(AdoptionResult.Failure(AdoptionFailureKind.NotFound, Snapshot, "unused"));
        public string? ReconcileLiveness() => null;
        public Task RestartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>只在被用到时才需要实现的弹幕客户端（预览用例才会触发 <see cref="SendRawAsync"/>）。</summary>
    private sealed class LocalStubDanmuClient : IDanmuApiClient
    {
        public Task<DanmuRawApiResponse> SendRawAsync(
            string host,
            int port,
            string? token,
            string apiKey,
            IReadOnlyDictionary<string, string?> parameters,
            string? jsonBody = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("本用例不覆盖弹幕预览");

        public Task<DanmuDownloadPayload> DownloadCommentAsync(string host, int port, string? token, long episodeId, string formatValue, TimeSpan? requestTimeout = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DanmuSearchAnimeResult> SearchAnimeAsync(string host, int port, string? token, string keyword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DanmuSearchEpisodesResult> SearchEpisodesAsync(string host, int port, string? token, string anime, string? episode = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DanmuMatchResult> MatchAsync(string host, int port, string? token, string fileName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DanmuBangumiResult> GetBangumiAsync(string host, int port, string? token, int animeId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DanmuResult> GetCommentAsync(string host, int port, string? token, int commentId, bool includeDuration = true, string format = "json", CancellationToken cancellationToken = default, bool segmentFlag = false) => throw new NotSupportedException();
        public Task<DanmuResult> GetCommentByUrlAsync(string host, int port, string? token, string videoUrl, bool includeDuration = true, string format = "json", CancellationToken cancellationToken = default, bool segmentFlag = false) => throw new NotSupportedException();
        public Task<DanmuResult> GetSegmentCommentAsync(string host, int port, string? token, System.Text.Json.JsonElement segment, string format = "json", CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DanmuFavoriteListResult> GetFavoritesAsync(string host, int port, string? token, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> AddFavoriteAsync(string host, int port, string? token, string? adminToken, string keyword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> RemoveFavoriteAsync(string host, int port, string? token, string? adminToken, string keyword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> RefreshFavoriteAsync(string host, int port, string? token, string? adminToken, string keyword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> SetFavoriteScheduleAsync(string host, int port, string? token, string? adminToken, string keyword, DanmuFavoriteSchedule? schedule, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>预览用例的弹幕客户端：只实现 SendRawAsync，返回一条标准 dandan JSON（两条弹幕）。</summary>
    private sealed class PreviewDanmuClient : IDanmuApiClient
    {
        public string? LastUrl { get; private set; }

        public Task<DanmuRawApiResponse> SendRawAsync(
            string host,
            int port,
            string? token,
            string apiKey,
            IReadOnlyDictionary<string, string?> parameters,
            string? jsonBody = null,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("getCommentByUrl", apiKey);
            LastUrl = parameters.TryGetValue("url", out var url) ? url : null;
            var body = Encoding.UTF8.GetBytes(
                """{"count":2,"comments":[{"cid":1,"p":"1.50,1,16777215","m":"第一条"},{"cid":2,"p":"3.00,5,16711680","m":"第二条"}]}""");
            return Task.FromResult(new DanmuRawApiResponse(200, "application/json", body, Encoding.UTF8.GetString(body), "/api/v2/comment", TimeSpan.FromMilliseconds(5)));
        }

        public Task<DanmuDownloadPayload> DownloadCommentAsync(string host, int port, string? token, long episodeId, string formatValue, TimeSpan? requestTimeout = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DanmuSearchAnimeResult> SearchAnimeAsync(string host, int port, string? token, string keyword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DanmuSearchEpisodesResult> SearchEpisodesAsync(string host, int port, string? token, string anime, string? episode = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DanmuMatchResult> MatchAsync(string host, int port, string? token, string fileName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DanmuBangumiResult> GetBangumiAsync(string host, int port, string? token, int animeId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DanmuResult> GetCommentAsync(string host, int port, string? token, int commentId, bool includeDuration = true, string format = "json", CancellationToken cancellationToken = default, bool segmentFlag = false) => throw new NotSupportedException();
        public Task<DanmuResult> GetCommentByUrlAsync(string host, int port, string? token, string videoUrl, bool includeDuration = true, string format = "json", CancellationToken cancellationToken = default, bool segmentFlag = false) => throw new NotSupportedException();
        public Task<DanmuResult> GetSegmentCommentAsync(string host, int port, string? token, System.Text.Json.JsonElement segment, string format = "json", CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DanmuFavoriteListResult> GetFavoritesAsync(string host, int port, string? token, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> AddFavoriteAsync(string host, int port, string? token, string? adminToken, string keyword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> RemoveFavoriteAsync(string host, int port, string? token, string? adminToken, string keyword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> RefreshFavoriteAsync(string host, int port, string? token, string? adminToken, string keyword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> SetFavoriteScheduleAsync(string host, int port, string? token, string? adminToken, string keyword, DanmuFavoriteSchedule? schedule, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class AlwaysAllowWriteGate : IAdminWriteGate
    {
        public Action? NavigateToSecurity { get; set; }
        public Task<bool> EnsureAsync(string action, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class RecordingDiagnostics : IAppDiagnostics
    {
        public List<string> Messages { get; } = [];
        public string? LastDiagnostic => Messages.Count == 0 ? null : Messages[^1];
        public void Record(string message, Exception? error = null) => Messages.Add(message);
    }
}
