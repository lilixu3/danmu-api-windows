using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DanmuApi.App.Controls;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

/// <summary>
/// 最近数据面板：分页、空缓存、失败提示、映射详情折叠，以及"只有两个变量有回填按钮"这条核心约定。
/// </summary>
public sealed class RecentDataPanelTests
{
    private sealed class RecordingFillTarget : IRecentDataFillTarget
    {
        public List<string> Calls { get; } = [];

        public string? FillMergeEntity(bool asPrimary, string title, string source)
        {
            Calls.Add($"merge:{(asPrimary ? "primary" : "secondary")}:{title}@{source}");
            return $"已填入 {title}@{source}";
        }

        public string? FillOffsetEntity(string title, string source)
        {
            Calls.Add($"offset:{title}@{source}");
            return $"已填入 {title}@{source}";
        }
    }

    private static CacheAnimeEntry Entry(string title, int childCount = 0, int linkCount = 0)
    {
        var links = Enumerable.Range(1, linkCount)
            .Select(index => new CacheAnimeLink($"dandan:{index}", $"【动漫】{title} E{index:00}"))
            .ToArray();
        var children = Enumerable.Range(1, childCount)
            .Select(index => new CacheAnimeSource(
                "bilibili",
                $"b{index:00}",
                $"{title} 子源{index}",
                null,
                1,
                [new CacheAnimeLink($"b{index:00}", $"【番剧】{title} 第{index}话")]))
            .ToArray();
        return new CacheAnimeEntry(title, "dandan", null, linkCount, links, children);
    }

    private static RecentDataPanel CreatePanel(
        string key,
        IReadOnlyList<CacheAnimeEntry> items,
        IRecentDataFillTarget? fillTarget = null) =>
        new(key, _ => Task.FromResult(CoreCacheAnimeResult.Success($"已读取 {items.Count} 条缓存剧集", items)), fillTarget);

    private static List<T> Descendants<T>(Control root) where T : Control
    {
        var found = new List<T>();
        Walk(root);
        return found;

        void Walk(Control control)
        {
            if (control is T match)
            {
                found.Add(match);
            }

            switch (control)
            {
                case Border { Child: Control child }:
                    Walk(child);
                    break;
                case Panel panel:
                    foreach (var item in panel.Children.OfType<Control>())
                    {
                        Walk(item);
                    }
                    break;
                case ContentControl { Content: Control content }:
                    Walk(content);
                    break;
            }
        }
    }

    [AvaloniaFact]
    public async Task StartsCollapsedAndTogglesOpenOnFirstClick()
    {
        var panel = CreatePanel("CUSTOM_MERGE_RULES", [Entry("天气之子")]);

        Assert.False(panel.IsPanelOpen);
        Assert.Equal("查看最近数据", panel.ToggleText);

        await panel.ToggleAsync();
        Assert.True(panel.IsPanelOpen);
        Assert.Equal(1, panel.RenderedCardCount);

        // 再点一次收起，与核心前端一致
        await panel.ToggleAsync();
        Assert.False(panel.IsPanelOpen);
        Assert.Equal(1, panel.RenderedCardCount);
    }

    [AvaloniaFact]
    public async Task RendersFiveCardsPerPageAndLoadsMore()
    {
        var items = Enumerable.Range(1, 7).Select(index => Entry($"剧集{index}")).ToArray();
        var panel = CreatePanel("CUSTOM_MERGE_RULES", items);
        await panel.ToggleAsync();

        Assert.Equal(5, panel.RenderedCardCount);

        var loadMore = Descendants<Button>(panel).Single(button => button.Name == "RecentDataLoadMoreButton");
        Assert.True(loadMore.IsVisible);
        Assert.Equal("加载更多 (5/7)", (string)loadMore.Content!);

        loadMore.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(7, panel.RenderedCardCount);
        Assert.False(loadMore.IsVisible);
    }

