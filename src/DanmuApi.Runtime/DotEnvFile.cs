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

    /// <summary>
    /// 值 → .env 行内文本。规则与移动端 <c>DotEnvCodec.formatValue</c> 一致（同一份 .env 契约）：
    /// - 只在必要时加双引号（首尾空白，或含空白 / <c>=</c> / <c>#</c> / <c>"</c>）；
    /// - 反斜杠**只在后面跟着会被读成转义的字符时才写成 <c>\\</c>**，其余原样保留——
    ///   这样正则（<c>/^\d+/</c>）、Windows 路径写进文件后与用户输入逐字相同，
    ///   而且反复保存不会每次多加一层反斜杠（移动端为此专门有幂等回归测试）；
    /// - <c>"</c> → <c>\"</c>，换行/回车/制表符 → <c>\n</c>/<c>\r</c>/<c>\t</c>（唯一能在一行里表达的写法）。
    /// 宿主 <c>android-server.js</c> 的 unescapeDoubleQuotedEnvValue 只还原 \\ \" \n \r \t，
    /// 其余反斜杠序列原样保留，所以上面这些写法在核心侧拿到的就是用户输入的值。
    /// </summary>
    private static string FormatValue(string value)
    {
        if (!NeedsQuotes(value))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 2).Append('"');
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            switch (character)
            {
                case '\\':
                    var next = index + 1 < value.Length ? value[index + 1] : (char?)null;
                    builder.Append(next is not null && IsRecognizedEscapedCharacter(next.Value) ? "\\\\" : "\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.Append('"').ToString();
    }

    /// <summary>需要引号的条件与移动端一致：首尾空白，或含空白 / <c>=</c> / <c>#</c> / <c>"</c>。</summary>
    private static bool NeedsQuotes(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
        {
            return true;
        }

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || character is '=' or '#' or '"')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>宿主会还原的转义：只有这些字符跟在反斜杠后面时才必须再转义一层。</summary>
    private static bool IsRecognizedEscapedCharacter(char character) =>
        character is '\\' or '"' or 'n' or 'r' or 't' or '\n' or '\r' or '\t';

    private static string ParseValue(string value)
    {
        if (value.Length < 2)
        {
            return value;
        }

        var quote = value[0];
        if ((quote != '"' && quote != '\'') || value[^1] != quote)
        {
            return value;
        }

        var inner = value[1..^1];
        if (quote == '\'')
        {
            // 单引号只作包裹符、不还原转义，与宿主 parseDotEnv 的单引号分支一致。
            // 注意 Docker/独立 Node 部署（server.js 的 parseRawEnvText）只剥双引号，
            // 所以工作台保存时会规范化为双引号。
            return inner;
        }

        var builder = new StringBuilder(inner.Length);
        for (var index = 0; index < inner.Length; index++)
        {
            if (inner[index] != '\\' || index + 1 >= inner.Length)
            {
                builder.Append(inner[index]);
                continue;
            }

            // 只还原宿主会还原的五个转义（\\ \" \n \r \t）。其它反斜杠序列（\d、\w、\S、
            // Windows 路径里的 \U 等）一律原样保留——宿主也是这么做的，而且绝不再因为
            // "未知转义序列"抛异常让整个应用起不来（0.4.4 试用反馈的故障）。
            var next = inner[index + 1];
            switch (next)
            {
                case 'n':
                    builder.Append('\n');
                    index++;
                    break;
                case 'r':
                    builder.Append('\r');
                    index++;
                    break;
                case 't':
                    builder.Append('\t');
                    index++;
                    break;
                case '\\':
                    builder.Append('\\');
                    index++;
                    break;
                case '"':
                    builder.Append('"');
                    index++;
                    break;
                default:
                    builder.Append(inner[index]);
                    break;
            }
        }

        return builder.ToString();
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
