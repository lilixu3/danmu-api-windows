using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DanmuApi.Runtime;

namespace DanmuApi.App.Controls;

/// <summary>
/// "查看最近数据"面板的填充目标。对应核心前端 <c>generateButtons</c> 里那两个有回填语义的变量：
/// CUSTOM_MERGE_RULES（设为副 / 设为主）与 DANMU_OFFSET（填入）。
/// 实现方只需把值填进表单，返回一句给用户看的说明；未实现的能力返回 <c>null</c>。
/// </summary>
public interface IRecentDataFillTarget
{
    /// <summary>把「剧名@来源」填进合并规则的副源（<paramref name="asPrimary"/> = false）或主源实体。</summary>
    string? FillMergeEntity(bool asPrimary, string title, string source);

    /// <summary>把清洗后的剧名与来源填进偏移规则表单。</summary>
    string? FillOffsetEntity(string title, string source);
}

/// <summary>
/// 需要把「查看最近数据」按钮放到自己那一行里的编辑器实现这个接口
/// （核心前端就是这么排的：CUSTOM_MERGE_RULES / DANMU_OFFSET 与「添加规则」同行，
/// map 与「添加映射项」同行）。未实现时按钮由面板自己渲染在最上方。
/// </summary>
public interface IRecentDataSplitHost
{
    /// <summary>面板主体（按钮不在这里）。</summary>
    ContentControl RecentDataHost { get; }

    /// <summary>按钮的落点，由面板把自己的按钮填进去。</summary>
    ContentControl RecentDataButtonHost { get; }
}

/// <summary>
/// 最近数据面板。语义照抄核心自带前端 <c>renderRecentDataButton / renderRecentDataPanel /
/// fetchAndShowRecentData / renderAnimeCachePanel</c>：
/// <list type="bullet">
/// <item>按钮再点一次收起；首次展开才请求 <c>GET /api/cache/animes</c>。</item>
/// <item>每页 5 条，底部「加载更多 (已显示/总数)」。</item>
/// <item>卡片显示封面、剧名、<c>[来源] (N集)</c>，底部胶囊展开「N 个剧集」与「N 个被合并源」。</item>
/// <item>被合并源可展开映射详情：逐条对比主源/副源集数，标注「匹配」或「落单」。</item>
/// <item>只有 CUSTOM_MERGE_RULES 与 DANMU_OFFSET 出现回填按钮，其余变量仅作参照（与核心一致）。</item>
/// </list>
/// </summary>
public sealed class RecentDataPanel : StackPanel
{
    /// <summary>与核心前端 <c>RECENT_DATA_PAGE_SIZE</c> 一致。</summary>
    public const int PageSize = 5;

    private readonly string _key;
    private readonly Func<CancellationToken, Task<CoreCacheAnimeResult>>? _fetch;
    private readonly IRecentDataFillTarget? _fillTarget;
    private readonly Func<string, Task<Bitmap?>>? _posterLoader;
    private readonly Action<string>? _reportDiagnostic;

    private readonly Button _toggle;
    private readonly Border _panel;
    private readonly TextBlock _status;
    private readonly TextBlock _hint;
    private readonly StackPanel _list = new() { Spacing = 10 };
    private readonly Button _loadMore;

    private IReadOnlyList<CacheAnimeEntry> _items = [];
    private int _displayed;
    private bool _loaded;
    private bool _loading;

