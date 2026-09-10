using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DanmuApi.Core;
using DanmuApi.Runtime;

namespace DanmuApi.App.Controls;

public sealed class AutoMatchMappingEditor : Border
{
    private readonly IReadOnlyList<string> _platforms;
    private readonly StackPanel _rowsPanel = new() { Spacing = 14 };
    private readonly List<Row> _rows = [];

    public AutoMatchMappingEditor(
        IEnumerable<string> platforms,
        IEnumerable<AutoMatchMappingRule> rules)
    {
        ArgumentNullException.ThrowIfNull(platforms);
        ArgumentNullException.ThrowIfNull(rules);
        _platforms = platforms
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
            Name = "AddAutoMatchMappingButton",
            Content = "添加自动匹配规则",
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

    public IReadOnlyList<AutoMatchMappingRule> Rules => _rows.Select(ToRule).ToArray();

    private static TextBlock CreateHint()
    {
        var hint = new TextBlock
        {
            Text = "开放映射不填结束集；有限范围必须同时填写源和目标结束集，且两侧范围长度一致。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        hint.Classes.Add("body-muted");
        return hint;
    }

    private void AddRowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => AddRow(null);

    private void AddRow(AutoMatchMappingRule? rule)
    {
        var row = new Row
        {
            SourceTitle = new TextBox { Text = rule?.SourceTitle, Watermark = "源标题", Width = 230 },
            SourceSeason = CreatePositiveIntegerInput(rule?.SourceSeason, "季", 70),
            SourceStart = CreatePositiveIntegerInput(rule?.SourceStartEpisode, "起始集", 85),
            SourceEnd = CreatePositiveIntegerInput(rule?.SourceEndEpisode, "结束集（可空）", 115),
            TargetTitle = new TextBox
            {
                Text = rule?.TargetDisplayTitle,
                Watermark = "目标标题，可含 (年份)【类型】",
                Width = 300,
            },
            TargetSeason = CreatePositiveIntegerInput(rule?.TargetSeason, "季", 70),
            TargetStart = CreatePositiveIntegerInput(rule?.TargetStartEpisode, "起始集", 85),
            TargetEnd = CreatePositiveIntegerInput(rule?.TargetEndEpisode, "结束集（可空）", 115),
            TargetPlatform = new TagPicker(
                _platforms,
                rule?.TargetPlatform is null ? [] : [rule.TargetPlatform],
                singleSelect: true),
        };
        row.Container = new StackPanel { Spacing = 8 };
        row.Heading = new TextBlock { FontWeight = Avalonia.Media.FontWeight.SemiBold };
        row.Remove = new Button
        {
            Name = "RemoveAutoMatchMappingButton",
            Content = "删除此规则",
            HorizontalAlignment = HorizontalAlignment.Left,
            Tag = row,
        };
        row.Remove.Classes.Add("secondary-action");
        row.Remove.Click += RemoveRowClick;
        row.Container.Children.Add(row.Heading);
        row.Container.Children.Add(CreateSideEditor("源剧集", row.SourceTitle, row.SourceSeason, row.SourceStart, row.SourceEnd));
        row.Container.Children.Add(CreateSideEditor("目标剧集", row.TargetTitle, row.TargetSeason, row.TargetStart, row.TargetEnd));
        row.Container.Children.Add(CreateLabeledControl("目标平台（可选；不选择表示不指定）", row.TargetPlatform));
        row.Container.Children.Add(row.Remove);
        _rows.Add(row);
        _rowsPanel.Children.Add(row.Container);
        RenumberRows();
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
            _rows[index].Heading.Text = $"自动匹配规则 {index + 1}";
        }
    }

    private static AutoMatchMappingRule ToRule(Row row) =>
        new(
            RequiredText(row.SourceTitle, "AUTO_MATCH_MAPPING_TABLE 源标题"),
            ParsePositiveInteger(row.SourceSeason.Text, "AUTO_MATCH_MAPPING_TABLE 源季数"),
            ParsePositiveInteger(row.SourceStart.Text, "AUTO_MATCH_MAPPING_TABLE 源起始集数"),
            ParseOptionalPositiveInteger(row.SourceEnd.Text, "AUTO_MATCH_MAPPING_TABLE 源结束集数"),
            RequiredText(row.TargetTitle, "AUTO_MATCH_MAPPING_TABLE 目标标题"),
            ParsePositiveInteger(row.TargetSeason.Text, "AUTO_MATCH_MAPPING_TABLE 目标季数"),
            ParsePositiveInteger(row.TargetStart.Text, "AUTO_MATCH_MAPPING_TABLE 目标起始集数"),
            ParseOptionalPositiveInteger(row.TargetEnd.Text, "AUTO_MATCH_MAPPING_TABLE 目标结束集数"),
            row.TargetPlatform.Values.SingleOrDefault());

    private static StackPanel CreateSideEditor(
        string label,
        TextBox title,
        TextBox season,
        TextBox start,
        TextBox end) => new()
    {
        Spacing = 5,
        Children =
        {
            new TextBlock { Text = label },
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { title, season, start, end },
            },
        },
    };

    private static StackPanel CreateLabeledControl(string label, Control control) => new()
    {
        Spacing = 4,
        Children = { new TextBlock { Text = label }, control },
    };

    private static TextBox CreatePositiveIntegerInput(int? value, string watermark, double width) => new()
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

    private static int ParsePositiveInteger(string? text, string field) =>
        ParseOptionalPositiveInteger(text, field)
        ?? throw new FormatException($"{field}不能为空");

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

    private sealed class Row
    {
        public required TextBox SourceTitle { get; init; }
        public required TextBox SourceSeason { get; init; }
        public required TextBox SourceStart { get; init; }
        public required TextBox SourceEnd { get; init; }
        public required TextBox TargetTitle { get; init; }
        public required TextBox TargetSeason { get; init; }
        public required TextBox TargetStart { get; init; }
        public required TextBox TargetEnd { get; init; }
        public required TagPicker TargetPlatform { get; init; }
        public StackPanel Container { get; set; } = null!;
        public TextBlock Heading { get; set; } = null!;
        public Button Remove { get; set; } = null!;
    }
}
