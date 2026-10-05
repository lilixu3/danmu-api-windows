using System.Text.Json.Nodes;
using DanmuApi.Core.Frp;

namespace DanmuApi.Tests;

public sealed class FrpStrictTomlTests
{
    [Theory]
    [InlineData("auth = { method = 'token' }\nauth.token = 'dummy-secret-token'", 2)]
    [InlineData("auth = { method = 'token' }\n[auth]\ntoken = 'dummy-secret-token'", 2)]
    [InlineData("auth = { oidc = { audience = 'sample' } }\n[auth.oidc]\nclientSecret = 'dummy-secret-token'", 2)]
    [InlineData("extension = { nested = {} }\n[[extension.nested.items]]\nname = 'sample'", 2)]
    [InlineData("extension = { nested.enabled = true }\nextension.nested.other = true", 2)]
    [InlineData("auth.method = 'token'\n[auth]\ntoken = 'dummy-secret-token'", 2)]
    [InlineData("extension.options.enabled = true\n[extension.options]\nother = true", 2)]
    [InlineData("extension.options.enabled = true\n[extension]\nother = true", 2)]
    public void InlineTablesAreSealedAndDottedDefinitionsCannotBeReopened(string text, int line)
    {
        AssertSafeRejection(text, line);
    }

    [Theory]
    [InlineData("00")]
    [InlineData("07000")]
    [InlineData("0_0")]
    [InlineData("+07000")]
    [InlineData("-07000")]
    [InlineData("+0_0")]
    public void DecimalIntegersNeverNormalizeForbiddenLeadingZeros(string literal)
    {
        AssertSafeRejection("serverPort = " + literal, 1);
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("+0", 0L)]
    [InlineData("-0", 0L)]
    [InlineData("7_000", 7000L)]
    [InlineData("+7_000", 7000L)]
    [InlineData("-7_000", -7000L)]
    public void ValidDecimalIntegersStillParse(string literal, long expected)
    {
        Assert.True(FrpConfigToml.TryParse("number = " + literal, out var document, out var problems), string.Join(";", problems));
        Assert.Equal(expected, document["number"]!.GetValue<long>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(31)]
    [InlineData(127)]
    public void ForbiddenRawControlsAreRejectedInBothSingleLineStringKinds(int codePoint)
    {
        foreach (var quote in new[] { "'", "\"" })
            AssertSafeRejection("auth.token = " + quote + "dummy-secret" + (char)codePoint + "value" + quote, 1);
    }

    [Fact]
    public void TabsAndExplicitBasicStringControlEscapesAreNotRejectedAsRawControls()
    {
        const string text = "literal = 'with\ttab'\nbasic = \"with\ttab\"\nescaped = \"\\u0001\\b\\n\\r\\f\"";
        Assert.True(FrpConfigToml.TryParse(text, out var document, out var problems), string.Join(";", problems));
        Assert.Equal("with\ttab", document["literal"]!.GetValue<string>());
        Assert.Equal("with\ttab", document["basic"]!.GetValue<string>());
        Assert.Equal("\u0001\b\n\r\f", document["escaped"]!.GetValue<string>());
    }

    [Fact]
    public void ImplicitHeaderParentsAndDottedTableSiblingsCanStillBeDefined()
    {
        const string text = """
            [extension.options]
            enabled = true
            [extension]
            title = "sample"
            other.value = 1
            [extension.other.extra]
            enabled = false
            """;
        Assert.True(FrpConfigToml.TryParse(text, out var document, out var problems), string.Join(";", problems));
        Assert.True(document["extension"]!["options"]!["enabled"]!.GetValue<bool>());
        Assert.Equal("sample", document["extension"]!["title"]!.GetValue<string>());
        Assert.Equal(1L, document["extension"]!["other"]!["value"]!.GetValue<long>());
        Assert.False(document["extension"]!["other"]!["extra"]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void InlineDottedSiblingsRemainValidWithinOneDefinition()
    {
        Assert.True(FrpConfigToml.TryParse("extension = { options.first = true, options.second = false }", out var document, out var problems), string.Join(";", problems));
        Assert.True(document["extension"]!["options"]!["first"]!.GetValue<bool>());
        Assert.False(document["extension"]!["options"]!["second"]!.GetValue<bool>());
    }

    [Fact]
    public void ConventionalNestedTablesStayScopedToTheirOwnProxyAndArrayItem()
    {
        const string text = """
            serverAddr = "127.0.0.1"
            [[proxies]]
            name = "first"
            type = "tcp"
            localPort = 9321
            [proxies.transport]
            useEncryption = true
            [[proxies.extensions]]
            name = "item-one"
            [proxies.extensions.options]
            enabled = true
            [[proxies.extensions]]
            name = "item-two"
            [proxies.extensions.options]
            enabled = false
            [[proxies]]
            name = "second"
            type = "tcp"
            localPort = 9322
            [proxies.transport]
            useCompression = true
            """;
        Assert.True(FrpConfigToml.TryParse(text, out var document, out var problems), string.Join(";", problems));
        var proxies = document["proxies"]!.AsArray();
        Assert.Equal(2, proxies.Count);
        Assert.True(proxies[0]!["transport"]!["useEncryption"]!.GetValue<bool>());
        Assert.False(proxies[0]!["transport"]!.AsObject().ContainsKey("useCompression"));
        Assert.True(proxies[1]!["transport"]!["useCompression"]!.GetValue<bool>());
        Assert.False(proxies[1]!["transport"]!.AsObject().ContainsKey("useEncryption"));
        var extensions = proxies[0]!["extensions"]!.AsArray();
        Assert.Equal(2, extensions.Count);
        Assert.True(extensions[0]!["options"]!["enabled"]!.GetValue<bool>());
        Assert.False(extensions[1]!["options"]!["enabled"]!.GetValue<bool>());
        Assert.Equal(2, FrpNativeConfig.Parse(text).Proxies.Count);
    }

    [Theory]
    [InlineData("auth.token = true_dummy_secret_marker")]
    [InlineData("auth.token = false_dummy_secret_marker")]
    [InlineData("\"dummy-secret-key\"")]
    [InlineData("\"dummy-secret-key\" = 1\n\"dummy-secret-key\" = 2")]
    [InlineData("\"dummy-secret-key\" = 1\n[\"dummy-secret-key\"]")]
    [InlineData("\"dummy-secret-key\" = true\n\"dummy-secret-key\".child = 1")]
    [InlineData("serverPort = 9999999999999999999999999999")]
    public void ImportDiagnosticsNeverEchoSourceKeysValuesOrPartialSecretMarkers(string text)
    {
        var imported = FrpConfigText.Import(text, FrpSettings.Default(9321));
        Assert.False(imported.Succeeded);
        Assert.NotEmpty(imported.Problems);
        Assert.All(imported.Problems, problem =>
        {
            Assert.Contains("行", problem, StringComparison.Ordinal);
            Assert.DoesNotContain("dummy", problem, StringComparison.Ordinal);
            Assert.DoesNotContain("999999", problem, StringComparison.Ordinal);
        });
    }

    private static void AssertSafeRejection(string text, int line)
    {
        Assert.False(FrpConfigToml.TryParse(text, out _, out var problems));
        Assert.NotEmpty(problems);
        Assert.All(problems, problem => Assert.Contains($"第 {line} 行", problem, StringComparison.Ordinal));
        Assert.All(problems, problem => Assert.DoesNotContain("dummy-secret", problem, StringComparison.Ordinal));
        var error = Assert.Throws<FrpConfigurationException>(() => FrpNativeConfig.Parse("serverAddr = '127.0.0.1'\n" + text));
        Assert.Contains($"第 {line + 1} 行", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("dummy-secret", error.ToString(), StringComparison.Ordinal);
    }
}
