using System.Text;

namespace DanmuApi.Platform;

/// <summary>DPAPI 保护的单个短字符串秘密（管理员会话令牌等）的本地存取。</summary>
public interface IProtectedStringStore
{
    /// <summary>读取明文；存储不存在时返回 null。解密失败显式抛 IOException。</summary>
    string? Load();
    void Save(string value);
    void Clear();
}

public sealed class WindowsProtectedStringStore : IProtectedStringStore
{
    private readonly byte[] _entropy;
    private readonly string _path;

    public WindowsProtectedStringStore(string path, string entropyLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(entropyLabel);
        _path = Path.GetFullPath(path);
        _entropy = Encoding.UTF8.GetBytes(entropyLabel);
    }

    public string? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        byte[] protectedBytes;
        try
        {
            protectedBytes = File.ReadAllBytes(_path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"读取受保护存储失败：{_path}", error);
        }

        if (protectedBytes.Length is 0 or > 64 * 1024)
        {
            throw new IOException("受保护存储内容大小无效");
        }

        var plainBytes = Dpapi.Unprotect(protectedBytes, _entropy);
        try
        {
            var value = new UTF8Encoding(false, true).GetString(plainBytes).Trim();
            return value.Length == 0 ? null : value;
        }
        catch (DecoderFallbackException error)
        {
            throw new IOException("受保护存储内容不是有效 UTF-8", error);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    public void Save(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (normalized.Length > 4096)
        {
            throw new ArgumentException("受保护字符串长度无效", nameof(value));
        }

        var plainBytes = Encoding.UTF8.GetBytes(normalized);
        byte[] protectedBytes;
        try
        {
            protectedBytes = Dpapi.Protect(plainBytes, _entropy);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plainBytes);
        }

        var directory = Path.GetDirectoryName(_path) ?? throw new IOException($"受保护存储没有父目录：{_path}");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"{Path.GetFileName(_path)}.tmp-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(temporary, protectedBytes);
            File.Move(temporary, _path, overwrite: true);
            if (!string.Equals(Load(), normalized, StringComparison.Ordinal))
            {
                throw new IOException("受保护存储回读校验失败");
            }
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(protectedBytes);
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public void Clear()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            File.Delete(_path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"清除受保护存储失败：{_path}", error);
        }
    }
}
