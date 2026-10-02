using DanmuApi.Core.Frp;

namespace DanmuApi.Tests;

/// <summary>
/// frp 管理接口解析测试。所有 JSON 报文都是在本机用 frp v0.71.0 实测抓下来的原文
/// （frps + frpc + 一个本地 TCP 后端跑通后直接读 <c>/api/status</c>），不是照着文档编的。
/// </summary>
public sealed class FrpAdminStatusReaderTests
{
    private const string RunningPayload =
        """{"http":[{"name":"danmu-api-http","type":"http","status":"running","err":"","local_addr":"127.0.0.1:9321","plugin":"","remote_addr":"danmu.example.com:8080"}],"tcp":[{"name":"danmu-api","type":"tcp","status":"running","err":"","local_addr":"127.0.0.1:9321","plugin":"","remote_addr":"127.0.0.1:19321"}]}""";

    private const string PortConflictPayload =
        """{"tcp":[{"name":"badtarget","type":"tcp","status":"running","err":"","local_addr":"127.0.0.1:65530","plugin":"","remote_addr":"127.0.0.1:19322"},{"name":"conflict","type":"tcp","status":"start error","err":"port already used","local_addr":"127.0.0.1:9321","plugin":"","remote_addr":""}]}""";

    private const string ServerInfoPayload =
        """{"version":"0.71.0","bindPort":7999,"vhostHTTPPort":8080,"vhostHTTPSPort":0,"tcpmuxHTTPConnectPort":0,"kcpBindPort":0,"quicBindPort":0,"subdomainHost":"","maxPoolCount":5,"maxPortsPerClient":0,"heartbeatTimeout":-1,"totalTrafficIn":4,"totalTrafficOut":13,"curConns":0,"clientCounts":1,"proxyTypeCount":{"http":1,"tcp":1}}""";

    [Fact]
    public void ParsesTheRunningProxyPayloadCapturedFromARealTunnel()
    {
        var proxies = FrpAdminStatusReader.ParseClientStatus(RunningPayload);

        Assert.Equal(2, proxies.Count);
        var tcp = proxies.Single(proxy => proxy.Name == "danmu-api");
        Assert.Equal("tcp", tcp.Type);
        Assert.Equal("running", tcp.Status);
        Assert.Equal("127.0.0.1:9321", tcp.LocalAddress);
        Assert.Equal("127.0.0.1:19321", tcp.RemoteAddress);
        Assert.Equal(FrpProxyState.Running, tcp.State);
        Assert.True(tcp.IsRunning);

        var http = proxies.Single(proxy => proxy.Name == "danmu-api-http");
        Assert.Equal("danmu.example.com:8080", http.RemoteAddress);
    }

    [Fact]
    public void ReadsTheEmptyObjectAsNoProxiesInsteadOfFailing()
    {
        // 实测：frpc 未登录服务器时 /api/status 返回 {}。这是合法响应，代表代理还没被创建。
        Assert.Empty(FrpAdminStatusReader.ParseClientStatus("{}"));
    }

    [Fact]
    public void SurfacesTheStartErrorWithTheReasonFrpGave()
    {
        var proxies = FrpAdminStatusReader.ParseClientStatus(PortConflictPayload);

        var conflict = proxies.Single(proxy => proxy.Name == "conflict");
        Assert.Equal(FrpProxyState.StartError, conflict.State);
        Assert.Equal("port already used", conflict.Error);
        Assert.Contains("port already used", conflict.Describe(), StringComparison.Ordinal);
        Assert.Empty(conflict.RemoteAddress);
    }

