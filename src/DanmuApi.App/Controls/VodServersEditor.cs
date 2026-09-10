using Avalonia.Controls;
using Avalonia.Layout;

namespace DanmuApi.App.Controls;

public sealed class VodServersEditor : Border
{
    private readonly StackPanel _rowsPanel = new() { Spacing = 8 };
    private readonly List<Row> _rows = [];

    public VodServersEditor(string initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        foreach (var item in initial.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = item.LastIndexOf('@');
            AddRow(
                separator > 0 ? item[..separator].Trim() : string.Empty,
                separator > 0 ? item[(separator + 1)..].Trim() : item.Trim());
        }

        if (_rows.Count == 0)
        {
            AddRow(string.Empty, string.Empty);
        }

        var add = new Button
        {
            Name = "AddVodServerButton",
            Content = "添加站点",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        add.Classes.Add("secondary-action");
        add.Click += (_, _) => AddRow(string.Empty, string.Empty);

        var hint = new TextBlock
        {
            Text = "每行填写站点名称和 HTTP/HTTPS 地址。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        hint.Classes.Add("body-muted");
        Child = new StackPanel
        {
            Spacing = 10,
            Children = { hint, _rowsPanel, add },
        };
    }

    public string Value => string.Join(',', _rows
        .Select(row => (Name: row.Name.Text?.Trim() ?? string.Empty, Url: row.Url.Text?.Trim() ?? string.Empty))
        .Where(row => row.Name.Length > 0 || row.Url.Length > 0)
        .Select(row => row.Name.Length > 0 ? $"{row.Name}@{row.Url}" : row.Url));

    private void AddRow(string name, string url)
    {
        var row = new Row
        {
            Name = new TextBox { Name = "VodServerNameTextBox", Watermark = "名称", Width = 130, Text = name },
            Url = new TextBox { Name = "VodServerUrlTextBox", Watermark = "https://...", Width = 300, Text = url },
        };
        row.Container = new WrapPanel { Orientation = Orientation.Horizontal };
        row.Remove = new Button { Name = "RemoveVodServerButton", Content = "删除", Tag = row };
        row.Remove.Classes.Add("secondary-action");
        row.Remove.Click += RemoveRowClick;
        row.Container.Children.Add(row.Name);
        row.Container.Children.Add(row.Url);
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
        public required TextBox Name { get; init; }
        public required TextBox Url { get; init; }
        public WrapPanel Container { get; set; } = null!;
        public Button Remove { get; set; } = null!;
    }
}