    [AvaloniaFact]
    public async Task EmptyCacheExplainsHowToPopulateIt()
    {
        var panel = CreatePanel("CUSTOM_MERGE_RULES", []);
        await panel.ToggleAsync();

        Assert.Equal(0, panel.RenderedCardCount);
        var text = string.Join(
            "\n",
            Descendants<TextBlock>(panel).Select(block => block.Text ?? string.Empty));
        Assert.Contains("缓存中暂无番剧数据", text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task FetchFailureIsShownInsteadOfLookingEmpty()
    {
        var panel = new RecentDataPanel(
            "DANMU_OFFSET",
            _ => Task.FromResult(CoreCacheAnimeResult.Failure("核心服务未运行；最近数据来自核心内存缓存，请先启动服务。")),
            new RecordingFillTarget());

        await panel.ToggleAsync();

        var status = Descendants<TextBlock>(panel).Single(block => block.Classes.Contains("danger-text"));
        Assert.True(status.IsVisible);
        Assert.Contains("核心服务未运行", status.Text!, StringComparison.Ordinal);
        Assert.Equal(0, panel.RenderedCardCount);
    }

    [AvaloniaFact]
    public async Task MissingClientIsReportedExplicitly()
    {
        var panel = new RecentDataPanel("CUSTOM_MERGE_RULES", fetch: null);

        await panel.ToggleAsync();

        var status = Descendants<TextBlock>(panel).Single(block => block.Classes.Contains("danger-text"));
        Assert.Contains("无法读取最近数据", status.Text!, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task OnlyMergeRulesAndOffsetGetFillButtons()
    {
        var entries = new[] { Entry("天气之子") };

        var mergePanel = CreatePanel("CUSTOM_MERGE_RULES", entries, new RecordingFillTarget());
        await mergePanel.ToggleAsync();
        var mergeLabels = ButtonLabels(mergePanel);
        Assert.Contains("设为副", mergeLabels);
        Assert.Contains("设为主", mergeLabels);
        Assert.DoesNotContain("填入", mergeLabels);

        var offsetPanel = CreatePanel("DANMU_OFFSET", entries, new RecordingFillTarget());
        await offsetPanel.ToggleAsync();
        var offsetLabels = ButtonLabels(offsetPanel);
        Assert.Contains("填入", offsetLabels);
        Assert.DoesNotContain("设为副", offsetLabels);

        // 其余变量只有查看，没有回填（与核心前端的 generateButtons 一致）
        foreach (var key in new[]
                 {
                     "MERGE_SOURCE_PAIRS", "TITLE_MAPPING_TABLE", "AUTO_MATCH_MAPPING_TABLE",
                     "ANIME_TITLE_FILTER", "EPISODE_TITLE_FILTER", "TITLE_NOISE_FILTER",
                 })
        {
            var panel = CreatePanel(key, entries, new RecordingFillTarget());
            await panel.ToggleAsync();
            var labels = ButtonLabels(panel);
            Assert.DoesNotContain("设为副", labels);
            Assert.DoesNotContain("设为主", labels);
            Assert.DoesNotContain("填入", labels);
        }
    }

    [AvaloniaFact]
    public async Task FillButtonInvokesTargetAndReportsResult()
    {
        var target = new RecordingFillTarget();
        var panel = CreatePanel("CUSTOM_MERGE_RULES", [Entry("天气之子")], target);
        await panel.ToggleAsync();

        var secondary = Descendants<Button>(panel).Single(button => Equals(button.Content, "设为副"));
        secondary.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Single(target.Calls);
        Assert.Contains("merge:secondary:天气之子@dandan", target.Calls[0], StringComparison.Ordinal);

        var status = Descendants<TextBlock>(panel).Single(block => block.Classes.Contains("success-text"));
        Assert.Contains("已填入", status.Text!, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task ShowsEpisodeAndMergedChildBadgesWithMappingDetail()
    {
        var panel = CreatePanel("CUSTOM_MERGE_RULES", [Entry("天气之子", childCount: 2, linkCount: 3)]);
        await panel.ToggleAsync();

        var labels = ButtonLabels(panel);
        Assert.Contains("3 个剧集", labels);
        Assert.Contains("2 个被合并源", labels);

        // 映射详情默认收起，点开可见
        var mappingToggles = Descendants<Button>(panel).Where(button => button.Name == "RecentDataMappingToggleButton").ToArray();
        Assert.Equal(2, mappingToggles.Length);
        Assert.All(mappingToggles, button => Assert.Equal("展开映射详情", (string)button.Content!));

        mappingToggles[0].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("收起映射详情", (string)mappingToggles[0].Content!);
    }

    private static List<string> ButtonLabels(Control root) =>
        Descendants<Button>(root)
            .Select(button => button.Content as string ?? string.Empty)
            .Where(label => label.Length > 0)
            .ToList();
}
