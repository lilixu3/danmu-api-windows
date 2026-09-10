using Avalonia.Controls;
using Avalonia.Layout;

namespace DanmuApi.App.Controls;

public sealed class MappingTableEditor : Border
{
    private readonly StackPanel _rowsPanel = new() { Spacing = 8 };
    private readonly List<Row> _rows = [];

    public MappingTableEditor(string initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        foreach (var item in initial.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = item.IndexOf("->", StringComparison.Ordinal);
            AddRow(
                separator > 0 ? item[..separator].Trim() : item.Trim(),
                separator > 0 ? item[(separator + 2)..].Trim() : string.Empty);
        }

        if (_rows.Count == 0)
        {
            AddRow(string.Empty, string.Empty);
        }

        var add = new Button
        {
            Name = "AddMappingTableRowButton",
            Content = "添加映射",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        add.Classes.Add("secondary-action");
        add.Click += (_, _) => AddRow(string.Empty, string.Empty);
        Child = new StackPanel
        {
            Spacing = 10,
            Children = { _rowsPanel, add },
        };
    }

    public string Value => string.Join(';', _rows
        .Select(row => $"{row.Left.Text?.Trim()}->{row.Right.Text?.Trim()}")
        .Where(value => !value.Equals("->", StringComparison.Ordinal)));

    private void AddRow(string left, string right)
    {
        var row = new Row
        {
            Left = new TextBox { Name = "MappingSourceTextBox", Watermark = "原值", Width = 220, Text = left },
            Right = new TextBox { Name = "MappingTargetTextBox", Watermark = "映射值", Width = 220, Text = right },
        };
        row.Container = new WrapPanel { Orientation = Orientation.Horizontal };
        row.Remove = new Button { Name = "RemoveMappingTableRowButton", Content = "删除", Tag = row };
        row.Remove.Classes.Add("secondary-action");
        row.Remove.Click += RemoveRowClick;
        row.Container.Children.Add(row.Left);
        row.Container.Children.Add(new TextBlock { Text = "→", VerticalAlignment = VerticalAlignment.Center });
        row.Container.Children.Add(row.Right);
        row.Container.Children.Add(row.Remove);
        _rows.Add(row);
        _rowsPanel.Children.Add(row.Container);
    }

    private void RemoveRowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (sender is not Button { Tag: Row row })
        {
            return;
        }

        _rows.Remove(row);
        _rowsPanel.Children.Remove(row.Container);
        if (_rows.Count == 0)
        {
            AddRow(string.Empty, string.Empty);
        }
    }

    private sealed class Row
    {
        public required TextBox Left { get; init; }
        public required TextBox Right { get; init; }
        public WrapPanel Container { get; set; } = null!;
        public Button Remove { get; set; } = null!;
    }
}
