using DanmuApi.Core.Frp;

namespace DanmuApi.Tests;

/// <summary>
/// 粘贴导入（<see cref="FrpConfigText"/>）：JSON 与 TOML 都认，且两种格式共用同一套导入契约。
/// 验收样本是用户真实粘贴的那份服务商 frpc.toml（<see cref="FrpProviderSample"/>）。
/// </summary>
public sealed class FrpConfigTextTests
{
    [Fact]
    public void ProviderTomlImportsEverythingItManages()
    {
        var imported = FrpConfigText.Import(FrpProviderSample.ClientToml, FrpSettings.Default(9321));

        Assert.True(imported.Succeeded, string.Join("；", imported.Problems));
        var settings = imported.Settings!;
        Assert.Equal(FrpRole.Client, settings.Role);
        Assert.Equal(FrpProviderSample.ServerAddress, settings.Client.ServerAddress);
        Assert.Equal(1210, settings.Client.ServerPort);
        Assert.Equal(FrpProviderSample.User, settings.Client.User);
        Assert.Equal(FrpProxyKind.Tcp, settings.Client.ProxyKind);
        Assert.Equal(FrpProviderSample.ProxyName, settings.Client.ProxyName);
        Assert.Equal("127.0.0.1", settings.Client.LocalAddress);
        Assert.Equal(9321, settings.Client.LocalPort);
        Assert.Equal(9321, settings.Client.RemotePort);
        Assert.True(settings.Client.UseEncryption);
        Assert.True(settings.Client.UseCompression);
        // 这份配置没有 auth 段：Token 必须按"清除"回报，而不是静默保留旧值。
        Assert.Null(imported.Token);
        // 注释行（包括被注释掉的 transport.protocol）不是字段，不该被当成"认得出但不管理"报出来。
        Assert.Empty(imported.Unsupported);
        Assert.Contains(imported.AppliedDefaults, entry => entry.Contains("webServer.addr", StringComparison.Ordinal));
        Assert.Contains(imported.AppliedDefaults, entry => entry.Contains("auth.token", StringComparison.Ordinal));
    }

    [Fact]
    public void GeneratedTomlRoundTripsThroughThePasteImport()
    {
        // 应用自己写出的 frpc.toml（含 user、TLS 关、压缩开）必须能被粘贴导入还原成同一份配置。
        var settings = FrpSettings.Default(9321) with
        {
            Client = FrpSettings.Default(9321).Client with
            {
                ServerAddress = "frp.example.com",
                ServerPort = 7100,
                User = "panel-user",
                ProxyName = "my-danmu",
                RemotePort = 19321,
                UseCompression = true,
                UseEncryption = true,
                TransportTls = false,
            },
        };
        var toml = FrpConfigWriter.WriteClient(settings.Client, "s3cret", "admin", "local-pass");

        var imported = FrpConfigText.Import(toml, FrpSettings.Default(9321));

        Assert.True(imported.Succeeded, string.Join("；", imported.Problems));
        Assert.Equal("s3cret", imported.Token);
        var client = imported.Settings!.Client;
        Assert.Equal(settings.Client.ServerAddress, client.ServerAddress);
        Assert.Equal(settings.Client.ServerPort, client.ServerPort);
        Assert.Equal(settings.Client.User, client.User);
        Assert.Equal(settings.Client.ProxyName, client.ProxyName);
        Assert.Equal(settings.Client.RemotePort, client.RemotePort);
        Assert.Equal(settings.Client.UseCompression, client.UseCompression);
        Assert.Equal(settings.Client.UseEncryption, client.UseEncryption);
        Assert.Equal(settings.Client.TransportTls, client.TransportTls);
        Assert.Equal(settings.Client.AdminPort, client.AdminPort);
        // 本机管理接口的用户名/密码认得出但不管理：只报字段名，绝不把值带出来。
        Assert.Contains("webServer.user", imported.Unsupported);
        Assert.Contains("webServer.password", imported.Unsupported);
        Assert.DoesNotContain(imported.Unsupported, entry => entry.Contains("local-pass", StringComparison.Ordinal));
    }

