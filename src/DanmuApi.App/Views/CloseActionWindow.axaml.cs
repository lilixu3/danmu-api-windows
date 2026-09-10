using Avalonia.Controls;
using DanmuApi.App.Services;

namespace DanmuApi.App.Views;

public partial class CloseActionWindow : Window
{
    public CloseActionWindow()
    {
        InitializeComponent();
        ConfirmButton.Click += OnConfirm;
        CancelButton.Click += (_, _) => Close(null);
    }

    private void OnConfirm(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        var action = TrayOption.IsChecked == true ? CloseAction.Tray : CloseAction.Exit;
        Close(new CloseActionDecision(action, RememberChoice.IsChecked == true));
    }
}
