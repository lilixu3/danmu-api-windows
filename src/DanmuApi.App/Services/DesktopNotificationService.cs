using System.Security;
using System.Text;
using DanmuApi.Platform;
using Microsoft.Win32;

namespace DanmuApi.App.Services;

public sealed record DesktopNotificationAction(string Label, string ActivationUri);

public enum DesktopNotificationKind { General, UpdateDiscovered, UpdateCompleted, UpdateFailed, CoreDependencyMissing, StartupSucceeded, ManualTest }
public enum DesktopNotificationStatus { Submitted, SuppressedByPreference, Failed }
public enum DesktopNotificationLevel { Off, Updates, StartupSuccess, All }

public interface IDesktopNotificationService
{
    Task<DesktopNotificationResult> ShowAsync(
        DesktopNotificationKind kind, string title, string message,
        DesktopNotificationAction? action = null, CancellationToken cancellationToken = default) =>
        ShowAsync(title, message, action, cancellationToken);
    Task<DesktopNotificationResult> ShowAsync(
        string title,
        string message,
        CancellationToken cancellationToken = default);
    Task<DesktopNotificationResult> ShowAsync(
        string title,
        string message,
        DesktopNotificationAction? action,
        CancellationToken cancellationToken = default) =>
        ShowAsync(title, message, cancellationToken);
}

public sealed record DesktopNotificationResult(bool Succeeded, string Diagnostic)
{
    public DesktopNotificationStatus Status { get; init; } = Succeeded ? DesktopNotificationStatus.Submitted : DesktopNotificationStatus.Failed;
    public static DesktopNotificationResult Failure(string diagnostic) => new(false, diagnostic);
    public static DesktopNotificationResult Suppressed() => new(false, "已按应用通知偏好屏蔽") { Status = DesktopNotificationStatus.SuppressedByPreference };
}

public static class DesktopNotificationPolicy
{
    public const string SettingsKey = "notification_level";
    public static DesktopNotificationLevel Read(IReadOnlyDictionary<string, string> values) =>
        !values.TryGetValue(SettingsKey, out var value) ? DesktopNotificationLevel.All : value switch
        {
            "off" => DesktopNotificationLevel.Off,
            "updates" => DesktopNotificationLevel.Updates,
            "startup_success" => DesktopNotificationLevel.StartupSuccess,
            "all" => DesktopNotificationLevel.All,
            _ => throw new FormatException("notification_level 无效；应为 off/updates/startup_success/all"),
        };
    public static string Serialize(DesktopNotificationLevel level) => level switch
    {
        DesktopNotificationLevel.Off => "off",
        DesktopNotificationLevel.Updates => "updates",
        DesktopNotificationLevel.StartupSuccess => "startup_success",
        DesktopNotificationLevel.All => "all",
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };
    public static bool Allows(DesktopNotificationLevel level, DesktopNotificationKind kind) =>
        kind == DesktopNotificationKind.ManualTest || level == DesktopNotificationLevel.All ||
        (level == DesktopNotificationLevel.Updates && kind is DesktopNotificationKind.UpdateDiscovered or DesktopNotificationKind.UpdateCompleted or DesktopNotificationKind.UpdateFailed or DesktopNotificationKind.CoreDependencyMissing) ||
        (level == DesktopNotificationLevel.StartupSuccess && kind == DesktopNotificationKind.StartupSucceeded);
}

public sealed class PreferenceDesktopNotificationService(IDesktopNotificationService transport, ISettingsStore settings) : IDesktopNotificationService
{
    public Task<DesktopNotificationResult> ShowAsync(string title, string message, CancellationToken cancellationToken = default) =>
        ShowAsync(DesktopNotificationKind.General, title, message, cancellationToken: cancellationToken);
    public Task<DesktopNotificationResult> ShowAsync(string title, string message, DesktopNotificationAction? action, CancellationToken cancellationToken = default) =>
        ShowAsync(DesktopNotificationKind.General, title, message, action, cancellationToken);
    public async Task<DesktopNotificationResult> ShowAsync(DesktopNotificationKind kind, string title, string message, DesktopNotificationAction? action = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            if (kind != DesktopNotificationKind.ManualTest && !DesktopNotificationPolicy.Allows(DesktopNotificationPolicy.Read(settings.Read()), kind))
                return DesktopNotificationResult.Suppressed();
            return await transport.ShowAsync(title, message, action, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { return DesktopNotificationResult.Failure($"通知提交失败：{error.Message}"); }
    }
}

public sealed class WindowsToastNotificationService : IDesktopNotificationService
{
    private readonly IPlatformCommandExecutor _executor;
    private readonly Func<string?> _systemRootResolver;
    private readonly Func<string, bool> _schemeRegistrationResolver;

