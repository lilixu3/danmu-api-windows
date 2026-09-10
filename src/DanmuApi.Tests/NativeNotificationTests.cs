using DanmuApi.App.Services;
using Microsoft.Win32;
using Windows.UI.Notifications;

namespace DanmuApi.Tests;

public sealed class NativeNotificationTests
{
    [Fact]
    public void EmbeddedPngIsPersistentAndCorruptionFailsExplicitly()
    {
        var directory = Path.Combine(Path.GetTempPath(), "通知 图标 ' &", Guid.NewGuid().ToString("N"));
        try
        {
            var path = NativeToastNotificationService.EnsureIcon(directory);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, File.ReadAllBytes(path)[..8]);
            Assert.Equal(path, NativeToastNotificationService.EnsureIcon(directory));
            File.WriteAllText(path, "corrupt");
            Assert.Throws<IOException>(() => NativeToastNotificationService.EnsureIcon(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ToastUsesLocalPngAndEscapesTextAndActions()
    {
        var path = Path.Combine(Path.GetTempPath(), "中文 空格 & ' 图标.png");
        var action = new DesktopNotificationAction("打开 & 查看", "danmuapi://open");
        var xml = System.Xml.Linq.XDocument.Parse(NativeToastNotificationService.BuildToastXml("标题 < &", "正文 ' >", action.ActivationUri, action, path));
        var image = Assert.Single(xml.Descendants("image"));
        Assert.Equal("appLogoOverride", (string?)image.Attribute("placement"));
        Assert.Equal(path, new Uri((string)image.Attribute("src")!).LocalPath);
        Assert.Equal(new[] { "标题 < &", "正文 ' >" }, xml.Descendants("text").Select(item => item.Value));
        Assert.Equal(action.Label, (string?)Assert.Single(xml.Descendants("action")).Attribute("content"));
    }

    [Fact]
    public async Task IconPreparationFailureReturnsDiagnosticBeforeIdentityRegistration()
    {
        var blocker = Path.GetTempFileName();
        try
        {
            var diagnostics = new Diagnostics();
            var service = new NativeToastNotificationService(diagnostics)
            {
                RegisteredApplicationId = "DanmuApi.NotificationTest." + Guid.NewGuid().ToString("N"),
                TestExecutable = Path.Combine(Path.GetTempPath(), "DanmuApi.App.exe"),
                TestIconDirectory = blocker,
                RegisterProtocol = false,
            };
            var result = await service.ShowAsync("test", "test");
            Assert.False(result.Succeeded);
            Assert.Contains("准备通知 PNG 图标", result.Diagnostic);
            Assert.Contains("HRESULT", diagnostics.LastDiagnostic);
            using var identity = Registry.CurrentUser.OpenSubKey($@"Software\Classes\AppUserModelId\{service.RegisteredApplicationId}");
            Assert.Null(identity);
            Assert.False(File.Exists(NotificationShortcut.PathFor(service.RegisteredApplicationId)));
        }
        finally { File.Delete(blocker); }
    }

    [SkippableFact]
    public async Task IsolatedWindowsNotificationIsAcceptedAndIdentityIsCleaned()
    {
        var executable = Environment.GetEnvironmentVariable("DANMU_TEST_NOTIFICATION_EXE");
        Skip.If(string.IsNullOrEmpty(executable), "Real notification probe is opt-in.");
        var appId = "DanmuApi.NotificationTest." + Guid.NewGuid().ToString("N");
        var iconDirectory = Path.Combine(Path.GetTempPath(), appId);
        var diagnostics = new Diagnostics();
        var service = new NativeToastNotificationService(diagnostics)
        {
            RegisteredApplicationId = appId,
            TestExecutable = executable,
            TestIconDirectory = iconDirectory,
            RegisterProtocol = false,
        };
        try
        {
            var result = await service.ShowAsync("弹幕 API · 隔离通知测试", "此通知验证 Windows 提交通道，不会启动或更新核心。");
            Assert.True(result.Succeeded, result.Diagnostic);
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\AppUserModelId\{appId}");
            Assert.Equal("弹幕 API", key?.GetValue("DisplayName"));
            Assert.Equal(NativeToastNotificationService.EnsureIcon(iconDirectory), key?.GetValue("IconUri"));
            await Task.Delay(1500);
            Assert.DoesNotContain(diagnostics.Messages, item => item.Contains("失败", StringComparison.Ordinal));
        }
        finally
        {
            ToastNotificationManager.History.Clear(appId);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\AppUserModelId\{appId}", false);
            File.Delete(NotificationShortcut.PathFor(appId));
            if (Directory.Exists(iconDirectory)) Directory.Delete(iconDirectory, true);
        }
    }

    private sealed class Diagnostics : IAppDiagnostics
    {
        public List<string> Messages { get; } = [];
        public string? LastDiagnostic { get; private set; }
        public void Record(string message, Exception? error = null)
        {
            LastDiagnostic = message;
            Messages.Add(message);
        }
    }
}
