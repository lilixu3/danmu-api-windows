namespace DanmuApi.Platform;

/// <summary>
/// Locates the system programs this host invokes (netsh, powershell). A 32-bit build on 64-bit
/// Windows sees <c>System32</c> redirected to <c>SysWOW64</c>, so it must address the native
/// directory through the <c>Sysnative</c> alias; otherwise the firewall work is done by the 32-bit
/// tooling in a redirected view. On 32-bit Windows there is no redirection and System32 is native.
/// </summary>
public static class SystemPaths
{
    public static string NativeSystemDirectory(string systemRoot)
    {
        if (string.IsNullOrWhiteSpace(systemRoot)) throw new ArgumentException("SystemRoot 未设置。", nameof(systemRoot));
        return Path.Combine(systemRoot, NativeSystemDirectoryName(Environment.Is64BitOperatingSystem, Environment.Is64BitProcess));
    }

    /// <summary>Directory name inside SystemRoot that holds the native 64-bit system tools.</summary>
    public static string NativeSystemDirectoryName(bool is64BitOperatingSystem, bool is64BitProcess) =>
        is64BitOperatingSystem && !is64BitProcess ? "Sysnative" : "System32";
}
