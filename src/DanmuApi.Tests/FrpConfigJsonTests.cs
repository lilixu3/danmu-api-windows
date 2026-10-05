using DanmuApi.Core.Frp;

namespace DanmuApi.Tests;

/// <summary>
/// frp 原生 JSON 配置的导出/导入。
/// 导出物必须**能被官方 frpc 直接用**（这一条由集成测试里真实的 `frpc verify -c frpc.json` 把关），
/// 导入必须严格：认得出但不管理的字段要逐条列出来，绝不静默丢弃。
/// </summary>
public sealed class FrpConfigJsonTests
{
    private static FrpSettings ClientSettings() => FrpSettings.Default(9321) with
    {
        InstalledVersion = "0.71.0",
        Client = FrpSettings.Default(9321).Client with
        {
            ServerAddress = "frp.example.com",
            ServerPort = 7100,
            User = "panel-user",
            ProxyName = "my-danmu",
            LocalPort = 9321,
            RemotePort = 19321,
            UseCompression = true,
            TransportTls = false,
        },
    };

    private static FrpSettings ServerSettings() => FrpSettings.Default(9321) with
    {
        Role = FrpRole.Server,
        InstalledVersion = "0.71.0",
        Server = new FrpServerSettings(BindPort: 7200, VhostHttpPort: 8080, SubdomainHost: "example.com", AdminPort: 7501),
    };

