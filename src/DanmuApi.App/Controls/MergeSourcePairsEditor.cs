using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DanmuApi.Runtime;

namespace DanmuApi.App.Controls;

/// <summary>
/// MERGE_SOURCE_PAIRS 编辑器（源合并配置）。
///
/// <para>
/// 这个变量**只接受合并组**，所以「合并模式」是必需项：关闭合并模式时**禁止写入**——
/// 候选来源点击被禁用、确认按钮无效，并明确提示原因。刻意不退化成"单源写入"：
/// 那会让用户在不知情的情况下把 <c>dandan&amp;animeko</c> 写成单条 <c>dandan</c>。
/// </para>
/// </summary>
public sealed class MergeSourcePairsEditor : Border
{
    private readonly TagPicker _picker;
    private readonly StackPanel _groupsPanel = new() { Spacing = 6 };
    private readonly CheckBox _mergeMode = new()
    {
        Name = "MergeSourceModeCheckBox",
        Content = "合并模式（将暂存来源组合为一组）",
        IsChecked = true,
    };
    private readonly TextBlock _requiredHint = new()
    {
        Name = "MergeSourceModeRequiredHint",
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        IsVisible = false,
    };
    private readonly IReadOnlyList<string> _options;

    public MergeSourcePairsEditor(IEnumerable<string> options, IEnumerable<MergeSourceGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(groups);
        _options = options.ToArray();
        _picker = new TagPicker(
            _options,
            compareTokens: true,
            useStaging: true,
            combineStaging: true,
            allowSelectedReuse: true,
            allowCompositeValues: true,
            enableReorder: true);
        foreach (var group in groups)
        {
            AddGroup(group);
        }

        _requiredHint.Classes.Add("danger-text");
        _picker.SelectionChanged += (_, _) => RenderGroups();
        _mergeMode.Click += (_, _) => ApplyMergeMode(_mergeMode.IsChecked == true);
        Child = BuildContent();
        ApplyMergeMode(_mergeMode.IsChecked == true);
        RenderGroups();
    }

    public IReadOnlyList<MergeSourceGroup> Groups =>
        _picker.Values.Select(value =>
        {
            var parts = value.Split('&', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return new MergeSourceGroup(parts[0], parts[1..]);
        }).ToArray();

    public TagPicker Picker => _picker;

    /// <summary>合并模式开关状态。</summary>
    public bool MergeModeEnabled => _mergeMode.IsChecked == true;

    /// <summary>当前是否允许写入（关闭合并模式即为 false）。</summary>
    public bool CanWrite => MergeModeEnabled;

    /// <summary>供测试使用：设置合并模式。</summary>
    public void SetMergeMode(bool enabled)
    {
        _mergeMode.IsChecked = enabled;
        ApplyMergeMode(enabled);
    }

    public void AddGroup(MergeSourceGroup group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group.Primary);
        var value = string.Join('&', new[] { group.Primary }.Concat(group.Secondaries));
        if (!_picker.AddValue(value))
        {
            throw new FormatException($"合并来源组非法或重复：{value}");
        }

        RenderGroups();
    }

    public bool ConfirmStaging() => _picker.ConfirmStagedValues();

    /// <summary>
    /// 关闭合并模式就禁止写入：候选置灰 + 提示。打开时恢复。
    /// </summary>
    private void ApplyMergeMode(bool enabled)
    {
        _picker.CandidatesEnabled = enabled;
        _requiredHint.Text = enabled
            ? string.Empty
            : "该变量只支持合并来源：请先开启合并模式，再点候选来源进入暂存区并点「确认加入已选」。";
        _requiredHint.IsVisible = !enabled;
    }

    private Control BuildContent()
    {
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = "已生成合并组（第一个来源为主源，允许单源组）",
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
        });
        content.Children.Add(_groupsPanel);
        content.Children.Add(_mergeMode);
        content.Children.Add(_requiredHint);

        var hint = new TextBlock
        {
            Text = "开启合并模式后点候选来源加入暂存区，暂存区用 & 连接；点「✓」确认后生成一个合并组。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        hint.Classes.Add("body-muted");
        content.Children.Add(hint);
        content.Children.Add(_picker);
        content.Children.Add(RecentDataHost);
        return content;
    }

    /// <summary>最近数据面板的挂载点，由外层 Dialog 注入（MERGE_SOURCE_PAIRS 只查看，不回填）。</summary>
    public ContentControl RecentDataHost { get; } = new()
    {
        Name = "MergeSourcePairsRecentDataHost",
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };

    private void RenderGroups()
    {
        _groupsPanel.Children.Clear();
        if (_picker.Values.Count == 0)
        {
            var empty = new TextBlock { Text = "尚未生成合并组" };
            empty.Classes.Add("body-muted");
            _groupsPanel.Children.Add(empty);
            return;
        }

        foreach (var value in _picker.Values.ToArray())
        {
            var remove = new Button
            {
                Name = "RemoveMergeSourceGroupButton",
                Content = $"{value}  ×",
                MinWidth = 110,
            };
            remove.Classes.Add("chip");
            remove.Click += (_, _) =>
            {
                _picker.RemoveValue(value);
                RenderGroups();
            };
            remove.Margin = new Thickness(0, 0, 6, 6);
            _groupsPanel.Children.Add(remove);
        }
    }
}