    [Fact]
    public void JsonAndTomlTakeTheSameImportContract()
    {
        const string json = """
        {
          "serverAddr": "1.2.3.4",
          "serverPort": 7100,
          "user": "panel-user",
          "transport": { "tls": { "enable": false } },
          "auth": { "method": "token", "token": "s3cret" },
          "proxies": [
            { "name": "home", "type": "tcp", "localIP": "127.0.0.1", "localPort": 9321, "remotePort": 19321,
              "transport": { "useEncryption": true, "useCompression": true } }
          ]
        }
        """;
        const string toml = """
        serverAddr = "1.2.3.4"
        serverPort = 7100
        user = "panel-user"
        transport.tls.enable = false
        auth = { method = "token", token = "s3cret" }

        [[proxies]]
        name = "home"
        type = "tcp"
        localIP = "127.0.0.1"
        localPort = 9321
        remotePort = 19321
        transport.useEncryption = true
        transport.useCompression = true
        """;

        var fromJson = FrpConfigText.Import(json, FrpSettings.Default(9321));
        var fromToml = FrpConfigText.Import(toml, FrpSettings.Default(9321));

        Assert.True(fromJson.Succeeded, string.Join("；", fromJson.Problems));
        Assert.True(fromToml.Succeeded, string.Join("；", fromToml.Problems));
        // 逐字段比较不成立（CustomDomains 是列表，记录相等性按引用比），改成比较导出文本：
        // 同一份配置从两种格式进来，导出的 frp 原生 JSON 必须逐字节一致。
        Assert.Equal(
            FrpConfigJson.Export(fromJson.Settings!, fromJson.Token ?? string.Empty),
            FrpConfigJson.Export(fromToml.Settings!, fromToml.Token ?? string.Empty));
        Assert.Equal(fromJson.Token, fromToml.Token);
        Assert.Equal(fromJson.Unsupported.OrderBy(x => x, StringComparer.Ordinal), fromToml.Unsupported.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void ServerTomlImportsBindPortAndReportsTheClientOnlyUserField()
    {
        const string toml = """
        bindPort = 7000
        vhostHTTPPort = 8080
        subdomainHost = 'example.com'
        user = "panel-user"
        """;

        var imported = FrpConfigText.Import(toml, FrpSettings.Default(9321));

        Assert.True(imported.Succeeded, string.Join("；", imported.Problems));
        Assert.Equal(FrpRole.Server, imported.Settings!.Role);
        Assert.Equal(7000, imported.Settings.Server.BindPort);
        Assert.Equal(8080, imported.Settings.Server.VhostHttpPort);
        Assert.Equal("example.com", imported.Settings.Server.SubdomainHost);
        // user 只属于 frpc（官方 frps 会以 unknown field "user" 拒绝它），不能悄悄丢掉。
        Assert.Contains("user", imported.Unsupported);
    }

    [Fact]
    public void TomlKeepsSecretsOutOfProblemMessages()
    {
        var imported = FrpConfigText.Import(
            "serverAddr = \"1.2.3.4\"\nauth.token = \"super-secret-value\"\nserverPort = bad\n",
            FrpSettings.Default(9321));

        Assert.False(imported.Succeeded);
        Assert.DoesNotContain(imported.Problems, problem => problem.Contains("super-secret-value", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyPasteSaysSoInsteadOfPretendingItImported()
    {
        var imported = FrpConfigText.Import("   ", FrpSettings.Default(9321));

        Assert.False(imported.Succeeded);
        Assert.Contains("粘贴内容为空", imported.Problems[0]);
    }
}

/// <summary>TOML 子集解析器的边界：支持什么、拒绝什么，都必须是明确的。</summary>
public sealed class FrpConfigTomlTests
{
    [Fact]
    public void ParsesDottedKeysInlineTablesAndLiteralStrings()
    {
        const string text = """
        # 面板生成的配置
        serverAddr="1.2.3.4"   # 行尾注释
        serverPort = 1210
        transport.tls.enable = false
        auth = { method = "token", token = "s3cret" }

        [[proxies]]
        name = 'literal-name'
        type = "tcp"
        localPort = 9321
        remotePort = 9321
        customDomains = [
          "a.example.com",   # 多行数组 + 尾随逗号
          "b.example.com",
        ]
        """;

        var ok = FrpConfigToml.TryParse(text, out var document, out var problems);

        Assert.True(ok, string.Join("；", problems));
        Assert.Equal("1.2.3.4", document["serverAddr"]!.GetValue<string>());
        Assert.Equal(1210, document["serverPort"]!.GetValue<long>());
        Assert.False(document["transport"]!["tls"]!["enable"]!.GetValue<bool>());
        Assert.Equal("s3cret", document["auth"]!["token"]!.GetValue<string>());
        var proxy = Assert.IsType<System.Text.Json.Nodes.JsonArray>(document["proxies"]);
        var first = Assert.IsType<System.Text.Json.Nodes.JsonObject>(proxy[0]);
        Assert.Equal("literal-name", first["name"]!.GetValue<string>());
        var domains = Assert.IsType<System.Text.Json.Nodes.JsonArray>(first["customDomains"]);
        Assert.Equal(2, domains.Count);
    }

    [Fact]
    public void StringValuesKeepHashAndEqualsCharacters()
    {
        // 井号/等号出现在字符串里时是普通字符，不能被当成注释或键分隔符。
        var ok = FrpConfigToml.TryParse(
            "auth.token = \"a#b=c\"\nserverAddr = \"1.2.3.4\"\n",
            out var document,
            out var problems);

        Assert.True(ok, string.Join("；", problems));
        Assert.Equal("a#b=c", document["auth"]!["token"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("serverAddr = \"\"\"x\"\"\"", "多行字符串")]
    [InlineData("serverPort = 7000.5", "小数")]
    [InlineData("serverPort = 2026-10-04", "日期")]
    [InlineData("serverPort = 0x10", "十六进制")]
    [InlineData("serverAddr = \"1.2.3.4", "没有闭合")]
    [InlineData("serverAddr", "缺少 '='")]
    [InlineData("serverAddr = \"a\"\nserverAddr = \"b\"", "重复的键")]
    [InlineData("[proxies]\nname = \"a\"\n[proxies]\nname = \"b\"", "重复定义")]
    [InlineData("serverAddr = \"1.2.3.4\"\nserverPort = 7000 trailing", "多余内容")]
    [InlineData("= \"1.2.3.4\"", "键名")]
    public void RejectsUnsupportedTomlWithAReason(string text, string expected)
    {
        var ok = FrpConfigToml.TryParse(text, out _, out var problems);

        Assert.False(ok);
        Assert.Contains(problems, problem => problem.Contains(expected, StringComparison.Ordinal));
        // 每个问题都要能定位到行，用户才知道去哪里改。
        Assert.All(problems, problem => Assert.Contains("行", problem, StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsTheLineNumberOfTheProblem()
    {
        var ok = FrpConfigToml.TryParse(
            "serverAddr = \"1.2.3.4\"\nserverPort = 1210\n# 注释\n[[proxies]]\ntype = 1.5\n",
            out _,
            out var problems);

        Assert.False(ok);
        Assert.Contains(problems, problem => problem.Contains("第 5 行", StringComparison.Ordinal));
    }
}
