using System.Diagnostics;
using System.Text.Json;
using DanmuApi.App.Services;
using DanmuApi.Platform;
using Microsoft.Win32;
using Windows.UI.Notifications;

namespace DanmuApi.App;

internal static class ReleaseSelfTest
{
    public static int Run(string reportPath)
    {
        var testId = "DanmuApi.ReleaseProbe." + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), testId);
        var messages = new List<string>();
        var diagnostics = new ProbeDiagnostics(messages);
        var code = 1;
        try
        {
            var notificationOnly = Environment.GetEnvironmentVariable("DANMU_NOTIFICATION_ICON_PROBE") == "1";
            messages.Add($"隔离身份：{testId}；通知专用探针：{notificationOnly}");
            if (!notificationOnly)
            {
            var paths = new AppPaths(directory, Path.Combine(directory, "settings"));
            var preparationWatch = Stopwatch.StartNew();
            BundledRuntimePreparer.Prepare(Path.Combine(AppContext.BaseDirectory, "runtime-bundle"), paths);
            messages.Add($"首次准备耗时：{preparationWatch.Elapsed.TotalMilliseconds:0.0} ms");
            if (Directory.EnumerateDirectories(paths.NodeProjectDirectory, "danmu_api*").Any()) throw new IOException("依赖包意外包含核心");
            var start = new ProcessStartInfo(Path.Combine(paths.RuntimeDirectory, "node.exe"))
            {
                WorkingDirectory = paths.NodeProjectDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add("require('./runtime-polyfills.js');require('./startup-failure.js');require('./favorite-scheduler-host.js');for(const x of Object.keys(require('./package.json').dependencies)){require.resolve(x)};console.log(process.version)");
            using var node = Process.Start(start) ?? throw new IOException("Node未启动");
            var stdout = node.StandardOutput.ReadToEndAsync();
            var stderr = node.StandardError.ReadToEndAsync();
            if (!node.WaitForExit(15000)) { node.Kill(true); throw new TimeoutException("Node依赖探针超时"); }
            if (node.ExitCode != 0) throw new IOException("Node依赖检查失败：" + stderr.GetAwaiter().GetResult());
            messages.Add("Node及生产依赖解析通过：" + stdout.GetAwaiter().GetResult().Trim());
            var config = Path.Combine(paths.NodeProjectDirectory, "config");
            Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, ".env"), "USER_SENTINEL=preserve");
            preparationWatch.Restart();
            BundledRuntimePreparer.Prepare(Path.Combine(AppContext.BaseDirectory, "runtime-bundle"), paths);
            messages.Add($"重复准备耗时：{preparationWatch.Elapsed.TotalMilliseconds:0.0} ms");
            if (File.ReadAllText(Path.Combine(config, ".env")) != "USER_SENTINEL=preserve") throw new IOException("配置被覆盖");
            messages.Add("空目录准备通过；无核心；重复准备保留用户配置");
            }
            var iconDirectory = Path.Combine(directory, "通知 图标 & ' assets");
            var iconPath = NativeToastNotificationService.EnsureIcon(iconDirectory);
            using var resource = typeof(NativeToastNotificationService).Assembly.GetManifestResourceStream("DanmuApi.NotificationIcon.png")
                ?? throw new IOException("单文件程序集缺少通知 PNG 资源");
            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            var png = File.ReadAllBytes(iconPath);
            if (!png.AsSpan().SequenceEqual(buffer.ToArray())) throw new IOException("提取 PNG 与内嵌资源不一致");
            if (png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new IOException("通知资源不是 PNG");
            if (!png.AsSpan(12, 4).SequenceEqual("IHDR"u8)
                || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)) != 256
                || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)) != 256)
                throw new IOException("通知 PNG IHDR 尺寸错误");
            messages.Add($"内嵌 PNG 提取/字节一致/IHDR 256x256通过：{iconPath}");
            const string title = "弹幕 API · 隔离图标验证";
            const string message = "单文件 PNG/XML/注册/通知提交验证，不会启动核心。";
            var xml = System.Xml.Linq.XDocument.Parse(NativeToastNotificationService.BuildToastXml(title, message, "danmuapi://open", null, iconPath));
            var image = xml.Descendants("image").Single();
            var source = new Uri((string?)image.Attribute("src") ?? throw new IOException("Toast 缺少图片 URI"));
            if ((string?)image.Attribute("placement") != "appLogoOverride" || !source.IsFile || source.LocalPath != iconPath)
                throw new IOException("Toast 图片未指向隔离 PNG");
            if (!xml.Descendants("text").Select(item => item.Value).SequenceEqual(new[] { title, message }))
                throw new IOException("Toast 文本不一致");
            messages.Add("Toast XML appLogoOverride/file URI/文本通过：" + xml.ToString(System.Xml.Linq.SaveOptions.DisableFormatting));
            var service = new NativeToastNotificationService(diagnostics)
            {
                RegisteredApplicationId = testId, RegisterProtocol = false, TestIconDirectory = iconDirectory,
            };
            var notification = service.ShowAsync(title, message).GetAwaiter().GetResult();
            if (!notification.Succeeded) throw new IOException(notification.Diagnostic);
            using var identity = Registry.CurrentUser.OpenSubKey($@"Software\Classes\AppUserModelId\{testId}");
            if (!Equals(identity?.GetValue("IconUri"), iconPath)) throw new IOException("隔离 AUMID IconUri 不是预期 PNG");
            messages.Add("隔离 AUMID PNG 注册值及最终发布EXE通知提交通过");
            code = 0;
        }
        catch (Exception error) { messages.Add(error.ToString()); }
        finally
        {
            try
            {
                ToastNotificationManager.History.Clear(testId);
                Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\AppUserModelId\{testId}", false);
                File.Delete(NotificationShortcut.PathFor(testId));
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            catch (Exception error) { messages.Add("测试资源清理失败：" + error.Message); code = 1; }
            File.WriteAllLines(reportPath, messages);
        }
        return code;
    }

    private sealed class ProbeDiagnostics(List<string> messages) : IAppDiagnostics
    {
        public string? LastDiagnostic { get; private set; }
        public void Record(string message, Exception? error = null) { LastDiagnostic = message; messages.Add(message); }
    }
}
