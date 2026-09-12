using Avalonia;
using Avalonia.Controls;

namespace DanmuApi.App.Views;

/// <summary>
/// 「分类侧栏 + 内容（+ 可选详情面板）」的宽窄收敛，配置页 / 设置页 / 工具页共用一份实现。
///
/// 手法与 <see cref="CorePageView"/> 一致：只改 ColumnDefinitions / RowDefinitions 与子元素的
/// Grid 行列，不重建可视树，因此选中项、滚动位置与绑定都不会丢。
/// 档位阈值沿用配置页：≥1100 三栏、≥900 两栏、其余单栏。没有详情面板的页面只用得到后两档。
///
/// rail 的 ItemsPanel 保持在 XAML 里（竖排 VirtualizingStackPanel），单栏档位不换面板：
/// rail 本身塌成 Auto 高度、多个分类纵向排开仍可用。换成横向 WrapPanel 需要运行期构造
/// ItemsPanelTemplate，而 <c>ItemsPanelTemplate.Build()</c> 只认 XAML 装载出来的节点，
/// 为此引入运行期 XAML 解析不划算。
/// </summary>
internal sealed class ResponsiveWorkspace
{
    public const double ThreeColumnMinimumWidth = 1100;
    public const double TwoColumnMinimumWidth = 900;

    private readonly Grid _root;
    private readonly Border _rail;
    private readonly StackPanel? _railHeader;
    private readonly Control _content;
    private readonly Control? _detail;
    private LayoutMode _mode = LayoutMode.Unknown;

    public ResponsiveWorkspace(Grid root, Border rail, StackPanel? railHeader, Control content, Control? detail = null)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _rail = rail ?? throw new ArgumentNullException(nameof(rail));
        _railHeader = railHeader;
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _detail = detail;
    }

    /// <summary>按给定宽度收敛布局。详情面板只在这一个页面里可选。</summary>
    public void ApplyWidth(double width) =>
        Apply(width switch
        {
            >= ThreeColumnMinimumWidth when _detail is not null => LayoutMode.ThreeColumn,
            >= TwoColumnMinimumWidth => LayoutMode.TwoColumn,
            _ => LayoutMode.SingleColumn,
        });

    private enum LayoutMode
    {
        Unknown,
        ThreeColumn,
        TwoColumn,
        SingleColumn,
    }

    private void Apply(LayoutMode mode)
    {
        // 只按「目标列数」判断是否已生效，不用 _mode 早退：_mode 可能已被真实 SizeChanged 设过
        // （例如窗口尚未缩到目标宽度时先触发了一次布局），此时早退会留下与目标断点不符的结构。
        // 渲染测试就是在 Show() 之后显式调 ApplyWidth 的。
        if (mode == _mode || IsApplied(mode))
        {
            _mode = mode;
            return;
        }

        _mode = mode;
        switch (mode)
        {
            case LayoutMode.ThreeColumn:
                // 侧栏 + 内容 + 详情面板。详情列用 Auto：面板隐藏（未选中）时该列收成 0，
                // 内容独占剩余宽度；面板自带固定宽度，选中时列宽即它本身。
                _root.ColumnDefinitions = new ColumnDefinitions("216,*,Auto");
                _root.RowDefinitions = new RowDefinitions("*");
                ApplyRailChrome(compact: false);
                Place(_rail, row: 0, column: 0);
                Place(_content, row: 0, column: 1);
                Place(_detail, row: 0, column: 2);
                break;

            case LayoutMode.TwoColumn:
                // 侧栏 + 内容；详情面板落到内容下方。
                _root.ColumnDefinitions = new ColumnDefinitions("216,*");
                _root.RowDefinitions = _detail is null ? new RowDefinitions("*") : new RowDefinitions("*,Auto");
                ApplyRailChrome(compact: false);
                Place(_rail, row: 0, column: 0);
                Place(_content, row: 0, column: 1);
                Place(_detail, row: 1, column: 1);
                break;

            default:
                // 单栏：侧栏塌成内容上方的窄条（隐藏说明文字省高度）。
                _root.ColumnDefinitions = new ColumnDefinitions("*");
                _root.RowDefinitions = _detail is null ? new RowDefinitions("Auto,*") : new RowDefinitions("Auto,*,Auto");
                ApplyRailChrome(compact: true);
                Place(_rail, row: 0, column: 0);
                Place(_content, row: 1, column: 0);
                Place(_detail, row: 2, column: 0);
                break;
        }
    }

    private void ApplyRailChrome(bool compact)
    {
        if (_railHeader is not null)
        {
            _railHeader.IsVisible = !compact;
        }

        _rail.Padding = compact ? new Thickness(6, 2) : new Thickness(6);
    }

    private bool IsApplied(LayoutMode mode)
    {
        var expectedColumns = mode switch
        {
            LayoutMode.ThreeColumn => 3,
            LayoutMode.TwoColumn => 2,
            LayoutMode.SingleColumn => 1,
            _ => 0,
        };
        return expectedColumns != 0 && _root.ColumnDefinitions.Count == expectedColumns;
    }

    private static void Place(Control? control, int row, int column)
    {
        if (control is null)
        {
            return;
        }

        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
    }
}
