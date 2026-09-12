using Avalonia.Controls;

namespace DanmuApi.App.Views;

/// <summary>弹幕文件详情弹窗（无额外代码逻辑：数据与动作都由 <see cref="ViewModels.LocalDanmuDetailDialogViewModel"/> 承担）。</summary>
public partial class LocalDanmuDetailWindow : Window
{
    public LocalDanmuDetailWindow()
    {
        InitializeComponent();
    }
}
