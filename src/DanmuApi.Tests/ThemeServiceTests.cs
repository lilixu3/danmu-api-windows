using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
using DanmuApi.Platform;

namespace DanmuApi.Tests;

public sealed partial class SettingsPageViewModelTests
{
    [AvaloniaFact]
    public void ThemeServicePersistsReloadsAndChangesRealApplicationTheme()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.properties");
        var store = new SettingsStore(path);
        var original = Application.Current!.RequestedThemeVariant;
        var window = new Window();
        try
        {
            var service = new ThemeService(store);
            service.Initialize();
            Assert.False(File.Exists(path));
            Assert.Equal(ThemeVariant.Default, Application.Current.RequestedThemeVariant);
            window.Show();
            foreach (var (name, variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light), ("system", ThemeVariant.Default) })
            {
                service.SetTheme(name);
                Assert.Equal(variant, Application.Current.RequestedThemeVariant);
                if (name != "system") Assert.Equal(variant, window.ActualThemeVariant);
                var reentered = new ThemeService(new SettingsStore(path));
                reentered.Initialize();
                Assert.Equal(name, reentered.CurrentTheme);
                Assert.Equal(name, store.Read()["theme"]);
            }
        }
        finally { window.Close(); Application.Current.RequestedThemeVariant = original; }
    }

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("LIGHT")]
    [InlineData(" light")]
    [InlineData("unknown")]
    public void ThemeRejectsInvalidStoredAndRequestedValues(string invalid)
    {
        var store = new RecordingSettingsStore();
        var service = new ThemeService(store);
        var original = Application.Current!.RequestedThemeVariant;
        Assert.Throws<FormatException>(() => service.SetTheme(invalid));
        Assert.Empty(store.Values);
        store.Values["theme"] = invalid;
        Assert.Throws<FormatException>(service.Initialize);
        Assert.Equal(invalid, store.Values["theme"]);
        Assert.Equal(original, Application.Current.RequestedThemeVariant);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThemeWriteFailureIsExplicitAndDoesNotApply(bool throws)
    {
        var store = new RecordingSettingsStore { FailWrites = throws, IgnoreWrites = !throws };
        var service = new ThemeService(store);
        var original = Application.Current!.RequestedThemeVariant;
        try
        {
            service.Initialize();
            Assert.Throws<IOException>(() => service.SetTheme("dark"));
            Assert.Equal("system", service.CurrentTheme);
            Assert.Equal(ThemeVariant.Default, Application.Current.RequestedThemeVariant);
            var model = new SettingsPageViewModel(store, new StubAutostartService(), new StubNotificationService(),
                new AppPaths(Path.GetTempPath(), Path.GetTempPath()), themeService: service);
            model.SetThemeCommand.Execute("dark");
            Assert.Contains("切换主题失败", model.ThemeStatusText);
            Assert.Equal(model.ThemeStatusText, model.Diagnostic);
            Assert.Equal("system", model.SelectedThemeOption.Value);
            Assert.Empty(store.Values);
        }
        finally { Application.Current.RequestedThemeVariant = original; }
    }

    [AvaloniaFact]
    public void ThemeReadFailureIsPropagatedWithoutWritingOrApplying()
    {
        var original = Application.Current!.RequestedThemeVariant;
        var service = new ThemeService(new UnreadableThemeStore());
        Assert.Throws<IOException>(service.Initialize);
        Assert.Equal(original, Application.Current.RequestedThemeVariant);
    }

    private sealed class UnreadableThemeStore : ISettingsStore
    {
        public IReadOnlyDictionary<string, string> Read() => throw new IOException("theme read failure");
        public void Write(IReadOnlyDictionary<string, string?> changes) => throw new Xunit.Sdk.XunitException("Initialize must not write");
    }

    [AvaloniaFact]
    public void ThemeSettingsSelectionAndHeaderCommandSharePersistedState()
    {
        var store = new RecordingSettingsStore();
        var service = new ThemeService(store);
        var original = Application.Current!.RequestedThemeVariant;
        try
        {
            service.Initialize();
            var model = new SettingsPageViewModel(store, new StubAutostartService(), new StubNotificationService(),
                new AppPaths(Path.GetTempPath(), Path.GetTempPath()), themeService: service);
            model.SelectedThemeOption = model.ThemeOptions.Single(option => option.Value == "dark");
            Assert.Equal(ThemeVariant.Dark, Application.Current.RequestedThemeVariant);
            model.SetThemeCommand.Execute("light");
            Assert.Equal("light", model.SelectedThemeOption.Value);
            Assert.Equal("light", store.Values["theme"]);
            model.SetThemeCommand.Execute("invalid");
            Assert.Contains("切换主题失败", model.Diagnostic);
            Assert.Equal("light", model.SelectedThemeOption.Value);
        }
        finally { Application.Current.RequestedThemeVariant = original; }
    }
}
