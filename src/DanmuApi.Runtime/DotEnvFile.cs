using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DanmuApi.Runtime;

public enum DotEnvMutationKind
{
    Set,
    Delete,
}

public sealed record DotEnvMutation(DotEnvMutationKind Kind, string Key, string? Value = null)
{
    public static DotEnvMutation Set(string key, string value) => new(DotEnvMutationKind.Set, key, value);

    public static DotEnvMutation Delete(string key) => new(DotEnvMutationKind.Delete, key);
}

public sealed record DotEnvFileFingerprint(bool Exists, string Sha256)
{
    public static DotEnvFileFingerprint Missing { get; } = new(false, string.Empty);
}

public sealed class DotEnvConflictException : IOException
{
    public DotEnvConflictException(string path, DotEnvFileFingerprint expected, DotEnvFileFingerprint actual)
        : base($".env 在编辑期间已被外部修改，拒绝覆盖：{path}（期望 {expected.Sha256}，实际 {actual.Sha256}）")
    {
        Path = path;
        Expected = expected;
        Actual = actual;
    }

    public string Path { get; }

    public DotEnvFileFingerprint Expected { get; }

    public DotEnvFileFingerprint Actual { get; }
}

public sealed class DotEnvTransactionException : IOException
{
    public DotEnvTransactionException(string message, Exception cause)
        : base(message, cause)
    {
    }
}

public sealed record DotEnvTransactionResult(
    DotEnvFileFingerprint Fingerprint,
    IReadOnlyDictionary<string, string> Values);

public static class DotEnvFile
{
    private static readonly Regex KeyPattern = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BareValuePattern = new(@"^[A-Za-z0-9_./:@-]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static IReadOnlyDictionary<string, string> ReadValues(string path)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var text = File.ReadAllText(path, StrictUtf8);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = raw.TrimStart('\uFEFF');
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            if (!KeyPattern.IsMatch(key))
            {
                continue;
            }

            values[key.ToUpperInvariant()] = ParseValue(line[(separator + 1)..].Trim());
        }

