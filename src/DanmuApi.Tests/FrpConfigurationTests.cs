using System.Runtime.InteropServices;
using System.Text;
using DanmuApi.Core.Frp;

namespace DanmuApi.Tests;

/// <summary>
/// frp 配置生成与校验的测试。
///
/// 期望值来自对 frp v0.71.0 官方包的实测：发行包里的 frpc.toml/frps.toml 样例决定了键名，
/// <c>frpc.exe verify -c</c> 决定了文档里这些键的写法能被接受（实测通过后才固化成断言）。
/// </summary>
public sealed class FrpConfigWriterTests
{
    private static FrpClientSettings Client(
        FrpProxyKind kind = FrpProxyKind.Tcp,
        string address = "frp.example.com",
        IReadOnlyList<string>? domains = null,
        bool encryption = false,
        bool compression = false,
        bool tls = true,
        string user = "") => new(
        ServerAddress: address,
        ServerPort: 7000,
        User: user,
        ProxyName: "danmu-api",
        ProxyKind: kind,
        LocalAddress: "127.0.0.1",
        LocalPort: 9321,
        RemotePort: 19321,
        CustomDomains: domains ?? (kind == FrpProxyKind.Tcp ? [] : ["danmu.example.com"]),
        UseEncryption: encryption,
        UseCompression: compression,
        TransportTls: tls,
        AdminPort: 7400);

