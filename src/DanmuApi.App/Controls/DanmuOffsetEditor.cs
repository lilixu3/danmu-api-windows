using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DanmuApi.Core;
using DanmuApi.Runtime;

namespace DanmuApi.App.Controls;

/// <summary>
/// DANMU_OFFSET 编辑器。结构与核心自带前端一致（<c>systemsettings.js</c> 的
/// <c>isDanmuOffset</c> 分支）：
///
/// <list type="number">
/// <item>「变量值」等宽多行框 —— **唯一真相**，保存写的就是这里的内容。</item>
/// <item>「添加规则」（左）+「查看最近数据」（右）同一行。</item>
/// <item>最近数据面板。</item>
/// <item>「添加规则」子表单：剧名 / 季 / 集 / 偏移秒 一行，百分比模式，
///       来源标签（纯选中态），底部「取消 / 确认添加」。</item>
/// </list>
/// </summary>
public sealed class DanmuOffsetEditor : StackPanel, IRecentDataFillTarget, IRecentDataSplitHost
{
    private readonly IReadOnlyList<string> _sources;
    private readonly CoreEnvDefinition _definition;
    private readonly TextBox _rawValue;
    private readonly TextBox _anime;
    private readonly TextBox _season;
    private readonly TextBox _episode;
    private readonly TextBox _seconds;
    private readonly CheckBox _percent;
    private readonly Border _rulePanel;
    private readonly Button _ruleToggle;
    private readonly List<(Button Button, string Source)> _sourcePills = [];
    private readonly TextBlock _errorText = new()
    {
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        IsVisible = false,
        Margin = new Thickness(0, 0, 0, 8),
    };

    public DanmuOffsetEditor(CoreEnvDefinition definition, string initial)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(initial);
        _definition = definition;
        _sources = definition.Sources;

        _rawValue = new TextBox
        {
            Name = "DanmuOffsetValueBox",
            Text = initial,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MinHeight = 80,
            MaxHeight = 220,
            Watermark = "格式：剧名:秒 或 剧名/S01:秒 或 剧名@来源:秒 或 剧名/S01/E01@来源%:秒",
        };
        _rawValue.Classes.Add("mono");

        _anime = new TextBox { Name = "OffsetAnimeBox", Watermark = "例如: overlord" };
        _season = new TextBox { Name = "OffsetSeasonBox", Watermark = "季" };
        _episode = new TextBox { Name = "OffsetEpisodeBox", Watermark = "集" };
        _seconds = new TextBox { Name = "OffsetSecondsBox", Watermark = "偏移秒数" };
        _percent = new CheckBox
        {
            Name = "OffsetPercentCheckBox",
            Content = "启用百分比模式（按视频时长缩放全部弹幕时间）",
        };

        // 先建好子表单再挂事件：lambda 在构造期捕获 _rulePanel，晚赋值会被判为空引用。
        _rulePanel = new Border { Name = "OffsetRulePanel", Child = BuildRulePanel(), IsVisible = false };

        _ruleToggle = ConfigForm.SmallButton("添加规则", primary: true);
        _ruleToggle.Name = "ToggleOffsetRulePanelButton";
        _ruleToggle.Click += (_, _) => SetRulePanelOpen(!_rulePanel.IsVisible);

