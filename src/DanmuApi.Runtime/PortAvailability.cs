using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DanmuApi.Runtime;

/// <summary>监听端口可用性判定结果。</summary>
public enum PortAvailabilityState
{
    /// <summary>端口可以绑定。</summary>
    Free,

    /// <summary>绑定失败，且系统 TCP 表里存在监听该端口的套接字：确实有进程在提供服务。</summary>
    Listening,

    /// <summary>
    /// 绑定失败，但没有任何监听套接字：端口被当作临时源端口占用。
    /// Windows 会把动态端口范围（默认 49152-65535，但部分优化/安全软件会把它放宽到 1024 起）内的端口
    /// 临时分配给外联连接。这种占用没有监听者，会随那条连接关闭自行释放，
    /// 必须与"有别的实例在监听"区别对待，否则启动会被误判成"已有其他实例在运行"。
    /// </summary>
    Unbindable,
}

public static class PortAvailability
{
    /// <summary>
    /// 判定端口是否可用。**先查监听表，再试绑定**，顺序不能反：
    /// Windows 上"非独占绑定"可以和一个已在监听的套接字共存（实测：frpc(Go) 监听 127.0.0.1:7400 时，
    /// <c>TcpListener(IPAddress.Any, 7400).Start()</c> 照样成功），所以只靠试绑会把"有别的进程在提供服务"
    /// 误判成空闲——这曾让预检放行、第二个 frpc 起不来，而状态查询读到的却是前一个进程的管理接口。
    /// </summary>
    public static PortAvailabilityState Probe(int port) => Probe(port, IPAddress.Any);

    public static PortAvailabilityState Probe(int port, IPAddress listenAddress)
    {
        RuntimeValidation.ValidatePort(port);
        ValidateAddress(listenAddress);
        var listeners = ReadListeners(port);
        if (listeners.Any(endpoint => Overlaps(endpoint.Address, listenAddress)))
        {
            return PortAvailabilityState.Listening;
        }

        // TCP 表不公开 IPV6_V6ONLY；独占 IPv4 试绑区分双栈监听与独立 IPv6 监听。
        if (listenAddress.AddressFamily == AddressFamily.InterNetwork &&
            listeners.Any(endpoint => endpoint.Address.Equals(IPAddress.IPv6Any)) &&
            !TryBind(port, listenAddress, exclusive: true))
        {
            return PortAvailabilityState.Listening;
        }

        return TryBind(port, listenAddress, exclusive: false)
            ? PortAvailabilityState.Free
            : PortAvailabilityState.Unbindable;
    }

    /// <summary>
    /// 以真实绑定探测端口是否可用。被本机任意套接字（含临时源端口）占用时返回 false。
    /// 注意：**不能单靠这个判断"有没有别的服务在监听"**——非独占绑定可与已存在的监听者共存，
    /// 见 <see cref="Probe"/>。
    /// </summary>
    public static bool IsBindable(int port) => IsBindable(port, IPAddress.Any);

    public static bool IsBindable(int port, IPAddress listenAddress)
    {
        RuntimeValidation.ValidatePort(port);
        ValidateAddress(listenAddress);
        return TryBind(port, listenAddress, exclusive: false);
    }

    /// <summary>端口上是否存在监听套接字。仅 bind 未 listen 的临时源端口不会出现在这里。</summary>
    public static bool HasListener(int port)
    {
        RuntimeValidation.ValidatePort(port);
        return ReadListeners(port).Length != 0;
    }

    private static IPEndPoint[] ReadListeners(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Where(endpoint => endpoint.Port == port)
                .ToArray();
        }
        catch (NetworkInformationException error)
        {
            throw new IOException($"读取系统 TCP 监听表失败（port={port}）：{error.Message}", error);
        }
    }

    private static bool Overlaps(IPAddress actual, IPAddress requested) =>
        actual.AddressFamily == requested.AddressFamily &&
        (actual.Equals(IPAddress.Any) || actual.Equals(IPAddress.IPv6Any) ||
         requested.Equals(IPAddress.Any) || requested.Equals(IPAddress.IPv6Any) ||
         actual.Equals(requested));

    private static void ValidateAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            throw new ArgumentException("监听地址必须是 IPv4 或 IPv6 地址", nameof(address));
        }
    }

    private static bool TryBind(int port, IPAddress address, bool exclusive)
    {
        try
        {
            using var listener = new TcpListener(address, port);
            listener.ExclusiveAddressUse = exclusive;
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                listener.Server.DualMode = false;
            }
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// 等待端口从 <see cref="PortAvailabilityState.Unbindable"/> 自行释放。
    /// 一旦发现端口可用、或出现真正的监听进程（等下去也不会变），立即返回当前状态。
    /// </summary>
    public static Task<PortAvailabilityState> WaitForReleaseAsync(
        int port,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken = default) =>
        WaitForReleaseAsync(port, IPAddress.Any, timeout, pollInterval, cancellationToken);

    public static async Task<PortAvailabilityState> WaitForReleaseAsync(
        int port,
        IPAddress listenAddress,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken = default)
    {
        RuntimeValidation.ValidatePort(port);
        ValidateAddress(listenAddress);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pollInterval, TimeSpan.Zero);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var state = Probe(port, listenAddress);
            if (state == PortAvailabilityState.Free ||
                state == PortAvailabilityState.Listening ||
                watch.Elapsed >= timeout)
            {
                return state;
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
