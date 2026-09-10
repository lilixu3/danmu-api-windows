using System.Security.Cryptography;
using System.Text;
using DanmuApi.Core;

namespace DanmuApi.Platform;

public sealed class WindowsGithubTokenStore : IGithubTokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DanmuApi.Windows.GithubToken.v1");
    private readonly string _path;

    public WindowsGithubTokenStore(string path)
    {
        _path = Path.GetFullPath(path);
    }

    public bool IsConfigured => File.Exists(_path);

    public string? GetToken()
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
            throw new IOException($"读取 GitHub Token 安全存储失败：{_path}", error);
        }

        if (protectedBytes.Length is 0 or > 64 * 1024)
        {
            throw new IOException("GitHub Token 安全存储大小无效");
        }

        var plainBytes = Unprotect(protectedBytes, Entropy);
        try
        {
            var token = new UTF8Encoding(false, true).GetString(plainBytes).Trim();
            if (token.Length == 0)
            {
                throw new IOException("GitHub Token 安全存储内容为空");
            }

            return token;
        }
        catch (DecoderFallbackException error)
        {
            throw new IOException("GitHub Token 安全存储内容不是有效 UTF-8", error);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    public void Save(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var normalized = NormalizeToken(token);
        if (normalized.Length is 0 or > 4096)
        {
            throw new ArgumentException("GitHub Token 长度无效", nameof(token));
        }

        var plainBytes = Encoding.UTF8.GetBytes(normalized);
        byte[] protectedBytes;
        try
        {
            protectedBytes = Protect(plainBytes, Entropy);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }

        var directory = Path.GetDirectoryName(_path) ?? throw new IOException($"GitHub Token 存储没有父目录：{_path}");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"{Path.GetFileName(_path)}.tmp-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(temporary, protectedBytes);
            File.Move(temporary, _path, overwrite: true);
            var readBack = GetToken();
            if (!string.Equals(readBack, normalized, StringComparison.Ordinal))
            {
                throw new IOException("GitHub Token 安全存储回读校验失败");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
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
            throw new IOException($"清除 GitHub Token 安全存储失败：{_path}", error);
        }
    }

    private static string NormalizeToken(string token)
    {
        var value = token.Trim();
        return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? value[7..].Trim()
            : value.StartsWith("token ", StringComparison.OrdinalIgnoreCase)
                ? value[6..].Trim()
                : value;
    }

    private static byte[] Protect(byte[] value, byte[] entropy) => Dpapi.Protect(value, entropy);

    private static byte[] Unprotect(byte[] value, byte[] entropy) => Dpapi.Unprotect(value, entropy);
}
