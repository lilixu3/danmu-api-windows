using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace DanmuApi.App.Services;

internal static class NotificationShortcut
{
    internal static string PathFor(string appId) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), appId == NativeToastNotificationService.ApplicationId ? "弹幕API.lnk" : appId + ".lnk");

    internal static void Ensure(string executable, string appId)
    {
        var shortcut = PathFor(appId);
        Directory.CreateDirectory(Path.GetDirectoryName(shortcut)!);
        var instance = new ShellLink();
        try
        {
            var link = (IShellLinkW)instance;
            link.SetPath(executable);
            link.SetWorkingDirectory(Path.GetDirectoryName(executable)!);
            link.SetDescription("弹幕 API Windows");
            link.SetIconLocation(executable, 0);
            var propertyStore = (IPropertyStore)instance;
            var key = new PropertyKey { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = 5 };
            var value = new PropVariant { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(appId) };
            try { Marshal.ThrowExceptionForHR(propertyStore.SetValue(ref key, ref value)); }
            finally { Marshal.FreeCoTaskMem(value.Pointer); }
            key.PropertyId = 26;
            value.Type = 72;
            value.Pointer = Marshal.AllocCoTaskMem(16);
            try
            {
                Marshal.Copy(new Guid("AA964618-C944-4E34-BBC2-49012FB77344").ToByteArray(), 0, value.Pointer, 16);
                Marshal.ThrowExceptionForHR(propertyStore.SetValue(ref key, ref value));
            }
            finally { Marshal.FreeCoTaskMem(value.Pointer); }
            Marshal.ThrowExceptionForHR(propertyStore.Commit());
            ((IPersistFile)instance).Save(shortcut, true);
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid FormatId; public uint PropertyId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant { public ushort Type; public ushort Reserved1; public ushort Reserved2; public ushort Reserved3; public IntPtr Pointer; public int Padding; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maximum, IntPtr data, uint flags);
        void GetIDList(out IntPtr id);
        void SetIDList(IntPtr id);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int maximum);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maximum);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maximum);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maximum, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string file, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
