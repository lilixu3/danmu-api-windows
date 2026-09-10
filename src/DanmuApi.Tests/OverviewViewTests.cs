using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DanmuApi.App.Views;

namespace DanmuApi.Tests;

public sealed class OverviewViewTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverviewReflowsWithoutDuplicatingMaintenanceActions(bool dark)
    {
        var view = new OverviewView();
        var window = new Window { Width = 1100, Height = 800, Content = view,
            RequestedThemeVariant = dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var service = view.FindControl<Border>("ServicePanel")!;
            var connections = view.FindControl<Border>("DiagnosticsPanel")!;
            var details = view.FindControl<Border>("AddressDetailsPanel")!;
            var trend = view.FindControl<Border>("TrendPanel")!;
            Assert.Equal(1, Grid.GetRowSpan(service));
            Assert.Equal(2, Grid.GetColumnSpan(details));
            Assert.Equal(1, Grid.GetRow(details));
            Assert.Equal(120, view.FindControl<RequestTrendControl>("RequestTrend")!.Height);
            Assert.Equal(double.PositiveInfinity, view.FindControl<StackPanel>("OverviewContent")!.MaxWidth);
            var connectionBottom = connections.TranslatePoint(default, view)!.Value.Y + connections.Bounds.Height;
            var serviceBottom = service.TranslatePoint(default, view)!.Value.Y + service.Bounds.Height;
            Assert.InRange(Math.Abs(serviceBottom - connectionBottom), 0, 1);
            window.Width = 680;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, Grid.GetRowSpan(service));
            Assert.Equal(0, Grid.GetColumn(trend));
            Assert.Equal(2, Grid.GetRow(details));
            Assert.Single(view.FindControl<Grid>("DetailColumns")!.ColumnDefinitions);
            window.Width = 1100;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal(1, Grid.GetRowSpan(service));
            Assert.NotNull(view.FindControl<Button>("QuickActionsButton")!.Flyout);
            Assert.NotNull(view.FindControl<Button>("TokenEyeButton"));
            Assert.NotNull(view.FindControl<Button>("OpenRuntimeDirectoryButton"));
        }
        finally { window.Close(); }
    }
}
