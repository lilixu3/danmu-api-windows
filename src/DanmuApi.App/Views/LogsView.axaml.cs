using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using DanmuApi.App.ViewModels;

namespace DanmuApi.App.Views;

public partial class LogsView : UserControl
{
    private LogsPageViewModel? _viewModel;
    private ScrollViewer? _scrollViewer;

    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnDataContextChanged(object? sender, EventArgs args)
    {
        if (_viewModel is not null)
        {
            _viewModel.ScrollToEndRequested -= OnScrollToEndRequested;
        }

        _viewModel = DataContext as LogsPageViewModel;
        if (_viewModel is not null)
        {
            _viewModel.ScrollToEndRequested += OnScrollToEndRequested;
            ReportViewportPosition();
        }
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs args)
    {
        AttachScrollViewer();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs args)
    {
        DetachScrollViewer();
    }

    private void AttachScrollViewer()
    {
        var viewer = LogList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (ReferenceEquals(_scrollViewer, viewer))
        {
            return;
        }

        DetachScrollViewer();
        _scrollViewer = viewer;
        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollChanged += OnScrollChanged;
            ReportViewportPosition();
        }
    }

    private void DetachScrollViewer()
    {
        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollChanged -= OnScrollChanged;
            _scrollViewer = null;
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs args)
    {
        ReportViewportPosition();
    }

    private void ReportViewportPosition()
    {
        if (_viewModel is null || _scrollViewer is null)
        {
            return;
        }

        var remaining = _scrollViewer.Extent.Height -
                        _scrollViewer.Viewport.Height -
                        _scrollViewer.Offset.Y;
        _viewModel.NotifyViewportAtLatest(remaining <= 8);
    }

    private void OnScrollToEndRequested(object? sender, EventArgs args)
    {
        var items = LogList.ItemsSource as System.Collections.IList;
        if (items is { Count: > 0 })
        {
            LogList.ScrollIntoView(items[^1]!);
            _viewModel?.NotifyViewportAtLatest(true);
            AttachScrollViewer();
        }
    }
}
