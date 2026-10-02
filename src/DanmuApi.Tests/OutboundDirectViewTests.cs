using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DanmuApi.App.Services;
using DanmuApi.App.Controls;
using DanmuApi.App.ViewModels;
using DanmuApi.App.Views;
using DanmuApi.Platform;

namespace DanmuApi.Tests;

public sealed class OutboundDirectViewTests
{
    [AvaloniaTheory]
    [InlineData(960, false)]
    [InlineData(960, true)]
    [InlineData(1280, false)]
    [InlineData(1280, true)]
    public async Task MainPageHasImmediateToggleAndOptionsAndTestsStayInOwnedDialogs(int width, bool dark)
    {
        var service = new OutboundUiTestService(OutboundSettings.Default with { Enabled = true });
        service.Publish(OutboundUiTestService.Ready(service.Settings));
        service.Rows =
        [
            new("bahamut", "api.gamer.com.tw", true, "h3", true, 200, TimeSpan.FromMilliseconds(124), null, "源站连接已完成，HTTP/3，ECH 已接受。"),
            new("tmdb", "api.tmdb.org", false, "h2", false, 401, TimeSpan.FromMilliseconds(82), "http", "源站连接已建立，需要 TMDB API 密钥。"),
            new("dandan", "api.danmaku.weeblify.app", true, null, false, null, TimeSpan.FromMilliseconds(3000), "ech", "原目标失败：ECH 未被接受。"),
            new("animeko", "danmaku-global.myani.org", true, "h3", true, 200, TimeSpan.FromMilliseconds(98), null, "HTTP/3，ECH 已接受。"),
            new("tmdb", "api.themoviedb.org", false, "h2", false, 401, TimeSpan.FromMilliseconds(90), "http", "需要业务 API 密钥。"),
            new("dandan", "nipaplay.aimes-soft.com", false, "h2", false, 404, TimeSpan.FromMilliseconds(88), "http", "源站返回 HTTP 404。"),
            new("animeko", "api.bangumi.vip", true, "h3", true, 200, TimeSpan.FromMilliseconds(99), null, "ECH 已接受。"),
            new("animeko", "api.animeko.org", false, "h2", false, 200, TimeSpan.FromMilliseconds(76), null, "普通 TLS 连接已建立。"),
            new("animeko", "danmaku-cn.myani.org", false, "h2", false, 200, TimeSpan.FromMilliseconds(66), null, "普通 TLS 连接已建立。"),
            new("animeko", "s1.animeko.openani.org", false, "h2", false, 200, TimeSpan.FromMilliseconds(62), null, "普通 TLS 连接已建立。"),
        ];
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        var view = new OutboundDirectView { DataContext = model };
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("200,216,14,*"), Margin = new Thickness(14) };
        layout.Children.Add(new Border { Child = new TextBlock { Text = "弹幕 API\n\n设置", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, Padding = new Thickness(12) });
        var categories = new Border { Child = new TextBlock { Text = "增强直连\n源站连接设置", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, Padding = new Thickness(12) };
        Grid.SetColumn(categories, 1);
        layout.Children.Add(categories);
        var scroller = new ScrollViewer { Content = view, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroller, 3);
        layout.Children.Add(scroller);
        var theme = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var window = new Window { Width = width, Height = 800, Content = layout, RequestedThemeVariant = theme };
        window.Show();
        try
        {
            Layout(window);
            Assert.True(model.IsMonitoring);
            Assert.True(view.FindControl<ToggleSwitch>("EnabledToggle")!.IsEffectivelyVisible);
            Assert.Null(view.FindControl<ListBox>("OutboundTabs"));
            Assert.Empty(view.GetVisualDescendants().OfType<TextBox>());
            Assert.Empty(view.GetVisualDescendants().OfType<ListBox>());
            Assert.DoesNotContain("未知", view.FindControl<TextBlock>("EngineStatus")!.Text!);
            var quickSources = view.FindControl<Grid>("QuickSources")!;
            Assert.Equal(4, quickSources.GetVisualDescendants().OfType<CheckBox>().Count());
            foreach (var source in quickSources.GetVisualDescendants().OfType<CheckBox>())
            {
                Assert.True(source.IsEffectivelyVisible);
                Assert.True(source.IsChecked);
                var origin = source.TranslatePoint(default, window)!.Value;
                Assert.True(origin.X >= scroller.TranslatePoint(default, window)!.Value.X);
                Assert.True(origin.X + source.Bounds.Width <= window.Bounds.Width - 14);
                Assert.True(origin.Y + source.Bounds.Height <= window.Bounds.Height);
                Assert.Same(model.ToggleSourceCommand, source.Command);
            }
            var details = view.FindControl<StackPanel>("RuntimeDetailsContent")!;
            var detailsButton = view.FindControl<Button>("RuntimeDetailsToggle")!;
            Assert.False(details.IsVisible);
            Assert.Equal("查看运行详情", detailsButton.Content);
            Render(window, $"outbound-main-{width}-{(dark ? "dark" : "light")}");
            detailsButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Layout(window);
            Assert.True(details.IsVisible);
            Assert.Equal("收起运行详情", detailsButton.Content);
            Assert.Contains(details.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == model.CapabilityText && block.IsEffectivelyVisible);
            detailsButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Layout(window);
            Assert.False(details.IsVisible);

            Assert.True(model.BeginConfigurationEdit());
            var options = new OutboundConfigurationWindow(model) { RequestedThemeVariant = theme };
            var optionsClosed = options.ShowDialog(window);
            try
            {
                Layout(options);
                Assert.Same(window, options.Owner);
                Assert.Equal(3, options.FindControl<ComboBox>("HttpMode")!.ItemCount);
                Assert.Null(options.FindControl<ToggleSwitch>("EnabledToggle"));
                model.DohUrl = "https://draft.example.test/dns-query";
                Layout(options);
                Assert.True(options.FindControl<Button>("SaveConfiguration")!.IsEnabled);
                Assert.DoesNotContain("draft.example.test", model.ConnectionSummary);
                Render(options, $"outbound-options-{width}-{(dark ? "dark" : "light")}");
            }
            finally { options.Close(); }
            await optionsClosed;
            Assert.Equal(string.Empty, model.DohUrl);
            Assert.False(model.HasUnsavedChanges);
            Assert.Empty(service.Saves);

            await model.DiagnoseCommand.ExecuteAsync(null);
            Assert.Empty(model.Diagnostic);
            var tests = new OutboundDiagnosticsWindow(model) { Width = width == 960 ? 760 : 920, RequestedThemeVariant = theme };
            var testsClosed = tests.ShowDialog(window);
            try
            {
                Layout(tests);
                Assert.Same(window, tests.Owner);
                var list = tests.FindControl<ListBox>("DiagnosticList")!;
                Assert.Equal(10, list.ItemCount);
                var headers = tests.FindControl<Grid>("DiagnosticHeaders")!;
                var rowGrid = list.GetVisualDescendants().OfType<Grid>().First(grid => grid.ColumnDefinitions.Count == 7);
                foreach (var cell in rowGrid.Children.OfType<TextBlock>())
                {
                    var heading = headers.Children.OfType<TextBlock>().Single(block => Grid.GetColumn(block) == Grid.GetColumn(cell));
                    Assert.InRange(Math.Abs(cell.TranslatePoint(default, tests)!.Value.X - heading.TranslatePoint(default, tests)!.Value.X), 0, 1);
                }
                var resultHeader = headers.Children.OfType<TextBlock>().Single(block => Grid.GetColumn(block) == 6);
                Assert.True(resultHeader.TranslatePoint(default, tests)!.Value.X + resultHeader.Bounds.Width <= tests.Bounds.Width - 20);
                model.SelectedDiagnostic = model.DiagnosticRows.Single(row => row.Host == "api.tmdb.org");
                Layout(tests);
                Render(tests, $"outbound-speedtest-{width}-{(dark ? "dark" : "light")}");
            }
            finally { tests.Close(); }
            await testsClosed;
            Layout(window);
            Assert.True(view.FindControl<StackPanel>("RecentTestResults")!.IsVisible);
            Assert.False(view.FindControl<TextBlock>("NoRecentTest")!.IsVisible);
            Assert.False(view.FindControl<SelectableTextBlock>("RuntimeFailure")!.IsVisible);
            Render(window, $"outbound-main-results-{width}-{(dark ? "dark" : "light")}");
        }
        finally { window.Close(); }
        Assert.False(model.IsMonitoring);
    }

    [AvaloniaFact]
    public async Task DisabledPageCanEnableImmediatelyAndHidingStopsOnePollingSession()
    {
        var service = new OutboundUiTestService();
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        var view = new OutboundDirectView { DataContext = model };
        var parent = new Border { Child = view };
        var window = new Window { Width = 960, Height = 800, Content = parent };
        window.Show();
        try
        {
            Layout(window);
            Assert.Equal(1, service.RefreshCalls);
            Assert.False(view.FindControl<StackPanel>("RuntimeDetails")!.IsVisible);
            Assert.DoesNotContain("未知", model.StatusText);
            var toggle = view.FindControl<ToggleSwitch>("EnabledToggle")!;
            Assert.True(toggle.IsEnabled);
            Assert.IsType<SavedStateToggleSwitch>(toggle);
            Assert.False(model.Enabled);
            Assert.Empty(service.Saves);
            Click(window, toggle);
            if (model.ToggleEnabledCommand.ExecutionTask is { } enabledTask) await enabledTask;
            Layout(window);
            Assert.True(model.Enabled);
            Assert.True(toggle.IsChecked);
            Assert.True(Assert.Single(service.Saves).Enabled);
            Assert.Null(view.FindControl<TextBox>("DohAddress"));
            parent.IsVisible = false;
            Layout(window);
            Assert.False(model.IsMonitoring);
            parent.IsVisible = true;
            Layout(window);
            Assert.True(model.IsMonitoring);
            var refreshes = service.RefreshCalls;
            model.BeginMonitoring();
            Assert.Equal(refreshes, service.RefreshCalls);
            window.Content = null;
            Layout(window);
            Assert.False(model.IsMonitoring);
            window.Content = parent;
            Layout(window);
            Assert.True(model.IsMonitoring);
        }
        finally { window.Close(); }
        Assert.False(model.IsMonitoring);
    }

    [AvaloniaFact]
    public async Task MainActionsOpenOwnedDialogsAndFailedSaveKeepsTheFormOpen()
    {
        var service = new OutboundUiTestService();
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        var view = new OutboundDirectView { DataContext = model };
        var owner = new Window { Width = 960, Height = 800, Content = view };
        owner.Show();
        try
        {
            Layout(owner);
            view.FindControl<Button>("ConfigureOutbound")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Layout(owner);
            var options = Assert.IsType<OutboundConfigurationWindow>(Assert.Single(owner.OwnedWindows));
            owner.RequestedThemeVariant = ThemeVariant.Dark;
            Layout(options);
            Assert.Equal(ThemeVariant.Dark, options.ActualThemeVariant);
            owner.RequestedThemeVariant = ThemeVariant.Light;
            Layout(options);
            Assert.Equal(ThemeVariant.Light, options.ActualThemeVariant);
            model.ConnectTimeoutText = "8000";
            service.SaveError = new IOException("fixture save denied");
            options.FindControl<Button>("SaveConfiguration")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Layout(options);
            Assert.True(options.IsVisible);
            Assert.False(model.LastSaveSucceeded);
            Assert.Equal("8000", model.ConnectTimeoutText);
            service.SaveError = null;
            options.FindControl<Button>("SaveConfiguration")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Layout(owner);
            Assert.False(options.IsVisible);
            Assert.True(model.LastSaveSucceeded);
            Assert.Equal(8000, Assert.Single(service.Saves).ConnectTimeoutMs);
            view.FindControl<Button>("TestOutbound")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Layout(owner);
            var tests = Assert.IsType<OutboundDiagnosticsWindow>(Assert.Single(owner.OwnedWindows));
            Assert.Contains("启用", model.DiagnoseBlockedReason);
            Assert.False(model.IsDiagnosing);
            tests.Close();
            Layout(owner);
            Assert.False(tests.IsVisible);
        }
        finally
        {
            foreach (var dialog in owner.OwnedWindows.ToArray()) dialog.Close();
            owner.Close();
        }
        await Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task ClosingSpeedTestCancelsItsRequestWithoutStoppingTheService()
    {
        var service = new OutboundUiTestService(OutboundSettings.Default with { Enabled = true });
        service.Publish(OutboundUiTestService.Ready(service.Settings));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DiagnoseAction = async cancellationToken =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Test must be cancelled");
        };
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        var owner = new Window { Width = 960, Height = 800 };
        owner.Show();
        try
        {
            var test = new OutboundDiagnosticsWindow(model);
            var closed = test.ShowDialog(owner);
            var running = test.StartTestAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(model.IsDiagnosing);
            test.Close();
            await closed;
            await running.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(model.IsDiagnosing);
            Assert.True(model.Enabled);
            Assert.Empty(service.Saves);
            Assert.Contains("取消", model.DiagnosticStatus);
        }
        finally { owner.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(960, false)]
    [InlineData(960, true)]
    [InlineData(1280, false)]
    [InlineData(1280, true)]
    public void QuickSourceSelectionRemainsClearWhileEnhancementIsDisabled(int width, bool dark)
    {
        var service = new OutboundUiTestService(OutboundSettings.Default with { Sources = ["bahamut", "animeko"] });
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        var view = new OutboundDirectView { DataContext = model };
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("200,216,14,*"), Margin = new Thickness(14) };
        layout.Children.Add(new TextBlock { Text = "弹幕 API\n\n设置", Margin = new Thickness(12) });
        var categories = new TextBlock { Text = "增强直连\n源站连接设置", Margin = new Thickness(12) };
        Grid.SetColumn(categories, 1);
        layout.Children.Add(categories);
        var scroller = new ScrollViewer { Content = view, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroller, 3);
        layout.Children.Add(scroller);
        var window = new Window { Width = width, Height = 800, Content = layout, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Show();
        try
        {
            Layout(window);
            Assert.False(view.FindControl<ToggleSwitch>("EnabledToggle")!.IsChecked);
            Assert.True(view.FindControl<Grid>("QuickSources")!.IsEnabled);
            Assert.True(view.FindControl<CheckBox>("QuickBahamut")!.IsChecked);
            Assert.False(view.FindControl<CheckBox>("QuickTmdb")!.IsChecked);
            Assert.False(view.FindControl<CheckBox>("QuickDandan")!.IsChecked);
            Assert.True(view.FindControl<CheckBox>("QuickAnimeko")!.IsChecked);
            var cards = view.FindControl<Grid>("QuickSources")!.Children.OfType<Border>().ToArray();
            Assert.Equal(2, cards.Count(card => card.Classes.Contains("selected")));
            Assert.True(view.FindControl<TextBlock>("NoRecentTest")!.IsVisible);
            Assert.Empty(service.Saves);
            Render(window, $"outbound-main-disabled-{width}-{(dark ? "dark" : "light")}");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task QuickSourcePointerAndKeyboardClicksSaveAndFailedClicksReturnToSavedSelection()
    {
        var service = new OutboundUiTestService(OutboundSettings.Default with { Enabled = true });
        service.Publish(OutboundUiTestService.Ready(service.Settings));
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        var view = new OutboundDirectView { DataContext = model };
        var window = new Window { Width = 800, Height = 900, Content = new ScrollViewer { Content = view } };
        window.Show();
        try
        {
            Layout(window);
            var tmdb = view.FindControl<CheckBox>("QuickTmdb")!;
            Click(window, tmdb);
            Assert.False(tmdb.IsChecked);
            Assert.DoesNotContain("tmdb", service.Settings.Sources);
            Assert.False(model.HasUnsavedChanges);
            Assert.Single(service.Saves);
            Assert.True(tmdb.Focus());
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Layout(window);
            Assert.True(tmdb.IsChecked);
            Assert.Contains("tmdb", service.Settings.Sources);
            Assert.Equal(2, service.Saves.Count);

            service.SaveError = new IOException("fixture quick selection denied");
            Click(window, tmdb);
            Assert.True(tmdb.IsChecked);
            Assert.True(model.SavedTmdbSelected);
            Assert.Equal(2, service.Saves.Count);
            Assert.Contains("fixture quick selection denied", model.Diagnostic);
            Assert.True(view.FindControl<SelectableTextBlock>("RuntimeFailure")!.IsVisible);
            service.SaveError = null;

            Click(window, view.FindControl<CheckBox>("QuickBahamut")!);
            Click(window, view.FindControl<CheckBox>("QuickDandan")!);
            Click(window, view.FindControl<CheckBox>("QuickAnimeko")!);
            Assert.Equal("tmdb", Assert.Single(service.Settings.Sources));
            var saves = service.Saves.Count;
            Click(window, tmdb);
            Assert.True(tmdb.IsChecked);
            Assert.Equal(saves, service.Saves.Count);
            Assert.Contains("至少", model.Diagnostic);
            Assert.True(service.Settings.Enabled);
        }
        finally { window.Close(); }
        await Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task QuickSourceControlsWaitForSaveAndReflectSuccessfulReadback()
    {
        var service = new OutboundUiTestService();
        var saving = new TaskCompletionSource<DanmuApi.App.Services.OutboundOperationResult>();
        service.SaveAction = async (settings, _) =>
        {
            var result = await saving.Task;
            service.Settings = settings;
            service.Publish(new("off", "来源已保存，服务未运行", settings));
            return result;
        };
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        var view = new OutboundDirectView { DataContext = model };
        var window = new Window { Width = 800, Height = 900, Content = new ScrollViewer { Content = view } };
        window.Show();
        try
        {
            Layout(window);
            Click(window, view.FindControl<CheckBox>("QuickTmdb")!);
            Assert.True(model.IsBusy);
            Assert.False(view.FindControl<Grid>("QuickSources")!.IsEnabled);
            Assert.False(view.FindControl<CheckBox>("QuickBahamut")!.IsEffectivelyEnabled);
            var saves = service.Saves.Count;
            Click(window, view.FindControl<CheckBox>("QuickBahamut")!);
            Assert.Equal(saves, service.Saves.Count);
            saving.SetResult(new(true, "来源已保存"));
            await model.ToggleSourceCommand.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(2));
            Layout(window);
            Assert.False(model.IsBusy);
            Assert.True(view.FindControl<Grid>("QuickSources")!.IsEnabled);
            Assert.False(view.FindControl<CheckBox>("QuickTmdb")!.IsChecked);
            Assert.False(model.HasUnsavedChanges);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("pointer", "empty")]
    [InlineData("space", "empty")]
    [InlineData("enter", "empty")]
    [InlineData("drag", "empty")]
    [InlineData("pointer", "denied")]
    [InlineData("space", "denied")]
    [InlineData("enter", "denied")]
    [InlineData("drag", "denied")]
    [InlineData("pointer", "pending")]
    [InlineData("space", "pending")]
    [InlineData("enter", "pending")]
    [InlineData("drag", "pending")]
    public async Task SavedStateEnableInputNeverDriftsOrOptimisticallyReverses(string input, string outcome)
    {
        var service = new OutboundUiTestService(OutboundSettings.Default with { Sources = outcome == "empty" ? [] : ["tmdb"] });
        var result = new TaskCompletionSource<OutboundOperationResult>();
        service.SaveAction = async (settings, token) =>
        {
            if (outcome == "denied") return new(false, "fixture write denied");
            var saved = await result.Task.WaitAsync(token);
            service.Settings = settings;
            service.Publish(new("off", "fixture saved", settings));
            return saved;
        };
        using var model = new OutboundDirectViewModel(service, new OutboundUiTestDiagnostics());
        var view = new OutboundDirectView { DataContext = model };
        var window = new Window { Width = 900, Height = 900, Content = new ScrollViewer { Content = view } };
        window.Show();
        try
        {
            Layout(window);
            model.EndMonitoring();
            var toggle = Assert.IsType<SavedStateToggleSwitch>(view.FindControl<ToggleSwitch>("EnabledToggle"));
            Assert.False(toggle.IsChecked);
            InputSwitch(window, toggle, input);
            Layout(window);
            Assert.False(toggle.IsChecked);
            Assert.False(model.Enabled);
            Assert.False(service.Settings.Enabled);
            Assert.Equal(outcome == "empty" ? 0 : 1, service.Saves.Count);
            if (outcome == "pending")
            {
                Assert.True(model.IsBusy);
                Assert.False(toggle.IsEffectivelyEnabled);
                result.SetResult(new(true, "fixture saved"));
                await model.ToggleEnabledCommand.ExecutionTask!;
                Layout(window);
                Assert.True(toggle.IsChecked);
                Assert.True(model.Enabled);
                Assert.True(service.Settings.Enabled);
                Assert.Single(service.Saves);
            }
            else
            {
                if (model.ToggleEnabledCommand.ExecutionTask is { } task) await task;
                model.DohUrl = "https://unrelated-draft.example.test/query";
                Layout(window);
                Assert.False(toggle.IsChecked);
                Assert.False(model.LastSaveSucceeded);
                Assert.True(model.HasDiagnostic);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongMultilinePromptKeepsActionsInsideWindowAndTextScrollsInternally(bool readOnly)
    {
        var owner = new Window { Width = 1100, Height = 800 };
        owner.Show();
        try
        {
            Layout(owner);
            var text = "[\n" + string.Join(",\n", Enumerable.Range(0, 200).Select(i => "  {\"testRow\":" + i + "}")) + "\n]";
            var waiting = new UiDialogService(() => owner).PromptMultilineTextAsync("Long JSON test", "Non-sensitive test data", text, "Test confirm", readOnly);
            Layout(owner);
            var dialog = Assert.Single(owner.OwnedWindows);
            Assert.Same(owner, dialog.Owner);
            foreach (var height in new[] { 560, 400, 320 })
            {
                dialog.Height = height;
                Layout(dialog);
                var input = Assert.Single(dialog.GetVisualDescendants().OfType<TextBox>());
                Assert.Equal(text, input.Text);
                Assert.Equal(readOnly, input.IsReadOnly);
                Assert.InRange(input.Bounds.Height, 1, dialog.Bounds.Height - 48);
                var scroll = input.GetVisualDescendants().OfType<ScrollViewer>().First();
                Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
                foreach (var action in dialog.GetVisualDescendants().OfType<Button>().Where(button => Equals(button.Content, "取消") || Equals(button.Content, "Test confirm")))
                {
                    var top = action.TranslatePoint(default, dialog)!.Value;
                    Assert.InRange(top.Y, 0, dialog.Bounds.Height - action.Bounds.Height);
                }
            }
            dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Test confirm"))
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(text, await waiting);
        }
        finally { foreach (var dialog in owner.OwnedWindows.ToArray()) dialog.Close(); owner.Close(); }
    }

    private static void InputSwitch(Window window, ToggleSwitch toggle, string input)
    {
        if (input == "pointer") { Click(window, toggle); return; }
        if (input is "space" or "enter")
        {
            Assert.True(toggle.Focus());
            var key = input == "space" ? Key.Space : Key.Enter;
            var physical = input == "space" ? PhysicalKey.Space : PhysicalKey.Enter;
            window.KeyPress(key, RawInputModifiers.None, physical, input == "space" ? " " : "\r");
            window.KeyRelease(key, RawInputModifiers.None, physical, input == "space" ? " " : "\r");
            return;
        }
        // Fluent's PART_SwitchKnob Canvas is only the moving knob's 20px input area.
        // SwitchKnobBounds is the full 40px rail; target its far side, not the small Canvas.
        var track = Assert.Single(toggle.GetVisualDescendants().OfType<Border>(), part => part.Name == "SwitchKnobBounds");
        var knob = Assert.Single(toggle.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>(),
            part => part.Name == "SwitchKnobOff");
        Assert.True(track.IsEffectivelyVisible && knob.IsEffectivelyVisible);
        Assert.True(knob.Bounds.Width > 0 && track.Bounds.Width > knob.Bounds.Width);
        var start = knob.TranslatePoint(new Point(knob.Bounds.Width / 2, knob.Bounds.Height / 2), window)!.Value;
        var end = track.TranslatePoint(new Point(track.Bounds.Width - knob.Bounds.Width / 2, track.Bounds.Height / 2), window)!.Value;
        Assert.True(end.X - start.X > knob.Bounds.Width, "Drag must cross the real switch track, not merely click its knob.");
        var pressed = false;
        var movedWithButtonHeld = false;
        var released = false;
        Point pressedPosition = default;
        EventHandler<PointerPressedEventArgs> onPressed = (_, e) =>
        {
            pressed = e.GetCurrentPoint(toggle).Properties.IsLeftButtonPressed;
            pressedPosition = e.GetPosition(toggle);
        };
        EventHandler<PointerEventArgs> onMoved = (_, e) =>
        {
            if (pressed && e.GetCurrentPoint(toggle).Properties.IsLeftButtonPressed &&
                e.GetPosition(toggle).X - pressedPosition.X > knob.Bounds.Width)
                movedWithButtonHeld = true;
        };
        EventHandler<PointerReleasedEventArgs> onReleased = (_, _) => released = true;
        toggle.AddHandler(InputElement.PointerPressedEvent, onPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        toggle.AddHandler(InputElement.PointerMovedEvent, onMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        toggle.AddHandler(InputElement.PointerReleasedEvent, onReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        try
        {
            window.MouseMove(start);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            window.MouseUp(end, MouseButton.Left);
            Assert.True(pressed, "The real knob must receive left-button press input.");
            Assert.True(movedWithButtonHeld, "The real switch must receive held-button pointer movement across the track.");
            Assert.True(released, "The real switch must receive pointer release input.");
        }
        finally
        {
            toggle.RemoveHandler(InputElement.PointerPressedEvent, onPressed);
            toggle.RemoveHandler(InputElement.PointerMovedEvent, onMoved);
            toggle.RemoveHandler(InputElement.PointerReleasedEvent, onReleased);
        }
    }

    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Layout(window);
    }

    private static void Layout(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static void Render(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("DANMU_TEST_RENDER_DIRECTORY");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(directory, name + ".png"));
    }
}
