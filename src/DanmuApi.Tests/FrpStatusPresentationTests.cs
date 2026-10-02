using DanmuApi.App.Services;
using DanmuApi.Core.Frp;

namespace DanmuApi.Tests;

/// <summary>概览页穿透卡片的文案推导（纯函数），覆盖"通没通 / 地址是什么 / 该提醒什么"。</summary>
public sealed class FrpStatusPresentationTests
{
    private static FrpSurfaceInput Input(
        FrpTunnelState state = FrpTunnelState.Stopped,
        string? remote = null,
        string? diagnostic = null,
        FrpRole role = FrpRole.Client,
        FrpProxyKind proxyKind = FrpProxyKind.Tcp,
        int bindPort = 7000,
        bool installed = true,
        bool serviceRunning = true,
        string token = "87654321") =>
        new(state, remote, diagnostic, role, proxyKind, bindPort, installed, serviceRunning, token);

    [Theory]
    [InlineData(FrpTunnelState.Stopped, "未启动")]
    [InlineData(FrpTunnelState.Starting, "正在连接服务器")]
    [InlineData(FrpTunnelState.Running, "穿透正常")]
    [InlineData(FrpTunnelState.Reconnecting, "连接中断，重连中")]
    [InlineData(FrpTunnelState.Stopping, "正在停止")]
    [InlineData(FrpTunnelState.Failed, "穿透失败")]
    public void MapsEveryStateToAnExplicitLabel(FrpTunnelState state, string expected)
    {
        Assert.Equal(expected, FrpStatusPresentation.StatusText(Input(state)));
    }

    [Fact]
    public void ShowsTheAddressOnlyWhileRunning()
    {
        // 地址必须是可直接访问的 API 链接：与局域网地址同形，且带上核心的 Token。
        Assert.Equal(
            "http://203.0.113.10:19321/87654321",
            FrpStatusPresentation.AddressText(Input(FrpTunnelState.Running, "203.0.113.10:19321")));
        Assert.True(FrpStatusPresentation.HasAddress(Input(FrpTunnelState.Running, "203.0.113.10:19321")));
        Assert.True(FrpStatusPresentation.IsCopyableAddress(Input(FrpTunnelState.Running, "203.0.113.10:19321")));

        // 重启中/失败时地址必须消失：留着上一个地址会让人以为还能访问。
        Assert.Equal(string.Empty, FrpStatusPresentation.AddressText(Input(FrpTunnelState.Reconnecting, "203.0.113.10:19321")));
        Assert.False(FrpStatusPresentation.HasAddress(Input(FrpTunnelState.Failed, "203.0.113.10:19321")));
    }

    [Fact]
    public void HttpDomainProxyCarriesTheTokenAndScheme()
    {
        var input = Input(
            FrpTunnelState.Running,
            "danmu.example.com:8080",
            proxyKind: FrpProxyKind.Http,
            token: "s3cret");

        Assert.Equal("http://danmu.example.com:8080/s3cret", FrpStatusPresentation.AddressText(input));
        Assert.Equal(
            "https://danmu.example.com/abc",
            FrpStatusPresentation.AddressText(input with { ProxyKind = FrpProxyKind.Https, RemoteAddress = "danmu.example.com:443", CoreToken = "abc" }));
    }

    [Fact]
    public void AddressWithoutATokenStillEndsWithASlashInsteadOfFakingOne()
    {
        var input = Input(FrpTunnelState.Running, "203.0.113.10:19321", token: "  ");

        Assert.Equal("http://203.0.113.10:19321/", FrpStatusPresentation.AddressText(input));
    }

    [Fact]
    public void RunningWithoutRemoteAddressShowsNoAddressRatherThanInventingOne()
    {
        var input = Input(FrpTunnelState.Running, remote: null);

        Assert.False(FrpStatusPresentation.HasAddress(input));
        Assert.Equal(string.Empty, FrpStatusPresentation.AddressText(input));
    }

    [Fact]
    public void ServerRoleDescribesTheListeningPortInsteadOfAFabricatedPublicAddress()
    {
        var input = Input(FrpTunnelState.Running, remote: null, role: FrpRole.Server, bindPort: 7200);

        var text = FrpStatusPresentation.AddressText(input);

        Assert.Contains("7200", text, StringComparison.Ordinal);
        Assert.Contains("需公网 IP 或端口映射", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticWinsOverTheGenericHint()
    {
        var input = Input(FrpTunnelState.Failed, diagnostic: "代理 danmu-api 启动失败：port already used");

        Assert.Equal("代理 danmu-api 启动失败：port already used", FrpStatusPresentation.HintText(input));
    }

    [Fact]
    public void NotInstalledIsCalledOutEvenWhenStopped()
    {
        var input = Input(FrpTunnelState.Stopped, installed: false);

        Assert.Contains("尚未安装 frp", FrpStatusPresentation.HintText(input), StringComparison.Ordinal);
    }

    [Fact]
    public void WarnsWhenTheTunnelRunsButTheDanmuServiceDoesNot()
    {
        Assert.True(FrpStatusPresentation.ShowServiceWarning(Input(FrpTunnelState.Running, serviceRunning: false)));
        Assert.Contains(
            "弹幕服务当前未运行",
            FrpStatusPresentation.HintText(Input(FrpTunnelState.Running, serviceRunning: false)),
            StringComparison.Ordinal);

        Assert.False(FrpStatusPresentation.ShowServiceWarning(Input(FrpTunnelState.Running, serviceRunning: true)));
        Assert.False(FrpStatusPresentation.ShowServiceWarning(Input(FrpTunnelState.Stopped, serviceRunning: false)));
        // 服务端模式下弹幕服务起没起与穿透无关，不该提醒。
        Assert.False(FrpStatusPresentation.ShowServiceWarning(
            Input(FrpTunnelState.Running, role: FrpRole.Server, serviceRunning: false)));
    }
}
