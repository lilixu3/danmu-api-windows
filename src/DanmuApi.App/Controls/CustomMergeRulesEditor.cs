using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DanmuApi.Core;
using DanmuApi.Runtime;

namespace DanmuApi.App.Controls;

/// <summary>
/// CUSTOM_MERGE_RULES 编辑器。结构与核心自带前端一致（<c>systemsettings.js</c> 的
/// <c>isCustomMergeRules</c> 分支）：
///
/// <list type="number">
/// <item>「变量值」等宽多行框 —— **它是唯一真相**，保存时写的就是这里的内容。</item>
/// <item>紧接一行：「添加规则」（左）与「查看最近数据」（右），两者同为实心按钮。</item>
/// <item>最近数据面板（按钮行正下方，可见性由面板自己控制）。</item>
/// <item>「添加规则」子表单：副源实体 / 关系 / 主源实体三列一行，集数路由一行，
///       来源快捷标签（点一下追加到当前聚焦的输入框），底部「取消 / 确认添加」。</item>
/// </list>
///
/// 子表单只是往变量值里**追加文本**，不做结构化拆条；保存时由调用方对整段文本做严格校验。
/// </summary>
public sealed class CustomMergeRulesEditor : StackPanel, IRecentDataFillTarget, IRecentDataSplitHost
{
    private readonly IReadOnlyList<string> _sources;
    private readonly CoreEnvDefinition _definition;
    private readonly TextBox _rawValue;
    private readonly TextBox _secondaryTitle;
    private readonly TextBox _primaryTitle;
    private readonly TextBox _routes;
    private readonly ComboBox _relation;
    private readonly Border _rulePanel;
    private readonly TextBlock _relationHint = new();
    private readonly Button _ruleToggle;
    private readonly StackPanel _routeRow;
    private readonly TextBlock _errorText = new()
    {
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        IsVisible = false,
        Margin = new Thickness(0, 0, 0, 8),
    };
    private TextBox _focusTarget;

    public CustomMergeRulesEditor(CoreEnvDefinition definition, string initial)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(initial);
        _definition = definition;
        _sources = definition.Sources;

        _rawValue = new TextBox
        {
            Name = "MergeRulesValueBox",
            Text = initial,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MinHeight = 80,
            MaxHeight = 220,
            Watermark = "格式：副源 -> 主源 | 路由规则 或 副源 × 主源",
        };
        _rawValue.Classes.Add("mono");

        _secondaryTitle = new TextBox
        {
            Name = "MergeSecondaryEntityBox",
            Watermark = "例: 我推的孩子/S01@bahamut",
        };
        _primaryTitle = new TextBox
        {
            Name = "MergePrimaryEntityBox",
            Watermark = "例: 我推的孩子/S03@dandan",
        };
        _routes = new TextBox
        {
            Name = "MergeRouteBox",
            Watermark = "留空则交由系统自动计算偏移",
        };
        _relation = new ComboBox
        {
            Name = "MergeRelationBox",
            ItemsSource = new[] { "->", "×" },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 15,
            FontWeight = Avalonia.Media.FontWeight.Bold,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };

        // 焦点跟踪：来源快捷标签要插进"最后聚焦的那个实体输入框"。
        _focusTarget = _secondaryTitle;
        _secondaryTitle.GotFocus += (_, _) => _focusTarget = _secondaryTitle;
        _primaryTitle.GotFocus += (_, _) => _focusTarget = _primaryTitle;

        // 先建好子表单与它引用的行，再挂事件：lambda 在构造期捕获字段，晚赋值会被判为空引用。
        _routeRow = new StackPanel { Children = { ConfigForm.Field("集数路由规则 (选填，可多组。例如: E01>E01,E25~E35>E25~E35)", _routes) } };
        _rulePanel = new Border { Name = "MergeRulePanel", Child = BuildRulePanel(), IsVisible = false };

        _ruleToggle = ConfigForm.SmallButton("添加规则", primary: true);
        _ruleToggle.Name = "ToggleMergeRulePanelButton";
        _ruleToggle.Click += (_, _) => SetRulePanelOpen(!_rulePanel.IsVisible);

        _relation.SelectionChanged += (_, _) =>
        {
            var blocked = _relation.SelectedIndex == 1;
            _relationHint.Text = blocked ? "阻断" : "合并";
            _routeRow.IsVisible = !blocked;
        };

