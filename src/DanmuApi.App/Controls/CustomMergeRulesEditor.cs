using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DanmuApi.Core;
using DanmuApi.Runtime;

namespace DanmuApi.App.Controls;

public sealed class CustomMergeRulesEditor : Border
{
    private static readonly string[] Relations = ["合并 →", "阻断 ×"];
    private readonly IReadOnlyList<string> _sources;
    private readonly StackPanel _rowsPanel = new() { Spacing = 14 };
    private readonly List<Row> _rows = [];

    public CustomMergeRulesEditor(IEnumerable<string> sources, IEnumerable<CustomMergeRule> rules)
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
            Name = "AddCustomMergeRuleButton",
            Content = "添加自定义规则",
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

    public IReadOnlyList<CustomMergeRule> Rules => _rows.Select(ToRule).ToArray();

    private static TextBlock CreateHint()
    {
        var hint = new TextBlock
        {
            Text = "实体由剧名、可选季数和一个或多个来源组成。阻断关系不使用集数路由。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        hint.Classes.Add("body-muted");
        return hint;
    }

    private void AddRowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => AddRow(null);

    private void AddRow(CustomMergeRule? rule)
    {
        var row = new Row
        {
            SecondaryTitle = new TextBox { Text = rule?.Secondary.Title, Watermark = "副源剧名", Width = 230 },
            SecondarySeason = CreateSeasonInput(rule?.Secondary.Season, "季（可空）"),
            SecondarySources = new TagPicker(_sources, rule?.Secondary.Sources ?? [], compareTokens: true),
            Relation = new ComboBox
            {
                ItemsSource = Relations,
                SelectedIndex = rule?.IsBlocked == true ? 1 : 0,
                Width = 120,
            },
            PrimaryTitle = new TextBox { Text = rule?.Primary.Title, Watermark = "主源剧名", Width = 230 },
            PrimarySeason = CreateSeasonInput(rule?.Primary.Season, "季（可空）"),
            PrimarySources = new TagPicker(_sources, rule?.Primary.Sources ?? [], compareTokens: true),
            Routes = new TextBox
            {
                Text = rule is null ? string.Empty : FormatRoutes(rule.Routes),
                Watermark = "E01>E01,E25~E35>E25~E35（可空）",
                Width = 500,
            },
        };
        row.Container = new StackPanel { Spacing = 8 };
        row.Heading = new TextBlock { FontWeight = Avalonia.Media.FontWeight.SemiBold };
        row.Remove = new Button
        {
            Name = "RemoveCustomMergeRuleButton",
            Content = "删除此规则",
            HorizontalAlignment = HorizontalAlignment.Left,
            Tag = row,
        };
        row.Remove.Classes.Add("secondary-action");
        row.Remove.Click += RemoveRowClick;
        row.Relation.Tag = row;
        row.Relation.SelectionChanged += RelationChanged;
        row.Container.Children.Add(row.Heading);
        row.Container.Children.Add(CreateEntityEditor("副源实体", row.SecondaryTitle, row.SecondarySeason, row.SecondarySources));
        row.Container.Children.Add(CreateLabeledControl("关系", row.Relation));
        row.Container.Children.Add(CreateEntityEditor("主源实体", row.PrimaryTitle, row.PrimarySeason, row.PrimarySources));
        row.Container.Children.Add(CreateLabeledControl("集数路由（仅合并关系）", row.Routes));
        row.Container.Children.Add(row.Remove);
        row.Routes.IsEnabled = row.Relation.SelectedIndex == 0;
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

    private static void RelationChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (sender is ComboBox { Tag: Row row })
        {
            row.Routes.IsEnabled = row.Relation.SelectedIndex == 0;
        }
    }

    private void RenumberRows()
    {
        for (var index = 0; index < _rows.Count; index++)
        {
            _rows[index].Heading.Text = $"自定义规则 {index + 1}";
        }
    }

    private static CustomMergeRule ToRule(Row row)
    {
        var blocked = row.Relation.SelectedIndex == 1;
        var routes = blocked ? Array.Empty<EpisodeRoute>() : ParseRoutes(row.Routes.Text ?? string.Empty);
        return new CustomMergeRule(
            CreateEntity(row.SecondaryTitle, row.SecondarySeason, row.SecondarySources),
            blocked,
            CreateEntity(row.PrimaryTitle, row.PrimarySeason, row.PrimarySources),
            routes);
    }

    private static CustomMergeEntity CreateEntity(TextBox title, TextBox season, TagPicker sources) =>
        new(
            RequiredText(title, "CUSTOM_MERGE_RULES 剧名"),
            ParseOptionalPositiveInteger(season.Text, "CUSTOM_MERGE_RULES 季数"),
            sources.Values);

    private static StackPanel CreateEntityEditor(string label, TextBox title, TextBox season, TagPicker sources) => new()
    {
        Spacing = 5,
        Children =
        {
            new TextBlock { Text = label },
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children = { title, season },
            },
            CreateLabeledControl("来源（可多选）", sources),
        },
    };

    private static StackPanel CreateLabeledControl(string label, Control control) => new()
    {
        Spacing = 4,
        Children = { new TextBlock { Text = label }, control },
    };

    private static TextBox CreateSeasonInput(int? value, string watermark) => new()
    {
        Text = value?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Watermark = watermark,
        Width = 90,
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

    private static IReadOnlyList<EpisodeRoute> ParseRoutes(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var definition = new CoreEnvDefinition(
            "CUSTOM_MERGE_RULES", "source", CoreEnvType.Text, string.Empty, [], ["secondary", "primary"], null, null, null, false, false);
        var parsed = CoreEnvStructuredValues.ParseCustomMergeRules(
            definition,
            $"副源@secondary -> 主源@primary | {value.Trim()}");
        return parsed[0].Routes;
    }

    private static string FormatRoutes(IReadOnlyList<EpisodeRoute> routes)
    {
        if (routes.Count == 0)
        {
            return string.Empty;
        }

        var sample = new CustomMergeRule(
            new CustomMergeEntity("副源", null, ["secondary"]),
            false,
            new CustomMergeEntity("主源", null, ["primary"]),
            routes);
        var formatted = CoreEnvStructuredValues.FormatCustomMergeRules([sample]);
        return formatted[(formatted.IndexOf('|') + 1)..].Trim();
    }

    private sealed class Row
    {
        public required TextBox SecondaryTitle { get; init; }
        public required TextBox SecondarySeason { get; init; }
        public required TagPicker SecondarySources { get; init; }
        public required ComboBox Relation { get; init; }
        public required TextBox PrimaryTitle { get; init; }
        public required TextBox PrimarySeason { get; init; }
        public required TagPicker PrimarySources { get; init; }
        public required TextBox Routes { get; init; }
        public StackPanel Container { get; set; } = null!;
        public TextBlock Heading { get; set; } = null!;
        public Button Remove { get; set; } = null!;
    }

}
