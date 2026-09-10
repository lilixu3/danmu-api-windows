using DanmuApi.Core;

namespace DanmuApi.Tests;

public sealed class CoreVersionReaderTests
{
    [Fact]
    public void PrefersConfigsGlobalsOverCompatibilityAndPackageJson()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "configs"));
        Directory.CreateDirectory(Path.Combine(directory.Path, "config"));
        File.WriteAllText(Path.Combine(directory.Path, "configs", "globals.js"), "export const VERSION = '2.3.4';");
        File.WriteAllText(Path.Combine(directory.Path, "config", "globals.js"), "export const VERSION = '1.0.0';");
        File.WriteAllText(Path.Combine(directory.Path, "package.json"), "{\"version\":\"0.1.0\"}");

        Assert.Equal("2.3.4", CoreVersionReader.ReadVersion(directory.Path));
    }

    [Fact]
    public void UsesCompatibilityGlobalsThenPackageJson()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "config"));
        File.WriteAllText(Path.Combine(directory.Path, "config", "globals.js"), "version: \"3.0.1\"");

        Assert.Equal("3.0.1", CoreVersionReader.ReadVersion(directory.Path));

        File.Delete(Path.Combine(directory.Path, "config", "globals.js"));
        File.WriteAllText(Path.Combine(directory.Path, "package.json"), "{\"version\":\"4.5.6\"}");
        Assert.Equal("4.5.6", CoreVersionReader.ReadVersion(directory.Path));
    }

    [Fact]
    public void MissingMetadataReturnsNullAndInvalidPackageFails()
    {
        using var directory = new TemporaryDirectory();
        Assert.Null(CoreVersionReader.ReadVersion(directory.Path));

        File.WriteAllText(Path.Combine(directory.Path, "package.json"), "{\"version\":42}");
        Assert.Throws<System.Text.Json.JsonException>(() => CoreVersionReader.ReadVersion(directory.Path));
    }
}
