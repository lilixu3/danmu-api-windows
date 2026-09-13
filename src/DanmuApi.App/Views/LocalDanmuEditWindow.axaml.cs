using Avalonia.Controls;

namespace DanmuApi.App.Views;

/// <summary>编辑本地弹幕弹窗（无额外代码逻辑：状态与动作都在 <see cref="ViewModels.LocalDanmuEditDialogViewModel"/>）。</summary>
public partial class LocalDanmuEditWindow : Window
{
    public LocalDanmuEditWindow()
    {
        InitializeComponent();
    }
}
