using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DanmuApi.App.Controls;
using DanmuApi.App.Views;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class ToolWorkflowViewTests
{
    [AvaloniaFact]
    public void DanmuDetailsViewContainsHeatmapSourceExportAndEmptyFilterControls()
    {
        var view = new DanmuDetailsView();

        Assert.NotNull(view.FindControl<DanmuHeatmapControl>("HeatmapTimeline"));
        Assert.NotNull(view.FindControl<Button>("CopySourceButton"));
        Assert.NotNull(view.FindControl<Button>("OpenSourceButton"));
        Assert.NotNull(view.FindControl<ComboBox>("ExportFormatComboBox"));
        Assert.NotNull(view.FindControl<Button>("ExportButton"));
        Assert.NotNull(view.FindControl<Border>("EmptyFilterState"));
    }

    [AvaloniaFact]
    public void ApiDebugViewContainsDynamicBodyCancelAndCurlControls()
    {
        var view = new ApiDebugView();

        Assert.NotNull(view.FindControl<ItemsControl>("DynamicParameters"));
        Assert.NotNull(view.FindControl<StackPanel>("RawBodyPanel"));
        Assert.NotNull(view.FindControl<Button>("CancelRequestButton"));
        Assert.NotNull(view.FindControl<Button>("ExecuteRequestButton"));
        Assert.NotNull(view.FindControl<Button>("CopyCurlButton"));
    }

    [AvaloniaFact]
    public void DanmuDownloadViewContainsAllSectionsAndControls()
    {
        var view = new DanmuDownloadView();

        Assert.NotNull(view.FindControl<ListBox>("SectionList"));
        Assert.NotNull(view.FindControl<ScrollViewer>("SearchPanel"));
        Assert.NotNull(view.FindControl<ScrollViewer>("QueuePanel"));
        Assert.NotNull(view.FindControl<ScrollViewer>("RecordsPanel"));
        Assert.NotNull(view.FindControl<ScrollViewer>("SettingsPanel"));
        Assert.NotNull(view.FindControl<StackPanel>("AnimeListStage"));
        Assert.NotNull(view.FindControl<StackPanel>("EpisodeStage"));
        Assert.NotNull(view.FindControl<StackPanel>("RecordAnimeStage"));
        Assert.NotNull(view.FindControl<StackPanel>("RecordEpisodeStage"));
    }

    [AvaloniaFact]
    public void SearchResultsAndRequestRecordsExposeRealActions()
    {        var search = new SearchResultsView();
        var records = new RequestRecordsView();

        Assert.NotNull(search.FindControl<Button>("FavoriteSnapshotButton"));
        Assert.NotNull(records.FindControl<Button>("ClearRecordsButton"));
        Assert.NotNull(records.FindControl<Button>("RefreshRecordsButton"));
        Assert.NotNull(records.FindControl<TextBox>("RecordSearchBox"));
        Assert.NotNull(records.FindControl<ComboBox>("OutcomeFilter"));
        Assert.NotNull(records.FindControl<ComboBox>("MethodFilter"));
        Assert.NotNull(records.FindControl<ComboBox>("TimeRangeFilter"));
    }

    /// <summary>
    /// 工具页侧栏与设置页共用一套外观（216 宽、rail 容器、rail-title/rail-caption 两行）。
    /// 窄窗口不再硬撑双栏：&lt;900 时侧栏塌到内容上方单列（与配置页同一套断点），
    /// 但「紧凑选择器」这类替代控件仍然不许出现——侧栏始终在，只是换位置。
    /// </summary>
    [AvaloniaTheory]
    [InlineData(720)]
    [InlineData(960)]
    [InlineData(1280)]
    public void ToolsCategoriesKeepSettingsStyleSidebar(int width)
    {
        var view = new ToolsView();
        var window = new Window { Width = width, Height = 800, Content = view };
        window.Show();
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            view.ApplyLayoutForWidth(width);
            window.UpdateLayout();

            Assert.Null(view.FindControl<ComboBox>("CompactSectionSelector"));
            var rail = view.FindControl<Border>("CategoryRail")!;
            var content = view.FindControl<Grid>("ContentColumn")!;
            var railHeader = view.FindControl<StackPanel>("RailHeader")!;
            Assert.True(rail.IsVisible);
            Assert.True(content.Bounds.Width > 0, "内容区必须有实际宽度");

            if (width >= 900)
            {
                Assert.Equal(216, rail.Bounds.Width);
                Assert.Equal(0, Grid.GetColumn(rail));
                Assert.Equal(1, Grid.GetColumn(content));
                Assert.Equal(0, Grid.GetRow(content));
                Assert.True(railHeader.IsVisible, "宽档位保留侧栏表头说明");
            }
            else
            {
                // 单栏档位：侧栏铺满整行、内容落到下一行，表头说明隐藏省高度。
                Assert.True(rail.Bounds.Width > 216, $"单栏档位侧栏应铺满整行：{rail.Bounds}");
                Assert.Equal(1, Grid.GetRow(content));
                Assert.Equal(0, Grid.GetColumn(content));
                Assert.False(railHeader.IsVisible, "单栏档位隐藏表头说明");
            }
        }
        finally { window.Close(); }
    }

    /// <summary>900 断点：两侧都必须是「900 并排、899 单列」，与配置页同一阈值。</summary>
    [AvaloniaTheory]
    [InlineData(900, true)]
    [InlineData(899, false)]
    public void ToolsWorkspaceCollapsesAtTheSharedBreakpoint(int width, bool sideBySide)
    {
        var view = new ToolsView();
        var window = new Window { Width = width, Height = 800, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            view.ApplyLayoutForWidth(width);
            window.UpdateLayout();

            var content = view.FindControl<Grid>("ContentColumn")!;
            Assert.Equal(sideBySide ? 1 : 0, Grid.GetColumn(content));
            Assert.Equal(sideBySide ? 0 : 1, Grid.GetRow(content));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void HeatmapSelectsBucketsByRatioAndKeyboard()
    {
        var control = new DanmuHeatmapControl
        {
            Buckets = Enumerable.Range(0, 20)
                .Select(index => new DanmuHeatmapBucket(index, index * 30, (index + 1) * 30, index, index / 19d))
                .ToArray(),
        };

        control.SelectAtRatio(0.5);
        Assert.Equal(10, control.SelectedIndex);
        Assert.True(control.SelectByKey(Key.Left));
        Assert.Equal(9, control.SelectedIndex);
        Assert.True(control.SelectByKey(Key.End));
        Assert.Equal(19, control.SelectedIndex);
        Assert.True(control.SelectByKey(Key.Home));
        Assert.Equal(0, control.SelectedIndex);
        Assert.False(control.SelectByKey(Key.Enter));
        Assert.Equal(0, control.SelectedIndex);
    }

    [Fact]
    public void HeatmapRangeUsesHoursForLongVideos()
    {
        var bucket = new DanmuHeatmapBucket(0, 3661, 3691, 2, 1);

        Assert.Equal("01:01:01 - 01:01:31", bucket.RangeText);
    }

    /// <summary>
    /// 工具页的可视契约（不靠看图）：分类侧栏是配置页/设置页那套外观（216px、rail 容器、
    /// rail-list 列表），内容列固定在工作台第二列，页头显示当前分类的标题与说明。
    /// 页级标题由 MainWindow 头部统一承担，页面内不再重复一行「工具工作台」。
    /// </summary>
    [AvaloniaTheory]
    [InlineData(960)]
    [InlineData(1280)]
    [InlineData(1920)]
    public void ToolsWorkspaceKeepsSidebarAndContentColumns(int width)
    {
        var view = new ToolsView();
        var window = new Window { Width = width, Height = 800, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            view.ApplyLayoutForWidth(width);
            window.UpdateLayout();

            var sidebar = view.FindControl<Border>("CategoryRail")!;
            var contentColumn = view.FindControl<Grid>("ContentColumn")!;
            var content = view.FindControl<ContentControl>("ToolContent")!;
            Assert.True(sidebar.IsVisible);
            Assert.Equal(216, sidebar.Bounds.Width);
            Assert.Equal(1, Grid.GetColumn(contentColumn));
            Assert.True(content.Bounds.Width > 0, "内容区必须有实际宽度");
            Assert.True(sidebar.Bounds.Bottom <= window.Height, $"侧栏纵向溢出：{sidebar.Bounds}");
            Assert.True(content.Bounds.Right <= window.Width + 0.5, $"内容区横向溢出：{content.Bounds}");

            // 侧栏列表必须用 rail-list（新词汇），旧 settings-list 已退役。
            var list = view.FindControl<ListBox>("CategoryList")!;
            Assert.Contains("rail-list", list.Classes);
            Assert.DoesNotContain("settings-list", list.Classes);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// 已迁移的页面不得再出现旧词汇类名。
    /// 新旧混用时同一个元素会有两套外观来源，是最难查的一类视觉问题；
    /// 这条按文件逐个点名，新增视图忘了迁移会在这里失败。
    /// </summary>
    [Theory]
    [InlineData("ConfigurationView.axaml")]
    [InlineData("DanmuDownloadView.axaml")]
    [InlineData("SettingsView.axaml")]
    [InlineData("CorePageView.axaml")]
    [InlineData("LogsView.axaml")]
    [InlineData("ActivityView.axaml")]
    [InlineData("ToolsView.axaml")]
    [InlineData("DanmuTestView.axaml")]
    [InlineData("AutoMatchView.axaml")]
    [InlineData("ManualSearchView.axaml")]
    [InlineData("SearchResultsView.axaml")]
    [InlineData("BangumiDetailsView.axaml")]
    [InlineData("DanmuDetailsView.axaml")]
    [InlineData("FavoritesView.axaml")]
    [InlineData("ApiDebugView.axaml")]
    [InlineData("RequestRecordsView.axaml")]
    [InlineData("ServiceManagementView.axaml")]
    [InlineData("BackupView.axaml")]
    [InlineData("LocalDanmuView.axaml")]
    [InlineData("LocalDanmuDetailWindow.axaml")]
    public void MigratedViewsUseOnlyTheSharedVocabulary(string fileName)
    {
        var path = Path.Combine(RepositoryRoot, "src", "DanmuApi.App", "Views", fileName);
        Assert.True(File.Exists(path), $"找不到视图文件：{path}");
        // 注释里可以提到旧类名（例如解释「为什么不再用 muted-row」），
        // 先把 XML 注释整段去掉再扫，避免文档把自己变成假失败。
        var xaml = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(path),
            "<!--.*?-->",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline);

        string[] retired =
        [
            "dashboard-panel", "muted-row", "section-title", "body-muted", "mono-text",
            "primary-action", "secondary-action", "ghost-action", "compact-action",
            "back-action", "danger-action", "anime-card", "settings-list",
            "metric-label", "metric-value",
        ];
        foreach (var legacy in retired)
        {
            Assert.DoesNotContain($"\"{legacy}\"", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain($"{legacy} ", xaml, StringComparison.Ordinal);
        }
    }

    private static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "src", "DanmuApi.App")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("找不到仓库根目录（含 src/DanmuApi.App 的目录）");
        }
    }
}