        Spacing = 8;
        Children.Add(ConfigForm.Field("变量值", _rawValue));
        Children.Add(ConfigForm.SplitActionRow(_ruleToggle, RecentDataButtonHost));
        Children.Add(RecentDataHost);
        Children.Add(_rulePanel);
    }

    /// <summary>最近数据面板的挂载点，由外层 Dialog 注入。</summary>
    public ContentControl RecentDataHost { get; } = new()
    {
        Name = "MergeRulesRecentDataHost",
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };

    /// <summary>「查看最近数据」按钮的落点：面板挂载后会把自己的按钮填进来，与「添加规则」同行。</summary>
    public ContentControl RecentDataButtonHost { get; } = new()
    {
        Name = "MergeRulesRecentDataButtonHost",
        HorizontalAlignment = HorizontalAlignment.Right,
    };

    /// <summary>「变量值」原文，保存时写这个（核心口径）。</summary>
    public string Value => _rawValue.Text ?? string.Empty;

    public bool IsRulePanelOpen => _rulePanel.IsVisible;

    public TextBox RouteBox => _routes;

    public ComboBox RelationBox => _relation;

    public void SetRulePanelOpen(bool open)
    {
        _rulePanel.IsVisible = open;
        _ruleToggle.Content = open ? "收起" : "添加规则";
    }

    /// <summary>
    /// 指定来源快捷标签的落点。对应核心的 <c>setMergeFocus('sec'|'prim')</c>：
    /// 除了输入框自身的 <c>onfocus</c>，最近数据回填与「设为副 / 设为主」也会调用它。
    /// </summary>
    public void SetFocusEntity(bool primary) =>
        _focusTarget = primary ? _primaryTitle : _secondaryTitle;

    /// <summary>当前落点是否是主源实体（供断言与调试）。</summary>
    public bool FocusedEntityIsPrimary => ReferenceEquals(_focusTarget, _primaryTitle);

    /// <summary>
    /// 来源快捷标签：没有 <c>@</c> 就追加 <c>@来源</c>，已经有 <c>@</c> 就追加 <c>&amp;来源</c>。
    /// 与核心 <c>appendSourceToMerge</c> 完全一致。
    /// </summary>
    public bool AppendSourceToFocusedEntity(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (!_sources.Contains(source, StringComparer.Ordinal))
        {
            return false;
        }

        var current = _focusTarget.Text ?? string.Empty;
        var trimmed = current.Trim();
        _focusTarget.Text = trimmed.Contains('@', StringComparison.Ordinal)
            ? $"{trimmed}&{source}"
            : $"{trimmed}@{source}";
        _focusTarget.Focus();
        return true;
    }

    /// <summary>把子表单拼成一条规则追加到变量值（核心 <c>appendMergeRule</c>）。</summary>
    public string AppendRuleFromForm()
    {
        var secondary = (_secondaryTitle.Text ?? string.Empty).Trim();
        var primary = (_primaryTitle.Text ?? string.Empty).Trim();
        if (secondary.Length == 0 || primary.Length == 0)
        {
            throw new FormatException("副源实体和主源实体不能为空");
        }

        var arrow = _relation.SelectedIndex == 1 ? "×" : "->";
        var rule = $"{secondary} {arrow} {primary}";
        if (arrow == "->")
        {
            var route = (_routes.Text ?? string.Empty).Trim();
            if (route.Length > 0)
            {
                rule += $" | {route}";
            }
        }

        AppendRawText(rule, ';');
        _secondaryTitle.Text = string.Empty;
        _primaryTitle.Text = string.Empty;
        _routes.Text = string.Empty;
        _focusTarget = _secondaryTitle;
        SetRulePanelOpen(false);
        return rule;
    }

    /// <summary>
    /// 最近数据面板的「设为副 / 设为主」：把「剧名@来源」写进对应的实体输入框并展开子表单。
    /// 与核心 <c>fillMergeEntity</c> 一致——只填表单，落盘仍要用户点保存。
    /// </summary>
    public string? FillMergeEntity(bool asPrimary, string title, string source)
    {
        if (!_sources.Contains(source, StringComparer.Ordinal))
        {
            return $"{source} 不在 CUSTOM_MERGE_RULES 允许的来源里（核心 MERGE_ALLOWED_SOURCES），未填入；" +
                   "可以先在「变量值」里手动写，或改用核心支持的来源。";
        }

        var target = asPrimary ? _primaryTitle : _secondaryTitle;
        target.Text = $"{title}@{source}";
        _focusTarget = target;
        SetRulePanelOpen(true);
        return $"已把「{title}@{source}」填进{(asPrimary ? "主源" : "副源")}实体，确认后点「确认添加」再保存。";
    }

    /// <summary>CUSTOM_MERGE_RULES 不提供偏移回填。</summary>
    public string? FillOffsetEntity(string title, string source) => null;

    /// <summary>校验当前变量值（保存前由调用方触发，失败时抛 FormatException 并带上原因）。</summary>
    public void Validate() =>
        _ = CoreEnvStructuredValues.ParseCustomMergeRules(_definition, Value.Trim());

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

    private Control BuildRulePanel()
    {
        // 三列一行：副源实体 | 关系 | 主源实体（核心 .offset-form-row）
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,80,*"), ColumnSpacing = 10 };
        row.Children.Add(ConfigForm.Field("副源实体（副源剧名@源）", _secondaryTitle));

        _relationHint.Text = "合并";
        _relationHint.VerticalAlignment = VerticalAlignment.Center;
        _relationHint.Classes.Add("form-help");
        var hintLabel = ConfigForm.Label("关系：");
        var hintRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Children = { hintLabel, _relationHint },
        };
        var relationBlock = new StackPanel
        {
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Bottom,
            Children = { hintRow, _relation },
        };
        Grid.SetColumn(relationBlock, 1);
        row.Children.Add(relationBlock);

        var primaryBlock = ConfigForm.Field("主源实体（主源剧名@源）", _primaryTitle);
        Grid.SetColumn(primaryBlock, 2);
        row.Children.Add(primaryBlock);
        row.Margin = new Thickness(0, 0, 0, 10);

        var sourcesRow = ConfigForm.PillRow();
        foreach (var source in _sources)
        {
            var pill = new Button { Content = source, Name = "MergeSourcePill" };
            pill.Classes.Add("source-pill");
            var captured = source;
            pill.Click += (_, _) => AppendSourceToFocusedEntity(captured);
            sourcesRow.Children.Add(pill);
        }

        var sourceBlock = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 10),
            Children =
            {
                ConfigForm.Label("快速追加来源至当前聚焦的输入框 (没有 @ 则追加 @xxx，已存在 @ 则追加 &xxx 合并写法)"),
                sourcesRow,
            },
        };

        var cancel = ConfigForm.SmallButton("取消", primary: false);
        cancel.Click += (_, _) => SetRulePanelOpen(false);
        var confirm = ConfigForm.SmallButton("确认添加", primary: true);
        confirm.Name = "ConfirmMergeRuleButton";
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
                row,
                _routeRow,
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
