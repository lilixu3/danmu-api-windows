using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class DotEnvFileTests
{
    [Fact]
    public void UpdatesHostKeysPreservingCommentsAndToken()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "config", ".env");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "# keep\nTOKEN=87654321\nDANMU_API_PORT=1\nDANMU_API_HOST=old\n\nOTHER=value\n", new System.Text.UTF8Encoding(false));

        DotEnvFile.UpdateValues(path, new Dictionary<string, string?>
        {
            ["DANMU_API_PORT"] = "9321",
            ["DANMU_API_HOST"] = "0.0.0.0",
            ["DANMU_API_VARIANT"] = "stable",
        });

        var values = DotEnvFile.ReadValues(path);
        Assert.Equal("87654321", values["TOKEN"]);
        Assert.Equal("9321", values["DANMU_API_PORT"]);
        Assert.Equal("0.0.0.0", values["DANMU_API_HOST"]);
        Assert.Equal("stable", values["DANMU_API_VARIANT"]);
        Assert.DoesNotContain("\n\n", File.ReadAllText(path));
        Assert.Contains("# keep", File.ReadAllText(path));
    }

    [Fact]
    public void QuotesAndUnquotesSpecialValues()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, ".env");
        var value = "text with spaces\nline\t\\quote\"";

        DotEnvFile.UpdateValues(path, new Dictionary<string, string?> { ["SPECIAL"] = value });

        Assert.Equal(value, DotEnvFile.ReadValue(path, "special"));
    }

    [Fact]
    public void EmptyValueDeletesExistingKeyAndNullAlsoDeletes()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, ".env");
        File.WriteAllText(path, "ADMIN_TOKEN=old\nOTHER=kept\n", new System.Text.UTF8Encoding(false));

        DotEnvFile.UpdateValues(path, new Dictionary<string, string?> { ["ADMIN_TOKEN"] = string.Empty });

        Assert.Null(DotEnvFile.ReadValue(path, "ADMIN_TOKEN"));
        Assert.Equal("kept", DotEnvFile.ReadValue(path, "OTHER"));
    }

    [Fact]
    public void RejectsInvalidEnvironmentKey()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, ".env");

        Assert.Throws<ArgumentException>(() => DotEnvFile.UpdateValues(path, new Dictionary<string, string?> { ["not-valid"] = "x" }));
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "danmu-api-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
