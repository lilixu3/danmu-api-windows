using System.Security.Cryptography;
using System.Text;

namespace DanmuApi.Platform;

/// <summary>A protected UTF-8 document, preserved byte-for-byte (unlike short, trimmed secrets).</summary>
public interface IProtectedDocumentStore
{
    string? Load();
    void Save(string document);
    void Clear();
}

/// <summary>Current-user DPAPI storage for the independent frp text source. No plaintext staging file.</summary>
public sealed class WindowsProtectedDocumentStore : IProtectedDocumentStore
{
    public const int MaxDocumentBytes = 32 * 1024;
    public const int MaxProtectedBytes = 64 * 1024;
    public const string FrpTextEntropy = "DanmuApi.Windows.FrpConfigText.v1";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string _path;
    private readonly byte[] _entropy;

    public WindowsProtectedDocumentStore(string path, string entropyLabel = FrpTextEntropy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(entropyLabel);
        _path = Path.GetFullPath(path);
        _entropy = Utf8.GetBytes(entropyLabel);
    }

    public string? Load()
    {
        if (!File.Exists(_path)) return null;
        byte[]? protectedBytes = null;
        byte[]? plainBytes = null;
        try
        {
            using (var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length is <= 0 or > MaxProtectedBytes)
                    throw new IOException("受保护配置文档密文大小无效（上限 64 KiB）");
                protectedBytes = new byte[(int)file.Length];
                file.ReadExactly(protectedBytes);
                if (file.ReadByte() != -1) throw new IOException("受保护配置文档在读取期间被修改");
            }
            plainBytes = Dpapi.Unprotect(protectedBytes, _entropy);
            if (plainBytes.Length is <= 0 or > MaxDocumentBytes)
                throw new IOException("受保护配置文档大小无效（上限 32 KiB UTF-8）");
            return Utf8.GetString(plainBytes);
        }
        catch (DecoderFallbackException error)
        {
            throw new IOException("受保护配置文档不是有效 UTF-8", error);
        }
        finally
        {
            if (plainBytes is not null) CryptographicOperations.ZeroMemory(plainBytes);
            if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    public void Save(string document)
    {
        ArgumentNullException.ThrowIfNull(document);
        byte[]? plainBytes = null;
        byte[]? protectedBytes = null;
        string? temporary = null;
        try
        {
            var size = Utf8.GetByteCount(document);
            if (size is <= 0 or > MaxDocumentBytes)
                throw new ArgumentException("配置文档大小必须在 1 到 32768 UTF-8 字节之间", nameof(document));
            plainBytes = Utf8.GetBytes(document);
            protectedBytes = Dpapi.Protect(plainBytes, _entropy);
            if (protectedBytes.Length is <= 0 or > MaxProtectedBytes)
                throw new IOException("受保护配置文档密文大小无效（上限 64 KiB）");
            var directory = Path.GetDirectoryName(_path) ?? throw new IOException("受保护配置文档没有父目录");
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $"{Path.GetFileName(_path)}.tmp-{Guid.NewGuid():N}");
            File.WriteAllBytes(temporary, protectedBytes);
            File.Move(temporary, _path, overwrite: true);
            if (!string.Equals(Load(), document, StringComparison.Ordinal))
                throw new IOException("受保护配置文档回读校验失败");
        }
        catch (EncoderFallbackException error)
        {
            throw new ArgumentException("配置文档不是有效 UTF-8", nameof(document), error);
        }
        finally
        {
            if (plainBytes is not null) CryptographicOperations.ZeroMemory(plainBytes);
            if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes);
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Clear()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
