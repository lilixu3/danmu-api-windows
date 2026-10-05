using System.Text;
using DanmuApi.Platform;

namespace DanmuApi.Tests;

public sealed class ProtectedDocumentStoreTests
{
    [Fact]
    public void PreservesWhitespaceAndLongUnicodeDocumentWithoutPlaintext()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "frp-config-text.dat");
        var document = "\uFEFF \r\n" + new string('中', 5000) + "\r\n secret-document-marker \t\n";
        var store = new WindowsProtectedDocumentStore(path);
        store.Save(document);
        Assert.Equal(document, store.Load());
        Assert.DoesNotContain("secret-document-marker", Encoding.UTF8.GetString(File.ReadAllBytes(path)), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp-*"));
        Assert.Throws<IOException>(() => new WindowsProtectedDocumentStore(path, "different-entropy").Load());
        store.Clear();
        Assert.Null(store.Load());
    }

    [Fact]
    public void LimitsByUtf8BytesAndRejectsCorruptionExplicitly()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "frp-config-text.dat");
        var store = new WindowsProtectedDocumentStore(path);
        var exact = new string('a', WindowsProtectedDocumentStore.MaxDocumentBytes);
        store.Save(exact);
        Assert.Equal(exact, store.Load());
        Assert.Throws<ArgumentException>(() => store.Save(exact + "a"));
        Assert.Throws<ArgumentException>(() => store.Save(new string('中', 11_000)));
        Assert.Throws<ArgumentException>(() => store.Save("bad\uD800"));
        Assert.Equal(exact, store.Load());
        File.WriteAllBytes(path, new byte[WindowsProtectedDocumentStore.MaxProtectedBytes + 1]);
        Assert.Throws<IOException>(() => store.Load());
        File.WriteAllBytes(path, [1, 2, 3]);
        Assert.Throws<IOException>(() => store.Load());
    }
}
