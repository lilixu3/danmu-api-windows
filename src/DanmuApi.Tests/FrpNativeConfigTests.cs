using System.Text;
using System.Text.Json.Nodes;
using DanmuApi.Core.Frp;

namespace DanmuApi.Tests;

public sealed class FrpNativeConfigTests
{
    // Anonymized panel-style input. No real account, endpoint or credential is used in these tests.
    private const string PanelToml = """
        serverAddr = "frp.example.invalid"
        serverPort = 1210
        user = "sample-account"
        # transport.protocol = "quic"
        [[proxies]]
        name = "sample-api"
        type = "tcp"
        localIP = "127.0.0.1"
        localPort = 9321
        remotePort = 19321
        transport.useEncryption = true
        transport.useCompression = true
        """;

    private const string ExtendedJson = """
        {
          "serverAddr": "frp.example.invalid", "serverPort": 7100, "user": "sample-account",
          "dnsServer": "192.0.2.53", "loginFailExit": true,
          "auth": {"method":"oidc", "oidc": {
            "clientID":"dummy-client", "clientSecret":"dummy-oidc-secret",
            "audience":"dummy-audience", "tokenEndpointURL":"https://identity.example.invalid/token",
            "additionalEndpointParams":{"refresh_token":"dummy-refresh-secret"}
          }},
          "transport": {"protocol":"quic", "tls":{"enable":false,"serverName":"frp.example.invalid"},
                        "quic":{"keepalivePeriod":15}},
          "log":{"to":"source.log","level":"debug","disablePrintColor":false,"maxDays":5},
          "webServer":{"addr":"::1","port":7401,"user":"dummy-source-user","password":"dummy-source-password",
                       "assetsDir":"assets","pprofEnable":true,"extension":{"enabled":true}},
          "extension":{"headers":{"X-Test-Credential":"dummy-header-secret"},"unknown":[1,true,null]},
          "proxies":[
            {"name":"api","type":"tcp","localIP":"127.0.0.1","localPort":9321,"remotePort":19321,
             "transport":{"useEncryption":true,"useCompression":true,"bandwidthLimit":"1MB"},
             "healthCheck":{"type":"http","path":"/__health","timeoutSeconds":3}},
            {"name":"datagrams","type":"udp","localPort":12345,"remotePort":22345},
            {"name":"private","type":"stcp","secretKey":"dummy-proxy-secret",
             "plugin":{"type":"static_file","localPath":"assets"}}
          ]
        }
        """;

    private const string ExtendedToml = """
        serverAddr = "frp.example.invalid"
        serverPort = 7100
        user = "sample-account"
        dnsServer = "192.0.2.53"
        loginFailExit = true
        auth.method = "oidc"
        auth.oidc.clientID = "dummy-client"
        auth.oidc.clientSecret = "dummy-oidc-secret"
        auth.oidc.audience = "dummy-audience"
        auth.oidc.tokenEndpointURL = "https://identity.example.invalid/token"
        auth.oidc.additionalEndpointParams = { refresh_token = "dummy-refresh-secret" }
        transport.protocol = "quic"
        transport.tls = { enable = false, serverName = "frp.example.invalid" }
        transport.quic.keepalivePeriod = 15
        log = { to = "source.log", level = "debug", disablePrintColor = false, maxDays = 5 }
        webServer = { addr = "::1", port = 7401, user = "dummy-source-user", password = "dummy-source-password", assetsDir = "assets", pprofEnable = true, extension = { enabled = true } }
        extension = { headers = { X-Test-Credential = "dummy-header-secret" }, unknown = [1, true] }
        [[proxies]]
        name = "api"
        type = "tcp"
        localIP = "127.0.0.1"
        localPort = 9321
        remotePort = 19321
        transport = { useEncryption = true, useCompression = true, bandwidthLimit = "1MB" }
        healthCheck = { type = "http", path = "/__health", timeoutSeconds = 3 }
        [[proxies]]
        name = "datagrams"
        type = "udp"
        localPort = 12345
        remotePort = 22345
        [[proxies]]
        name = "private"
        type = "stcp"
        secretKey = "dummy-proxy-secret"
        plugin = { type = "static_file", localPath = "assets" }
        """;

