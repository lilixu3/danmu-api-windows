using Avalonia;
using Avalonia.Controls;

namespace DanmuApi.App.Views;

/// <summary>
/// 配置页在 1100 / 900 两个断点收敛布局。手法与 <see cref="CorePageView"/> 一致：
/// 只改 ColumnDefinitions / RowDefinitions 与子元素的 Grid 行列，不重建可视树，
/// 因此选中项、滚动位置与绑定都不会丢。
///
/// 分类 rail 的 ItemsPanel 保持在 XAML 里（竖排 VirtualizingStackPanel）。
/// 单栏档位不换面板：rail 本身塌成 Auto 高度，多个分类纵向排开仍可用，
/// 换成横向 WrapPanel 需要运行期构造 ItemsPanelTemplate，
/// 而 <c>ItemsPanelTemplate.Build()</c> 只认 XAML 装载出来的节点
/// （用 FuncTemplate 包实例会抛 "Unexpected content ... FuncTemplate"，实测过），
/// 为此引入运行期 XAML 解析不划算。
/// </summary>
public partial class ConfigurationView : UserControl
{
    private const double ThreeColumnMinimumWidth = 1100;
    private const double TwoColumnMinimumWidth = 900;

    private LayoutMode _mode = LayoutMode.Unknown;

    public ConfigurationView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateLayoutColumns();
    }

    /// <summary>
    /// 按给定宽度收敛布局。headless 渲染测试里窗口的首次布局不一定给 content
    /// 一个带最终宽度的 measure pass（<c>SizeChanged</c> 因此不触发），
    /// 由测试在 <c>Window.Opened</c> 时显式调用，保证渲染出的就是目标断点。
    /// </summary>
    public void ApplyLayoutForWidth(double width)
    {
        var mode = width switch
        {
            >= ThreeColumnMinimumWidth => LayoutMode.ThreeColumn,
            >= TwoColumnMinimumWidth => LayoutMode.TwoColumn,
            _ => LayoutMode.SingleColumn,
        };
        ApplyMode(mode);
    }

    private enum LayoutMode
    {
        Unknown,
        ThreeColumn,
        TwoColumn,
        SingleColumn,
    }

    private void UpdateLayoutColumns() =>
        ApplyMode(Bounds.Width switch
        {
            >= ThreeColumnMinimumWidth => LayoutMode.ThreeColumn,
            >= TwoColumnMinimumWidth => LayoutMode.TwoColumn,
            _ => LayoutMode.SingleColumn,
        });

    private void ApplyMode(LayoutMode mode)
    {
        // 只按「目标列数」判断是否已生效，不用 _mode 早退：
        // _mode 可能已被真实 SizeChanged 设过（例如窗口尚未缩到目标宽度时先触发了
        // 一次布局），此时早退会留下与目标断点不符的结构。渲染测试就是在
        // Show() 之后显式调 ApplyLayoutForWidth 的。
        if (mode == _mode || IsApplied(mode))
        {
            _mode = mode;
            return;
        }

        _mode = mode;
        switch (mode)
        {
            case LayoutMode.ThreeColumn:
                // 分类 rail + 变量列表 + 详情面板。
                // 详情列用 Auto：面板隐藏（未选中变量）时该列收成 0，
                // 列表独占剩余宽度；面板自带 Width=320，选中时列宽即 320。
                LayoutRoot.ColumnDefinitions = new ColumnDefinitions("216,*,Auto");
                LayoutRoot.RowDefinitions = new RowDefinitions("*");
                RailHeader.IsVisible = true;
                CategoryRail.Padding = new Thickness(6);
                Place(CategoryRail, row: 0, column: 0);
                Place(ListColumn, row: 0, column: 1);
                Place(DetailPanel, row: 0, column: 2);
                break;

            case LayoutMode.TwoColumn:
                // 分类 rail + 变量列表；详情面板落到列表下方。
                LayoutRoot.ColumnDefinitions = new ColumnDefinitions("216,*");
                LayoutRoot.RowDefinitions = new RowDefinitions("*,Auto");
                RailHeader.IsVisible = true;
                CategoryRail.Padding = new Thickness(6);
                Place(CategoryRail, row: 0, column: 0);
                Place(ListColumn, row: 0, column: 1);
                Place(DetailPanel, row: 1, column: 1);
                break;

            default:
                // 单栏：分类 rail 塌成列表上方的窄条（隐藏说明文字省高度）。
                LayoutRoot.ColumnDefinitions = new ColumnDefinitions("*");
                LayoutRoot.RowDefinitions = new RowDefinitions("Auto,*,Auto");
                RailHeader.IsVisible = false;
                CategoryRail.Padding = new Thickness(6, 2);
                Place(CategoryRail, row: 0, column: 0);
                Place(ListColumn, row: 1, column: 0);
                Place(DetailPanel, row: 2, column: 0);
                break;
        }
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
        return expectedColumns != 0 && LayoutRoot.ColumnDefinitions.Count == expectedColumns;
    }

    private static void Place(Control control, int row, int column)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
    }
}
