using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace DanmuApi.App.Services;

/// <summary>
/// Applies the Windows immersive dark-mode attribute so the native title bar and its caption buttons
/// follow the application theme. Without it the system draws a light caption strip above the dark
/// client area, which is exactly the jarring white band users reported.
/// </summary>
internal static class ImmersiveTitleBar
{
    // Windows 10 1809 (build 17763) and later; 19 is the pre-20H1 attribute number.
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeBefore20H1 = 19;

    public static bool TryApply(Window window, bool dark, out string error)
    {
        ArgumentNullException.ThrowIfNull(window);
        error = string.Empty;
        var handle = window.TryGetPlatformHandle();
        if (handle is null || handle.Handle == IntPtr.Zero)
        {
            error = "窗口句柄尚未创建";
            return false;
        }

        var value = dark ? 1 : 0;
        var result = DwmSetWindowAttribute(handle.Handle, UseImmersiveDarkMode, ref value, sizeof(int));
        if (result != 0)
            result = DwmSetWindowAttribute(handle.Handle, UseImmersiveDarkModeBefore20H1, ref value, sizeof(int));
        if (result == 0) return true;
        error = $"DwmSetWindowAttribute 返回 0x{result:X8}";
        return false;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