    [Fact]
    public void KeepsUnknownStatusTextVisibleRatherThanGuessingRunning()
    {
        var payload =
            """{"tcp":[{"name":"x","type":"tcp","status":"quarantined","err":"","local_addr":"","plugin":"","remote_addr":""}]}""";

        var proxy = Assert.Single(FrpAdminStatusReader.ParseClientStatus(payload));

        Assert.Equal(FrpProxyState.Unknown, proxy.State);
        Assert.False(proxy.IsRunning);
        Assert.Contains("quarantined", proxy.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsPayloadsMissingRequiredFieldsWithTheRawBody()
    {
        var payload = """{"tcp":[{"name":"x","type":"tcp","status":"running"}]}""";

        var error = Assert.Throws<IOException>(() => FrpAdminStatusReader.ParseClientStatus(payload));

        Assert.Contains("缺少字段", error.Message, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"x\"", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsNonNullStringProxyFields()
    {
        var payload =
            """{"tcp":[{"name":"x","type":"tcp","status":"running","err":"","local_addr":"","plugin":null,"remote_addr":""}]}""";

        var error = Assert.Throws<IOException>(() => FrpAdminStatusReader.ParseClientStatus(payload));

        Assert.Contains("plugin", error.Message, StringComparison.Ordinal);
        Assert.Contains("不是字符串", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"running\"")]
    [InlineData("not json")]
    [InlineData("{\"tcp\":{\"name\":\"x\"}}")]
    public void RejectsPayloadsThatAreNotAnObjectOfArrays(string payload)
    {
        Assert.Throws<IOException>(() => FrpAdminStatusReader.ParseClientStatus(payload));
    }

    [Fact]
    public void ParsesServerInfoCapturedFromARealFrps()
    {
        var info = FrpAdminStatusReader.ParseServerInfo(ServerInfoPayload);

        Assert.Equal("0.71.0", info.Version);
        Assert.Equal(7999, info.BindPort);
        Assert.Equal(8080, info.VhostHttpPort);
        Assert.Equal(0, info.VhostHttpsPort);
        Assert.Equal(1, info.ClientCounts);
    }

    [Fact]
    public void RejectsServerInfoWithMissingOrMistypedFields()
    {
        Assert.Throws<IOException>(() => FrpAdminStatusReader.ParseServerInfo("""{"version":"0.71.0"}"""));
        Assert.Throws<IOException>(
            () => FrpAdminStatusReader.ParseServerInfo(
                """{"version":"0.71.0","bindPort":"7000","vhostHTTPPort":0,"vhostHTTPSPort":0,"clientCounts":0}"""));
    }
}

public sealed class FrpLatestReleaseTests
{
    // 实测响应（截取）：v0.71.0 的资产列表里只有 windows_amd64 与 windows_arm64。
    private const string Payload =
        """
        {"tag_name":"v0.71.0","assets":[{"name":"frp_0.71.0_linux_amd64.tar.gz"},{"name":"frp_0.71.0_windows_amd64.zip"},{"name":"frp_0.71.0_windows_arm64.zip"},{"name":"frp_sha256_checksums.txt"}]}
        """;

    [Fact]
    public void ParsesTagAndWindowsAssets()
    {
        var release = FrpReleaseDiscovery.Parse(Payload);

        Assert.Equal("0.71.0", release.Version);
        Assert.True(release.HasAssetFor("amd64"));
        Assert.True(release.HasAssetFor("arm64"));
        Assert.False(release.HasAssetFor("386"));
        Assert.Equal(2, release.WindowsAssets.Count);
    }

    [Fact]
    public void SaysWhatIsAvailableWhenTheAssetIsMissing()
    {
        var release = FrpReleaseDiscovery.Parse("""{"tag_name":"v0.51.3","assets":[]}""");

        Assert.False(release.HasAssetFor("amd64"));
        Assert.Contains("没有任何 Windows 资产", release.DescribeAvailableAssets(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"assets":[]}""")]
    [InlineData("""{"tag_name":"latest","assets":[]}""")]
    [InlineData("[]")]
    public void RejectsResponsesWithoutAUsableVersion(string payload)
    {
        Assert.Throws<IOException>(() => FrpReleaseDiscovery.Parse(payload));
    }
}
