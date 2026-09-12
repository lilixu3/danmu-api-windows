using Avalonia.Controls;

namespace DanmuApi.App.Views;

/// <summary>
/// 配置页在 1100 / 900 两个断点收敛布局，具体实现见 <see cref="ResponsiveWorkspace"/>
/// （配置页 / 设置页 / 工具页共用同一份，避免三处各写一套）。
/// </summary>
public partial class ConfigurationView : UserControl
{
    private readonly ResponsiveWorkspace _layout;

    public ConfigurationView()
    {
        InitializeComponent();
        _layout = new ResponsiveWorkspace(LayoutRoot, CategoryRail, RailHeader, ListColumn, DetailPanel);
        SizeChanged += (_, _) => _layout.ApplyWidth(Bounds.Width);
    }

    /// <summary>
    /// 按给定宽度收敛布局。headless 渲染测试里窗口的首次布局不一定给 content
    /// 一个带最终宽度的 measure pass（<c>SizeChanged</c> 因此不触发），
    /// 由测试在 <c>Window.Opened</c> 时显式调用，保证渲染出的就是目标断点。
    /// </summary>
    public void ApplyLayoutForWidth(double width) => _layout.ApplyWidth(width);
}
