using Microsoft.Win32;
using System.Security;

namespace DanmuApi.Platform;

public enum AutostartState { NotRegistered, Registered, InvalidPath, SystemDisabled, Unknown, Unsupported }

// Raw command values stay inside the platform layer; never put them in UI diagnostics.
public sealed record AutostartRegistryEntry(bool Exists, string? Command = null, bool? SystemEnabled = true,
    bool Succeeded = true, string Diagnostic = "");

public interface IAutostartRegistryReader
{
    AutostartRegistryEntry Read(string valueName);
}

public sealed class WindowsAutostartRegistryReader : IAutostartRegistryReader
{
    public AutostartRegistryEntry Read(string valueName)
    {
        if (!OperatingSystem.IsWindows()) return new(false, Succeeded: false, Diagnostic: "当前系统不支持 Windows 注册表");
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: false);
            if (run is null || !run.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase))
                return new(false);
            var command = run.GetValueKind(valueName) == RegistryValueKind.String
                ? run.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string : null;
            using var approved = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", writable: false);
            if (approved is null || !approved.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase))
                return new(true, command);
            var data = approved.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return new(true, command, DecodeStartupApproved(data), Diagnostic: DecodeStartupApproved(data) is null
                ? "StartupApproved 数据格式或状态无法识别" : "");
        }
        catch (Exception error) when (error is UnauthorizedAccessException or SecurityException or IOException or ArgumentException)
        {
            return new(false, Succeeded: false,
                Diagnostic: $"读取自启注册表失败（{error.GetType().Name}, HRESULT=0x{error.HResult:X8}）");
        }
    }

    public static bool? DecodeStartupApproved(object? value)
    {
        if (value is not byte[] { Length: 12 } data || data[1] != 0 || data[2] != 0 || data[3] != 0) return null;
        // Only known Run states are interpreted. Unknown Windows formats remain explicit.
        return data[0] switch { 2 => true, 3 => false, _ => null };
    }
}
