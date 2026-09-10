using Avalonia.Controls;
using Avalonia.Layout;
using DanmuApi.Runtime;

namespace DanmuApi.App.Controls;

public sealed class IpBlacklistEditor : Border
{
    private static readonly string[] EntryTypes = ["IP 地址", "CIDR 网段", "正则表达式"];
    private readonly StackPanel _rowsPanel = new() { Spacing = 8 };
    private readonly List<Row> _rows = [];

    public IpBlacklistEditor(IEnumerable<IpBlacklistEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var entry in entries)
        {
            AddRow(entry);
        }

        if (_rows.Count == 0)
        {
            AddRow(null);
        }

        var add = new Button
        {
            Name = "AddIpBlacklistButton",
            Content = "添加黑名单规则",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        add.Classes.Add("secondary-action");
        add.Click += (_, _) => AddRow(null);

        var hint = new TextBlock
        {
            Text = "分别选择 IP 地址、CIDR 网段或 JavaScript 风格正则；保存前会逐条严格校验。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        hint.Classes.Add("body-muted");
        Child = new StackPanel
        {
            Spacing = 10,
            Children = { hint, _rowsPanel, add },
        };
    }

    public IReadOnlyList<IpBlacklistEntry> Entries => _rows.Select(ToEntry).ToArray();

    private void AddRow(IpBlacklistEntry? entry)
    {
        var row = new Row
        {
            Type = new ComboBox
            {
                Name = "IpBlacklistTypeComboBox",
                ItemsSource = EntryTypes,
                SelectedIndex = entry?.Type switch
                {
                    IpBlacklistEntryType.Cidr => 1,
                    IpBlacklistEntryType.RegularExpression => 2,
                    _ => 0,
                },
                Width = 140,
            },
            Value = new TextBox
            {
                Name = "IpBlacklistValueTextBox",
                Text = entry?.Value,
                Watermark = "例如 127.0.0.1、10.0.0.0/8 或 /^10\\./i",
                Width = 430,
            },
        };
        row.Container = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        row.Remove = new Button
        {
            Name = "RemoveIpBlacklistButton",
            Content = "删除",
            Tag = row,
        };
        row.Remove.Classes.Add("secondary-action");
        row.Remove.Click += RemoveRowClick;
        row.Container.Children.Add(row.Type);
        row.Container.Children.Add(row.Value);
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
            AddRow(null);
        }
    }

    private static IpBlacklistEntry ToEntry(Row row) =>
        new(
            row.Type.SelectedIndex switch
            {
                1 => IpBlacklistEntryType.Cidr,
                2 => IpBlacklistEntryType.RegularExpression,
                _ => IpBlacklistEntryType.Address,
            },
            RequiredText(row.Value, "IP_BLACKLIST 规则"));

    private static string RequiredText(TextBox input, string field)
    {
        var value = input.Text?.Trim() ?? string.Empty;
        return value.Length == 0 ? throw new FormatException($"{field}不能为空") : value;
    }

    private sealed class Row
    {
        public required ComboBox Type { get; init; }
        public required TextBox Value { get; init; }
        public StackPanel Container { get; set; } = null!;
        public Button Remove { get; set; } = null!;
    }
}
