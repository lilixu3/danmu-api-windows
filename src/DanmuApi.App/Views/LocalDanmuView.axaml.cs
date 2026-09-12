using Avalonia.Controls;

namespace DanmuApi.App.Views;

/// <summary>
/// 本地弹幕页：列表独占整列（内容自己滚动）。详情改成弹窗后这里不再需要宽窄两栏的
/// 行列切换——那套切换正是「窄屏详情被截、列表被限高」的来源。
/// </summary>
public partial class LocalDanmuView : UserControl
{
    public LocalDanmuView()
    {
        InitializeComponent();
    }
}