    public RecentDataPanel(
        string key,
        Func<CancellationToken, Task<CoreCacheAnimeResult>>? fetch,
        IRecentDataFillTarget? fillTarget = null,
        Func<string, Task<Bitmap?>>? posterLoader = null,
        Action<string>? reportDiagnostic = null,
        bool splitToggle = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _key = key;
        _fetch = fetch;
        _fillTarget = fillTarget;
        _posterLoader = posterLoader;
        _reportDiagnostic = reportDiagnostic;

        Spacing = 8;

        _toggle = new Button
        {
            Name = "RecentDataToggleButton",
            Content = "查看最近数据",
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _toggle.Classes.Add("secondary-action");
        _toggle.Click += async (_, _) => await ToggleAsync().ConfigureAwait(true);

        _hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        _hint.Classes.Add("body-muted");

        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false };
        _status.Classes.Add("cap");

        _loadMore = new Button
        {
            Name = "RecentDataLoadMoreButton",
            Content = "加载更多",
            HorizontalAlignment = HorizontalAlignment.Left,
            IsVisible = false,
        };
        _loadMore.Classes.Add("secondary-action");
        _loadMore.Click += (_, _) => RenderNextPage();

        var inner = new StackPanel { Spacing = 10 };
        inner.Children.Add(_hint);
        inner.Children.Add(_status);
        inner.Children.Add(new ScrollViewer
        {
            // 面板有自己的滚动，别把外层弹窗撑高：3 条卡片左右就该滚。
            MaxHeight = 300,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _list,
        });
        inner.Children.Add(_loadMore);

        _panel = new Border { IsVisible = false, Padding = new Thickness(12) };
        _panel.Classes.Add("card");
        _panel.Child = inner;

        Children.Add(_panel);
        if (!splitToggle)
        {
            Children.Insert(0, _toggle);
        }
    }

    /// <summary>按钮本体。splitToggle 模式下由调用方把它放进自己的动作行里。</summary>
    public Button ToggleButton => _toggle;

    /// <summary>按钮文案，供渲染用例断言。</summary>
    public string ToggleText => _toggle.Content as string ?? string.Empty;

    public bool IsPanelOpen => _panel.IsVisible;

    /// <summary>当前已渲染的卡片数量，供渲染用例断言分页行为。</summary>
    public int RenderedCardCount => _list.Children.Count;

    /// <summary>再点一次收起，与核心前端 <c>fetchAndShowRecentData</c> 的行为一致。</summary>
    public async Task ToggleAsync()
    {
        if (_panel.IsVisible)
        {
            _panel.IsVisible = false;
            return;
        }

        _panel.IsVisible = true;
        if (_loaded || _loading)
        {
            return;
        }

        await LoadAsync().ConfigureAwait(true);
    }