    [Fact]
    public void ExportsFrpNativeClientDocument()
    {
        var json = FrpConfigJson.Export(ClientSettings(), "server-token");

        Assert.Contains("\"serverAddr\": \"frp.example.com\"", json, StringComparison.Ordinal);
        Assert.Contains("\"serverPort\": 7100", json, StringComparison.Ordinal);
        // 服务商面板的 user 是配置的一部分（frpc 用它登记 {user}.{proxy}），必须跟着走。
        Assert.Contains("\"user\": \"panel-user\"", json, StringComparison.Ordinal);
        Assert.Contains("\"token\": \"server-token\"", json, StringComparison.Ordinal);
        Assert.Contains("\"loginFailExit\": false", json, StringComparison.Ordinal);
        Assert.Contains("\"disablePrintColor\": true", json, StringComparison.Ordinal);
        Assert.Contains("\"enable\": false", json, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"my-danmu\"", json, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"tcp\"", json, StringComparison.Ordinal);
        Assert.Contains("\"remotePort\": 19321", json, StringComparison.Ordinal);
        Assert.Contains("\"useCompression\": true", json, StringComparison.Ordinal);
        // 本地管理接口只导出地址与端口；随机密码/用户名只属于这台机器，
        // 不该跟着一份会被粘贴、会被发给别人的文本出去。
        Assert.Contains("\"webServer\"", json, StringComparison.Ordinal);
        Assert.Contains("\"addr\": \"127.0.0.1\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"password\"", json, StringComparison.Ordinal);
        // 整份文档里只能有一处 user：客户端那个，绝不是 webServer.user。
        Assert.Equal(1, json.Split("\"user\":", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void ExportsFrpNativeServerDocument()
    {
        var json = FrpConfigJson.Export(ServerSettings(), "server-token");

        Assert.Contains("\"bindPort\": 7200", json, StringComparison.Ordinal);
        Assert.Contains("\"vhostHTTPPort\": 8080", json, StringComparison.Ordinal);
        Assert.Contains("\"subdomainHost\": \"example.com\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("proxies", json, StringComparison.Ordinal);
    }

    [Fact]
    public void RoundTripsThroughImportWithoutLosingAnythingItManages()
    {
        var exported = FrpConfigJson.Export(ClientSettings(), "server-token");

        var imported = FrpConfigJson.Import(exported, FrpSettings.Default(9321));

        Assert.True(imported.Succeeded, string.Join("；", imported.Problems));
        Assert.Empty(imported.Unsupported);
        // TCP documents omit the inactive domain field; replacing the role explicitly clears it.
        Assert.Contains(imported.AppliedDefaults, entry => entry.Contains("customDomains", StringComparison.Ordinal));
        Assert.Equal("server-token", imported.Token);
        var settings = imported.Settings!;
        Assert.Equal(FrpRole.Client, settings.Role);
        Assert.Equal("frp.example.com", settings.Client.ServerAddress);
        Assert.Equal(7100, settings.Client.ServerPort);
        Assert.Equal("panel-user", settings.Client.User);
        Assert.Equal("my-danmu", settings.Client.ProxyName);
        Assert.Equal(19321, settings.Client.RemotePort);
        Assert.True(settings.Client.UseCompression);
        Assert.False(settings.Client.TransportTls);
    }

    [Fact]
    public void ReportsTheDefaultsItFilledInInsteadOfSneakingThemIn()
    {
        var imported = FrpConfigJson.Import(
            """{"serverAddr":"1.2.3.4","proxies":[{"type":"tcp","localPort":9321,"remotePort":19321}]}""",
            FrpSettings.Default(9321));

        Assert.True(imported.Succeeded, string.Join("；", imported.Problems));
        Assert.Equal(FrpClientSettings.DefaultServerPort, imported.Settings!.Client.ServerPort);
        Assert.Equal(FrpClientSettings.DefaultLocalAddress, imported.Settings.Client.LocalAddress);
        // 缺省值必须逐条说明"是我们补的"。
        Assert.Contains(imported.AppliedDefaults, entry => entry.Contains("serverPort", StringComparison.Ordinal));
        Assert.Contains(imported.AppliedDefaults, entry => entry.Contains("proxies[0].localIP", StringComparison.Ordinal));
        Assert.Contains(imported.AppliedDefaults, entry => entry.Contains("proxies[0].name", StringComparison.Ordinal));
    }

    [Fact]
    public void RoundTripsTheServerDocumentToo()
    {
        var exported = FrpConfigJson.Export(ServerSettings(), "t");

        var imported = FrpConfigJson.Import(exported, FrpSettings.Default(9321));

        Assert.True(imported.Succeeded, string.Join("；", imported.Problems));
        Assert.Equal(FrpRole.Server, imported.Settings!.Role);
        Assert.Equal(7200, imported.Settings.Server.BindPort);
        Assert.Equal(8080, imported.Settings.Server.VhostHttpPort);
        Assert.Equal("example.com", imported.Settings.Server.SubdomainHost);
    }

    [Fact]
    public void ImportsDomainProxyWithDomains()
    {
        var imported = FrpConfigJson.Import(
            """
            {
              "serverAddr": "1.2.3.4",
              "serverPort": 7000,
              "proxies": [
                { "name": "danmu", "type": "https", "localIP": "127.0.0.1", "localPort": 9321,
                  "customDomains": ["a.example.com", "b.example.com"] }
              ]
            }
            """,
            FrpSettings.Default(9321));

        Assert.True(imported.Succeeded, string.Join("；", imported.Problems));
        Assert.Equal(FrpProxyKind.Https, imported.Settings!.Client.ProxyKind);
        Assert.Equal(["a.example.com", "b.example.com"], imported.Settings.Client.CustomDomains);
    }

    [Theory]
    [InlineData("", "粘贴内容为空")]
    [InlineData("not json", "不是合法的 JSON")]
    [InlineData("[]", "根节点必须是一个对象")]
    [InlineData("""{"foo": 1}""", "既没有 bindPort")]
    [InlineData("""{"bindPort": 7000, "serverAddr": "1.2.3.4"}""", "无法判断是客户端还是服务端")]
    [InlineData("""{"serverAddr": 12345}""", "serverAddr 必须是字符串")]
    [InlineData("""{"serverAddr": "1.2.3.4", "proxies": []}""", "至少一个 proxies")]
    [InlineData("""{"serverAddr": "1.2.3.4", "proxies": [{"name":"x","type":"socks","localPort":1}]}""", "type 取值无效")]
    public void RejectsDocumentsItCannotUseTrustworthily(string json, string expectedProblem)
    {
        var imported = FrpConfigJson.Import(json, FrpSettings.Default(9321));

        Assert.False(imported.Succeeded);
        Assert.Contains(imported.Problems, problem => problem.Contains(expectedProblem, StringComparison.Ordinal));
    }

    [Fact]
    public void RefusesToPointTheLocalAdminInterfaceAwayFromLoopback()
    {
        var imported = FrpConfigJson.Import(
            """{"serverAddr":"1.2.3.4","webServer":{"addr":"0.0.0.0","port":7400},"proxies":[{"name":"x","type":"tcp","localPort":1,"remotePort":2}]}""",
            FrpSettings.Default(9321));

        Assert.False(imported.Succeeded);
        Assert.Contains(imported.Problems, problem => problem.Contains("webServer.addr 只能是 127.0.0.1", StringComparison.Ordinal));
    }

    [Fact]
    public void ListsUnsupportedFieldsInsteadOfDroppingThemSilently()
    {
        var imported = FrpConfigJson.Import(
            """
            {
              "serverAddr": "1.2.3.4",
              "serverPort": 7000,
              "dnsServer": "1.1.1.1",
              "log": { "level": "debug", "disablePrintColor": true },
              "webServer": { "password": "theirs" },
              "proxies": [
                { "name": "x", "type": "tcp", "localIP": "127.0.0.1", "localPort": 9321, "remotePort": 2,
                  "healthCheck": { "type": "tcp" } }
              ]
            }
            """,
            FrpSettings.Default(9321));

        Assert.True(imported.Succeeded, string.Join("；", imported.Problems));
        Assert.Contains("dnsServer", imported.Unsupported);
        Assert.Contains("log.level", imported.Unsupported);
        Assert.Contains("webServer.password", imported.Unsupported);
        Assert.Contains("proxies[0].healthCheck", imported.Unsupported);
        // 我们管理的字段不该出现在"未导入"清单里。
        Assert.DoesNotContain("serverAddr", imported.Unsupported);
        Assert.DoesNotContain("proxies[0].remotePort", imported.Unsupported);
    }

    [Fact]
    public void ImportedValuesStillHaveToPassValidation()
    {
        var imported = FrpConfigJson.Import(
            """{"serverAddr":"1.2.3.4","proxies":[{"name":"x","type":"http","localPort":9321}]}""",
            FrpSettings.Default(9321));

        Assert.False(imported.Succeeded);
        Assert.Contains(imported.Problems, problem => problem.Contains("至少需要一个域名", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\"webServer\":123", "webServer")]
    [InlineData("\"webServer\":null", "webServer")]
    [InlineData("\"webServer\":{\"addr\":42}", "webServer.addr")]
    [InlineData("\"webServer\":{\"port\":0}", "webServer.port")]
    [InlineData("\"webServer\":{\"port\":65536}", "webServer.port")]
    [InlineData("\"webServer\":{\"port\":1.5}", "webServer.port")]
    [InlineData("\"auth\":false", "auth")]
    [InlineData("\"auth\":{\"method\":42}", "auth.method")]
    [InlineData("\"auth\":{\"method\":\"\"}", "auth.method")]
    [InlineData("\"auth\":{\"token\":null}", "auth.token")]
    [InlineData("\"log\":false", "log")]
    [InlineData("\"log\":{\"disablePrintColor\":\"true\"}", "log.disablePrintColor")]
    public void BothRolesClassifyMalformedCommonFields(string field, string expected)
    {
        foreach (var role in new[] { "\"bindPort\":7000", "\"serverAddr\":\"frp.example.com\",\"proxies\":[{\"localPort\":9321,\"remotePort\":19321}]" })
        {
            var imported = FrpConfigJson.Import("{" + role + "," + field + ",\"dnsServer\":\"1.1.1.1\"}", ClientSettings());
            Assert.False(imported.Succeeded);
            Assert.Null(imported.Settings);
            Assert.Contains(imported.Problems, problem => problem.Contains(expected, StringComparison.Ordinal));
            Assert.Contains("dnsServer", imported.Unsupported);
        }
    }

    [Theory]
    [InlineData("\"transport\":false", "transport")]
    [InlineData("\"transport\":{\"tls\":null}", "transport.tls")]
    [InlineData("\"transport\":{\"tls\":{\"enable\":1}}", "transport.tls.enable")]
    [InlineData("\"loginFailExit\":null", "loginFailExit")]
    [InlineData("\"serverPort\":0", "serverPort")]
    [InlineData("\"serverPort\":2147483648", "serverPort")]
    public void ClassifiesMalformedClientRootFields(string field, string expected)
    {
        var imported = FrpConfigJson.Import("{\"serverAddr\":\"frp.example.com\",\"proxies\":[{\"localPort\":9321,\"remotePort\":19321}]," + field + "}", ClientSettings());
        Assert.False(imported.Succeeded);
        Assert.Contains(imported.Problems, problem => problem.Contains(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\"transport\":false", "transport")]
    [InlineData("\"transport\":{\"useCompression\":1}", "useCompression")]
    [InlineData("\"transport\":{\"useEncryption\":null}", "useEncryption")]
    [InlineData("\"customDomains\":[42]", "customDomains[0]")]
    [InlineData("\"customDomains\":[null]", "customDomains[0]")]
    [InlineData("\"customDomains\":[\"\"]", "customDomains[0]")]
    [InlineData("\"remotePort\":0", "remotePort")]
    [InlineData("\"localIP\":false", "localIP")]
    public void ClassifiesMalformedManagedProxyFieldsEvenWhenInactive(string field, string expected)
    {
        var imported = FrpConfigJson.Import("{\"serverAddr\":\"frp.example.com\",\"proxies\":[{\"type\":\"http\",\"localPort\":9321," + field + "}]}", ClientSettings());
        Assert.False(imported.Succeeded);
        Assert.Contains(imported.Problems, problem => problem.Contains(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{\"bindPort\":7000,\"bindPort\":7100}", "bindPort")]
    [InlineData("{\"bindPort\":7000,\"auth\":{\"token\":\"a\",\"token\":\"b\"}}", "auth.token")]
    [InlineData("{\"bindPort\":7000,\"unknown\":{\"x\":1,\"x\":2}}", "unknown.x")]
    [InlineData("{\"serverAddr\":\"frp.example.com\",\"proxies\":[{\"localPort\":9321,\"remotePort\":19321,\"transport\":{\"useCompression\":true,\"useCompression\":false}}]}", "proxies[0].transport.useCompression")]
    public void DuplicateKeysAreClassifiedWithoutEscapingExceptions(string json, string path)
    {
        var imported = FrpConfigJson.Import(json, ClientSettings());
        Assert.False(imported.Succeeded);
        Assert.Contains(imported.Problems, problem => problem.Contains("重复字段 " + path, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("null", "proxies[1]")]
    [InlineData("{\"localPort\":9321,\"remotePort\":19321,\"customDomains\":[7]}", "customDomains[0]")]
    public void ExtraProxyShapeAndManagedElementsAreStillValidated(string extra, string expected)
    {
        var imported = FrpConfigJson.Import("{\"serverAddr\":\"frp.example.com\",\"proxies\":[{\"localPort\":9321,\"remotePort\":19321}," + extra + "]}", ClientSettings());
        Assert.False(imported.Succeeded);
        Assert.Contains(imported.Problems, problem => problem.Contains(expected, StringComparison.Ordinal));
        Assert.Contains(imported.Unsupported, field => field.StartsWith("proxies：", StringComparison.Ordinal));
    }

    [Fact]
    public void WholeServerImportClearsOldOptionalFieldsAndReportsDefaultsAndTokenClear()
    {
        var current = ServerSettings() with { FollowService = true };
        var imported = FrpConfigJson.Import("{\"bindPort\":7001}", current);
        Assert.True(imported.Succeeded, string.Join("；", imported.Problems));
        Assert.Equal(0, imported.Settings!.Server.VhostHttpPort);
        Assert.Equal("", imported.Settings.Server.SubdomainHost);
        Assert.Equal(FrpServerSettings.DefaultAdminPort, imported.Settings.Server.AdminPort);
        Assert.Null(imported.Token);
        Assert.Equal(current.Client, imported.Settings.Client);
        Assert.True(imported.Settings.FollowService);
        foreach (var field in new[] { "vhostHTTPPort", "subdomainHost", "webServer.port", "auth.token" })
            Assert.Contains(imported.AppliedDefaults, entry => entry.Contains(field, StringComparison.Ordinal));
    }

    [Fact]
    public void WholeClientImportResetsOptionsRatherThanInheritingSavedValues()
    {
        var current = ClientSettings() with { Client = ClientSettings().Client with { UseEncryption = true, CustomDomains = ["old.example.com"], AdminPort = 7444 } };
        var imported = FrpConfigJson.Import("{\"serverAddr\":\"new.example.com\",\"proxies\":[{\"localPort\":9322,\"remotePort\":19322}]}", current);
        Assert.True(imported.Succeeded, string.Join("；", imported.Problems));
        Assert.False(imported.Settings!.Client.UseCompression);
        Assert.False(imported.Settings.Client.UseEncryption);
        Assert.True(imported.Settings.Client.TransportTls);
        Assert.Empty(imported.Settings.Client.CustomDomains);
        Assert.Equal(FrpClientSettings.DefaultAdminPort, imported.Settings.Client.AdminPort);
        Assert.Equal(current.Server, imported.Settings.Server);
        Assert.Contains(imported.AppliedDefaults, field => field.Contains("useCompression", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(FrpProxyKind.Tcp)]
    [InlineData(FrpProxyKind.Http)]
    [InlineData(FrpProxyKind.Https)]
    public void AllProxyKindsRoundTripCompressionAndEncryption(FrpProxyKind kind)
    {
        var current = ClientSettings() with { Client = ClientSettings().Client with { ProxyKind = kind, CustomDomains = ["danmu.example.com"], UseEncryption = true } };
        var exported = FrpConfigJson.Export(current, "draft-token");
        var imported = FrpConfigJson.Import(exported, FrpSettings.Default(9321));
        Assert.True(imported.Succeeded, string.Join("；", imported.Problems));
        Assert.True(imported.Settings!.Client.UseCompression);
        Assert.True(imported.Settings.Client.UseEncryption);
        Assert.Equal("draft-token", imported.Token);
    }

    [Fact]
    public void KeepsTheOtherRoleAndInstalledVersionUntouched()
    {
        var current = ServerSettings();

        var imported = FrpConfigJson.Import(
            """{"serverAddr":"1.2.3.4","proxies":[{"name":"x","type":"tcp","localPort":9321,"remotePort":19321}]}""",
            current);

        Assert.True(imported.Succeeded, string.Join("；", imported.Problems));
        Assert.Equal("0.71.0", imported.Settings!.InstalledVersion);
        Assert.Equal(7200, imported.Settings.Server.BindPort);
    }
}
