using Avalonia.Controls;
using DanmuApi.App.ViewModels;

namespace DanmuApi.App.Views;

public partial class CorePageView : UserControl
{
    private bool _isNarrow;

    public CorePageView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateLayoutColumns();
    }

    /// <summary>窄窗口下把「来源与探索」的两栏收敛为单栏：探索入口移到来源下方。</summary>
    private void UpdateLayoutColumns()
    {
        var narrow = Bounds.Width < 900;
        if (narrow == _isNarrow)
        {
            return;
        }

        _isNarrow = narrow;
        SourceGrid.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "1.55*,1*");
        Grid.SetColumn(ExploreColumn, narrow ? 0 : 1);
        Grid.SetRow(ExploreColumn, narrow ? 1 : 0);
    }

    /// <summary>
    /// 点开分支下拉就直接刷新，但必须走静默刷新：LoadBranchesCommand 会弹模态进度对话框，
    /// 用户还没选完下拉就被打断，而且它每次都会把选择重置回当前分支。
    /// </summary>
    private async void OnBranchComboBoxDropDownOpened(object? sender, System.EventArgs e)
    {
        if (DataContext is CorePageViewModel viewModel)
        {
            await viewModel.RefreshBranchesQuietlyAsync();
        }
    }
}
