using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DanmuApi.Platform;

internal static class WindowsTcpPeerIdentity
{
    internal static bool MatchesConnectedServer(TcpClient client, int expectedProcessId)
    {
        if (!OperatingSystem.IsWindows()) throw new IOException("绑定本地退出通道需要 Windows TCP 进程身份验证");
        if (client.Client.LocalEndPoint is not IPEndPoint local || client.Client.RemoteEndPoint is not IPEndPoint remote ||
            !local.Address.Equals(IPAddress.Loopback) || !remote.Address.Equals(IPAddress.Loopback))
            throw new IOException("本地退出通道不是已连接的 IPv4 回环连接");
        var size = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0);
        if (result != 122 || size < sizeof(int) || size > 16 * 1024 * 1024)
            throw NativeFailure(result);
        var table = Marshal.AllocHGlobal(size);
        try
        {
            result = GetExtendedTcpTable(table, ref size, false, 2, 5, 0);
            if (result != 0) throw NativeFailure(result);
            var count = Marshal.ReadInt32(table);
            var rowSize = Marshal.SizeOf<TcpRow>();
            if (count < 0 || count > (size - sizeof(int)) / rowSize)
                throw new IOException("Windows TCP 进程表长度无效");
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<TcpRow>(IntPtr.Add(table, sizeof(int) + index * rowSize));
                if (row.State == 5 && row.OwnerPid == expectedProcessId &&
                    new IPAddress(row.LocalAddress).Equals(remote.Address) &&
                    new IPAddress(row.RemoteAddress).Equals(local.Address) &&
                    Port(row.LocalPort) == remote.Port && Port(row.RemotePort) == local.Port)
                    return true;
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(table); }
    }

    private static int Port(uint value) => ((int)value & 0xff) << 8 | ((int)value >> 8 & 0xff);
    private static IOException NativeFailure(uint result) => new($"读取 Windows TCP 进程表失败（Win32 {result}）", new Win32Exception(unchecked((int)result)));

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwnerPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order, uint family, int tableClass, uint reserved);
}