        return values;
    }

    public static IReadOnlyDictionary<string, string> ReadValuesStrict(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return values;
        }

        var text = File.ReadAllText(path, StrictUtf8);
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = raw.TrimStart('\uFEFF');
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            if (!KeyPattern.IsMatch(key))
            {
                continue;
            }

            var normalized = key.ToUpperInvariant();
            if (!values.TryAdd(normalized, ParseValue(line[(separator + 1)..].Trim())))
            {
                throw new FormatException($".env 包含重复环境变量定义：{normalized}");
            }
        }

        return values;
    }

    public static string? ReadValue(string path, string key)
    {
        ValidateKey(key);
        return ReadValues(path).GetValueOrDefault(key.ToUpperInvariant());
    }

    public static DotEnvFileFingerprint GetFingerprint(string path)
    {
        if (!File.Exists(path))
        {
            return DotEnvFileFingerprint.Missing;
        }

        return new DotEnvFileFingerprint(true, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }

    public static DotEnvTransactionResult ApplyMutations(
        string path,
        DotEnvFileFingerprint expectedFingerprint,
        IReadOnlyList<DotEnvMutation> mutations)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(expectedFingerprint);
        ArgumentNullException.ThrowIfNull(mutations);
        if (mutations.Count == 0)
        {
            throw new ArgumentException("至少需要一个 .env 变更", nameof(mutations));
        }

        var normalized = NormalizeMutations(mutations);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new IOException($".env 路径没有父目录: {path}");
        Directory.CreateDirectory(directory);

        var actualFingerprint = GetFingerprint(fullPath);
        if (actualFingerprint != expectedFingerprint)
        {
            throw new DotEnvConflictException(fullPath, expectedFingerprint, actualFingerprint);
        }

        var originalBytes = File.Exists(fullPath) ? File.ReadAllBytes(fullPath) : Array.Empty<byte>();
        var originalExists = File.Exists(fullPath);
        var existing = originalExists ? StrictUtf8.GetString(originalBytes) : string.Empty;
        var sourceLines = SplitLines(existing);
        var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in sourceLines)
        {
            var separator = line.IndexOf('=');
            var key = separator > 0 ? line[..separator].Trim() : string.Empty;
            if (!KeyPattern.IsMatch(key))
            {
                continue;
            }

            if (!existingKeys.Add(key.ToUpperInvariant()))
            {
                throw new FormatException($".env 包含重复环境变量定义，拒绝工作台写入：{key}");
            }
        }

        var output = new List<string>(sourceLines.Length + normalized.Count);
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in sourceLines)
        {
            var separator = line.IndexOf('=');
            var key = separator > 0 ? line[..separator].Trim() : string.Empty;
            var normalizedKey = KeyPattern.IsMatch(key) ? key.ToUpperInvariant() : string.Empty;
            if (!normalized.TryGetValue(normalizedKey, out var mutation))
            {
                if (line.Length > 0)
                {
                    output.Add(line);
                }

                continue;
            }

            written.Add(normalizedKey);
            if (mutation.Kind == DotEnvMutationKind.Set)
            {
                output.Add($"{normalizedKey}={FormatValue(mutation.Value!)}");
            }
        }

        foreach (var (key, mutation) in normalized)
        {
            if (written.Contains(key) || mutation.Kind == DotEnvMutationKind.Delete)
            {
                continue;
            }

            output.Add($"{key}={FormatValue(mutation.Value!)}");
        }

        var content = string.Join('\n', output) + '\n';
        var temporary = Path.Combine(directory, $"{Path.GetFileName(fullPath)}.tmp-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(temporary, content, StrictUtf8);
            ReplaceAtomically(temporary, fullPath);
            VerifyMutations(fullPath, normalized);
            var finalFingerprint = GetFingerprint(fullPath);
            return new DotEnvTransactionResult(finalFingerprint, ReadValues(fullPath));
        }
        catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException or FormatException)
        {
            try
            {
                RestoreOriginal(fullPath, directory, originalExists, originalBytes);
            }
            catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
            {
                throw new DotEnvTransactionException(
                    $".env 写入失败，且恢复原文件也失败：{fullPath}",
                    new AggregateException(writeError, rollbackError));
            }

            throw new DotEnvTransactionException($".env 写入失败，已恢复原文件：{fullPath}", writeError);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public static void UpdateValues(string path, IReadOnlyDictionary<string, string?> updates)
    {
        foreach (var key in updates.Keys)
        {
            ValidateKey(key);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new IOException($".env 路径没有父目录: {path}");
        Directory.CreateDirectory(directory);

        var existing = File.Exists(path)
            ? File.ReadAllText(path, StrictUtf8)
            : string.Empty;
        var sourceLines = existing.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();
        if (sourceLines.Count > 0 && sourceLines[^1].Length == 0)
        {
            sourceLines.RemoveAt(sourceLines.Count - 1);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var output = new List<string>(sourceLines.Count + updates.Count);
        foreach (var line in sourceLines)
        {
            var separator = line.IndexOf('=');
            var key = separator > 0 ? line[..separator].Trim() : string.Empty;
            if (KeyPattern.IsMatch(key) && updates.ContainsKey(key))
            {
                var normalized = key.ToUpperInvariant();
                seen.Add(normalized);
                var value = updates[key];
                if (!string.IsNullOrEmpty(value))
                {
                    output.Add($"{normalized}={FormatValue(value)}");
                }
            }
            else if (line.Length > 0)
            {
                output.Add(line);
            }
        }

        foreach (var (key, value) in updates)
        {
            var normalized = key.ToUpperInvariant();
            if (!seen.Contains(normalized) && !string.IsNullOrEmpty(value))
            {
                output.Add($"{normalized}={FormatValue(value!)}");
            }
        }

        var temp = Path.Combine(directory, $"{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");
        var content = string.Join('\n', output) + '\n';
        try
        {
            File.WriteAllText(temp, content, StrictUtf8);
            ReplaceAtomically(temp, path);
            var values = ReadValues(path);
            foreach (var (key, expected) in updates)
            {
                var actual = values.GetValueOrDefault(key.ToUpperInvariant());
                var normalizedExpected = string.IsNullOrEmpty(expected) ? null : expected;
                if (!string.Equals(actual, normalizedExpected, StringComparison.Ordinal))
                {
                    throw new IOException($"写入 .env 后校验失败: {key}");
                }
            }
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static Dictionary<string, DotEnvMutation> NormalizeMutations(IReadOnlyList<DotEnvMutation> mutations)
    {
        var result = new Dictionary<string, DotEnvMutation>(StringComparer.OrdinalIgnoreCase);
        foreach (var mutation in mutations)
        {
            ValidateKey(mutation.Key);
            if (mutation.Kind == DotEnvMutationKind.Set && mutation.Value is null)
            {
                throw new ArgumentException($"Set 变更不能使用 null：{mutation.Key}", nameof(mutations));
            }

            if (mutation.Kind is not DotEnvMutationKind.Set and not DotEnvMutationKind.Delete)
            {
                throw new ArgumentOutOfRangeException(nameof(mutations), mutation.Kind, "未知 .env 变更类型");
            }

            var normalized = mutation.Key.ToUpperInvariant();
            if (!result.TryAdd(normalized, mutation with { Key = normalized }))
            {
                throw new ArgumentException($"同一事务重复修改环境变量：{normalized}", nameof(mutations));
            }
        }

        return result;
    }

    private static string[] SplitLines(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        return lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    private static void VerifyMutations(string path, IReadOnlyDictionary<string, DotEnvMutation> mutations)
    {
        var values = ReadValues(path);
        foreach (var (key, mutation) in mutations)
        {
            var present = values.TryGetValue(key, out var actual);
            if (mutation.Kind == DotEnvMutationKind.Delete)
            {
                if (present)
                {
                    throw new IOException($"删除 .env 显式变量后校验失败：{key}");
                }
            }
            else if (!present || !string.Equals(actual, mutation.Value, StringComparison.Ordinal))
            {
                throw new IOException($"写入 .env 后校验失败：{key}");
            }
        }
    }

    private static void RestoreOriginal(string destination, string directory, bool existed, byte[] bytes)
    {
        if (!existed)
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            return;
        }

        var temporary = Path.Combine(directory, $"{Path.GetFileName(destination)}.rollback-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            ReplaceAtomically(temporary, destination);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void ReplaceAtomically(string source, string destination)
    {
        try
        {
            File.Move(source, destination, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"原子替换 .env 失败: {destination}", error);
        }
    }

    private static void ValidateKey(string key)
    {
        if (!KeyPattern.IsMatch(key))
        {
            throw new ArgumentException($"环境变量名非法: {key}", nameof(key));
        }
    }

    private static string FormatValue(string value)
    {
        if (value.Length > 0 && BareValuePattern.IsMatch(value))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 2).Append('"');
        foreach (var character in value)
        {
            builder.Append(character switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => character.ToString(),
            });
        }

        return builder.Append('"').ToString();
    }

    private static string ParseValue(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            var inner = value[1..^1];
            var builder = new StringBuilder(inner.Length);
            for (var index = 0; index < inner.Length; index++)
            {
                if (inner[index] != '\\' || index + 1 >= inner.Length)
                {
                    builder.Append(inner[index]);
                    continue;
                }

                builder.Append(inner[++index] switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '\\' => '\\',
                    '"' => '"',
                    _ => throw new FormatException(".env 双引号值包含未知转义序列"),
                });
            }

            return builder.ToString();
        }

        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1];
        }

        return value;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"清理临时 .env 文件失败: {path}", error);
        }
    }
}
