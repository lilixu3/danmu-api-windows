using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using DanmuApi.App;

[assembly: AvaloniaTestApplication(typeof(DanmuApi.Tests.TestAvaloniaApplication))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerTest)]

namespace DanmuApi.Tests;

public static class TestAvaloniaApplication
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<DanmuApi.App.App>()
            .WithInterFont()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DANMU_TEST_RENDER_DIRECTORY")),
            });
}
