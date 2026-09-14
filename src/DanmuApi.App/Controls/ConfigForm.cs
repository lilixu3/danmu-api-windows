using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace DanmuApi.App.Controls;

/// <summary>
/// 配置编辑器的字段块构造器。统一「标签 12px/600 灰 + 控件 + 可选帮助文案」的三段结构，
/// 与核心自带前端 <c>.form-group</c>（下边距 14、标签下边距 5）一致，
/// 这样 11 种编辑形态不必各写一遍排版。
/// </summary>
public static class ConfigForm
{
    /// <summary>标签 + 控件（+ 可选帮助文案）的字段块。</summary>
    public static StackPanel Field(string label, Control control, string? help = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(control);

        var block = new StackPanel();
        block.Classes.Add("form-field");
        block.Children.Add(Label(label));
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        block.Children.Add(control);
        if (!string.IsNullOrWhiteSpace(help))
        {
            var hint = new TextBlock { Text = help };
            hint.Classes.Add("form-help");
            block.Children.Add(hint);
        }

        return block;
    }

    /// <summary>字段块，但控件保持自身对齐（开关、标签胶囊这类不该被拉满）。</summary>
    public static StackPanel FieldShrink(string label, Control control, string? help = null)
    {
        var block = Field(label, control, help);
        control.HorizontalAlignment = HorizontalAlignment.Left;
        return block;
    }

    /// <summary>只读值字段块（变量类别 / 变量名 / 值类型 / 描述用的是同一种框）。</summary>
    public static StackPanel ReadonlyField(string label, string value)
    {
        var block = new StackPanel();
        block.Classes.Add("form-field");
        block.Children.Add(Label(label));

        var text = new TextBlock { Text = string.IsNullOrEmpty(value) ? "—" : value, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var box = new Border { Child = text };
        box.Classes.Add("readonly-field");
        block.Children.Add(box);
        return block;
    }

    public static TextBlock Label(string text)
    {
        var label = new TextBlock { Text = text };
        label.Classes.Add("form-label");
        return label;
    }

    public static TextBlock Help(string text)
    {
        var help = new TextBlock { Text = text };
        help.Classes.Add("form-help");
        return help;
    }

    /// <summary>
    /// 控件行：左边一个主要动作、右边一个次要动作，两端对齐。
    /// 核心的「添加规则」+「查看最近数据」就是这么排的（space-between）。
    /// </summary>
    public static Grid SplitActionRow(Control left, Control? right = null)
    {
        ArgumentNullException.ThrowIfNull(left);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        left.HorizontalAlignment = HorizontalAlignment.Left;
        row.Children.Add(left);
        if (right is not null)
        {
            right.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(right, 1);
            row.Children.Add(right);
        }

        return row;
    }

    /// <summary>横排按钮组，右对齐（核心 .offset-actions）。</summary>
    public static StackPanel ActionRow(params Control[] children)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        foreach (var child in children)
        {
            row.Children.Add(child);
        }

        return row;
    }

    /// <summary>可换行的胶囊容器（核心 .tag-selector / .available-tags / .offset-sources）。</summary>
    public static WrapPanel PillRow(double horizontalSpacing = 0)
    {
        _ = horizontalSpacing;
        return new WrapPanel { Orientation = Orientation.Horizontal };
    }

    public static Button Button(string content, bool primary)
    {
        var button = new Button { Content = content };
        button.Classes.Add(primary ? "primary" : "secondary");
        return button;
    }

    /// <summary>紧凑按钮（核心 .btn-sm 的尺寸口径）。</summary>
    public static Button SmallButton(string content, bool primary)
    {
        var button = Button(content, primary);
        button.FontSize = 12;
        button.Padding = new Thickness(12, 5);
        return button;
    }
}
