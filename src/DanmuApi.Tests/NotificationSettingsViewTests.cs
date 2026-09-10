using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DanmuApi.App.Services;
using DanmuApi.App.Views;

namespace DanmuApi.Tests;

public sealed partial class SettingsPageViewModelTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void NotificationSettingsRenderWithFourChoices(bool dark)
    {
        var model = CreateViewModel(new RecordingSettingsStore());
        Assert.Equal(4,model.NotificationLevelOptions.Count);
        var window = new Window { Width=1150, Height=850,Content=new SettingsView {DataContext=model},
            RequestedThemeVariant=dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();window.UpdateLayout();
            var directory=Environment.GetEnvironmentVariable("DANMU_TEST_RENDER_DIRECTORY");
            if(!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
                using var bitmap=new RenderTargetBitmap(new PixelSize(1150,850));bitmap.Render(window);
                bitmap.Save(Path.Combine(directory,dark ? "notification-settings-dark.png" : "notification-settings-light.png"));
            }
            model.SelectedNotificationLevelOption=model.NotificationLevelOptions.Single(option=>option.Value==DesktopNotificationLevel.Updates);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(DesktopNotificationLevel.Updates,model.SelectedNotificationLevelOption.Value);
        }
        finally{window.Close();}
    }
}
