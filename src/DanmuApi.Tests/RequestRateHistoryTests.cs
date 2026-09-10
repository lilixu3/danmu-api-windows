using DanmuApi.App.ViewModels;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class RequestRateHistoryTests
{
    private static RuntimeHealthSnapshot Health(long? count, string? identity = "instance", long? pid = 42) =>
        new(pid, "24", 0, "0.0.0.0", 9321, null, null, null, null, null, true, "stable", null,
            identity, count, null, null, null, null, null, null, null);

    [Fact]
    public void CounterDeltaUsesElapsedSecondsAndRealZeroOnlyAfterBaseline()
    {
        var trend = new RequestRateHistory();
        trend.Sample(Health(100), 10);
        Assert.Null(trend.CurrentRate);
        Assert.Equal("—", trend.RateText);
        trend.Sample(Health(120), 15);
        Assert.Equal(4d, trend.CurrentRate);
        trend.Sample(Health(135), 22.5);
        Assert.Equal(2d, trend.CurrentRate);
        trend.Sample(Health(135), 27.5);
        Assert.Equal(0d, trend.CurrentRate);
    }

    [Fact]
    public void FailuresBreakHistoryAndRecoveryRequiresTwoSamples()
    {
        var trend = new RequestRateHistory();
        trend.Sample(Health(1), 0);
        trend.Sample(Health(11), 5);
        trend.Disconnect(10);
        Assert.Null(trend.CurrentRate);
        Assert.Null(trend.Points[^1].Rate);
        trend.Sample(Health(31), 15);
        Assert.Null(trend.CurrentRate);
        trend.Sample(Health(41), 20);
        Assert.Equal(2d, trend.CurrentRate);
        Assert.Equal(20, trend.Points[^1].Seconds);
    }

    [Theory]
    [InlineData(50, "other", 42)]
    [InlineData(50, "instance", 43)]
    [InlineData(1, "instance", 42)]
    public void ChangedIdentityPidOrRollbackEstablishesNewBaseline(long count, string identity, long pid)
    {
        var trend = new RequestRateHistory();
        trend.Sample(Health(20), 0);
        trend.Sample(Health(count, identity, pid), 5);
        Assert.Null(trend.CurrentRate);
        trend.Sample(Health(count + 10, identity, pid), 10);
        Assert.Equal(2d, trend.CurrentRate);
    }

    [Theory]
    [InlineData(null, "instance", 42L)]
    [InlineData(-1L, "instance", 42L)]
    [InlineData(10L, null, 42L)]
    [InlineData(10L, "instance", null)]
    public void MissingDataIsNeverReportedAsZero(long? count, string? identity, long? pid)
    {
        var trend = new RequestRateHistory();
        trend.Sample(Health(1), 0);
        trend.Sample(Health(count, identity, pid), 5);
        Assert.Null(trend.CurrentRate);
        Assert.Contains("无数据", trend.Status);
        trend.Sample(Health(30), 10);
        Assert.Null(trend.CurrentRate);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-5d)]
    [InlineData(30d)]
    public void InvalidTimingOrLongPauseBreaksTheCurve(double time)
    {
        var trend = new RequestRateHistory();
        trend.Sample(Health(1), 0);
        trend.Sample(Health(20), time);
        Assert.Null(trend.CurrentRate);
        trend.Sample(Health(30), time + 5);
        Assert.Equal(2d, trend.CurrentRate);
    }

    [Fact]
    public void HistoryIsBoundedAndStopClearsBaseline()
    {
        var trend = new RequestRateHistory();
        for (var i = 0; i < 100; i++) trend.Sample(Health(i), i * 5);
        Assert.Equal(RequestRateHistory.Capacity, trend.Points.Count);
        Assert.Equal(200, trend.Points[0].Seconds);
        trend.Reset();
        Assert.Empty(trend.Points);
        Assert.Null(trend.CurrentRate);
        trend.Sample(Health(1000), 600);
        Assert.Null(trend.CurrentRate);
    }
}
