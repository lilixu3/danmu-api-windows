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
            window.UpdateLayout();
            Assert.Null(view.FindControl<ComboBox>("CompactSectionSelector"));
            Assert.True(view.FindControl<Border>("SectionSidebar")!.IsVisible);
            Assert.Equal(208, view.FindControl<Border>("SectionSidebar")!.Bounds.Width);
            Assert.Equal(1, Grid.GetColumn(view.FindControl<ContentControl>("ToolContent")!));
            Assert.True(view.FindControl<ContentControl>("ToolContent")!.Bounds.Width > 0);
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
    /// 工具页的可视契约（不靠看图）：分类侧栏是设置页那套外观并保持 208px 宽，
    /// 内容区固定在第二列；分类列表用与设置页一致的类名（rail-list 与 settings-list
    /// 在 Shell.axaml 里双写、数值相同），并渲染出「标题 + 说明」两行。
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
            window.UpdateLayout();

            var sidebar = view.FindControl<Border>("SectionSidebar")!;
            var content = view.FindControl<ContentControl>("ToolContent")!;
            Assert.True(sidebar.IsVisible);
            Assert.Equal(208, sidebar.Bounds.Width);
            Assert.Equal(1, Grid.GetColumn(content));
            Assert.True(content.Bounds.Width > 0, "内容区必须有实际宽度");
            Assert.True(sidebar.Bounds.Bottom <= window.Height, $"侧栏纵向溢出：{sidebar.Bounds}");
            Assert.True(content.Bounds.Right <= window.Width + 0.5, $"内容区横向溢出：{content.Bounds}");

            // 侧栏列表必须用 rail-list（新词汇），旧 settings-list 已退役。
            var list = sidebar.GetVisualDescendants().OfType<ListBox>().Single();
            Assert.Contains("rail-list", list.Classes);
            Assert.DoesNotContain("settings-list", list.Classes);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// 工具页整棵树（含子页面）不得再出现旧词汇类名。
    /// 新旧混用时同一个元素会有两套外观来源，是最难查的一类视觉问题；
    /// 这条按文件逐个点名，新增视图忘了迁移会在这里失败。
    /// </summary>
    [Theory]
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
    public void ToolsViewsUseOnlyTheSharedVocabulary(string fileName)
    {
        var path = Path.Combine(RepositoryRoot, "src", "DanmuApi.App", "Views", fileName);
        Assert.True(File.Exists(path), $"找不到视图文件：{path}");
        var xaml = File.ReadAllText(path);

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
