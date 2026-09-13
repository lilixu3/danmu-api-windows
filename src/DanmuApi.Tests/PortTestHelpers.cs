using System.Net;
using System.Net.Sockets;

namespace DanmuApi.Tests;

internal static class TestPorts
{
    /// <summary>拿一个当前空闲的端口（监听后立即释放）。</summary>
    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>在 0.0.0.0 上真正监听一个由系统分配的端口。</summary>
    public static TcpListener Listen(out int port)
    {
        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }
}

/// <summary>
/// 模拟 Windows 把动态端口范围内的端口临时分配给外联连接后的状态：只 bind、不 listen。
/// 这种占用会挡住别的进程绑定同一端口，但系统 TCP 表里没有对应的监听者。
/// </summary>
internal sealed class PortSquat : IDisposable
{
    private readonly Socket _socket;

    public PortSquat()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            // 与 TcpListener 的默认独占语义保持一致，保证随后同一端口的 bind 会失败。
            ExclusiveAddressUse = true,
        };
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
    }

    public int Port { get; }

    public void Dispose() => _socket.Dispose();
}
