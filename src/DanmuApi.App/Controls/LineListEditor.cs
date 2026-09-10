using Avalonia.Controls;
using Avalonia.Layout;

namespace DanmuApi.App.Controls;

public sealed class LineListEditor : Border
{
    private readonly StackPanel _rowsPanel = new() { Spacing = 8 };
    private readonly List<Row> _rows = [];
    private readonly string _separator;

    public LineListEditor(string initial, string separator)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentException.ThrowIfNullOrWhiteSpace(separator);
        _separator = separator;
        var splitSeparators = separator == ";"
            ? new[] { ';', '\r', '\n' }
            : new[] { ',', ';', '\r', '\n' };
        foreach (var item in initial.Split(splitSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            AddRow(item);
        }

        if (_rows.Count == 0)
        {
            AddRow(string.Empty);
        }

        var add = new Button
        {
            Name = "AddLineListRowButton",
            Content = "添加规则",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        add.Classes.Add("secondary-action");
        add.Click += (_, _) => AddRow(string.Empty);
        Child = new StackPanel
        {
            Spacing = 10,
            Children = { _rowsPanel, add },
        };
    }

    public string Value => string.Join(_separator, _rows
        .Select(row => row.Input.Text?.Trim() ?? string.Empty)
        .Where(value => value.Length > 0));

    private void AddRow(string value)
    {
        var row = new Row
        {
            Input = new TextBox
            {
                Name = "LineListValueTextBox",
                MinWidth = 260,
                MaxWidth = 500,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Text = value,
                Watermark = "输入一条规则",
            },
        };
        row.Container = new WrapPanel { Orientation = Orientation.Horizontal };
        row.Remove = new Button { Name = "RemoveLineListRowButton", Content = "删除", Tag = row };
        row.Remove.Classes.Add("secondary-action");
        row.Remove.Click += RemoveRowClick;
        row.Container.Children.Add(row.Input);
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
            AddRow(string.Empty);
        }
    }

    private sealed class Row
    {
        public required TextBox Input { get; init; }
        public WrapPanel Container { get; set; } = null!;
        public Button Remove { get; set; } = null!;
    }
}
