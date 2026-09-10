using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
using DanmuApi.App.Views;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed partial class MainWindowViewModelBehaviorTests
{
    [AvaloniaFact]
    public async Task ThemeHeaderButtonTogglesLightDarkImmediatelyAndPersists()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(directory.Path, "preview-theme-token");
        var store = new RecordingSettingsStore();
        var service = new ThemeService(store);
        var original = Application.Current!.RequestedThemeVariant;
        service.Initialize();
        var settingsPage = new SettingsPageViewModel(store, new RecordingAutostartService(), new RecordingNotificationService(), paths, themeService: service);
        await using var model = new MainWindowViewModel(new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped)),
            new RecordingHealthClient(), store, new RecordingCoreCacheClient(), paths, new RecordingDialogService(), settingsPage, new StubAdminSessionService());
        var window = new MainWindow { DataContext = model };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var button = window.FindControl<Button>("ThemeToggleButton")!;
            Assert.Null(button.Flyout);
            var iconHost = button.GetVisualDescendants().OfType<Viewbox>().ToList();
            Assert.Equal(2, iconHost.Count);

            // 初始跟随系统；headless 环境解析为浅色，点击后必须立即变为深色并落盘。
            var startDark = settingsPage.IsDarkTheme;
            var expectedFirst = startDark ? "light" : "dark";
            Assert.Equal(startDark ? ThemeVariant.Dark : ThemeVariant.Light, Application.Current!.ActualThemeVariant);

            button.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(expectedFirst, store.Values["theme"]);
            Assert.Equal(expectedFirst == "dark" ? ThemeVariant.Dark : ThemeVariant.Light, Application.Current!.RequestedThemeVariant);
            Assert.Equal(!startDark, settingsPage.IsDarkTheme);
            Assert.Equal(expectedFirst == "dark", settingsPage.IsDarkThemeSelected);
            SaveThemeRender(window, $"theme-toggle-{expectedFirst}.png");

            button.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(startDark ? "dark" : "light", store.Values["theme"]);
            Assert.Equal(startDark, settingsPage.IsDarkTheme);
            SaveThemeRender(window, $"theme-toggle-{(startDark ? "dark" : "light")}.png");

            // 只显示与当前外观对应的一个图标。
            var visible = iconHost.Count(icon => icon.IsVisible);
            Assert.Equal(1, visible);
        }
        finally { window.Close(); Application.Current.RequestedThemeVariant = original; }
    }

    [AvaloniaFact]
    public async Task SettingsThemeCategoryOffersSystemLightDarkAndMarksSelection()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(directory.Path, "preview-theme-token");
        var store = new RecordingSettingsStore();
        var service = new ThemeService(store);
        var original = Application.Current!.RequestedThemeVariant;
        service.Initialize();
        var settingsPage = new SettingsPageViewModel(store, new RecordingAutostartService(), new RecordingNotificationService(), paths, themeService: service);
        await using (settingsPage)
        {
            settingsPage.SelectedCategory = settingsPage.Categories.Single(category => category.Key == "theme");
            Assert.True(settingsPage.IsThemeCategory);

            var view = new SettingsView { DataContext = settingsPage };
            var window = new Window { Width = 1080, Height = 760, Content = view };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                var system = view.FindControl<Button>("ThemeSystemOption")!;
                var light = view.FindControl<Button>("ThemeLightOption")!;
                var dark = view.FindControl<Button>("ThemeDarkOption")!;

                Assert.Contains("theme-option-active", system.Classes);
                Assert.DoesNotContain("theme-option-active", light.Classes);
                Assert.DoesNotContain("theme-option-active", dark.Classes);

                dark.Command!.Execute(dark.CommandParameter);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("dark", store.Values["theme"]);
                Assert.Contains("theme-option-active", dark.Classes);
                Assert.DoesNotContain("theme-option-active", system.Classes);
                Assert.True(settingsPage.IsDarkTheme);
                SaveThemeRender(view, "settings-theme-dark.png");

                light.Command!.Execute(light.CommandParameter);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("light", store.Values["theme"]);
                Assert.Contains("theme-option-active", light.Classes);
                Assert.DoesNotContain("theme-option-active", dark.Classes);
                Assert.False(settingsPage.IsDarkTheme);
                Assert.Equal("light", settingsPage.SelectedThemeOption.Value);
                SaveThemeRender(view, "settings-theme-light.png");

                system.Command!.Execute(system.CommandParameter);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("system", store.Values["theme"]);
                Assert.Contains("theme-option-active", system.Classes);
                Assert.Equal("system", settingsPage.SelectedThemeOption.Value);
            }
            finally { window.Close(); Application.Current.RequestedThemeVariant = original; }
        }
    }

    private static void SaveThemeRender(Control control, string filename)
    {
        var output = Environment.GetEnvironmentVariable("DANMU_TEST_RENDER_DIRECTORY");
        if (string.IsNullOrEmpty(output)) return;
        Directory.CreateDirectory(output);
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(control.Bounds.Width), (int)Math.Ceiling(control.Bounds.Height)));
        bitmap.Render(control);
        bitmap.Save(Path.Combine(output, filename));
    }
}
