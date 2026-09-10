using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DanmuApi.Runtime;

namespace DanmuApi.App.Controls;

public sealed class DanmuOffsetEditor : Border
{
    private readonly IReadOnlyList<string> _sources;
    private readonly StackPanel _rowsPanel = new() { Spacing = 14 };
    private readonly List<Row> _rows = [];

    public DanmuOffsetEditor(
        IEnumerable<string> sources,
        IEnumerable<DanmuOffsetRule> rules)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(rules);
        _sources = sources.ToArray();
        foreach (var rule in rules)
        {
            AddRow(rule);
        }

        if (_rows.Count == 0)
        {
            AddRow(null);
        }

        var add = new Button
        {
            Name = "AddDanmuOffsetButton",
            Content = "添加偏移规则",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        add.Classes.Add("secondary-action");
        add.Click += AddRowClick;

        var content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                CreateHint(),
                _rowsPanel,
                add,
            },
        };
        Child = content;
    }

    public IReadOnlyList<DanmuOffsetRule> Rules => _rows.Select(ToRule).ToArray();

    private static TextBlock CreateHint()
    {
        var hint = new TextBlock
        {
            Text = "正数让弹幕延后，负数让弹幕提前；指定集数时必须同时指定季数。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        hint.Classes.Add("body-muted");
        return hint;
    }

    private void AddRowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => AddRow(null);

    private void AddRow(DanmuOffsetRule? rule)
    {
        if (rule?.AllSources == true && rule.Sources.Count > 0)
        {
            throw new FormatException("DANMU_OFFSET 不能同时指定全部来源和具体来源");
        }

        var row = new Row
        {
            Title = new TextBox { Text = rule?.Title, Watermark = "剧名", Width = 240 },
            Season = CreateOptionalIntegerInput(rule?.Season, "季（可空）", 90),
            Episode = CreateOptionalIntegerInput(rule?.Episode, "集（可空）", 90),
            Seconds = new TextBox
            {
                Text = rule?.Seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Watermark = "偏移秒数",
                Width = 120,
            },
            Sources = new TagPicker(_sources, rule?.Sources ?? [], compareTokens: true),
            AllSources = new CheckBox
            {
                Name = "AllDanmuSourcesCheckBox",
                Content = "匹配全部来源（@all）",
                IsChecked = rule?.AllSources == true,
            },
            Percent = new CheckBox
            {
                Name = "DanmuOffsetPercentCheckBox",
                Content = "百分比模式",
                IsChecked = rule?.UsePercent == true,
            },
        };
        row.Container = new StackPanel { Spacing = 8 };
        row.Heading = new TextBlock { FontWeight = Avalonia.Media.FontWeight.SemiBold };
        row.Remove = new Button
        {
            Name = "RemoveDanmuOffsetButton",
            Content = "删除此规则",
            HorizontalAlignment = HorizontalAlignment.Left,
            Tag = row,
        };
        row.Remove.Classes.Add("secondary-action");
        row.Remove.Click += RemoveRowClick;
        row.Sources.SelectionChanged += (_, _) => UpdateAllSourcesState(row);
        row.AllSources.Click += (_, _) =>
        {
            if (row.AllSources.IsChecked == true)
            {
                row.Sources.ClearValues();
                row.Sources.ClearStaging();
            }
            UpdateAllSourcesState(row);
        };

        row.Container.Children.Add(row.Heading);
        row.Container.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { row.Title, row.Season, row.Episode, row.Seconds },
        });
        row.Container.Children.Add(CreateLabeledControl("限定来源（可多选；不选表示不限定）", row.Sources));
        row.Container.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 18,
            Children = { row.AllSources, row.Percent },
        });
        row.Container.Children.Add(row.Remove);
        _rows.Add(row);
        _rowsPanel.Children.Add(row.Container);
        UpdateAllSourcesState(row);
        RenumberRows();
    }

    private static void UpdateAllSourcesState(Row row)
    {
        row.AllSources.IsEnabled = row.Sources.Values.Count == 0;
        if (row.Sources.Values.Count > 0)
        {
            row.AllSources.IsChecked = false;
        }
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
        else
        {
            RenumberRows();
        }
    }

    private void RenumberRows()
    {
        for (var index = 0; index < _rows.Count; index++)
        {
            _rows[index].Heading.Text = $"偏移规则 {index + 1}";
        }
    }

    private static DanmuOffsetRule ToRule(Row row) =>
        new(
            RequiredText(row.Title, "DANMU_OFFSET 剧名"),
            ParseOptionalPositiveInteger(row.Season.Text, "DANMU_OFFSET 季数"),
            ParseOptionalPositiveInteger(row.Episode.Text, "DANMU_OFFSET 集数"),
            row.Sources.Values,
            row.AllSources.IsChecked == true,
            row.Percent.IsChecked == true,
            ParseDecimal(row.Seconds.Text, "DANMU_OFFSET 偏移秒数"));

    private static StackPanel CreateLabeledControl(string label, Control control) => new()
    {
        Spacing = 4,
        Children = { new TextBlock { Text = label }, control },
    };

    private static TextBox CreateOptionalIntegerInput(int? value, string watermark, double width) => new()
    {
        Text = value?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Watermark = watermark,
        Width = width,
    };

    private static string RequiredText(TextBox input, string field)
    {
        var value = input.Text?.Trim() ?? string.Empty;
        return value.Length == 0 ? throw new FormatException($"{field}不能为空") : value;
    }

    private static int? ParseOptionalPositiveInteger(string? text, string field)
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

    private static decimal ParseDecimal(string? text, string field)
    {
        if (!decimal.TryParse(
                text?.Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value))
        {
            throw new FormatException($"{field}必须是数字");
        }

        return value;
    }

    private sealed class Row
    {
        public required TextBox Title { get; init; }
        public required TextBox Season { get; init; }
        public required TextBox Episode { get; init; }
        public required TextBox Seconds { get; init; }
        public required TagPicker Sources { get; init; }
        public required CheckBox AllSources { get; init; }
        public required CheckBox Percent { get; init; }
        public StackPanel Container { get; set; } = null!;
        public TextBlock Heading { get; set; } = null!;
        public Button Remove { get; set; } = null!;
    }
}
