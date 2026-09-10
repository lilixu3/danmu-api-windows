using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class DotEnvTransactionTests
{
    [Fact]
    public void SetEmptyAndDeleteHaveDifferentSemantics()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, ".env");
        File.WriteAllText(path, "TITLE_NOISE_FILTER=pattern\nOTHER=kept\n", new System.Text.UTF8Encoding(false));

        var initial = DotEnvFile.GetFingerprint(path);
        var afterSet = DotEnvFile.ApplyMutations(path, initial, [DotEnvMutation.Set("TITLE_NOISE_FILTER", string.Empty)]);

        Assert.True(afterSet.Values.ContainsKey("TITLE_NOISE_FILTER"));
        Assert.Equal(string.Empty, afterSet.Values["TITLE_NOISE_FILTER"]);

        var afterDelete = DotEnvFile.ApplyMutations(path, afterSet.Fingerprint, [DotEnvMutation.Delete("TITLE_NOISE_FILTER")]);

        Assert.False(afterDelete.Values.ContainsKey("TITLE_NOISE_FILTER"));
        Assert.Contains("OTHER=kept", File.ReadAllText(path));
    }

    [Fact]
    public void DetectsExternalChangesBeforeReplacingFile()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, ".env");
        File.WriteAllText(path, "A=one\n", new System.Text.UTF8Encoding(false));
        var fingerprint = DotEnvFile.GetFingerprint(path);
        File.WriteAllText(path, "A=changed\n", new System.Text.UTF8Encoding(false));

        var error = Assert.Throws<DotEnvConflictException>(() =>
            DotEnvFile.ApplyMutations(path, fingerprint, [DotEnvMutation.Set("A", "two")]));

        Assert.NotEqual(error.Expected.Sha256, error.Actual.Sha256);
        Assert.Equal("changed", DotEnvFile.ReadValue(path, "A"));
    }

    [Fact]
    public void PreservesCommentsAndAppliesBatchAtomically()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, ".env");
        File.WriteAllText(path, "# keep\nA=one\nOTHER=value\n", new System.Text.UTF8Encoding(false));

        var result = DotEnvFile.ApplyMutations(
            path,
            DotEnvFile.GetFingerprint(path),
            [DotEnvMutation.Set("A", "two words"), DotEnvMutation.Set("B", "new")]);

        Assert.Equal("two words", result.Values["A"]);
        Assert.Equal("new", result.Values["B"]);
        var content = File.ReadAllText(path);
        Assert.Contains("# keep", content);
        Assert.Contains("OTHER=value", content);
    }

    [Fact]
    public void RejectsDuplicateExistingKeysWithoutChangingFile()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, ".env");
        var original = "A=one\nA=two\n";
        File.WriteAllText(path, original, new System.Text.UTF8Encoding(false));

        Assert.Throws<FormatException>(() => DotEnvFile.ApplyMutations(
            path,
            DotEnvFile.GetFingerprint(path),
            [DotEnvMutation.Set("A", "three")]));
        Assert.Equal(original, File.ReadAllText(path));
    }
}
