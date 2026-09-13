using System.Diagnostics;
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
    public void ProbeRejectsPortsOutsideTheValidRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PortAvailability.Probe(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PortAvailability.Probe(65_536));
    }
}
