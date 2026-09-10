using DanmuApi.Platform;

namespace DanmuApi.Tests;

public sealed class WindowsGithubTokenStoreTests
{
    [Fact]
    public void SavesEncryptedTokenAndRoundTripsForCurrentUser()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "github-token.dat");
        var store = new WindowsGithubTokenStore(path);
        const string token = "github-test-token-that-must-not-be-plaintext";

        store.Save("Bearer " + token);

        Assert.True(store.IsConfigured);
        Assert.Equal(token, store.GetToken());
        var stored = File.ReadAllBytes(path);
        Assert.DoesNotContain(token, System.Text.Encoding.UTF8.GetString(stored), StringComparison.Ordinal);
    }

    [Fact]
    public void ClearIsIdempotent()
    {
        using var directory = new TemporaryDirectory();
        var store = new WindowsGithubTokenStore(Path.Combine(directory.Path, "github-token.dat"));
        store.Save("test-token");

        store.Clear();
        store.Clear();

        Assert.False(store.IsConfigured);
        Assert.Null(store.GetToken());
    }

    [Fact]
    public void CorruptCiphertextFailsExplicitly()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "github-token.dat");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        var store = new WindowsGithubTokenStore(path);

        var error = Assert.Throws<IOException>(() => store.GetToken());

        Assert.Contains("DPAPI", error.Message, StringComparison.Ordinal);
    }
}
