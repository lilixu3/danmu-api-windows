using DanmuApi.Core.Frp;

namespace DanmuApi.Tests;

public sealed class FrpNativeProxyTests
{
    [Fact]
    public void NativeProxyFormattingDoesNotExposeRegisteredUserOrProxyNames()
    {
        var native = FrpNativeConfig.Parse("""
            {"serverAddr":"127.0.0.1","user":"private-account",
             "proxies":[{"name":"private-proxy","type":"tcp","localIP":"127.0.0.1","localPort":9321,"remotePort":19321}]}
            """);
        var proxy = Assert.Single(native.Proxies);

        Assert.Equal("private-account.private-proxy", proxy.Name);
        Assert.Equal("private-proxy", proxy.AdminName);
        Assert.Contains(nameof(FrpNativeProxy), proxy.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-account", proxy.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-proxy", proxy.ToString(), StringComparison.Ordinal);
    }
}
