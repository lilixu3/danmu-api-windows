using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DanmuApi.App.ViewModels;

namespace DanmuApi.App.Views;

public partial class OutboundDiagnosticsWindow : Window
{
    private bool _closing;
    private bool _started;

    public OutboundDiagnosticsWindow()
    {
        InitializeComponent();
        Closing += (_, _) => _closing = true;
        Closed += (_, _) =>
        {
            if (DataContext is OutboundDirectViewModel { CanCancel: true } model) model.CancelCommand.Execute(null);
        };
        Opened += (_, _) =>
        {
            if (DataContext is OutboundDirectViewModel { IsDiagnosing: true }) _started = true;
        };
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) { args.Handled = true; Close(); }
        };
    }

    public OutboundDiagnosticsWindow(OutboundDirectViewModel model) : this() => DataContext = model;

    public async Task StartTestAsync()
    {
        if (_started || _closing || DataContext is not OutboundDirectViewModel model) return;
        _started = true;
        await model.ActivateAsync();
        if (!_closing && model.DiagnoseCommand.CanExecute(null)) await model.DiagnoseCommand.ExecuteAsync(null);
    }

    private void CloseTest(object? sender, RoutedEventArgs args) => Close();
}
