using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DanmuApi.Core;
using DanmuApi.Runtime;

namespace DanmuApi.App.Controls;

/// <summary>
/// 映射表编辑器（TITLE_MAPPING_TABLE / AUTO_MATCH_MAPPING_TABLE）。结构与核心自带前端
/// <c>type === 'map'</c> 分支一致：
///
/// <list type="number">
/// <item>「映射配置」批量多行框（<c>原值-&gt;映射值;原值2-&gt;映射值2</c>）。</item>
/// <item>「解析并更新列表」按钮，把批量文本拆成下面的逐行编辑。</item>
/// <item>逐行 <c>[原始值] -&gt; [映射值] [删除]</c>。</item>
/// <item>底部一行：「添加映射项」（左）与「查看最近数据」（右）。</item>
/// </list>
///
/// 保存值仍由逐行内容拼成 <c>left-&gt;right;…</c>；调用方在保存前对整串做严格校验。
/// </summary>
public sealed class MappingTableEditor : StackPanel, IRecentDataSplitHost
{
    private static readonly char[] BulkSeparators = [';', '\r', '\n'];

    private readonly CoreEnvDefinition _definition;
    private readonly StackPanel _rowsPanel = new() { Spacing = 0 };
    private readonly TextBox _bulk;
    private readonly List<Row> _rows = [];
    private readonly TextBlock _errorText = new()
    {
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        IsVisible = false,
        Margin = new Thickness(0, 0, 0, 8),
    };

    public MappingTableEditor(CoreEnvDefinition definition, string initial)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(initial);
        _definition = definition;

        _bulk = new TextBox
        {
            Name = "MapBulkValueBox",
            Text = initial,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MinHeight = 80,
            MaxHeight = 200,
            Watermark = "原值->映射值;原值2->映射值2",
        };
        _bulk.Classes.Add("mono");

        var parse = ConfigForm.SmallButton("解析并更新列表", primary: false);
        parse.Name = "ParseBulkMapItemsButton";
        parse.Click += (_, _) =>
        {
            try
            {
                ParseBulkIntoRows();
            }
            catch (FormatException error)
            {
                _errorText.Text = error.Message;
                _errorText.IsVisible = true;
            }
        };

        var add = ConfigForm.SmallButton("添加映射项", primary: true);
        add.Name = "AddMapItemButton";
        add.Click += (_, _) => AddRow(string.Empty, string.Empty);

        Spacing = 8;
        Children.Add(ConfigForm.Field("映射配置", _bulk));
        Children.Add(new StackPanel { Margin = new Thickness(0, 0, 0, 8), Children = { parse } });
        Children.Add(_rowsPanel);
        Children.Add(_errorText);
        Children.Add(ConfigForm.SplitActionRow(add, RecentDataButtonHost));
        Children.Add(RecentDataHost);

        foreach (var (left, right) in SplitPairs(initial))
        {
            AddRow(left, right);
        }
    }

    public ContentControl RecentDataHost { get; } = new()
    {
        Name = "MappingTableRecentDataHost",
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };

    public ContentControl RecentDataButtonHost { get; } = new()
    {
        Name = "MappingTableRecentDataButtonHost",
        HorizontalAlignment = HorizontalAlignment.Right,
    };

    /// <summary>逐行内容拼成的保存值：<c>left-&gt;right</c> 用 <c>;</c> 连接，空行跳过。</summary>
    public string Value => string.Join(';', _rows
        .Select(row => $"{row.Left.Text?.Trim()}->{row.Right.Text?.Trim()}")
        .Where(value => !value.Equals("->", StringComparison.Ordinal)));

    public string BulkText => _bulk.Text ?? string.Empty;

    public int RowCount => _rows.Count;

    /// <summary>把批量框里的文本拆成逐行编辑（核心 <c>parseBulkMapItems</c>）。</summary>
    public void ParseBulkIntoRows()
    {
        foreach (var row in _rows)
        {
            _rowsPanel.Children.Remove(row.Container);
        }

        _rows.Clear();
        foreach (var (left, right) in SplitPairs(_bulk.Text ?? string.Empty))
        {
            AddRow(left, right);
        }

        _errorText.IsVisible = false;
    }

    /// <summary>校验当前值（保存前由调用方触发）。</summary>
    public void Validate() =>
        CoreEnvStructuredValidation.Validate(_definition, Value);

    private static IEnumerable<(string Left, string Right)> SplitPairs(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (var item in text.Split(BulkSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            // 核心用第一个 '->' 切分；缺分隔符的条目原样当作左值，交给保存校验拒绝。
            var separator = item.IndexOf("->", StringComparison.Ordinal);
            yield return separator < 0
                ? (item, string.Empty)
                : (item[..separator].Trim(), item[(separator + 2)..].Trim());
        }
    }

    private void AddRow(string left, string right)
    {
        var row = new Row
        {
            Left = new TextBox { Name = "MappingSourceTextBox", Watermark = "原始值", Text = left },
            Right = new TextBox { Name = "MappingTargetTextBox", Watermark = "映射值", Text = right },
        };

        row.Remove = new Button { Name = "RemoveMapItemButton", Content = "删除" };
        row.Remove.Classes.Add("danger");
        row.Remove.Tag = row;
        row.Remove.Click += (_, _) =>
        {
            _rows.Remove(row);
            _rowsPanel.Children.Remove(row.Container);
        };

        var separator = new TextBlock
        {
            Text = "->",
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = Avalonia.Media.FontWeight.Bold,
        };
        separator.Classes.Add("cap");

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*,Auto"), ColumnSpacing = 10 };
        grid.Children.Add(row.Left);
        Grid.SetColumn(separator, 1);
        grid.Children.Add(separator);
        Grid.SetColumn(row.Right, 2);
        grid.Children.Add(row.Right);
        Grid.SetColumn(row.Remove, 3);
        grid.Children.Add(row.Remove);

        row.Container = new Border { Child = grid };
        row.Container.Classes.Add("map-row");
        _rows.Add(row);
        _rowsPanel.Children.Add(row.Container);
    }

    private sealed class Row
    {
        public required TextBox Left { get; init; }
        public required TextBox Right { get; init; }
        public Border Container { get; set; } = null!;
        public Button Remove { get; set; } = null!;
    }
}
