using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DanmuApi.Runtime;

namespace DanmuApi.App.Controls;

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
            allowCompositeValues: true);
        foreach (var group in groups)
        {
            AddGroup(group);
        }

        _picker.SelectionChanged += (_, _) => RenderGroups();
        _mergeMode.Click += (_, _) => _picker.CombineStaging = _mergeMode.IsChecked == true;
        Child = BuildContent();
        RenderGroups();
    }

    public IReadOnlyList<MergeSourceGroup> Groups =>
        _picker.Values.Select(value =>
        {
            var parts = value.Split('&', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return new MergeSourceGroup(parts[0], parts[1..]);
        }).ToArray();

    public TagPicker Picker => _picker;

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
        var hint = new TextBlock
        {
            Text = "点击候选来源加入暂存区，暂存区用 & 连接；确认后生成一个合并组。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        hint.Classes.Add("body-muted");
        content.Children.Add(hint);
        content.Children.Add(_picker);
        return content;
    }

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