    [Fact]
    public void WriteClientEmitsVerifiedTomlShapeForTcp()
    {
        var text = FrpConfigWriter.WriteClient(Client(), "s3cret", "admin", "local-pass");

        Assert.Contains("serverAddr = \"frp.example.com\"", text, StringComparison.Ordinal);
        Assert.Contains("serverPort = 7000", text, StringComparison.Ordinal);
        // loginFailExit=false 是刻意的：默认 true 会让 frpc 在服务器暂时不可达时直接退出。
        Assert.Contains("loginFailExit = false", text, StringComparison.Ordinal);
        Assert.Contains("auth.method = \"token\"", text, StringComparison.Ordinal);
        Assert.Contains("auth.token = \"s3cret\"", text, StringComparison.Ordinal);
        Assert.Contains("log.disablePrintColor = true", text, StringComparison.Ordinal);
        Assert.Contains("transport.tls.enable = true", text, StringComparison.Ordinal);
        Assert.Contains("webServer.addr = \"127.0.0.1\"", text, StringComparison.Ordinal);
        Assert.Contains("webServer.port = 7400", text, StringComparison.Ordinal);
        Assert.Contains("webServer.user = \"admin\"", text, StringComparison.Ordinal);
        Assert.Contains("webServer.password = \"local-pass\"", text, StringComparison.Ordinal);
        Assert.Contains("[[proxies]]", text, StringComparison.Ordinal);
        Assert.Contains("name = \"danmu-api\"", text, StringComparison.Ordinal);
        Assert.Contains("type = \"tcp\"", text, StringComparison.Ordinal);
        Assert.Contains("localIP = \"127.0.0.1\"", text, StringComparison.Ordinal);
        Assert.Contains("localPort = 9321", text, StringComparison.Ordinal);
        Assert.Contains("remotePort = 19321", text, StringComparison.Ordinal);
        Assert.Contains("transport.useEncryption = false", text, StringComparison.Ordinal);
        Assert.Contains("transport.useCompression = false", text, StringComparison.Ordinal);
        // tcp 代理不该出现域名键：那是 http/https 分支的字段。
        Assert.DoesNotContain("customDomains", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteClientEmitsDomainArrayForHttpProxy()
    {
        var text = FrpConfigWriter.WriteClient(
            Client(FrpProxyKind.Http, domains: ["a.example.com", "b.example.com"]),
            string.Empty,
            "admin",
            "local-pass");

        Assert.Contains("type = \"http\"", text, StringComparison.Ordinal);
        Assert.Contains("customDomains = [\"a.example.com\", \"b.example.com\"]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("remotePort", text, StringComparison.Ordinal);
        // 没有 Token 时不该写出空的 auth.token：空串在 frp 里等同于"不校验"，写出来只会误导。
        Assert.DoesNotContain("auth.token", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FrpProxyKind.Tcp)]
    [InlineData(FrpProxyKind.Http)]
    [InlineData(FrpProxyKind.Https)]
    public void EveryProxyKindGeneratesCompressionAndEncryption(FrpProxyKind kind)
    {
        var text = FrpConfigWriter.WriteClient(Client(kind, encryption: true, compression: true), "", "admin", "password");
        Assert.Contains("transport.useCompression = true", text, StringComparison.Ordinal);
        Assert.Contains("transport.useEncryption = true", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteClientEscapesTokensThatWouldOtherwiseBreakToml()
    {
        var text = FrpConfigWriter.WriteClient(Client(), "a\"b\\c\nd\te", "admin", "p");

        Assert.Contains("auth.token = \"a\\\"b\\\\c\\nd\\te\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void QuoteEscapesControlCharacters()
    {
        Assert.Equal("\"a\\u0001b\"", FrpConfigWriter.Quote("a\u0001b"));
    }

    [Fact]
    public void WriteServerEmitsVerifiedTomlShape()
    {
        var text = FrpConfigWriter.WriteServer(
            new FrpServerSettings(BindPort: 7000, VhostHttpPort: 8080, SubdomainHost: "example.com", AdminPort: 7500),
            "s3cret",
            "admin",
            "local-pass");

        Assert.Contains("bindPort = 7000", text, StringComparison.Ordinal);
        Assert.Contains("vhostHTTPPort = 8080", text, StringComparison.Ordinal);
        Assert.Contains("subdomainHost = \"example.com\"", text, StringComparison.Ordinal);
        Assert.Contains("webServer.port = 7500", text, StringComparison.Ordinal);
        Assert.Contains("auth.token = \"s3cret\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteServerOmitsVhostPortWhenDisabled()
    {
        var text = FrpConfigWriter.WriteServer(
            new FrpServerSettings(7000, 0, string.Empty, 7500),
            string.Empty,
            "admin",
            "local-pass");

        Assert.DoesNotContain("vhostHTTPPort", text, StringComparison.Ordinal);
        Assert.DoesNotContain("subdomainHost", text, StringComparison.Ordinal);
    }
}

public sealed class FrpSettingsValidationTests
{
    private static FrpClientSettings ValidClient() => new(
        "frp.example.com", 7000, "", "danmu-api", FrpProxyKind.Tcp, "127.0.0.1", 9321, 19321, [], false, false, true, 7400);

    [Fact]
    public void AcceptsACompleteTcpClientConfiguration()
    {
        Assert.Empty(FrpSettingsValidation.ValidateClient(ValidClient()));
    }

    [Fact]
    public void ReportsMissingServerAddress()
    {
        var problems = FrpSettingsValidation.ValidateClient(ValidClient() with { ServerAddress = " " });

        Assert.Contains(problems, problem => problem.Contains("服务器地址不能为空", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsAddressWithSchemeOrPort()
    {
        var problems = FrpSettingsValidation.ValidateClient(
            ValidClient() with { ServerAddress = "https://frp.example.com:7000" });

        Assert.Contains(problems, problem => problem.Contains("不要带端口、协议或路径", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65_536)]
    public void RejectsPortsOutsideRange(int port)
    {
        var problems = FrpSettingsValidation.ValidateClient(ValidClient() with { RemotePort = port });

        Assert.Contains(problems, problem => problem.Contains("公网端口必须在", StringComparison.Ordinal));
    }

    [Fact]
    public void RequiresDomainForHttpProxy()
    {
        var problems = FrpSettingsValidation.ValidateClient(
            ValidClient() with { ProxyKind = FrpProxyKind.Http, CustomDomains = [] });

        Assert.Contains(problems, problem => problem.Contains("至少需要一个域名", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsDomainCarryingScheme()
    {
        var problems = FrpSettingsValidation.ValidateClient(
            ValidClient() with { ProxyKind = FrpProxyKind.Https, CustomDomains = ["https://danmu.example.com"] });

        Assert.Contains(problems, problem => problem.Contains("域名格式无效", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsAdminPortCollidingWithLocalPort()
    {
        var problems = FrpSettingsValidation.ValidateClient(ValidClient() with { AdminPort = 9321 });

        Assert.Contains(problems, problem => problem.Contains("不能与被穿透的本地服务端口相同", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsProxyNameWithSpaces()
    {
        var problems = FrpSettingsValidation.ValidateClient(ValidClient() with { ProxyName = "danmu api" });

        Assert.Contains(problems, problem => problem.Contains("代理名称只能包含", StringComparison.Ordinal));
    }

    [Fact]
    public void ServerValidationAcceptsZeroVhostPort()
    {
        Assert.Empty(FrpSettingsValidation.ValidateServer(new FrpServerSettings(7000, 0, string.Empty, 7500)));
    }

    [Fact]
    public void ServerValidationRejectsCollidingPorts()
    {
        var problems = FrpSettingsValidation.ValidateServer(new FrpServerSettings(7500, 8080, string.Empty, 7500));

        Assert.Contains(problems, problem => problem.Contains("穿透端口不能与本地状态端口相同", StringComparison.Ordinal));
    }
}

public sealed class FrpReleaseCatalogTests
{
    [Fact]
    public void MapsProcessArchitecturesToPublishedAssetNames()
    {
        Assert.Equal("amd64", FrpReleaseCatalog.ResolveArchitecture(Architecture.X64));
        Assert.Equal("arm64", FrpReleaseCatalog.ResolveArchitecture(Architecture.Arm64));
    }

    [Fact]
    public void RejectsX86WithTheReasonInsteadOfPickingAnOlderRelease()
    {
        var error = Assert.Throws<NotSupportedException>(
            () => FrpReleaseCatalog.ResolveArchitecture(Architecture.X86));

        // 结论必须自带证据：最后一个 32 位版本是什么、为什么不能用，都要说清楚。
        Assert.Contains("v0.52.0", error.Message, StringComparison.Ordinal);
        Assert.Contains("v0.51.3", error.Message, StringComparison.Ordinal);
        Assert.Contains("INI", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.71.0", "0.71.0")]
    [InlineData("v0.71.0", "0.71.0")]
    [InlineData(" v0.52.1 ", "0.52.1")]
    public void NormalizesVersionStrings(string input, string expected)
    {
        Assert.Equal(expected, FrpReleaseCatalog.NormalizeVersion(input));
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("0.71")]
    [InlineData("")]
    public void RejectsUnparsableVersions(string input)
    {
        Assert.ThrowsAny<Exception>(() => FrpReleaseCatalog.NormalizeVersion(input));
    }

    [Fact]
    public void BuildsTheDocumentedDownloadAndChecksumUrls()
    {
        var asset = FrpReleaseCatalog.ForVersion("v0.71.0", Architecture.X64);

        Assert.Equal("frp_0.71.0_windows_amd64.zip", asset.FileName);
        Assert.Equal(
            "https://github.com/fatedier/frp/releases/download/v0.71.0/frp_0.71.0_windows_amd64.zip",
            asset.DownloadUri.ToString());
        Assert.Equal(
            "https://github.com/fatedier/frp/releases/download/v0.71.0/frp_sha256_checksums.txt",
            asset.ChecksumsUri.ToString());
    }
}

public sealed class FrpSha256ChecksumsTests
{
    // 取自 v0.71.0 官方 frp_sha256_checksums.txt 的真实一行（本地实测比对通过）。
    private const string OfficialLine =
        "9e5062e3e5cf07e67144a3a4acf175ef6a2486f3605dd6cf288bae34ab39819f  frp_0.71.0_windows_amd64.zip";

    [Fact]
    public void ParsesOfficialChecksumLines()
    {
        var checksums = FrpSha256Checksums.Parse(OfficialLine + "\n" + new string('a', 64) + " *other.zip\n", "test");

        Assert.Equal(2, checksums.Count);
        Assert.Equal(
            "9e5062e3e5cf07e67144a3a4acf175ef6a2486f3605dd6cf288bae34ab39819f",
            checksums.Require("frp_0.71.0_windows_amd64.zip", "test"));
    }

    [Fact]
    public void FailsWhenTheWantedFileIsMissingFromTheManifest()
    {
        var checksums = FrpSha256Checksums.Parse(OfficialLine, "test");

        var error = Assert.Throws<IOException>(() => checksums.Require("frp_0.71.0_windows_arm64.zip", "test"));

        Assert.Contains("没有 frp_0.71.0_windows_arm64.zip", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FailsOnMalformedLinesInsteadOfSkippingThem()
    {
        Assert.Throws<FormatException>(() => FrpSha256Checksums.Parse("not-a-hash  file.zip", "test"));
        Assert.Throws<FormatException>(
            () => FrpSha256Checksums.Parse(new string('a', 64) + " file.zip  extra", "test"));
        Assert.Throws<FormatException>(() => FrpSha256Checksums.Parse(string.Empty, "test"));
    }

    [Fact]
    public async Task VerifiesRealFileHashes()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "payload.bin");
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("danmu"));

        var expected = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("danmu"))).ToLowerInvariant();
        Assert.Null(await FrpSha256Checksums.VerifyFileAsync(path, expected));

        var mismatch = await FrpSha256Checksums.VerifyFileAsync(path, new string('0', 64));
        Assert.NotNull(mismatch);
        Assert.Contains("SHA256 不匹配", mismatch!, StringComparison.Ordinal);
    }
}
