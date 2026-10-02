using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DanmuApi.Platform;

namespace DanmuApi.Tests;

public sealed class OutboundSettingsStoreTests
{
    [Fact]
    public void MissingFileHasLegalDisabledDefaultsWithoutCreatingFiles()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(Path.Combine(directory.Path, "中文 空格"), Path.Combine(directory.Path, "appdata"));
        var store = new OutboundSettingsStore(paths);
        var settings = store.Read();
        Assert.False(settings.Enabled);
        Assert.Equal(new[] { "bahamut", "tmdb", "dandan", "animeko" }, settings.Sources);
        Assert.Equal("auto", settings.HttpVersion);
        Assert.Equal("", settings.DohUrl);
        Assert.Equal(3000, settings.ConnectTimeoutMs);
        Assert.Equal(1, settings.SchemaVersion);
        Assert.Equal(Path.Combine(paths.NodeProjectDirectory, "config", "outbound", "settings.json"), store.SettingsPath);
        Assert.False(Directory.Exists(store.DirectoryPath));
    }

    [Fact]
    public void WritesCompleteJsonAtomicallyAndPreservesExplicitValuesOnReadback()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);
        var expected = new OutboundSettings(true, new[] { "animeko", "bahamut" }, "h3", "https://dns.example/dns-query", 60000);
        store.Write(expected);
        Assert.True(OutboundSettings.Equivalent(expected, store.Read()));
        using var json = JsonDocument.Parse(File.ReadAllBytes(store.SettingsPath));
        Assert.Equal(6, json.RootElement.EnumerateObject().Count());
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("h3", json.RootElement.GetProperty("httpVersion").GetString());
        Assert.Equal(expected.Sources, store.Read().Sources);
        Assert.Empty(Directory.GetFiles(store.DirectoryPath, "*.tmp-*"));
        store.Write(expected with { Enabled = false, HttpVersion = "h2", Sources = Array.Empty<string>(), ConnectTimeoutMs = 1 });
        Assert.Empty(store.Read().Sources);
        Assert.Equal("h2", store.Read().HttpVersion);
    }

    [Theory]
    [InlineData("schemaVersion", "2")]
    [InlineData("schemaVersion", "1.1")]
    [InlineData("schemaVersion", "\"1\"")]
    [InlineData("enabled", "\"false\"")]
    [InlineData("enabled", "null")]
    [InlineData("sources", "[\"unknown\"]")]
    [InlineData("sources", "[\"BAHAMUT\"]")]
    [InlineData("sources", "[\"bahamut\",\"bahamut\"]")]
    [InlineData("sources", "[true]")]
    [InlineData("sources", "null")]
    [InlineData("httpVersion", "\"h1\"")]
    [InlineData("httpVersion", "true")]
    [InlineData("dohUrl", "null")]
    [InlineData("dohUrl", "\"http://dns.example/dns-query\"")]
    [InlineData("dohUrl", "\"https://user:private-password@dns.example/dns-query\"")]
    [InlineData("dohUrl", "\"https://dns.example/dns-query#private-fragment\"")]
    [InlineData("dohUrl", "\"https://dns.example/dns-query#\"")]
    [InlineData("dohUrl", "\" https://dns.example/dns-query\"")]
    [InlineData("connectTimeoutMs", "0")]
    [InlineData("connectTimeoutMs", "60001")]
    [InlineData("connectTimeoutMs", "3000.5")]
    [InlineData("connectTimeoutMs", "\"3000\"")]
    public void InvalidExistingFieldsThrowAndRetainTheDocument(string field, string replacement)
    {
        using var directory = new TemporaryDirectory();
        var diagnostics = new List<string>();
        var store = new OutboundSettingsStore(new AppPaths(directory.Path, Path.Combine(directory.Path, "appdata")), diagnostics.Add);
        Directory.CreateDirectory(store.DirectoryPath);
        var document = JsonNode.Parse(JsonSerializer.Serialize(OutboundSettings.Default))!.AsObject();
        document[field] = JsonNode.Parse(replacement);
        var content = document.ToJsonString();
        File.WriteAllText(store.SettingsPath, content, new UTF8Encoding(false));
        Assert.Throws<FormatException>(() => store.Read());
        Assert.Equal(content, File.ReadAllText(store.SettingsPath));
        Assert.NotEmpty(diagnostics);
        Assert.NotNull(store.LastDiagnostic);
        Assert.DoesNotContain("private-password", store.LastDiagnostic!, StringComparison.Ordinal);
        Assert.DoesNotContain("private-fragment", store.LastDiagnostic!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("enabled")]
    [InlineData("sources")]
    [InlineData("httpVersion")]
    [InlineData("dohUrl")]
    [InlineData("connectTimeoutMs")]
    public void EachRequiredFieldMustExist(string field)
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);
        var json = JsonNode.Parse(JsonSerializer.Serialize(OutboundSettings.Default))!.AsObject();
        json.Remove(field);
        Directory.CreateDirectory(store.DirectoryPath);
        File.WriteAllText(store.SettingsPath, json.ToJsonString());
        Assert.Throws<FormatException>(() => store.Read());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("{secret-is-not-valid-json")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"enabled\":false,\"sources\":[],\"httpVersion\":\"auto\",\"dohUrl\":\"\",\"connectTimeoutMs\":3000}")]
    [InlineData("{\"schemaVersion\":1,\"enabled\":false,\"sources\":[],\"httpVersion\":\"auto\",\"dohUrl\":\"\",\"connectTimeoutMs\":3000,\"token\":\"private-value\"}")]
    public void MalformedDuplicateAndUnknownFieldsNeverBecomeDefaults(string content)
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);
        Directory.CreateDirectory(store.DirectoryPath);
        File.WriteAllText(store.SettingsPath, content);
        Assert.Throws<FormatException>(() => store.Read());
        Assert.Equal(content, File.ReadAllText(store.SettingsPath));
        Assert.DoesNotContain("private-value", store.LastDiagnostic!, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-is-not-valid-json", store.LastDiagnostic!, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidSaveAndEnabledEmptySelectionDoNotTouchExistingFile()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);
        var expected = new OutboundSettings(true, new[] { "tmdb" }, "h2", "https://dns.example/custom", 999);
        store.Write(expected);
        var original = File.ReadAllBytes(store.SettingsPath);
        var invalid = new[]
        {
            expected with { Sources = Array.Empty<string>() },
            expected with { Sources = new[] { "tmdb", "tmdb" } },
            expected with { Sources = new[] { "uncovered-host" } },
            expected with { ConnectTimeoutMs = 0 },
            expected with { DohUrl = "https://user:private-password@dns.example/" },
            expected with { SchemaVersion = 2 },
        };
        foreach (var settings in invalid)
        {
            Assert.Throws<FormatException>(() => store.Write(settings));
            Assert.Equal(original, File.ReadAllBytes(store.SettingsPath));
        }
        Assert.Empty(Directory.GetFiles(store.DirectoryPath, "*.tmp-*"));
    }

    [Fact]
    public void LockedDestinationReportsMoveFailureAndKeepsUserValues()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);
        store.Write(OutboundSettings.Default);
        var expected = File.ReadAllBytes(store.SettingsPath);
        using (var locked = new FileStream(store.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // Windows can reject overwrite without delete sharing as access denied or sharing violation.
            const int accessDenied = unchecked((int)0x80070005);
            const int sharingViolation = unchecked((int)0x80070020);
            var error = Record.Exception(() => store.Write(OutboundSettings.Default with { Enabled = true }));
            Assert.True(error is UnauthorizedAccessException { HResult: accessDenied } or
                IOException { HResult: sharingViolation },
                $"预期文件替换被拒绝或共享冲突，实际：{error?.GetType().Name ?? "未抛异常"}，HRESULT：{error?.HResult:X8}");
            Assert.Equal(expected, File.ReadAllBytes(store.SettingsPath));
        }
        Assert.Contains("保存", store.LastDiagnostic!, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(store.DirectoryPath, "*.tmp-*"));
        Assert.False(store.Read().Enabled);
    }

    [Fact]
    public void OversizedSettingsFailWithDiagnostic()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);
        Directory.CreateDirectory(store.DirectoryPath);
        File.WriteAllText(store.SettingsPath, new string(' ', OutboundSettingsStore.MaxDocumentBytes + 1));
        Assert.Throws<FormatException>(() => store.Read());
        Assert.Contains("1 MiB", store.LastDiagnostic!, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitEmptyDisabledSelectionIsPreservedInsteadOfBeingExpanded()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);
        store.Write(OutboundSettings.Default with { Sources = Array.Empty<string>() });
        Assert.Empty(store.Read().Sources);
    }

    private static OutboundSettingsStore CreateStore(TemporaryDirectory directory) =>
        new(new AppPaths(directory.Path, Path.Combine(directory.Path, "appdata")));
}