    [Fact]
    public void PanelTomlRunsIndependentlyWithRegisteredNamesAndNativeTransport()
    {
        var native = FrpNativeConfig.Parse(PanelToml);
        Assert.Equal("TOML", native.Format);
        Assert.Equal(FrpRole.Client, native.Role);
        Assert.Equal("frp.example.invalid", native.ServerAddress);
        Assert.Equal(1210, native.ServerPort);
        Assert.Equal("sample-account", native.User);
        Assert.Contains("sample-account", native.Secrets);
        Assert.DoesNotContain("sample-account", native.Summary, StringComparison.Ordinal);
        Assert.Equal(7400, native.AdminPort);
        Assert.Equal(new FrpNativeProxy("sample-account.sample-api", "tcp", "127.0.0.1", 9321) { AdminName = "sample-api" }, Assert.Single(native.Proxies));
        var runtime = Runtime(native);
        Assert.Equal("sample-api", runtime["proxies"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("sample-account", runtime["user"]!.GetValue<string>());
        Assert.True(runtime["proxies"]![0]!["transport"]!["useEncryption"]!.GetValue<bool>());
        Assert.True(runtime["proxies"]![0]!["transport"]!["useCompression"]!.GetValue<bool>());
    }

    [Fact]
    public void ManuallyConstructedNativeProxyKeepsItsNameAsTheDefaultAdminName()
    {
        var proxy = new FrpNativeProxy("api", "tcp", "127.0.0.1", 9321);
        Assert.Equal("api", proxy.Name);
        Assert.Equal(proxy.Name, proxy.AdminName);
        var explicitAdminName = proxy with { AdminName = "source-api" };
        Assert.Equal("api", explicitAdminName.Name);
        Assert.Equal("source-api", explicitAdminName.AdminName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RegisteredNamesAndFrpcAdminNamesAreDistinctWithRootUserForBothFormats(bool toml)
    {
        const string json = """
            {"serverAddr":"frp.example.invalid","user":"dummy-native-user","proxies":[
              {"name":"api","type":"tcp","localPort":9321},
              {"name":"other","type":"udp","localPort":9333}]}
            """;
        const string tomlText = """
            serverAddr = "frp.example.invalid"
            user = "dummy-native-user"
            [[proxies]]
            name = "api"
            type = "tcp"
            localPort = 9321
            [[proxies]]
            name = "other"
            type = "udp"
            localPort = 9333
            """;
        var native = FrpNativeConfig.Parse(toml ? tomlText : json);
        Assert.Equal("dummy-native-user.api", native.Proxies[0].Name);
        Assert.Equal("api", native.Proxies[0].AdminName);
        Assert.Equal("dummy-native-user.other", native.Proxies[1].Name);
        Assert.Equal("other", native.Proxies[1].AdminName);
        Assert.NotEqual(native.Proxies[0].Name, native.Proxies[0].AdminName);
        var runtime = Runtime(native);
        Assert.Equal("api", runtime["proxies"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("other", runtime["proxies"]![1]!["name"]!.GetValue<string>());
        Assert.False(runtime["proxies"]![0]!.AsObject().ContainsKey("AdminName"));
        Assert.Contains("dummy-native-user", native.Secrets);
    }

    [Fact]
    public void JsonAndTomlProduceEquivalentRuntimeDocumentsWithoutLosingAnyProxyOrExtensions()
    {
        // TOML has no null literal: remove only the JSON-only unknown null when comparing the same AST.
        var equivalentJson = ExtendedJson.Replace("[1,true,null]", "[1,true]", StringComparison.Ordinal);
        var jsonNative = FrpNativeConfig.Parse(equivalentJson);
        var tomlNative = FrpNativeConfig.Parse(ExtendedToml);
        Assert.Equal("JSON", jsonNative.Format);
        Assert.Equal("TOML", tomlNative.Format);
        Assert.Equal(jsonNative.Proxies, tomlNative.Proxies);
        Assert.True(JsonNode.DeepEquals(Runtime(jsonNative), Runtime(tomlNative)));
        Assert.Equal(3, tomlNative.Proxies.Count);
        Assert.Equal("sample-account.private", tomlNative.Proxies[2].Name);
        Assert.Equal("private", tomlNative.Proxies[2].AdminName);
        Assert.Equal(0, tomlNative.Proxies[2].LocalPort);
        Assert.Equal(string.Empty, tomlNative.Proxies[2].LocalAddress);
    }

    [Fact]
    public void ConventionalTomlTablesAreScopedToTheirOwnProxyArrayItem()
    {
        const string text = """
            serverAddr = "frp.example.invalid"
            user = "sample-account"
            [[proxies]]
            name = "first"
            type = "tcp"
            localPort = 9321
            remotePort = 19321
            [proxies.transport]
            useEncryption = true
            bandwidthLimit = "1MB"
            [proxies.healthCheck]
            type = "http"
            path = "/__health"
            [[proxies]]
            name = "second"
            type = "http"
            localPort = 9444
            customDomains = ["api.example.invalid"]
            [proxies.transport]
            useCompression = true
            [proxies.healthCheck]
            type = "tcp"
            timeoutSeconds = 3
            """;
        Assert.True(FrpConfigToml.TryParse(text, out _, out var problems), string.Join(";", problems));
        var native = FrpNativeConfig.Parse(text);
        Assert.Equal(2, native.Proxies.Count);
        var runtime = Runtime(native);
        Assert.True(runtime["proxies"]![0]!["transport"]!["useEncryption"]!.GetValue<bool>());
        Assert.Equal("1MB", runtime["proxies"]![0]!["transport"]!["bandwidthLimit"]!.GetValue<string>());
        Assert.False(runtime["proxies"]![0]!["transport"]!.AsObject().ContainsKey("useCompression"));
        Assert.True(runtime["proxies"]![1]!["transport"]!["useCompression"]!.GetValue<bool>());
        Assert.False(runtime["proxies"]![1]!["transport"]!.AsObject().ContainsKey("useEncryption"));
        Assert.Equal("http", runtime["proxies"]![0]!["healthCheck"]!["type"]!.GetValue<string>());
        Assert.Equal("tcp", runtime["proxies"]![1]!["healthCheck"]!["type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("serverAddr = \"a\"\n[[proxies]]\nname = \"first\"\nlocalPort = 9321\n[proxies.transport]\nuseEncryption = true\n[proxies.transport]")]
    [InlineData("serverAddr = \"a\"\nproxies = [{name = \"first\", localPort = 9321}]\n[proxies.transport]\nuseEncryption = true")]
    [InlineData("serverAddr = \"a\"\n[proxies.transport]\nuseEncryption = true\n[[proxies]]\nname = \"first\"\nlocalPort = 9321")]
    public void TomlTableTraversalDoesNotGuessArrayItemsOrPermitSameItemTableRedefinition(string text)
    {
        Assert.False(FrpConfigToml.TryParse(text, out _, out var problems));
        Assert.NotEmpty(problems);
        Assert.Throws<FrpConfigurationException>(() => FrpNativeConfig.Parse(text));
    }

    [Fact]
    public void NestedTomlArrayTablesKeepLastItemAndItsTablesDistinct()
    {
        const string text = """
            serverAddr = "frp.example.invalid"
            [[proxies]]
            name = "native"
            type = "stcp"
            [[proxies.extensions]]
            name = "first"
            [proxies.extensions.options]
            enabled = true
            [[proxies.extensions]]
            name = "second"
            [proxies.extensions.options]
            enabled = false
            """;
        var native = FrpNativeConfig.Parse(text);
        var extensions = Runtime(native)["proxies"]![0]!["extensions"]!.AsArray();
        Assert.Equal(2, extensions.Count);
        Assert.Equal("first", extensions[0]!["name"]!.GetValue<string>());
        Assert.True(extensions[0]!["options"]!["enabled"]!.GetValue<bool>());
        Assert.Equal("second", extensions[1]!["name"]!.GetValue<string>());
        Assert.False(extensions[1]!["options"]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void RuntimeOverlayPreservesEverythingExceptExplicitHostPolicies()
    {
        var source = JsonNode.Parse(ExtendedJson)!.AsObject();
        var runtime = Runtime(FrpNativeConfig.Parse(ExtendedJson));
        var expected = (JsonObject)source.DeepClone();
        expected["webServer"]!["addr"] = "127.0.0.1";
        expected["webServer"]!["port"] = 7401;
        expected["webServer"]!["user"] = "dummy-host-user";
        expected["webServer"]!["password"] = "dummy-host-password";
        expected["log"]!["to"] = "console";
        expected["log"]!["disablePrintColor"] = true;
        expected["loginFailExit"] = false;
        Assert.True(JsonNode.DeepEquals(expected, runtime));
        Assert.True(JsonNode.DeepEquals(source["auth"], runtime["auth"]));
        Assert.True(JsonNode.DeepEquals(source["transport"], runtime["transport"]));
        Assert.True(JsonNode.DeepEquals(source["proxies"], runtime["proxies"]));
        Assert.Equal("192.0.2.53", runtime["dnsServer"]!.GetValue<string>());
        Assert.Equal(5, runtime["log"]!["maxDays"]!.GetValue<int>());
        Assert.True(runtime["webServer"]!["extension"]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void RuntimeGenerationNeverChangesSourceSecretsSettingsOrSubsequentGenerations()
    {
        var saved = FrpSettings.Default(9321) with { ConfigMode = FrpConfigMode.Text, RawConfig = ExtendedToml };
        var native = FrpNativeConfig.Parse(saved.RawConfig);
        var originalSecrets = native.Secrets.ToArray();
        var first = native.CreateRuntimeConfig("dummy-host-one", "dummy-host-password-one");
        var second = native.CreateRuntimeConfig("dummy-host-two", "dummy-host-password-two");
        Assert.Contains("dummy-host-one", first, StringComparison.Ordinal);
        Assert.DoesNotContain("dummy-host-one", second, StringComparison.Ordinal);
        Assert.Equal(originalSecrets, native.Secrets);
        Assert.Contains("dummy-source-password", native.Secrets);
        Assert.DoesNotContain("dummy-host-password-one", native.Secrets);
        Assert.Equal(ExtendedToml, native.Describe(saved).RawConfig);
        Assert.Equal(FrpConfigMode.Text, native.Describe(saved).ConfigMode);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(first),
            JsonNode.Parse(FrpNativeConfig.Parse(ExtendedToml).CreateRuntimeConfig("dummy-host-one", "dummy-host-password-one"))));
    }

    [Fact]
    public void TextValidationIgnoresAllInactiveVisualValues()
    {
        var settings = FrpSettings.Default(0) with
        {
            ConfigMode = FrpConfigMode.Text,
            RawConfig = PanelToml,
            Role = (FrpRole)999,
            Client = FrpSettings.Default(0).Client with { ProxyKind = (FrpProxyKind)999, AdminPort = -1 },
            Server = new FrpServerSettings(-1, -1, "/invalid", -1),
        };
        Assert.Empty(settings.Validate());
        settings.EnsureValid();
        var withoutVisual = settings with { Client = null!, Server = null! };
        Assert.Empty(withoutVisual.Validate());
        Assert.Equal("sample-api", FrpNativeConfig.Parse(PanelToml).Describe(withoutVisual).Client.ProxyName);
        var badText = settings with { RawConfig = "serverAddr =" };
        Assert.NotEmpty(badText.Validate());
        Assert.Throws<FrpConfigurationException>(badText.EnsureValid);
    }

    [Fact]
    public void VisualValidationIgnoresInvalidTextAndKeepsConstructorCompatibility()
    {
        var baseline = FrpSettings.Default(9321);
        var settings = new FrpSettings(FrpRole.Client, false,
            baseline.Client with { ServerAddress = "frp.example.invalid", RemotePort = 19321 }, baseline.Server, "")
        { RawConfig = "not parseable raw text" };
        Assert.Equal(FrpConfigMode.Visual, settings.ConfigMode);
        Assert.Equal(string.Empty, baseline.RawConfig);
        Assert.Empty(settings.Validate());
        settings.EnsureValid();
    }

    [Fact]
    public void UnknownModesAndActiveVisualEnumsAreExplicitProblems()
    {
        var baseline = FrpSettings.Default(9321);
        var unknownMode = baseline with { ConfigMode = (FrpConfigMode)999, RawConfig = PanelToml };
        Assert.Contains(unknownMode.Validate(), error => error.Contains("配置模式", StringComparison.Ordinal));
        Assert.Throws<FrpConfigurationException>(unknownMode.EnsureValid);
        Assert.Contains((baseline with { Role = (FrpRole)999 }).Validate(), error => error.Contains("角色", StringComparison.Ordinal));
        Assert.Contains((baseline with { Client = baseline.Client with { ProxyKind = (FrpProxyKind)999 } }).Validate(),
            error => error.Contains("代理类型", StringComparison.Ordinal));
    }

    [Fact]
    public void DescribeUsesFirstApiTypeAndRetainsSourceNameSeparateFromUser()
    {
        const string text = """
            {"serverAddr":"frp.example.invalid","user":"sample-account",
             "proxies":[{"name":"udp","type":"udp","localPort":9333},
                        {"name":"api","type":"https","localPort":9321,"customDomains":["api.example.invalid"]},
                        {"name":"second-api","type":"tcp","localPort":9444}]}
            """;
        var visual = FrpSettings.Default(5555) with { ConfigMode = FrpConfigMode.Text, RawConfig = text, FollowService = true, InstalledVersion = "0.71.0" };
        var native = FrpNativeConfig.Parse(text);
        var metadata = native.Describe(visual);
        Assert.Equal("sample-account.api", native.Proxies[1].Name);
        Assert.Equal("api", metadata.Client.ProxyName);
        Assert.Equal("sample-account", metadata.Client.User);
        Assert.Equal(FrpProxyKind.Https, metadata.Client.ProxyKind);
        Assert.Equal("127.0.0.1", metadata.Client.LocalAddress);
        Assert.Equal(9321, metadata.Client.LocalPort);
        Assert.Equal("api.example.invalid", Assert.Single(metadata.Client.CustomDomains));
        Assert.Equal(visual.ConfigMode, metadata.ConfigMode);
        Assert.Equal(visual.RawConfig, metadata.RawConfig);
        Assert.Equal(visual.InstalledVersion, metadata.InstalledVersion);
        Assert.True(metadata.FollowService);
        Assert.Same(visual.Server, metadata.Server);
        Assert.Equal(5555, visual.Client.LocalPort);
    }

    [Theory]
    [InlineData("stcp")]
    [InlineData("sudp")]
    [InlineData("xtcp")]
    [InlineData("future-native-type")]
    public void ExtraProxyTypesNeverThrowOrFabricateApiMetadata(string type)
    {
        var root = new JsonObject
        {
            ["serverAddr"] = "frp.example.invalid", ["user"] = "sample-account",
            ["proxies"] = new JsonArray(new JsonObject { ["name"] = "native", ["type"] = type, ["secretKey"] = "dummy-private-secret" }),
        };
        var native = FrpNativeConfig.Parse(root.ToJsonString());
        var proxy = Assert.Single(native.Proxies);
        Assert.Equal("sample-account.native", proxy.Name);
        Assert.Equal(type, proxy.Type);
        Assert.Equal(0, proxy.LocalPort);
        Assert.Equal(string.Empty, proxy.LocalAddress);
        var metadata = native.Describe(FrpSettings.Default(9321));
        Assert.Equal(string.Empty, metadata.Client.ProxyName);
        Assert.Equal(string.Empty, metadata.Client.LocalAddress);
        Assert.Equal(0, metadata.Client.LocalPort);
        Assert.Equal(type, Runtime(native)["proxies"]![0]!["type"]!.GetValue<string>());
        Assert.Contains("dummy-private-secret", native.Secrets);
    }

    [Fact]
    public void TcpPluginHasNoOrdinaryTargetEvenWhenUnusedLocalPortIsPresent()
    {
        const string text = """
            {"serverAddr":"frp.example.invalid","proxies":[
              {"name":"files","type":"tcp","localPort":9321,"plugin":{"type":"static_file","localPath":"assets"}}
            ]}
            """;
        var native = FrpNativeConfig.Parse(text);
        Assert.Equal(0, Assert.Single(native.Proxies).LocalPort);
        Assert.Equal(string.Empty, native.Proxies[0].LocalAddress);
        Assert.Equal(0, native.Describe(FrpSettings.Default(9321)).Client.LocalPort);
        Assert.Equal(9321, Runtime(native)["proxies"]![0]!["localPort"]!.GetValue<int>());
        Assert.Equal("static_file", Runtime(native)["proxies"]![0]!["plugin"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void LegitimateNativeMissingDefaultsDoNotCopyVisualValuesIntoRuntime()
    {
        const string text = """
            {"serverAddr":"frp.example.invalid","proxies":[{"name":"api","localPort":9321}]}
            """;
        var native = FrpNativeConfig.Parse(text);
        Assert.Equal(7000, native.ServerPort);
        Assert.Equal(7400, native.AdminPort);
        Assert.Equal(new FrpNativeProxy("api", "tcp", "127.0.0.1", 9321), Assert.Single(native.Proxies));
        Assert.Equal("api", native.Proxies[0].AdminName);
        var runtime = Runtime(native);
        Assert.False(runtime.ContainsKey("serverPort"));
        Assert.False(runtime["proxies"]![0]!.AsObject().ContainsKey("type"));
        Assert.False(runtime["proxies"]![0]!.AsObject().ContainsKey("remotePort"));
        Assert.False(runtime["proxies"]![0]!.AsObject().ContainsKey("localIP"));
        Assert.Equal(7400, runtime["webServer"]!["port"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("{\"serverAddr\":\"frp.example.invalid\"}")]
    [InlineData("{\"serverAddr\":\"frp.example.invalid\",\"proxies\":[]}")]
    [InlineData("{\"serverAddr\":\"frp.example.invalid\",\"visitors\":[{\"name\":\"visitor\",\"type\":\"stcp\"}]}")]
    public void ClientRequiresAtLeastOneProxyForAuthoritativeHostMonitoring(string text)
    {
        var error = Assert.Throws<FrpConfigurationException>(() => FrpNativeConfig.Parse(text));
        Assert.Contains("proxies", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VisitorsAlongsideProxiesRemainNativeAndHaveNoInventedApiTarget()
    {
        const string text = """
            {"serverAddr":"frp.example.invalid","proxies":[{"name":"private","type":"stcp"}],"visitors":[
              {"name":"sample-visitor","type":"stcp","serverName":"sample-private","secretKey":"dummy-visitor-secret"}
            ]}
            """;
        var native = FrpNativeConfig.Parse(text);
        Assert.Single(native.Proxies);
        Assert.Equal(0, native.Describe(FrpSettings.Default(9321)).Client.LocalPort);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text)!["visitors"], Runtime(native)["visitors"]));
        Assert.Contains("dummy-visitor-secret", native.Secrets);
    }

    [Fact]
    public void ServerMetadataAndRuntimePreserveExtendedServerConfiguration()
    {
        const string text = """
            bindPort = 7001
            vhostHTTPPort = 8080
            vhostHTTPSPort = 8443
            subdomainHost = "example.invalid"
            auth = { method = "oidc", oidc = { issuer = "https://identity.example.invalid", audience = "dummy-audience", skipExpiryCheck = true } }
            transport.maxPoolCount = 25
            allowPorts = [{ start = 20000, end = 21000 }]
            webServer.port = 7501
            """;
        var visual = FrpSettings.Default(9321) with { ConfigMode = FrpConfigMode.Text, RawConfig = text };
        var native = FrpNativeConfig.Parse(text);
        Assert.Equal(FrpRole.Server, native.Role);
        Assert.Equal(7001, native.BindPort);
        Assert.Equal(8080, native.VhostHttpPort);
        Assert.Equal(7501, native.AdminPort);
        Assert.Equal(string.Empty, native.ServerAddress);
        Assert.Equal(0, native.ServerPort);
        var metadata = native.Describe(visual);
        Assert.Equal(new FrpServerSettings(7001, 8080, "example.invalid", 7501), metadata.Server);
        Assert.Same(visual.Client, metadata.Client);
        Assert.Equal(text, metadata.RawConfig);
        var runtime = Runtime(native);
        Assert.False(runtime.ContainsKey("loginFailExit"));
        Assert.Equal(8443, runtime["vhostHTTPSPort"]!.GetValue<int>());
        Assert.True(runtime["auth"]!["oidc"]!["skipExpiryCheck"]!.GetValue<bool>());
        Assert.Equal(25, runtime["transport"]!["maxPoolCount"]!.GetValue<int>());
        Assert.Equal(20000, runtime["allowPorts"]![0]!["start"]!.GetValue<int>());
        Assert.Equal(7501, runtime["webServer"]!["port"]!.GetValue<int>());
    }

    [Fact]
    public void ServerBindAndAdminDefaultsApplyOnlyWhenAbsent()
    {
        var native = FrpNativeConfig.Parse("bindAddr = \"0.0.0.0\"");
        Assert.Equal(7000, native.BindPort);
        Assert.Equal(7500, native.AdminPort);
        Assert.Equal(0, native.VhostHttpPort);
        Assert.False(Runtime(native).ContainsKey("bindPort"));
    }

    [Theory]
    [InlineData("{\"serverAddr\":\"a\",\"serverAddr\":\"b\"}")]
    [InlineData("{\"serverAddr\":\"a\",\"auth\":{\"token\":\"dummy-first-secret\",\"\\u0074oken\":\"dummy-second-secret\"}}")]
    [InlineData("{\"serverAddr\":\"a\",\"extension\":[{\"dummy-secret-key-name\":1,\"dummy-secret-key-name\":2}]}")]
    public void JsonDuplicatePropertiesAreRejectedRecursivelyBeforeNodeConversion(string text)
    {
        var error = Assert.Throws<FrpConfigurationException>(() => FrpNativeConfig.Parse(text));
        Assert.Contains(error.Problems, problem => problem.Contains("重复", StringComparison.Ordinal));
        Assert.DoesNotContain("dummy", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("serverAddr = \"a\"\nserverAddr = \"b\"", "第 2 行", "重复")]
    [InlineData("serverAddr = \"a\"\n[auth]\n[auth]", "第 3 行", "重复")]
    [InlineData("serverAddr = \"a\"\nserverPort = 7000.5", "第 2 行", "小数")]
    [InlineData("serverAddr = \"a\"\nserverPort = 2026-10-04", "第 2 行", "日期")]
    [InlineData("serverAddr = \"a\"\nserverPort = 0x20", "第 2 行", "十六进制")]
    [InlineData("serverAddr = \"a\"\nauth.token = \"\"\"dummy-multiline-secret\"\"\"", "第 2 行", "多行字符串")]
    [InlineData("serverAddr = \"a\"\nauth.token = true_dummy_unquoted_secret", "第 2 行", "语法")]
    [InlineData("serverAddr = \"a\"\n\"dummy-key-secret\" = 1\n\"dummy-key-secret\" = 2", "第 3 行", "重复")]
    public void UnsupportedOrMalformedTomlHasExplicitSafeLineDiagnostics(string text, string line, string reason)
    {
        var error = Assert.Throws<FrpConfigurationException>(() => FrpNativeConfig.Parse(text));
        Assert.Contains(line, error.Message, StringComparison.Ordinal);
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("dummy", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("FFFFFFFF")]
    [InlineData("00110000")]
    [InlineData("0000D800")]
    public void InvalidTomlUnicodeCodePointsRemainExplicitLineProblems(string codePoint)
    {
        var text = "serverAddr = \"a\"\nauth.token = \"\\U" + codePoint + "\"";
        Assert.False(FrpConfigToml.TryParse(text, out _, out var parserProblems));
        Assert.Contains(parserProblems, problem => problem.Contains("第 2 行", StringComparison.Ordinal));
        var error = Assert.Throws<FrpConfigurationException>(() => FrpNativeConfig.Parse(text));
        Assert.Contains("第 2 行", error.Message, StringComparison.Ordinal);
        Assert.Contains("Unicode", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(codePoint, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"serverAddr\":\"\\uD800\"}")]
    [InlineData("{\"serverAddr\":\"a\",\"extension\":\"\\uDC00\"}")]
    [InlineData("{\"serverAddr\":\"a\",\"\\uD800\":true}")]
    public void MalformedSurrogateTextAndEscapesAreExplicitSafeProblems(string text)
    {
        var error = Assert.Throws<FrpConfigurationException>(() => FrpNativeConfig.Parse(text));
        Assert.Contains("Unicode", error.Message, StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void InvalidUtf16SourceIsRejectedInsteadOfTranscodedWithReplacementCharacters()
    {
        var text = "serverAddr = '" + (char)0xD800 + "'";
        var error = Assert.Throws<FrpConfigurationException>(() => FrpNativeConfig.Parse(text));
        Assert.Contains("Unicode", error.Message, StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void JsonSyntaxDiagnosticsUseOnlyPositionsNotSourceCharactersValuesOrPaths()
    {
        const string text = """
            {
              "serverAddr":"frp.example.invalid",
              "auth":{"token":dummy_unquoted_secret}
            }
            """;
        var error = Assert.Throws<FrpConfigurationException>(() => FrpNativeConfig.Parse(text));
        Assert.Contains("第 3 行", error.Message, StringComparison.Ordinal);
        Assert.Contains("字节位置", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("dummy", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("\"serverPort\":null")]
    [InlineData("\"serverPort\":\"7000\"")]
    [InlineData("\"serverPort\":0")]
    [InlineData("\"serverPort\":65536")]
    [InlineData("\"serverPort\":1.5")]
    [InlineData("\"serverPort\":true")]
    [InlineData("\"user\":null")]
    [InlineData("\"auth\":null")]
    [InlineData("\"auth\":{\"token\":null}")]
    [InlineData("\"auth\":{\"method\":null}")]
    [InlineData("\"auth\":{\"oidc\":null}")]
    [InlineData("\"transport\":null")]
    [InlineData("\"transport\":{\"protocol\":null}")]
    [InlineData("\"transport\":{\"tls\":null}")]
    [InlineData("\"transport\":{\"tls\":{\"enable\":null}}")]
    [InlineData("\"log\":null")]
    [InlineData("\"log\":{\"to\":null}")]
    [InlineData("\"log\":{\"disablePrintColor\":0}")]
    [InlineData("\"loginFailExit\":null")]
    [InlineData("\"webServer\":null")]
    [InlineData("\"webServer\":{\"addr\":\"0.0.0.0\"}")]
    [InlineData("\"webServer\":{\"addr\":\"192.0.2.1\"}")]
    [InlineData("\"webServer\":{\"port\":0}")]
    [InlineData("\"webServer\":{\"port\":\"7400\"}")]
    [InlineData("\"webServer\":{\"password\":false}")]
    [InlineData("\"proxies\":null")]
    [InlineData("\"proxies\":[null]")]
    [InlineData("\"proxies\":[{\"name\":null,\"localPort\":9321}]")]
    [InlineData("\"proxies\":[{\"name\":\"api\",\"type\":null,\"localPort\":9321}]")]
    [InlineData("\"proxies\":[{\"name\":\"api\",\"localPort\":0}]")]
    [InlineData("\"proxies\":[{\"name\":\"api\",\"localPort\":null}]")]
    [InlineData("\"proxies\":[{\"name\":\"api\"}]")]
    [InlineData("\"proxies\":[{\"name\":\"api\",\"localPort\":9321,\"remotePort\":-1}]")]
    [InlineData("\"proxies\":[{\"name\":\"api\",\"localPort\":9321,\"customDomains\":null}]")]
    [InlineData("\"proxies\":[{\"name\":\"api\",\"localPort\":9321,\"customDomains\":[null]}]")]
    [InlineData("\"proxies\":[{\"name\":\"api\",\"localPort\":9321,\"transport\":{\"useEncryption\":0}}]")]
    [InlineData("\"proxies\":[{\"name\":\"api\",\"localPort\":9321,\"healthCheck\":null}]")]
    [InlineData("\"visitors\":null")]
    public void PresentMalformedManagedValuesNeverSilentlyUseDefaults(string fragment)
    {
        var error = Assert.Throws<FrpConfigurationException>(() => FrpNativeConfig.Parse("{\"serverAddr\":\"frp.example.invalid\"," + fragment + "}"));
        Assert.NotEmpty(error.Problems);
    }

    [Theory]
    [InlineData("serverAddr = \"a\"\nserverPort = \"7000\"")]
    [InlineData("serverAddr = \"a\"\nwebServer.port = 0")]
    [InlineData("serverAddr = \"a\"\nwebServer.port = 65536")]
    [InlineData("bindPort = -1")]
    [InlineData("bindPort = 7000\nvhostHTTPPort = 65536")]
    [InlineData("bindPort = 7500")]
    [InlineData("bindPort = 7000\nvhostHTTPSPort = 7500")]
    [InlineData("serverAddr = \"a\"\n[[proxies]]\nname = \"api\"\nlocalPort = 7400")]
    public void NativePortRangesAndHostPortConflictsAreExplicit(string text)
    {
        Assert.NotEmpty(Assert.Throws<FrpConfigurationException>(() => FrpNativeConfig.Parse(text)).Problems);
    }

    [Theory]
    [InlineData("{\"serverAddr\":\"a\",\"bindPort\":7000}")]
    [InlineData("{\"serverAddr\":\"a\",\"vhostHTTPPort\":8080}")]
    [InlineData("{\"bindPort\":7000,\"proxies\":[]}")]
    [InlineData("{\"auth\":{\"method\":\"oidc\"}}")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData(" ")]
    public void AmbiguousRolesMissingMarkersAndNonObjectDocumentsAreRejected(string text)
    {
        Assert.Throws<FrpConfigurationException>(() => FrpNativeConfig.Parse(text));
    }

    [Fact]
    public void DuplicateProxyNamesAreRejectedWithoutEchoingSourceName()
    {
        const string text = """
            {"serverAddr":"a","user":"sample-account","proxies":[
              {"name":"dummy-sensitive-name","type":"udp"},
              {"name":"dummy-sensitive-name","type":"udp"}]}
            """;
        var error = Assert.Throws<FrpConfigurationException>(() => FrpNativeConfig.Parse(text));
        Assert.Contains("重复", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("dummy", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sample-account", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"certFile\":\"dummy-cert\",\"keyFile\":\"dummy-key-secret\"}")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("false")]
    public void WebServerTlsCannotBeSilentlyDeletedOrHiddenByTheHostOverlay(string tls)
    {
        var error = Assert.Throws<FrpConfigurationException>(() => FrpNativeConfig.Parse("{\"serverAddr\":\"a\",\"webServer\":{\"tls\":" + tls + "}}"));
        Assert.Contains("webServer.tls", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("dummy", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.2")]
    [InlineData("::1")]
    [InlineData("localhost")]
    public void ExplicitLoopbackAddressesAreOverlayedToTheHostsIpv4Endpoint(string address)
    {
        var root = new JsonObject
        {
            ["serverAddr"] = "a", ["webServer"] = new JsonObject { ["addr"] = address, ["port"] = 7410 },
            ["proxies"] = new JsonArray(new JsonObject { ["name"] = "native", ["type"] = "stcp" }),
        };
        var native = FrpNativeConfig.Parse(root.ToJsonString());
        Assert.Equal(7410, native.AdminPort);
        Assert.Equal("127.0.0.1", Runtime(native)["webServer"]!["addr"]!.GetValue<string>());
    }

    [Fact]
    public void InputLimitIsExactly32KiBUtf8BytesNotCharacters()
    {
        Assert.Equal(32768, FrpNativeConfig.MaxDocumentBytes);
        const string valid = "{\"serverAddr\":\"frp.example.invalid\",\"proxies\":[{\"name\":\"private\",\"type\":\"stcp\"}]}";
        var atLimit = valid + new string(' ', FrpNativeConfig.MaxDocumentBytes - Encoding.UTF8.GetByteCount(valid));
        Assert.Equal(FrpNativeConfig.MaxDocumentBytes, Encoding.UTF8.GetByteCount(atLimit));
        Assert.Equal(FrpRole.Client, FrpNativeConfig.Parse(atLimit).Role);
        var large = Assert.Throws<FrpConfigurationException>(() => FrpNativeConfig.Parse(atLimit + " "));
        Assert.Contains("字节", large.Message, StringComparison.Ordinal);
        var multibyte = new JsonObject { ["serverAddr"] = "a", ["notes"] = new string('中', 12000) }.ToJsonString(
            new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Assert.True(multibyte.Length < FrpNativeConfig.MaxDocumentBytes);
        Assert.Throws<FrpConfigurationException>(() => FrpNativeConfig.Parse(multibyte));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExcessiveNestingFailsBeforeRecursiveParsingOrSerialization(bool json)
    {
        var value = new string('[', 1000) + "1" + new string(']', 1000);
        var text = json ? "{\"serverAddr\":\"a\",\"extension\":" + value + "}" : "serverAddr = \"a\"\nextension = " + value;
        var error = Assert.Throws<FrpConfigurationException>(() => FrpNativeConfig.Parse(text));
        Assert.Contains("64", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepDottedTomlTablesCannotBypassTheDepthLimit()
    {
        var path = string.Join('.', Enumerable.Repeat("extension", 80));
        var error = Assert.Throws<FrpConfigurationException>(() => FrpNativeConfig.Parse("serverAddr = \"a\"\n" + path + " = 1"));
        Assert.Contains("64", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonCommentsTrailingCommasAndBomAreAcceptedWithoutLosingUnknownFields()
    {
        const string text = "\uFEFF \n{\"serverAddr\":\"a\",/* native extension */\"proxies\":[{\"name\":\"native\",\"type\":\"stcp\"}],\"extension\":{\"unknown\":true,},}";
        var native = FrpNativeConfig.Parse(text);
        Assert.Equal("JSON", native.Format);
        Assert.True(Runtime(native)["extension"]!["unknown"]!.GetValue<bool>());
    }

    [Fact]
    public void SecretsIncludeAllAuthLeavesAndNestedExtensionCredentialsButNeverEnterDiagnostics()
    {
        var native = FrpNativeConfig.Parse(ExtendedJson);
        foreach (var secret in new[] { "dummy-client", "dummy-oidc-secret", "dummy-audience", "dummy-refresh-secret",
                     "https://identity.example.invalid/token", "dummy-header-secret", "dummy-proxy-secret", "dummy-source-password",
                     "sample-account", "dummy-source-user" })
        {
            Assert.Contains(secret, native.Secrets);
            Assert.DoesNotContain(secret, native.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, native.ToString(), StringComparison.Ordinal);
        }
        var settings = FrpSettings.Default(9321) with { ConfigMode = FrpConfigMode.Text, RawConfig = ExtendedJson };
        Assert.DoesNotContain("dummy", settings.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sample-account", settings.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ExactUserAndUsernameKeysAreSensitiveAtTheRootAndInsideNestedExtensions()
    {
        const string text = """
            {"serverAddr":"frp.example.invalid","user":"dummy-provider-account","username":"dummy-root-username",
             "webServer":{"user":"dummy-admin-account"},"proxies":[{"name":"private","type":"stcp"}],
             "extension":{"UsErNaMe":"dummy-nested-username","items":[{"USER":"dummy-nested-user"}],
                          "userAgent":"not-a-credential-agent","usernameHint":"not-a-credential-hint"}}
            """;
        var native = FrpNativeConfig.Parse(text);
        Assert.Equal(5, native.Secrets.Count);
        foreach (var account in new[] { "dummy-provider-account", "dummy-root-username", "dummy-admin-account",
                     "dummy-nested-username", "dummy-nested-user" })
        {
            Assert.Contains(account, native.Secrets);
            Assert.DoesNotContain(account, native.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain(account, native.ToString(), StringComparison.Ordinal);
        }
        Assert.DoesNotContain("not-a-credential-agent", native.Secrets);
        Assert.DoesNotContain("not-a-credential-hint", native.Secrets);
        Assert.Equal("dummy-provider-account", Runtime(native)["user"]!.GetValue<string>());
        Assert.Equal("dummy-nested-username", Runtime(native)["extension"]!["UsErNaMe"]!.GetValue<string>());
    }

    [Fact]
    public void CredentialCollectionFollowsSensitiveObjectsAndArraysCaseInsensitively()
    {
        const string text = """
            {"serverAddr":"a","proxies":[{"name":"private","type":"stcp"}],"extension":{
              "AUTH":{"options":["dummy-auth-leaf", {"plain":"dummy-nested-auth-leaf"}]},
              "API_KEY":"dummy-api-key","Cookie":"dummy-cookie","Authorization":"dummy-authorization",
              "clientSecret":"dummy-client-secret","requestHeaders":{"X-Anything":"dummy-request-header"},
              "credentials":{"account":"dummy-account"},"password":"   ","plain":"not-a-secret"
            }}
            """;
        var secrets = FrpNativeConfig.Parse(text).Secrets;
        Assert.Equal(8, secrets.Count);
        Assert.DoesNotContain("   ", secrets);
        Assert.DoesNotContain("not-a-secret", secrets);
    }

    [Theory]
    [InlineData("", "dummy-password")]
    [InlineData("dummy:user", "dummy-password")]
    [InlineData("dummy-user", "")]
    [InlineData("dummy-user", "dummy\npassword")]
    public void InvalidHostCredentialsAreExplicitAndNeverEchoed(string user, string password)
    {
        var error = Assert.Throws<FrpConfigurationException>(() => FrpNativeConfig.Parse(PanelToml).CreateRuntimeConfig(user, password));
        Assert.DoesNotContain("dummy", error.Message, StringComparison.Ordinal);
    }

    private static JsonObject Runtime(FrpNativeConfig config) =>
        JsonNode.Parse(config.CreateRuntimeConfig("dummy-host-user", "dummy-host-password"))!.AsObject();
}