    public async Task LoadAsync()
    {
        if (_fetch is null)
        {
            SetStatus("当前上下文没有可用的核心缓存客户端，无法读取最近数据。", isError: true);
            return;
        }

        _loading = true;
        SetStatus("数据加载中…", isError: false);
        try
        {
            var result = await _fetch(CancellationToken.None).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                SetStatus(result.Diagnostic, isError: true);
                return;
            }

            _items = result.Items;
            _displayed = 0;
            _list.Children.Clear();
            _loaded = true;
            if (_items.Count == 0)
            {
                SetStatus("缓存中暂无番剧数据，请先通过客户端请求弹幕接口以生成缓存。", isError: false);
                _loadMore.IsVisible = false;
                return;
            }

            if (_fillTarget is not null && (_key is "CUSTOM_MERGE_RULES" or "DANMU_OFFSET"))
            {
                _hint.Text = _key == "DANMU_OFFSET"
                    ? "点「填入」把剧名与来源填进规则表单，确认无误后再保存。"
                    : "点「设为副」「设为主」把「剧名@来源」填进规则表单，确认无误后再保存。";
                _hint.IsVisible = true;
            }

            SetStatus(result.Diagnostic, isError: false);
            RenderNextPage();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or FormatException)
        {
            SetStatus(error.Message, isError: true);
        }
        finally
        {
            _loading = false;
        }
    }

    private void RenderNextPage()
    {
        var next = Math.Min(_displayed + PageSize, _items.Count);
        for (var index = _displayed; index < next; index++)
        {
            _list.Children.Add(BuildCard(_items[index]));
        }

        _displayed = next;
        if (_displayed < _items.Count)
        {
            _loadMore.Content = $"加载更多 ({_displayed}/{_items.Count})";
            _loadMore.IsVisible = true;
        }
        else
        {
            _loadMore.IsVisible = false;
        }
    }

    private Control BuildCard(CacheAnimeEntry entry)
    {
        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 10,
        };

        var cover = CreateCover(entry.ImageUrl, 56, 78);
        body.Children.Add(cover);

        var info = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = CacheAnimePresentation.CleanTitle(entry.AnimeTitle),
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        var meta = new TextBlock { Text = $"[{entry.Source}] ({entry.Episodes}集)" };
        meta.Classes.Add("cap");
        info.Children.Add(meta);
        Grid.SetColumn(info, 1);
        body.Children.Add(info);

        var actions = new StackPanel
        {
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var button in BuildFillButtons(CacheAnimePresentation.CleanTitle(entry.AnimeTitle), entry.Source))
        {
            actions.Children.Add(button);
        }

        Grid.SetColumn(actions, 2);
        body.Children.Add(actions);

        var card = new StackPanel { Spacing = 8 };
        card.Children.Add(body);

        var childrenContainer = new StackPanel { Spacing = 8, IsVisible = false };
        foreach (var child in entry.MergedChildren)
        {
            childrenContainer.Children.Add(BuildChildItem(entry, child));
        }

        var episodesContainer = new StackPanel { Spacing = 4, IsVisible = false };
        foreach (var link in entry.Links)
        {
            var title = new TextBlock
            {
                Text = link.Title.Length == 0 ? "未知剧集" : link.Title,
                TextWrapping = TextWrapping.Wrap,
            };
            title.Classes.Add("cap");
            episodesContainer.Children.Add(title);
        }

        var badges = new StackPanel { Orientation = Orientation.Horizontal };
        if (entry.Links.Count > 0)
        {
            badges.Children.Add(CreateSectionBadge(
                $"{entry.Links.Count} 个剧集",
                $"{entry.Links.Count} 个剧集",
                "收起剧集",
                episodesContainer));
        }

        if (entry.MergedChildren.Count > 0)
        {
            badges.Children.Add(CreateSectionBadge(
                $"{entry.MergedChildren.Count} 个被合并源",
                $"{entry.MergedChildren.Count} 个被合并源",
                "收起被合并源",
                childrenContainer));
        }

        if (badges.Children.Count > 0)
        {
            card.Children.Add(badges);
        }

        card.Children.Add(childrenContainer);
        card.Children.Add(episodesContainer);

        var border = new Border { Padding = new Thickness(10) };
        border.Classes.Add("inset");
        border.Child = card;
        return border;
    }

    private Control BuildChildItem(CacheAnimeEntry parent, CacheAnimeSource child)
    {
        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 10,
        };
        body.Children.Add(CreateCover(child.ImageUrl, 40, 56));

        var info = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = CacheAnimePresentation.CleanTitle(child.AnimeTitle),
            TextWrapping = TextWrapping.Wrap,
        });
        var meta = new TextBlock { Text = $"[{child.Source}] ({child.Episodes}集)" };
        meta.Classes.Add("cap");
        info.Children.Add(meta);
        Grid.SetColumn(info, 1);
        body.Children.Add(info);

        var actions = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        foreach (var button in BuildFillButtons(CacheAnimePresentation.CleanTitle(child.AnimeTitle), child.Source))
        {
            actions.Children.Add(button);
        }

        Grid.SetColumn(actions, 2);
        body.Children.Add(actions);

        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(body);

        var mappingRows = CacheAnimePresentation.BuildMappingRows(parent, child);
        if (mappingRows.Count > 0)
        {
            var mappingContainer = new StackPanel { Spacing = 4, IsVisible = false };
            foreach (var row in mappingRows)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                var status = new Border { VerticalAlignment = VerticalAlignment.Center };
                status.Classes.Add("chip");
                status.Classes.Add(row.Status == CacheMappingStatus.Matched ? "ok" : "warn");
                status.Child = new TextBlock
                {
                    Text = row.Status == CacheMappingStatus.Matched ? "匹配" : "落单",
                };
                line.Children.Add(status);

                var text = new TextBlock
                {
                    Text = $"{row.MainSide} ↔ {row.ChildSide}",
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                text.Classes.Add("cap");
                line.Children.Add(text);
                mappingContainer.Children.Add(line);
            }

            var toggle = new Button
            {
                Name = "RecentDataMappingToggleButton",
                Content = "展开映射详情",
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            toggle.Classes.Add("secondary-action");
            toggle.Click += (_, _) =>
            {
                mappingContainer.IsVisible = !mappingContainer.IsVisible;
                toggle.Content = mappingContainer.IsVisible ? "收起映射详情" : "展开映射详情";
            };
            stack.Children.Add(toggle);
            stack.Children.Add(mappingContainer);
        }

        var border = new Border { Padding = new Thickness(8) };
        border.Classes.Add("inset");
        border.Child = stack;
        return border;
    }

    private static Control CreateSectionBadge(
        string label,
        string openText,
        string closeText,
        Control target)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 6, 0) };
        button.Classes.Add("secondary-action");
        button.Click += (_, _) =>
        {
            target.IsVisible = !target.IsVisible;
            button.Content = target.IsVisible ? closeText : openText;
        };
        return button;
    }

    private IEnumerable<Control> BuildFillButtons(string title, string source)
    {
        if (_fillTarget is null)
        {
            yield break;
        }

        if (_key == "CUSTOM_MERGE_RULES")
        {
            var secondary = new Button { Content = "设为副" };
            secondary.Classes.Add("secondary-action");
            secondary.Click += (_, _) => ApplyFill(() => _fillTarget.FillMergeEntity(false, title, source));
            yield return secondary;

            var primary = new Button { Content = "设为主" };
            primary.Classes.Add("primary-action");
            primary.Click += (_, _) => ApplyFill(() => _fillTarget.FillMergeEntity(true, title, source));
            yield return primary;
        }
        else if (_key == "DANMU_OFFSET")
        {
            var fill = new Button { Content = "填入" };
            fill.Classes.Add("primary-action");
            fill.Click += (_, _) => ApplyFill(() => _fillTarget.FillOffsetEntity(title, source));
            yield return fill;
        }
    }

    private void ApplyFill(Func<string?> fill)
    {
        try
        {
            var message = fill();
            SetStatus(message ?? "已填入。", isError: false);
        }
        catch (Exception error) when (error is FormatException or InvalidOperationException)
        {
            SetStatus(error.Message, isError: true);
        }
    }

    private Control CreateCover(string? url, double width, double height)
    {
        var placeholder = new Border
        {
            Width = width,
            Height = height,
            VerticalAlignment = VerticalAlignment.Top,
        };
        placeholder.Classes.Add("inset");

        if (_posterLoader is null || string.IsNullOrWhiteSpace(url))
        {
            return placeholder;
        }

        var image = new Image
        {
            Width = width,
            Height = height,
            Stretch = Stretch.UniformToFill,
            IsVisible = false,
        };
        placeholder.Child = image;

        // 封面是纯装饰：加载失败只影响这一格，不改变面板的数据与状态。
        // 例外情况（异常/空结果）走 diagnostics，不做静默丢弃。
        _ = LoadCoverAsync(url, image, placeholder);
        return placeholder;
    }

    private async Task LoadCoverAsync(string url, Image image, Border placeholder)
    {
        try
        {
            var bitmap = await _posterLoader!(url).ConfigureAwait(true);
            if (bitmap is null)
            {
                _reportDiagnostic?.Invoke($"最近数据封面加载返回空：{url}");
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                image.Source = bitmap;
                image.IsVisible = true;
                placeholder.Child = image;
            });
        }
        catch (Exception error) when (error is IOException or HttpRequestException or InvalidOperationException)
        {
            _reportDiagnostic?.Invoke($"最近数据封面加载失败：{url}（{error.Message}）");
        }
    }

    private void SetStatus(string message, bool isError)
    {
        _status.Text = message;
        _status.IsVisible = !string.IsNullOrWhiteSpace(message);
        _status.Classes.Remove("danger-text");
        _status.Classes.Remove("success-text");
        _status.Classes.Add(isError ? "danger-text" : "success-text");
    }
}
