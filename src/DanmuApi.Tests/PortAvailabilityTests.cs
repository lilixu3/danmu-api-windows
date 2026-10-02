using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class PortAvailabilityTests
{
    [Fact]
    public void ProbeReportsFreeForAnUnusedPort()
    {
        var port = TestPorts.FreePort();

        Assert.Equal(PortAvailabilityState.Free, PortAvailability.Probe(port));
        Assert.True(PortAvailability.IsBindable(port));
        Assert.False(PortAvailability.HasListener(port));
    }

    [Fact]
    public void ProbeReportsListeningForAListeningSocket()
    {
        using var listener = TestPorts.Listen(out var port);

        Assert.Equal(PortAvailabilityState.Listening, PortAvailability.Probe(port));
        Assert.True(PortAvailability.HasListener(port));
    }

    [Fact]
    public void ProbeReportsUnbindableForBoundButNotListeningSocket()
    {
        using var squat = new PortSquat();

        Assert.False(PortAvailability.IsBindable(squat.Port));
        Assert.False(PortAvailability.HasListener(squat.Port));
        Assert.Equal(PortAvailabilityState.Unbindable, PortAvailability.Probe(squat.Port));
    }

    /// <summary>
    /// 回归用例（真机事故）：Go 写的 frp 监听 127.0.0.1:P 时，<c>TcpListener(Any, P).Start()</c>
    /// 在 Windows 上**照样能成功**（非独占绑定可与已存在的监听者共存），于是"试绑成功"被误判成空闲：
    /// 预检放行、第二个 frpc 起不来，而状态查询读到的却是前一个进程的管理接口。
    /// 这里造一个不带 SO_EXCLUSIVEADDRUSE 的监听者复现该行为：**有人监听就必须报 Listening**。
    /// </summary>
    [Fact]
    public void ProbeReportsListeningEvenWhenANonExclusiveBindWouldSucceed()
    {
        using var holder = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            // 与 frpc(Go) 一致：普通监听，不要求独占。
            ExclusiveAddressUse = false,
        };
        holder.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        holder.Listen(1);
        var port = ((IPEndPoint)holder.LocalEndPoint!).Port;

        // 前提：这种监听者确实挡不住一次普通绑定（否则这条用例就退化成上面那条了）。
        Assert.True(PortAvailability.IsBindable(port));
        Assert.True(PortAvailability.HasListener(port));

        Assert.Equal(PortAvailabilityState.Listening, PortAvailability.Probe(port));
    }

    [Fact]
    public async Task WaitForReleaseReturnsFreeOnceTheTemporaryHolderGoesAway()
    {
        var squat = new PortSquat();
        var port = squat.Port;
        _ = Task.Run(async () =>
        {
            await Task.Delay(300).ConfigureAwait(false);
            squat.Dispose();
        });

        try
        {
            var state = await PortAvailability.WaitForReleaseAsync(port, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(50));

            Assert.Equal(PortAvailabilityState.Free, state);
        }
        finally
        {
            squat.Dispose();
        }
    }

    [Fact]
    public async Task WaitForReleaseReturnsListeningWithoutBurningTheWholeTimeout()
    {
        using var listener = TestPorts.Listen(out var port);
        var watch = Stopwatch.StartNew();

        var state = await PortAvailability.WaitForReleaseAsync(port, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(50));

        Assert.Equal(PortAvailabilityState.Listening, state);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"监听者不会自行消失，不该等到超时：{watch.Elapsed}");
    }

    [Fact]
    public async Task WaitForReleaseReturnsUnbindableAfterTimeout()
    {
        using var squat = new PortSquat();
        var watch = Stopwatch.StartNew();

        var state = await PortAvailability.WaitForReleaseAsync(squat.Port, TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(50));

        Assert.Equal(PortAvailabilityState.Unbindable, state);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(400), $"应至少等待给定超时：{watch.Elapsed}");
    }

    [Fact]
    public async Task WaitForReleaseRejectsNonPositiveTimeout()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => PortAvailability.WaitForReleaseAsync(1, TimeSpan.Zero, TimeSpan.FromMilliseconds(50)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => PortAvailability.WaitForReleaseAsync(1, TimeSpan.FromSeconds(1), TimeSpan.Zero));
    }

    [Fact]
    public void IPv6OnlyLoopbackListenerDoesNotBlockIPv4()
    {
        using var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Server.DualMode = false;
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.True(PortAvailability.HasListener(port));
        Assert.True(PortAvailability.IsBindable(port));
        Assert.Equal(PortAvailabilityState.Free, PortAvailability.Probe(port));
        Assert.Equal(PortAvailabilityState.Listening, PortAvailability.Probe(port, IPAddress.IPv6Loopback));
    }

    [Fact]
    public void IPv6OnlyWildcardListenerDoesNotBlockIPv4()
    {
        using var listener = new TcpListener(IPAddress.IPv6Any, 0);
        listener.Server.DualMode = false;
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.True(PortAvailability.IsBindable(port));
        Assert.Equal(PortAvailabilityState.Free, PortAvailability.Probe(port));
    }

    [Fact]
    public void DualStackListenerBlocksIPv4()
    {
        using var listener = new TcpListener(IPAddress.IPv6Any, 0);
        listener.Server.DualMode = true;
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.Equal(PortAvailabilityState.Listening, PortAvailability.Probe(port));
        Assert.Equal(PortAvailabilityState.Listening, PortAvailability.Probe(port, IPAddress.Loopback));
    }

    [Fact]
    public void TargetAddressDoesNotConflictWithAnotherIPv4Interface()
    {
        using var listener = new TcpListener(IPAddress.Parse("127.0.0.2"), 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.Equal(PortAvailabilityState.Listening, PortAvailability.Probe(port));
        Assert.Equal(PortAvailabilityState.Free, PortAvailability.Probe(port, IPAddress.Loopback));
    }

    [Fact]
    public async Task IPv6OnlyListenerDoesNotDelayIPv4ReleaseCheck()
    {
        using var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Server.DualMode = false;
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.Equal(PortAvailabilityState.Free,
            await PortAvailability.WaitForReleaseAsync(port, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(20)));
    }

    [Fact]
    public void ProbeRejectsPortsOutsideTheValidRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PortAvailability.Probe(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PortAvailability.Probe(65_536));
    }
}
