using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DanmuApi.App.ViewModels;

namespace DanmuApi.App.Views;

public partial class OutboundDirectView : UserControl
{
    private readonly List<Visual> _visibilityParents = [];
    private OutboundDirectViewModel? _activeModel;
    private bool _attached;
    private Window? _dialog;

    public OutboundDirectView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            foreach (var parent in this.GetVisualAncestors())
            {
                _visibilityParents.Add(parent);
                parent.PropertyChanged += OnVisibilityChanged;
            }
            UpdateMonitoring();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            foreach (var parent in _visibilityParents) parent.PropertyChanged -= OnVisibilityChanged;
            _visibilityParents.Clear();
            UpdateMonitoring();
        };
        DataContextChanged += (_, _) => UpdateMonitoring();
        PropertyChanged += OnVisibilityChanged;
    }

    private void ToggleRuntimeDetails(object? sender, RoutedEventArgs args)
    {
        RuntimeDetailsContent.IsVisible = !RuntimeDetailsContent.IsVisible;
        RuntimeDetailsToggle.Content = RuntimeDetailsContent.IsVisible ? "收起运行详情" : "查看运行详情";
    }

    private async void OpenConfiguration(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not OutboundDirectViewModel model || _dialog is not null) return;
        try
        {
            if (!model.BeginConfigurationEdit()) return;
            var owner = TopLevel.GetTopLevel(this) as Window ?? throw new InvalidOperationException("无法取得连接选项窗口的父窗口。");
            var dialog = new OutboundConfigurationWindow(model);
            using var theme = dialog.Bind(ThemeVariantScope.RequestedThemeVariantProperty, owner.GetObservable(TopLevel.ActualThemeVariantProperty));
            FitDialogToOwner(dialog, owner);
            _dialog = dialog;
            await dialog.ShowDialog(owner);
        }
        catch (Exception error)
        {
            model.DiscardConfigurationEdit();
            model.ReportDialogFailure("打开连接选项失败", error);
        }
        finally { _dialog = null; }
    }

    private async void OpenDiagnostics(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not OutboundDirectViewModel model || _dialog is not null) return;
        try
        {
            var owner = TopLevel.GetTopLevel(this) as Window ?? throw new InvalidOperationException("无法取得连接测速窗口的父窗口。");
            var dialog = new OutboundDiagnosticsWindow(model);
            using var theme = dialog.Bind(ThemeVariantScope.RequestedThemeVariantProperty, owner.GetObservable(TopLevel.ActualThemeVariantProperty));
            FitDialogToOwner(dialog, owner);
            _dialog = dialog;
            var completion = dialog.ShowDialog(owner);
            try { await dialog.StartTestAsync(); }
            catch (Exception error) { model.ReportDialogFailure("启动连接测速失败", error); }
            await completion;
        }
        catch (Exception error) { model.ReportDialogFailure("打开连接测速失败", error); }
        finally { _dialog = null; }
    }

    private static void FitDialogToOwner(Window dialog, Window owner)
    {
        dialog.Width = Math.Min(dialog.Width, Math.Max(dialog.MinWidth, owner.Bounds.Width - 40));
        var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
        if (screen is null || screen.Scaling <= 0) return;
        var width = Math.Max(320, screen.WorkingArea.Width / screen.Scaling - 40);
        var height = Math.Max(320, screen.WorkingArea.Height / screen.Scaling - 40);
        dialog.MinWidth = Math.Min(dialog.MinWidth, width);
        dialog.MinHeight = Math.Min(dialog.MinHeight, height);
        dialog.MaxWidth = width;
        dialog.MaxHeight = height;
        dialog.Width = Math.Min(dialog.Width, width);
        dialog.Height = Math.Min(dialog.Height, height);
    }

    private void OnVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == IsVisibleProperty) UpdateMonitoring();
    }

    private void UpdateMonitoring()
    {
        var next = _attached && IsEffectivelyVisible ? DataContext as OutboundDirectViewModel : null;
        if (!ReferenceEquals(_activeModel, next))
        {
            _activeModel?.EndMonitoring();
            _activeModel = next;
        }
        _activeModel?.BeginMonitoring();
    }
}
