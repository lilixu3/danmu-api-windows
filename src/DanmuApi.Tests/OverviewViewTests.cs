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

    /// <summary>
    /// 服务控制按钮的图标必须是矢量 Path，不能退回文字字形。
    ///
    /// 这三个按钮原先把图标写成文字字符塞进 Content（"▷  启动服务" / "□  停止服务" /
    /// "↻  重启服务"）：字号跟着按钮走、无法单独调色、间距靠空格、换字体就换形状。
    /// 这条断言按结构锁住「图标是 Path、文字是独立 TextBlock」这个形态，
    /// 顺便挡住旧字形被重新写回去。
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ServiceButtonsUseVectorIconsInsteadOfTextGlyphs(bool dark)
    {
        var view = new OverviewView();
        var window = new Window
        {
            Width = 1100,
            Height = 800,
            Content = view,
            RequestedThemeVariant = dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light,
        };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var expected = new (string Name, string Label)[]
            {
                ("StartServiceButton", "启动服务"),
                ("StopServiceButton", "停止服务"),
                ("RestartServiceButton", "重启服务"),
            };

            foreach (var (name, label) in expected)
            {
                var button = view.FindControl<Button>(name)!;
                // 内容不再是纯文本
                Assert.False(button.Content is string, $"{name} 的 Content 不应再是文字字形");
                var stack = Assert.IsType<StackPanel>(button.Content);
                var path = Assert.Single(stack.Children.OfType<Avalonia.Controls.Shapes.Path>());
                Assert.NotNull(path.Data);
                Assert.True(path.Bounds.Width > 0 && path.Bounds.Height > 0, $"{name} 的图标没有渲染出尺寸");
                // 图标与文字分开，间距由 Spacing 控制而不是空格
                var text = Assert.Single(stack.Children.OfType<TextBlock>());
                Assert.Equal(label, text.Text);
                Assert.True(stack.Spacing > 0, $"{name} 的图标与文字之间必须有间距");
            }

            // 旧的字形字符不许再出现在这三个按钮的内容里。
            foreach (var glyph in new[] { "▷", "□", "↻" })
            {
                foreach (var (name, _) in expected)
                {
                    Assert.DoesNotContain(glyph, ContentText(view.FindControl<Button>(name)!) ?? string.Empty, StringComparison.Ordinal);
                }
            }
        }
        finally { window.Close(); }
    }

    private static string? ContentText(Button button) =>
        button.Content is string text
            ? text
            : string.Join(
                " ",
                (button.Content as StackPanel)?.Children.OfType<TextBlock>().Select(block => block.Text) ?? []);
}
