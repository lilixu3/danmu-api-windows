using Avalonia.Controls;

namespace DanmuApi.App.Views;

/// <summary>
/// 设置页的侧栏/内容宽度收敛，实现见 <see cref="ResponsiveWorkspace"/>（与配置页、工具页同一份）。
/// </summary>
public partial class SettingsView : UserControl
{
    private readonly ResponsiveWorkspace _layout;

    public SettingsView()
    {
        InitializeComponent();
        _layout = new ResponsiveWorkspace(LayoutRoot, CategoryRail, RailHeader, SettingsScroller);
        SizeChanged += (_, _) => _layout.ApplyWidth(Bounds.Width);
    }

    /// <summary>供渲染测试在 <c>Window.Opened</c> 时显式指定断点。</summary>
    public void ApplyLayoutForWidth(double width) => _layout.ApplyWidth(width);
}
