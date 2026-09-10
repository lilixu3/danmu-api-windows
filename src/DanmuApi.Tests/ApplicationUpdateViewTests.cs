using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
using DanmuApi.App.Views;
using DanmuApi.Platform;

namespace DanmuApi.Tests;

public sealed class ApplicationUpdateViewTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpdateControlsBindAndPersistIndependentSoftwareSetting(bool dark)
    {
        using var directory = new TemporaryDirectory();
        var store = new SettingsStore(Path.Combine(directory.Path, "settings.properties"));
        using var model = new ApplicationUpdateViewModel(store, new RecordingDialogService(), new Notifications(), new Diagnostics())
        {
            HasUpdate = true, AvailableVersion = "0.1.4-preview.1", PackageText = "免安装版 · 原目录更新 · 100.0 MB",
            ReleaseNotes = "修复服务启动问题\n优化下载列表\n更新运行依赖", PublishedText = "2026-09-09 10:00", Status = "发现新测试版，下载后仍需确认安装。",
        };
        model.AutomaticChecks = false;
        Assert.Equal("false", store.Read()["app_update_auto"]);
        Assert.False(store.Read().ContainsKey("core_update_background_enabled"));
        var window = new Window { Width = 720, Height = 700, Padding = new Thickness(24), Content = new ApplicationUpdateView { DataContext = model }, RequestedThemeVariant = dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var target = Environment.GetEnvironmentVariable("DANMU_TEST_RENDER_DIRECTORY");
            if (!string.IsNullOrEmpty(target))
            {
                Directory.CreateDirectory(target);
                using var bitmap = new RenderTargetBitmap(new PixelSize(720,700)); bitmap.Render(window);
                bitmap.Save(Path.Combine(target, dark ? "app-update-dark.png" : "app-update-light.png"));
            }
            Assert.Equal("发现软件更新", model.UpdateBadge);
            model.HasUpdate = false;
            Assert.Equal("", model.UpdateBadge);
        }
        finally { window.Close(); }
    }
    private sealed class Notifications : IDesktopNotificationService
    {
        public Task<DesktopNotificationResult> ShowAsync(string title,string message,CancellationToken token=default) => Task.FromResult(new DesktopNotificationResult(true,"test"));
    }
    private sealed class Diagnostics : IAppDiagnostics
    {
        public string? LastDiagnostic { get; private set; }
        public void Record(string message,Exception? error=null) => LastDiagnostic=message;
    }
}
