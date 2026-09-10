using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using DanmuApi.App.ViewModels;
using DanmuApi.App.Views;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed partial class MainWindowViewModelBehaviorTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverviewShowsRealHealthAndAboutHasVersionAndNavigation(bool dark)
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(directory.Path, "preview-only-token");
        var settings = new RecordingSettingsStore();
        settings.Values["ipv6_enabled"] = "true";
        var dialogs = new RecordingDialogService();
        await using var model = CreateViewModel(paths, settings,
            new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped)), dialogs: dialogs, healthClient: new PreviewHealthClient());
        model.Runtime = new RuntimeSnapshot(DesktopRuntimeState.Running, Pid: 1234, Port: 9321);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("健康接口响应正常", model.DiagnosticText, StringComparison.Ordinal);
        var view = new OverviewView { DataContext = new OverviewPageViewModel(model) };
        var window = new Window { Width = 1200, Height = 800, Padding = new Thickness(24), Content = view,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Show();
        try
        {
            window.FocusManager!.ClearFocus();
            RenderFeedbackPreview(window, dark ? "overview-dark-waiting" : "overview-light-waiting");
            window.Width = 720;
            window.Height = 900;
            RenderFeedbackPreview(window, dark ? "overview-dark-narrow-waiting" : "overview-light-narrow-waiting");
            window.Width = 1200;
            window.Height = 800;
            var portButton = view.FindControl<Button>("QuickActionsButton")!;
            Assert.True(portButton.Focus());
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Assert.IsAssignableFrom<Button>(window.FocusManager!.GetFocusedElement());
            Assert.NotSame(portButton, window.FocusManager.GetFocusedElement());
            window.FocusManager.ClearFocus();
            // Deterministic fixture counters only; no requests are sent to a live service.
            var fixture = new RuntimeHealthSnapshot(1234, "24", 183, "::", 9321, null, null, null, null,
                null, true, "stable", "稳定核心", "preview-instance", 100, null, null, null, null, null, null, null);
            model.RequestTrend.Reset();
            long count = 100;
            for (var i = 0; i < 36; i++)
            {
                count += new[] { 4, 8, 12, 25, 14, 5, 9, 18, 30, 16, 7, 5 }[i % 12];
                if (i is 19 or 20) model.RequestTrend.Disconnect(i * 5);
                else model.RequestTrend.Sample(fixture with { RequestCount = count }, i * 5);
            }
            RenderFeedbackPreview(window, dark ? "overview-dark" : "overview-light");
            var primary = model.EndpointItems[1];
            Assert.Equal("局域网 IPv4 API", primary.Title);
            var primaryRow = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Classes.Contains("addressRow") && ((DanmuApi.App.Services.EndpointItem)g.DataContext!).Title == primary.Title);
            Assert.Equal(primary.Title, ((TextBlock)primaryRow.Children[0]).Text);
            Assert.Equal(primary.DisplayAddress, ((StackPanel)primaryRow.Children[1]).Children.OfType<TextBlock>().First().Text);
            var copy = (Button)primaryRow.Children[2];
            Assert.Equal(primary.HasAddress, copy.IsEnabled);
            Assert.Equal(primary.HasAddress, copy.Command!.CanExecute(null));
            if (primary.HasAddress)
            {
                Assert.DoesNotContain("127.0.0.1", primary.Address);
                await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)copy.Command).ExecuteAsync(null);
                Assert.Equal(primary.Address, dialogs.CopiedText);
            }
            else Assert.Null(dialogs.CopiedText);
            var stop = view.FindControl<Button>("StopServiceButton")!;
            var restart = view.FindControl<Button>("RestartServiceButton")!;
            Assert.Equal(44, stop.Bounds.Height);
            Assert.Equal(stop.Bounds.Size, restart.Bounds.Size);
            var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First();
            scroll.ScrollToEnd();
            RenderFeedbackPreview(window, dark ? "overview-dark-diagnostics" : "overview-light-diagnostics");
            scroll.Offset = default;
            window.Width = 720;
            window.Height = 900;
            RenderFeedbackPreview(window, dark ? "overview-dark-narrow" : "overview-light-narrow");
            window.Width = 1200;
            window.Height = 800;
            model.Runtime = new RuntimeSnapshot(DesktopRuntimeState.Stopped);
            model.RequestTrend.Reset();
            RenderFeedbackPreview(window, dark ? "overview-dark-stopped" : "overview-light-stopped");
            Assert.True(view.FindControl<Button>("StartServiceButton")!.IsVisible);
            Assert.False(view.FindControl<Button>("StopServiceButton")!.IsVisible);
            Assert.False(restart.IsVisible);
            Assert.False(view.FindControl<RequestTrendControl>("RequestTrend")!.IsVisible);
            var stoppedRows = view.GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("addressRow")).ToArray();
            Assert.All(stoppedRows, row => Assert.False(((Button)row.Children[2]).IsEnabled));
            Assert.All(stoppedRows, row => Assert.False(((Button)row.Children[2]).Command!.CanExecute(null)));
            model.Runtime = new RuntimeSnapshot(DesktopRuntimeState.Failed, Port: 9321, FailureReason: "进程异常退出：exit 1");
            RenderFeedbackPreview(window, dark ? "overview-dark-failed" : "overview-light-failed");
            Assert.Contains("exit 1", view.FindControl<TextBlock>("DiagnosticsText")!.Text);
            window.Width = 720;
            window.Height = 900;
            RenderFeedbackPreview(window, dark ? "overview-dark-narrow-failed" : "overview-light-narrow-failed");
            scroll.ScrollToEnd();
            RenderFeedbackPreview(window, dark ? "overview-dark-narrow-diagnostics" : "overview-light-narrow-diagnostics");
            window.Width = 1200;
            window.Height = 900;
            model.SelectedNavigationItem = model.NavigationItems.Single(item => item.Key == "about");
            var aboutModel = Assert.IsType<AboutPageViewModel>(model.CurrentPage);
            Assert.False(string.IsNullOrWhiteSpace(aboutModel.Version));
            var about = new AboutView { DataContext = aboutModel };
            window.Content = about;
            RenderFeedbackPreview(window, dark ? "about-dark" : "about-light");
            Assert.Contains(aboutModel.Version, about.FindControl<TextBlock>("VersionText")!.Text!, StringComparison.Ordinal);
            model.Runtime = new RuntimeSnapshot(DesktopRuntimeState.Stopped);
            Assert.DoesNotContain("健康接口响应正常", model.DiagnosticText, StringComparison.Ordinal);
            Assert.Equal("未运行", model.UptimeText);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false, 1920, 1080)]
    [InlineData(true, 1920, 1080)]
    [InlineData(false, 1280, 800)]
    [InlineData(true, 1280, 800)]
    [InlineData(false, 960, 800)]
    [InlineData(true, 960, 800)]
    [InlineData(false, 720, 900)]
    [InlineData(true, 720, 900)]
    public async Task OverviewConsoleUsesRealShellAndContainedAddresses(bool dark, int width, int height)
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(directory.Path, "preview-only-token");
        var settings = new RecordingSettingsStore();
        settings.Values["ipv6_enabled"] = "true";
        await using var model = CreateViewModel(paths, settings,
            new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped)),
            dialogs: new RecordingDialogService(), healthClient: new PreviewHealthClient());
        // The production minimum is 960. Lower it only in this fixture to stress the actual shell at 720.
        var window = new MainWindow { DataContext = model, MinWidth = 0, Width = width, Height = height,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        if (width < 960) model.ToggleSidebarCommand.Execute(null);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var view = Assert.Single(window.GetVisualDescendants().OfType<OverviewView>());
            var content = view.FindControl<StackPanel>("OverviewContent")!;
            var restart = view.FindControl<Button>("RestartServiceButton")!;
            var chart = view.FindControl<RequestTrendControl>("RequestTrend")!;
            Assert.Null(view.FindControl<Control>("AllAddresses"));
            Assert.Null(view.FindControl<Control>("PrimaryAddressRow"));
            Assert.Empty(view.GetVisualDescendants().OfType<Expander>());
            Assert.Empty(view.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>());
            var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First();
            var prefix = $"shell-{(dark ? "dark" : "light")}-{width}x{height}";
            Assert.InRange(Math.Abs(content.Bounds.Width - view.Bounds.Width), 0, 20);
            var point = content.TranslatePoint(default, view)!.Value;
            Assert.InRange(Math.Abs(point.X - (view.Bounds.Width - content.Bounds.Width) / 2), 0, 12);
            Assert.False(restart.IsVisible);
            Assert.False(chart.IsVisible);
            RenderFeedbackPreview(window, prefix + "-stopped");
            model.Runtime = new RuntimeSnapshot(DesktopRuntimeState.Running, Pid: 1234, Port: 9321);
            Dispatcher.UIThread.RunJobs();
            model.RequestTrend.Reset(true);
            var health = new RuntimeHealthSnapshot(1234, "24", 183, "::", 9321, null, null, null, null,
                null, true, "stable", "稳定核心", "preview-instance", 100, null, null, null, null, null, null, null);
            long count = 100;
            for (var i = 0; i < 36; i++)
            {
                count += new[] { 4, 8, 12, 25, 14, 5, 9, 18, 30, 16, 7, 5 }[i % 12];
                if (i is 19 or 20) model.RequestTrend.Disconnect(i * 5);
                else model.RequestTrend.Sample(health with { RequestCount = count }, i * 5);
            }
            RenderFeedbackPreview(window, prefix + "-running");
            Assert.True(restart.IsVisible);
            Assert.True(chart.IsVisible);
            var service = view.FindControl<Border>("ServicePanel")!;
            var core = view.FindControl<Border>("DiagnosticsPanel")!;
            var details = view.FindControl<Border>("AddressDetailsPanel")!;
            Assert.True(details.IsEffectivelyVisible);
            var right = view.FindControl<StackPanel>("RightPanels")!;
            Assert.Equal(new Control[] { view.FindControl<Border>("TrendPanel")!, core }, right.Children);
            Assert.Same(model.NavigateCoreCommand, view.FindControl<Button>("ManageCoreButton")!.Command);
            Assert.Same(model.OpenCoreRuntimeDirectoryCommand, view.FindControl<Button>("OpenRuntimeDirectoryButton")!.Command);
            var eye = view.FindControl<Button>("TokenEyeButton")!;
            Assert.Same(model.ToggleTokenVisibilityCommand, eye.Command);
            var maskedAddress = model.EndpointItems[0].DisplayAddress;
            eye.Command!.Execute(null);
            Assert.NotEqual(maskedAddress, model.EndpointItems[0].DisplayAddress);
            eye.Command.Execute(null);
            Assert.Equal(maskedAddress, model.EndpointItems[0].DisplayAddress);
            var menu = Assert.IsType<MenuFlyout>(view.FindControl<Button>("QuickActionsButton")!.Flyout);
            menu.ShowAt(view.FindControl<Button>("QuickActionsButton")!);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, menu.Items.Count);
            Assert.Same(model.OpenTokenEditorCommand, Assert.IsType<MenuItem>(menu.Items[0]).Command);
            Assert.Same(model.OpenPortEditorCommand, Assert.IsType<MenuItem>(menu.Items[1]).Command);
            menu.Hide();
            Assert.Same(model.OpenCacheCommand, view.FindControl<Button>("CacheEntryButton")!.Command);
            if (view.Bounds.Width >= 700)
            {
                var serviceBottom = service.TranslatePoint(default, view)!.Value.Y + service.Bounds.Height;
                var coreBottom = core.TranslatePoint(default, view)!.Value.Y + core.Bounds.Height;
                Assert.InRange(Math.Abs(serviceBottom - coreBottom), 0, 1);
                Assert.Equal(2, Grid.GetColumnSpan(details));
                Assert.InRange(details.TranslatePoint(default, view)!.Value.Y - coreBottom, 14, 16);
            }
            var inset = view.FindControl<Border>("AllAddressesContent")!;
            Assert.Equal(new Thickness(0), inset.Padding);
            var addressRows = inset.GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("addressRow")).ToArray();
            Assert.Equal(model.EndpointItems.Count, addressRows.Length);
            Assert.All(addressRows, row => Assert.True(row.IsEffectivelyVisible));
            Assert.Contains(model.EndpointItems, item => item.Title.Contains("本机") && item.Address.Contains("127.0.0.1"));
            Assert.Contains(model.EndpointItems, item => item.Title.Contains("IPv6"));
            Assert.Equal(model.EndpointItems.Select(item => item.Title), addressRows.Select(row => ((TextBlock)row.Children[0]).Text));
            scroll.ScrollToEnd();
            RenderFeedbackPreview(window, prefix + "-addresses");
            foreach (var row in addressRows)
            {
                var button = (Button)row.Children[2];
                var position = button.TranslatePoint(default, inset)!.Value;
                Assert.True(position.X >= 0);
                Assert.InRange(position.X + button.Bounds.Width, inset.Bounds.Width - 2, inset.Bounds.Width);
                Assert.True(row.Children[1].Bounds.Right <= button.Bounds.Left);
            }
            model.Runtime = new RuntimeSnapshot(DesktopRuntimeState.Failed, FailureReason: "测试退出：exit 1");
            Dispatcher.UIThread.RunJobs();
            Assert.False(restart.IsVisible);
            model.Runtime = new RuntimeSnapshot(DesktopRuntimeState.Stopped);
            model.RequestTrend.Reset();
            scroll.Offset = new Vector(0, 360);
            RenderFeedbackPreview(window, prefix + "-stopped-addresses");
            Assert.All(inset.GetVisualDescendants().OfType<Button>(), button => Assert.False(button.IsEnabled));
            // Network-independent boundary cases: missing LAN IPv4 and a long IPv6 URL.
            var endpointList = inset.GetVisualDescendants().OfType<ItemsControl>().Single();
            var boundaryDialogs = new RecordingDialogService();
            endpointList.ItemsSource = new[]
            {
                new DanmuApi.App.Services.EndpointItem("局域网 IPv4 API", "未检测到可用的局域网 IPv4 地址", "", false, new RecordingDialogService()),
                new DanmuApi.App.Services.EndpointItem("局域网 IPv6 API", "仅支持 IPv6 的设备可访问", "http://[2001:db8:1234:5678:9abc:def0:1234:5678]:9321/" + new string('a', 256), true, boundaryDialogs),
            };
            RenderFeedbackPreview(window, prefix + "-address-boundaries");
            var rows = inset.GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("addressRow")).ToArray();
            Assert.Equal(2, rows.Length);
            Assert.False(((Button)rows[0].Children[2]).IsEnabled);
            Assert.True(((Button)rows[1].Children[2]).IsEnabled);
            var longAddress = ((StackPanel)rows[1].Children[1]).Children.OfType<TextBlock>().First();
            Assert.Equal(Avalonia.Media.TextWrapping.Wrap, longAddress.TextWrapping);
            Assert.True(longAddress.Bounds.Height > 20);
            await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)((Button)rows[1].Children[2]).Command!).ExecuteAsync(null);
            Assert.Equal(longAddress.Text, boundaryDialogs.CopiedText);
            foreach (var row in rows)
            {
                var copyButton = (Button)row.Children[2];
                var copyPoint = copyButton.TranslatePoint(default, inset)!.Value;
                Assert.InRange(copyPoint.X + copyButton.Bounds.Width, inset.Bounds.Width - 2, inset.Bounds.Width);
                Assert.True(row.Children[1].Bounds.Right <= row.Bounds.Width);
            }
        }
        finally { window.Close(); }
    }

    private static void RenderFeedbackPreview(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var directory = Environment.GetEnvironmentVariable("DANMU_TEST_RENDER_DIRECTORY");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(directory, name + ".png"));
    }

    private sealed class PreviewHealthClient : IRuntimeHealthClient
    {
        public Task<RuntimeHealthSnapshot> ReadAsync(string host, int port, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RuntimeHealthSnapshot(1234, "24", 183, "::", port, null, null, null, null,
                null, true, "stable", "稳定核心", null, 45, null, null, null, null, null, null, null));
    }
}
