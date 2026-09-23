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
        // 同一份 .env 的读-改-写在本进程内必须串行：启动路径要写三键与 ADMIN_TOKEN，
        // 配置工作台也在写同一个文件，交错进行时会互相覆盖掉对方的写入。
        using var writeGate = EnterWriteGate(fullPath);
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
        Exception? bodyFailure = null;
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
            bodyFailure = writeError;
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
            // 同 UpdateValues：写入本身已经失败时，清理临时文件的异常不许盖掉根因。
            if (bodyFailure is null)
            {
                TryDelete(temporary);
            }
            else
            {
                TryDeleteQuietly(temporary);
            }
        }
    }

    public static void UpdateValues(string path, IReadOnlyDictionary<string, string?> updates)
    {
        foreach (var key in updates.Keys)
        {
            ValidateKey(key);
        }

        var fullPath = Path.GetFullPath(path);
        using var writeGate = EnterWriteGate(fullPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new IOException($".env 路径没有父目录: {path}");
        Directory.CreateDirectory(directory);

        var existing = File.Exists(fullPath)
            ? File.ReadAllText(fullPath, StrictUtf8)
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

        var temp = Path.Combine(directory, $"{Path.GetFileName(fullPath)}.tmp-{Guid.NewGuid():N}");
        var content = string.Join('\n', output) + '\n';
        var originalBytes = File.Exists(fullPath) ? File.ReadAllBytes(fullPath) : Array.Empty<byte>();
        var originalExists = File.Exists(fullPath);
        Exception? bodyFailure = null;
        try
        {
            File.WriteAllText(temp, content, StrictUtf8);
            ReplaceAtomically(temp, fullPath);
            var values = ReadValues(fullPath);
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
        catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException or FormatException)
        {
            bodyFailure = writeError;
            // 校验失败时新内容已经在盘上了，必须回滚：否则启动路径会把一份「宿主自己都不认」的
            // .env 留给核心去读。
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
            // 清理临时文件不能顶掉真正的写入故障——原来 finally 里抛的 IOException 会把根因整个盖掉。
            if (bodyFailure is null)
            {
                TryDelete(temp);
            }
            else
            {
                TryDeleteQuietly(temp);
            }
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

        // 首尾是同一种引号字符时必须加引号包裹：核心（android-server.js parseDotEnv）与宿主
        // ParseValue 都会把成对的 '…' 当包裹符剥掉，值里本来就带的 '$1' 不包就会少两个字符。
        if (value.Length >= 2 && value[0] == value[^1] && (value[0] == '\'' || value[0] == '"'))
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

    /// <summary>已经另有根因时的清理：留下临时文件只是难看，不许它盖掉真正的失败原因。</summary>
    private static void TryDeleteQuietly(string path)
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
            // 有意忽略：调用方正带着真正的写入故障往外抛。
        }
    }

    /// <summary>按 .env 绝对路径分的进程内写锁（同名文件的大小写不敏感，与 Windows 文件系统一致）。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> WriteGates =
        new(StringComparer.OrdinalIgnoreCase);

    private static IDisposable EnterWriteGate(string fullPath)
    {
        var gate = WriteGates.GetOrAdd(fullPath, _ => new object());
        Monitor.Enter(gate);
        return new WriteGateScope(gate);
    }

    private sealed class WriteGateScope : IDisposable
    {
        private readonly object _gate;
        private bool _released;

        internal WriteGateScope(object gate) => _gate = gate;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            Monitor.Exit(_gate);
        }
    }
}
