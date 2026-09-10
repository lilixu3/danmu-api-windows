using System.Security;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace DanmuApi.App.Services;

public sealed class NativeToastNotificationService(IAppDiagnostics diagnostics) : IDesktopNotificationService
{
    internal string RegisteredApplicationId { get; init; } = ApplicationId;
    internal string? TestExecutable { get; init; }
    internal bool RegisterProtocol { get; init; } = true;
    internal string? TestIconDirectory { get; init; }
    public const string ApplicationId = "DanmuApi.Windows";
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    private readonly List<ToastNotification> _submitted = [];
    private readonly object _sync = new();

    public Task<DesktopNotificationResult> ShowAsync(string title, string message, CancellationToken cancellationToken = default) =>
        ShowAsync(title, message, null, cancellationToken);

    public async Task<DesktopNotificationResult> ShowAsync(string title, string message, DesktopNotificationAction? action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stage = "注册应用身份";
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            ArgumentException.ThrowIfNullOrWhiteSpace(message);
            var executable = TestExecutable ?? Environment.ProcessPath ?? throw new InvalidOperationException("无法解析当前程序路径");
            if (!string.Equals(Path.GetFileName(executable), "DanmuApi.App.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("通知身份只能由已发布的 DanmuApi.App.exe 注册");
            stage = "准备通知 PNG 图标";
            var iconPath = EnsureIcon(TestIconDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DanmuApi", "notification-assets"));
            stage = "注册应用身份";
            RegisterIdentity(executable, iconPath);
            NotificationShortcut.Ensure(executable, RegisteredApplicationId);
            System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(RegisteredApplicationId));
            stage = "创建通知发送器";
            var notifier = ToastNotificationManager.CreateToastNotifier(RegisteredApplicationId);
            stage = "初始化系统通知通道";
            var bootstrapXml = new XmlDocument();
            bootstrapXml.LoadXml("<toast><visual><binding template='ToastGeneric'><text>弹幕 API</text></binding></visual></toast>");
            var bootstrap = new ToastNotification(bootstrapXml) { SuppressPopup = true, Tag = "identity-init", Group = "identity", ExpirationTime = DateTimeOffset.Now.AddSeconds(15) };
            notifier.Show(bootstrap);
            ToastNotificationManager.History.Remove("identity-init", "identity", RegisteredApplicationId);
            stage = "读取系统通知设置";
            if (notifier.Setting != NotificationSetting.Enabled)
            {
                var reason = $"Windows 通知未启用：{notifier.Setting}";
                diagnostics.Record(reason);
                return DesktopNotificationResult.Failure(reason);
            }
            var target = action?.ActivationUri ?? "danmuapi://open";
            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme != "danmuapi")
                throw new ArgumentException("通知动作必须使用 danmuapi 协议");
            var xml = new XmlDocument();
            xml.LoadXml(BuildToastXml(title, message, target, action, iconPath));
            var toast = new ToastNotification(xml) { ExpirationTime = DateTimeOffset.Now.AddMinutes(10) };
            var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            toast.Failed += (_, error) =>
            {
                failed.TrySetResult($"Windows 通知异步提交失败：HRESULT=0x{error.ErrorCode.HResult:X8}");
                diagnostics.Record($"Windows 通知异步提交失败：HRESULT=0x{error.ErrorCode.HResult:X8}");
                lock (_sync) _submitted.Remove(toast);
            };
            toast.Dismissed += (_, _) => { lock (_sync) _submitted.Remove(toast); };
            lock (_sync)
            {
                _submitted.RemoveAll(item => item.ExpirationTime < DateTimeOffset.Now);
                _submitted.Add(toast);
            }
            stage = "提交通知";
            notifier.Show(toast);
            var observed = await Task.WhenAny(failed.Task, Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (observed == failed.Task) return DesktopNotificationResult.Failure(await failed.Task.ConfigureAwait(false));
            diagnostics.Record($"Windows 通知已提交，应用标识={RegisteredApplicationId}；实际横幅受系统设置控制");
            return new DesktopNotificationResult(true, "已提交 Windows 通知；横幅是否显示取决于系统通知设置及专注助手。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            diagnostics.Record("Windows 通知等待已取消；已提交的通知可能仍由系统显示");
            throw;
        }
        catch (Exception error)
        {
            var reason = $"Windows 通知失败（{stage}）：{error.GetType().Name}，HRESULT=0x{error.HResult:X8}，{error.Message}";
            diagnostics.Record(reason);
            return DesktopNotificationResult.Failure(reason);
        }
    }

    internal static string EnsureIcon(string directory)
    {
        using var resource = typeof(NativeToastNotificationService).Assembly.GetManifestResourceStream("DanmuApi.NotificationIcon.png")
            ?? throw new InvalidOperationException("缺少内嵌通知 PNG 图标");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        Directory.CreateDirectory(directory);
        var path = Path.GetFullPath(Path.Combine(directory, $"app-icon-{hash}.png"));
        if (!File.Exists(path))
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, bytes);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            throw new IOException("本地通知 PNG 图标与内嵌资源不一致：" + path);
        return path;
    }

    internal static string BuildToastXml(string title, string message, string target, DesktopNotificationAction? action, string iconPath)
    {
        var iconUri = new Uri(Path.GetFullPath(iconPath)).AbsoluteUri;
        var button = action is null ? string.Empty : $"<actions><action content='{SecurityElement.Escape(action.Label)}' arguments='{SecurityElement.Escape(target)}' activationType='protocol'/></actions>";
        return $"<toast activationType='protocol' launch='{SecurityElement.Escape(target)}'><visual><binding template='ToastGeneric'><image placement='appLogoOverride' src='{SecurityElement.Escape(iconUri)}' alt='弹幕 API'/><text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(message)}</text></binding></visual>{button}</toast>";
    }

    private void RegisterIdentity(string executable, string iconPath)
    {
        using var app = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{RegisteredApplicationId}", true);
        app.SetValue("DisplayName", "弹幕 API");
        app.SetValue("IconUri", iconPath);
        app.SetValue("ShowInSettings", 1, RegistryValueKind.DWord);
        app.SetValue("CustomActivator", "{AA964618-C944-4E34-BBC2-49012FB77344}");
        if (!RegisterProtocol) return;
        using var scheme = Registry.CurrentUser.CreateSubKey(@"Software\Classes\danmuapi", true);
        scheme.SetValue(string.Empty, "URL:Danmu API Protocol");
        scheme.SetValue("URL Protocol", string.Empty);
        using var command = scheme.CreateSubKey(@"shell\open\command", true);
        command.SetValue(string.Empty, $"\"{executable}\" \"%1\"");
    }
}