    public WindowsToastNotificationService(
        IPlatformCommandExecutor executor,
        Func<string?>? systemRootResolver = null,
        Func<string, bool>? schemeRegistrationResolver = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _systemRootResolver = systemRootResolver ?? (() => Environment.GetEnvironmentVariable("SystemRoot"));
        _schemeRegistrationResolver = schemeRegistrationResolver ?? IsUriSchemeRegistered;
    }

    public Task<DesktopNotificationResult> ShowAsync(
        string title,
        string message,
        CancellationToken cancellationToken = default) =>
        ShowAsync(title, message, null, cancellationToken);

    public Task<DesktopNotificationResult> ShowAsync(
        string title,
        string message,
        DesktopNotificationAction? action,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ValidateAction(action);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows())
            {
                return DesktopNotificationResult.Failure("Windows Toast 通知仅支持 Windows");
            }

            var systemRoot = _systemRootResolver();
            if (string.IsNullOrWhiteSpace(systemRoot))
            {
                return DesktopNotificationResult.Failure("SystemRoot 未设置，无法定位 PowerShell");
            }

            var powershellPath = Path.GetFullPath(Path.Combine(
                systemRoot,
                "System32",
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"));
            var effectiveAction = action is not null && _schemeRegistrationResolver(new Uri(action.ActivationUri).Scheme)
                ? action
                : null;
            var script = BuildToastScript(title, message, effectiveAction);
            var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            CommandExecutionResult result;
            try
            {
                result = _executor.Execute(
                    powershellPath,
                    ["-NoProfile", "-NonInteractive", "-EncodedCommand", encodedCommand]);
            }
            catch (Exception error)
            {
                return DesktopNotificationResult.Failure($"启动 Windows Toast 通知失败: {error.Message}");
            }

            if (!result.Succeeded)
            {
                var detail = string.IsNullOrWhiteSpace(result.CombinedOutput)
                    ? result.Diagnostic ?? $"exitCode={result.ExitCode?.ToString() ?? "未启动"}"
                    : result.CombinedOutput;
                return DesktopNotificationResult.Failure($"Windows Toast 通知失败: {Limit(detail)}");
            }

            return new DesktopNotificationResult(
                true,
                action is not null && effectiveAction is null
                    ? "Windows Toast 通知已提交；danmuapi 协议未注册，动作按钮未添加，请使用托盘菜单"
                    : "Windows Toast 通知已提交");
        }, cancellationToken);
    }

    private static string BuildToastScript(
        string title,
        string message,
        DesktopNotificationAction? action)
    {
        var escapedTitle = SecurityElement.Escape(title) ?? string.Empty;
        var escapedMessage = SecurityElement.Escape(message) ?? string.Empty;
        var actions = action is null
            ? string.Empty
            : $"<actions><action content='{SecurityElement.Escape(action.Label)}' arguments='{SecurityElement.Escape(action.ActivationUri)}' activationType='protocol'/></actions>";
        var xml = $"<toast><visual><binding template='ToastGeneric'><text>{escapedTitle}</text><text>{escapedMessage}</text></binding></visual>{actions}</toast>";
        var escapedXml = xml.Replace("'", "''", StringComparison.Ordinal);
        return "$ErrorActionPreference = 'Stop'; " +
               "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null; " +
               "[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null; " +
               "$xml = New-Object Windows.Data.Xml.Dom.XmlDocument; " +
               $"$xml.LoadXml('{escapedXml}'); " +
               "$toast = [Windows.UI.Notifications.ToastNotification]::new($xml); " +
               "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('DanmuApi').Show($toast)";
    }

    private static void ValidateAction(DesktopNotificationAction? action)
    {
        if (action is null)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(action.Label);
        if (!Uri.TryCreate(action.ActivationUri, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "danmuapi", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("通知动作必须使用 danmuapi:// 协议", nameof(action));
        }
    }

    private static bool IsUriSchemeRegistered(string scheme)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{scheme}");
            if (key?.GetValue("URL Protocol") is not null)
            {
                return true;
            }

            using var machineKey = Registry.ClassesRoot.OpenSubKey(scheme);
            return machineKey?.GetValue("URL Protocol") is not null;
        }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static string Limit(string value)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ');
        return normalized.Length <= 500 ? normalized : normalized[..500] + "...";
    }
}
