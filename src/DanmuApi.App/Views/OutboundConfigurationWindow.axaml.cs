using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DanmuApi.App.ViewModels;

namespace DanmuApi.App.Views;

public partial class OutboundConfigurationWindow : Window
{
    private bool _saved;

    public OutboundConfigurationWindow()
    {
        InitializeComponent();
        Closing += (_, args) =>
        {
            if (DataContext is OutboundDirectViewModel { IsBusy: true }) args.Cancel = true;
        };
        Closed += (_, _) =>
        {
            if (!_saved && DataContext is OutboundDirectViewModel model) model.DiscardConfigurationEdit();
        };
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape && DataContext is OutboundDirectViewModel { IsBusy: false })
            {
                args.Handled = true;
                Close();
            }
        };
    }

    public OutboundConfigurationWindow(OutboundDirectViewModel model) : this() => DataContext = model;

    private void CancelEdit(object? sender, RoutedEventArgs args) => Close();

    private async void SaveEdit(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not OutboundDirectViewModel model || !model.ApplyConfigurationCommand.CanExecute(null)) return;
        try
        {
            await model.ApplyConfigurationCommand.ExecuteAsync(null);
            if (!model.LastSaveSucceeded) return;
            _saved = true;
            Close();
        }
        catch (Exception error) { model.ReportDialogFailure("保存连接选项失败", error); }
    }
}
