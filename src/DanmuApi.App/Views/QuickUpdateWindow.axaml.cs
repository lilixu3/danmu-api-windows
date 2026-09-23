using Avalonia.Controls;

namespace DanmuApi.App.Views;

/// <summary>
/// 侧栏「有更新」卡片的快速更新弹窗（无额外代码逻辑：数据与动作都由
/// <see cref="ViewModels.QuickUpdateDialogViewModel"/> 承担，关闭由服务的 CloseRequested 驱动）。
/// </summary>
public partial class QuickUpdateWindow : Window
{
    public QuickUpdateWindow()
    {
        InitializeComponent();
    }
}