        Spacing = 8;
        Children.Add(ConfigForm.Field("变量值", _rawValue));
        Children.Add(ConfigForm.SplitActionRow(_ruleToggle, RecentDataButtonHost));
        Children.Add(RecentDataHost);
        Children.Add(_rulePanel);
    }

    public ContentControl RecentDataHost { get; } = new()
    {
        Name = "DanmuOffsetRecentDataHost",
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };

    public ContentControl RecentDataButtonHost { get; } = new()
    {
        Name = "DanmuOffsetRecentDataButtonHost",
        HorizontalAlignment = HorizontalAlignment.Right,
    };

    /// <summary>「变量值」原文，保存时写这个（核心口径）。</summary>
    public string Value => _rawValue.Text ?? string.Empty;

    public bool IsRulePanelOpen => _rulePanel.IsVisible;

    public IReadOnlyList<string> SelectedSources =>
        _sourcePills.Where(pill => pill.Button.Classes.Contains("selected")).Select(pill => pill.Source).ToArray();

    public void SetRulePanelOpen(bool open)
    {
        _rulePanel.IsVisible = open;
        _ruleToggle.Content = open ? "收起" : "添加规则";
    }

    /// <summary>把子表单拼成一条偏移规则追加到变量值（核心 <c>appendOffsetRule</c>）。</summary>
    public string AppendRuleFromForm()
    {
        var anime = (_anime.Text ?? string.Empty).Trim();
        if (anime.Length == 0)
        {
            throw new FormatException("剧名不能为空");
        }

        var seconds = (_seconds.Text ?? string.Empty).Trim();
        if (seconds.Length == 0)
        {
            throw new FormatException("偏移秒数不能为空");
        }

        var season = ParseOptionalPositive(_season.Text, "季");
        var episode = ParseOptionalPositive(_episode.Text, "集");
        if (episode is not null && season is null)
        {
            throw new FormatException("填了集数就必须同时填季数");
        }

        var path = anime;
        if (season is int s)
        {
            path += $"/S{s:00}";
        }

        if (episode is int e)
        {
            path += $"/E{e:00}";
        }

        var sources = SelectedSources;
        if (sources.Count > 0)
        {
            path += "@" + string.Join('&', sources);
        }

        if (_percent.IsChecked == true)
        {
            path += "%";
        }

        var rule = $"{path}:{seconds}";
        AppendRawText(rule, ',');
        ResetForm();
        return rule;
    }

    /// <summary>
    /// 最近数据面板的「填入」：清洗标题后写进剧名，并选中对应来源。
    /// 清洗顺序与核心 <c>fillOffsetEntity</c> 一致（去零宽字符 → 去年份括号 → 去末尾【类型】）。
    /// </summary>
    public string? FillOffsetEntity(string title, string source)
    {
        var cleaned = CacheAnimePresentation.CleanOffsetTitle(
            CacheAnimePresentation.CleanTitle(title));
        _anime.Text = cleaned;
        SetRulePanelOpen(true);

        var matched = _sourcePills.FirstOrDefault(pill =>
            string.Equals(pill.Source, source, StringComparison.Ordinal));
        if (matched.Button is null)
        {
            return $"已把剧名「{cleaned}」填进规则表单；来源 {source} 不在核心允许的来源里，未自动勾选。";
        }

        foreach (var pill in _sourcePills)
        {
            pill.Button.Classes.Set("selected", ReferenceEquals(pill.Button, matched.Button));
        }

        return $"已把剧名「{cleaned}」与来源 {source} 填进规则表单，确认后点「确认添加」再保存。";
    }

    /// <summary>DANMU_OFFSET 不提供合并规则回填。</summary>
    public string? FillMergeEntity(bool asPrimary, string title, string source) => null;

    /// <summary>校验当前变量值（保存前由调用方触发）。</summary>
    public void Validate() =>
        _ = CoreEnvStructuredValues.ParseDanmuOffsets(_definition, Value.Trim());

    private void ResetForm()
    {
        _anime.Text = string.Empty;
        _season.Text = string.Empty;
        _episode.Text = string.Empty;
        _seconds.Text = string.Empty;
        _percent.IsChecked = false;
        foreach (var pill in _sourcePills)
        {
            pill.Button.Classes.Set("selected", false);
        }

        SetRulePanelOpen(false);
    }

    private void AppendRawText(string text, char separator)
    {
        var current = (_rawValue.Text ?? string.Empty).Trim();
        if (current.Length == 0)
        {
            _rawValue.Text = text;
        }
        else if (current.EndsWith(separator))
        {
            _rawValue.Text = current + text;
        }
        else
        {
            _rawValue.Text = current + separator + text;
        }
    }

    private static int? ParseOptionalPositive(string? text, string field)
    {
        var value = text?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return null;
        }

        if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) || number <= 0)
        {
            throw new FormatException($"{field}必须是正整数");
        }

        return number;
    }

    private Control BuildRulePanel()
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,72,72,96"), ColumnSpacing = 10 };
        row.Children.Add(ConfigForm.Field("剧名 *", _anime));
        var seasonBlock = ConfigForm.Field("季", _season);
        Grid.SetColumn(seasonBlock, 1);
        row.Children.Add(seasonBlock);
        var episodeBlock = ConfigForm.Field("集", _episode);
        Grid.SetColumn(episodeBlock, 2);
        row.Children.Add(episodeBlock);
        var secondsBlock = ConfigForm.Field("偏移秒 *", _seconds);
        Grid.SetColumn(secondsBlock, 3);
        row.Children.Add(secondsBlock);
        row.Margin = new Thickness(0, 0, 0, 4);

        var percentRow = new StackPanel { Margin = new Thickness(0, 0, 0, 10), Children = { _percent } };

        var sourcesRow = ConfigForm.PillRow();
        foreach (var source in _sources)
        {
            var pill = new Button { Content = source, Name = "OffsetSourcePill" };
            pill.Classes.Add("source-pill");
            var captured = source;
            pill.Click += (_, _) =>
            {
                // 核心是纯视觉选中态，写入发生在「确认添加」。
                pill.Classes.Set("selected", !pill.Classes.Contains("selected"));
            };
            _sourcePills.Add((pill, captured));
            sourcesRow.Children.Add(pill);
        }

        var sourceBlock = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 10),
            Children =
            {
                ConfigForm.Label("来源 (可选，不选则对所有来源生效)"),
                sourcesRow,
            },
        };

        var cancel = ConfigForm.SmallButton("取消", primary: false);
        cancel.Click += (_, _) => SetRulePanelOpen(false);
        var confirm = ConfigForm.SmallButton("确认添加", primary: true);
        confirm.Name = "ConfirmOffsetRuleButton";
        confirm.Click += (_, _) =>
        {
            try
            {
                AppendRuleFromForm();
            }
            catch (FormatException error)
            {
                _errorText.Text = error.Message;
                _errorText.IsVisible = true;
            }
        };

        var body = new StackPanel
        {
            Children =
            {
                ConfigForm.Help("季和集不填则对所有季/集生效"),
                row,
                percentRow,
                sourceBlock,
                _errorText,
                ConfigForm.ActionRow(cancel, confirm),
            },
        };

        var panel = new Border { Child = body };
        panel.Classes.Add("rule-panel");
        return panel;
    }
}
