using System.Text;
using DanmuApi.Platform;

namespace DanmuApi.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public void WritesSortedEscapedUtf8ValuesAndReadsThemBack()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings", "settings.properties");
        var store = new SettingsStore(path);
        var values = new Dictionary<string, string?>
        {
            ["z-last"] = "中文\\路径\nline",
            ["a-first"] = "value=with:separators\tand\\slash",
        };

        store.Write(values);

        var content = File.ReadAllText(path, new UTF8Encoding(false));
        Assert.StartsWith("a-first=", content, StringComparison.Ordinal);
        Assert.True(content.IndexOf("a-first=", StringComparison.Ordinal) < content.IndexOf("z-last=", StringComparison.Ordinal));
        Assert.Equal(values["a-first"], store.Read()["a-first"]);
        Assert.Equal(values["z-last"], store.Read()["z-last"]);
    }

    [Fact]
    public void RejectsMalformedInputInsteadOfResettingTheFile()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "settings.properties");
        File.WriteAllText(path, "valid=value\nmalformed\n", new UTF8Encoding(false));
        var store = new SettingsStore(path);

        Assert.Throws<FormatException>(() => store.Read());
        Assert.Contains("malformed", File.ReadAllText(path, new UTF8Encoding(false)), StringComparison.Ordinal);
    }
}
