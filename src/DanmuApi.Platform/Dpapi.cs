using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DanmuApi.Platform;

/// <summary>
/// Windows DPAPI（当前用户作用域）加解密原语。GitHub Token 与管理员会话共用；
/// 与系统凭据管理器绑定同一用户，密文不落任何明文副本。
/// </summary>
internal static class Dpapi
{
    private const int CryptProtectUiForbidden = 0x1;

    public static byte[] Protect(byte[] value, byte[] entropy)
    {
        using var input = DataBlob.From(value);
        using var entropyBlob = DataBlob.From(entropy);
        if (!CryptProtectData(
                ref input.Value,
                null,
                ref entropyBlob.Value,
                IntPtr.Zero,
                IntPtr.Zero,
                CryptProtectUiForbidden,
                out var output))
        {
            throw new IOException("Windows DPAPI 加密失败", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return CopyAndFree(output);
    }

    public static byte[] Unprotect(byte[] value, byte[] entropy)
    {
        using var input = DataBlob.From(value);
        using var entropyBlob = DataBlob.From(entropy);
        if (!CryptUnprotectData(
                ref input.Value,
                IntPtr.Zero,
                ref entropyBlob.Value,
                IntPtr.Zero,
                IntPtr.Zero,
                CryptProtectUiForbidden,
                out var output))
        {
            throw new IOException("Windows DPAPI 解密失败", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return CopyAndFree(output);
    }

    private static byte[] CopyAndFree(NativeDataBlob blob)
    {
        try
        {
            if (blob.Size <= 0 || blob.Size > 64 * 1024 || blob.Data == IntPtr.Zero)
            {
                throw new IOException("Windows DPAPI 返回无效数据");
            }

            var result = new byte[blob.Size];
            Marshal.Copy(blob.Data, result, 0, blob.Size);
            return result;
        }
        finally
        {
            if (blob.Data != IntPtr.Zero)
            {
                LocalFree(blob.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeDataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    private sealed class DataBlob : IDisposable
    {
        private readonly SafeCoTaskMemHandle _memory;

        private DataBlob(NativeDataBlob value, SafeCoTaskMemHandle memory)
        {
            Value = value;
            _memory = memory;
        }

        public NativeDataBlob Value;

        public static DataBlob From(byte[] value)
        {
            var memory = new SafeCoTaskMemHandle(Marshal.AllocCoTaskMem(value.Length), true);
            Marshal.Copy(value, 0, memory.DangerousGetHandle(), value.Length);
            return new DataBlob(new NativeDataBlob
            {
                Size = value.Length,
                Data = memory.DangerousGetHandle(),
            }, memory);
        }

        public void Dispose()
        {
            if (!_memory.IsInvalid && Value.Size > 0)
            {
                var zeros = new byte[Value.Size];
                Marshal.Copy(zeros, 0, _memory.DangerousGetHandle(), zeros.Length);
            }

            _memory.Dispose();
        }
    }

    private sealed class SafeCoTaskMemHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeCoTaskMemHandle(IntPtr handle, bool ownsHandle)
            : base(ownsHandle)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle()
        {
            Marshal.FreeCoTaskMem(handle);
            return true;
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref NativeDataBlob dataIn,
        string? description,
        ref NativeDataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out NativeDataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref NativeDataBlob dataIn,
        IntPtr description,
        ref NativeDataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out NativeDataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
