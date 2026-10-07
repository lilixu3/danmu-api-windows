using DanmuApi.App.Services;

namespace DanmuApi.Tests;

public sealed class SingleShotGateTests
{
    [Fact]
    public void OnlyTheFirstCallIsAllowed()
    {
        var gate = new SingleShotGate();
        Assert.False(gate.Entered);
        Assert.True(gate.TryEnter());
        Assert.True(gate.Entered);
        // 主窗口再被打开（托盘隐藏后显示）时不能重新放行更新后回执与服务恢复。
        Assert.False(gate.TryEnter());
        Assert.False(gate.TryEnter());
    }
}
