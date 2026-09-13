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
    public static PortAvailabilityState Probe(int port)
    {
        RuntimeValidation.ValidatePort(port);
        if (IsBindable(port))
        {
            return PortAvailabilityState.Free;
        }

        return HasListener(port) ? PortAvailabilityState.Listening : PortAvailabilityState.Unbindable;
    }

    /// <summary>以真实绑定探测端口是否可用。被本机任意套接字（含临时源端口）占用时返回 false。</summary>
    public static bool IsBindable(int port)
    {
        RuntimeValidation.ValidatePort(port);
        try
        {
            using var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>端口上是否存在监听套接字。仅 bind 未 listen 的临时源端口不会出现在这里。</summary>
    public static bool HasListener(int port)
    {
        RuntimeValidation.ValidatePort(port);
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == port);
        }
        catch (NetworkInformationException error)
        {
            throw new IOException($"读取系统 TCP 监听表失败（port={port}）：{error.Message}", error);
        }
    }

    /// <summary>
    /// 等待端口从 <see cref="PortAvailabilityState.Unbindable"/> 自行释放。
    /// 一旦发现端口可用、或出现真正的监听进程（等下去也不会变），立即返回当前状态。
    /// </summary>
    public static async Task<PortAvailabilityState> WaitForReleaseAsync(
        int port,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pollInterval, TimeSpan.Zero);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var state = Probe(port);
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
