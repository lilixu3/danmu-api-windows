using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DanmuApi.App.Services;

internal static class AppUpdateTrust
{
    public static X509Certificate2 Certificate()
    {
        using var stream = typeof(AppUpdateTrust).Assembly.GetManifestResourceStream("DanmuApi.App.Assets.update-publisher.cer")
            ?? throw new InvalidOperationException("应用更新公钥缺失");
        using var data = new MemoryStream();
        stream.CopyTo(data);
        return new X509Certificate2(data.ToArray());
    }

    public static byte[] PublicKey()
    {
        using var certificate = Certificate();
        using var rsa = certificate.GetRSAPublicKey() ?? throw new InvalidOperationException("更新签名公钥无效");
        return rsa.ExportSubjectPublicKeyInfo();
    }

    public static void VerifyExecutable(string path)
    {
        using var expected = Certificate();
        using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
        if (!signer.RawData.AsSpan().SequenceEqual(expected.RawData)) throw new IOException("更新程序发布者与可信证书不一致");
        var info = new FileInfoData { Size = (uint)Marshal.SizeOf<FileInfoData>(), Path = path };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfoData>());
        try
        {
            Marshal.StructureToPtr(info, pointer, false);
            var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1, File = pointer, StateAction = 1, ProviderFlags = 0x1000 };
            var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            try
            {
                var result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                if (result != 0 && result != unchecked((int)0x800B0109))
                    throw new IOException($"更新程序Authenticode校验失败：0x{result:X8}");
            }
            finally { data.StateAction = 2; WinVerifyTrust(new IntPtr(-1), ref action, ref data); }
        }
        finally { Marshal.DestroyStructure<FileInfoData>(pointer); Marshal.FreeHGlobal(pointer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfoData { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr Handle; public IntPtr Subject; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size; public IntPtr PolicyCallback; public IntPtr Sip; public uint UiChoice; public uint RevocationChecks;
        public uint UnionChoice; public IntPtr File; public uint StateAction; public IntPtr State; public IntPtr Url;
        public uint ProviderFlags; public uint UiContext;
    }
    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}
