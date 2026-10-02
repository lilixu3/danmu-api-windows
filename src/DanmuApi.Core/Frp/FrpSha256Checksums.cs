using System.Globalization;
using System.Security.Cryptography;

namespace DanmuApi.Core.Frp;

/// <summary>
/// frp 官方 <c>frp_sha256_checksums.txt</c> 的严格解析与比对。
///
/// 这里刻意不提供"校验失败就跳过校验继续装"的分支：那份 zip 接下来会被执行（frpc.exe），
/// 校验不过就说明拿到的不是官方发布物，必须停在这里。文件里没有目标条目同样按失败处理，
/// 而不是当成"官方没提供校验"放过。
/// </summary>
public sealed class FrpSha256Checksums
{
    private readonly Dictionary<string, string> _entries;

    private FrpSha256Checksums(Dictionary<string, string> entries) => _entries = entries;

    public int Count => _entries.Count;

    /// <summary>
    /// 解析校验和清单。接受两种官方排布：<c>&lt;hash&gt;  &lt;name&gt;</c> 与 <c>&lt;hash&gt; *&lt;name&gt;</c>。
    /// 任何一行不符合这个形状都直接报错——静默跳过一行就可能恰好跳过我们要用的那一行。
    /// </summary>
    public static FrpSha256Checksums Parse(string content, string sourceLabel)
    {
        ArgumentNullException.ThrowIfNull(content);
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lineNumber = 0;
        foreach (var rawLine in content.Split('\n'))
        {
            lineNumber++;
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(' ', StringComparison.Ordinal);
            if (separator != 64)
            {
                throw new FormatException($"校验和文件格式无效（{sourceLabel} 第 {lineNumber} 行）：{Trim(line)}");
            }

            var hash = line[..separator];
            if (!hash.All(char.IsAsciiHexDigit))
            {
                throw new FormatException($"校验和文件里的哈希不是 64 位十六进制（{sourceLabel} 第 {lineNumber} 行）：{Trim(line)}");
            }

            var name = line[(separator + 1)..].Trim().TrimStart('*');
            if (name.Length == 0)
            {
                throw new FormatException($"校验和文件缺少文件名（{sourceLabel} 第 {lineNumber} 行）：{Trim(line)}");
            }

            // 文件名里不可能有空白（官方清单是 frp_x.y.z_os_arch.ext 的形状）。出现空白说明这一行
            // 不是"哈希 + 文件名"两段，多半是多了第三列，此时按原样收下会让 Require 永远查不到目标条目。
            if (name.Any(char.IsWhiteSpace))
            {
                throw new FormatException($"校验和文件的名字段含空白字符（{sourceLabel} 第 {lineNumber} 行）：{Trim(line)}");
            }

            entries[name] = hash;
        }

        if (entries.Count == 0)
        {
            throw new FormatException($"校验和文件没有任何条目：{sourceLabel}");
        }

        return new FrpSha256Checksums(entries);
    }

    public bool TryGet(string fileName, out string hash) => _entries.TryGetValue(fileName, out hash!);

    /// <summary>取指定文件的期望哈希；清单里没有该条目就显式失败。</summary>
    public string Require(string fileName, string sourceLabel) =>
        TryGet(fileName, out var hash)
            ? hash
            : throw new IOException($"官方校验和清单里没有 {fileName}（{sourceLabel}），无法验证下载内容，已停止安装");

    /// <summary>比对文件哈希，返回差异说明；一致时返回 null。</summary>
    public static async Task<string?> VerifyFileAsync(
        string path,
        string expectedHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHash);
        string actual;
        await using (var stream = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         bufferSize: 1024 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            actual = Convert.ToHexString(hash).ToLowerInvariant();
        }

        return string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"SHA256 不匹配：期望 {expectedHash}，实际 {actual}";
    }

    private static string Trim(string value) => value.Length <= 200 ? value : value[..200] + "…";
}
