using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DanmuApi.Runtime;

namespace DanmuApi.App.Services;

/// <summary>缓存目录快路径的读取结果。<see cref="Available"/> 为 false 时必须回退到核心接口，
/// 并且 <see cref="Diagnostic"/> 要进诊断日志（不许静默兜底）。</summary>
public sealed record LocalDanmuCacheReadResult(
    bool Available,
    IReadOnlyList<CoreLocalDanmuResource> Resources,
    string? Diagnostic)
{
    public static LocalDanmuCacheReadResult Success(IReadOnlyList<CoreLocalDanmuResource> resources) =>
        new(true, resources, null);

    public static LocalDanmuCacheReadResult Unavailable(string diagnostic) =>
        new(false, [], diagnostic);
}

public interface ILocalDanmuCacheReader
{
    LocalDanmuCacheReadResult Read(string nodeProjectDirectory);
}

/// <summary>
/// 直接读核心的本地弹幕落盘目录（Node 部署时 <c>&lt;cwd&gt;/.cache/local-danmu/&lt;sha256(resourceKey)&gt;.json</c>）。
///
/// 为什么要有这条快路径：核心的 <c>GET /api/v2/local-danmu/list</c> 会把目录下每个文件整份 JSON.parse
/// （含弹幕正文）再返回，资源多时会在核心里产生很高的内存峰值。核心落盘对象是
/// <c>JSON.stringify(resource)</c>，字段顺序为
/// <c>resourceKey, videoId, title, year, type, season, episode, filename, size, format, status,
/// count, matchKeys, comments, updatedAt</c> —— 元数据全在文件头、<c>updatedAt</c> 在文件尾，
/// 所以只读「头 16KB + 尾 512B」即可拿到全部元数据。
///
/// 这是优化而不是契约：任何文件读不出完整元数据（字段顺序变了、文件损坏、命名规则变了）
/// 都判定整批不可用并回退接口，同时留下诊断原因。
/// </summary>
public sealed partial class LocalDanmuCacheReader : ILocalDanmuCacheReader
{
    internal const string CacheDirectoryName = "local-danmu";
    internal const string CommentsMarker = ",\"comments\":[";
    private const int HeadBytes = 16 * 1024;
    private const int TailBytes = 512;

    public LocalDanmuCacheReadResult Read(string nodeProjectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeProjectDirectory);
        var directory = Path.Combine(Path.GetFullPath(nodeProjectDirectory), ".cache", CacheDirectoryName);
        if (!Directory.Exists(directory))
        {
            // 还没导入过任何文件：目录不存在就是空列表，不是异常。
            return LocalDanmuCacheReadResult.Success([]);
        }

        string[] files;
        try
        {
            // 核心写盘用 tmp+rename（<sha>.json.tmp-<ts>），这里显式只认最终文件。
            files = Directory
                .GetFiles(directory, "*.json")
                .Where(path => Path.GetFileName(path).EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .Where(path => !Path.GetFileName(path).Contains(".tmp-", StringComparison.Ordinal))
                .ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return LocalDanmuCacheReadResult.Unavailable($"读取核心本地弹幕缓存目录失败：{error.Message}");
        }

        var resources = new List<CoreLocalDanmuResource>(files.Length);
        foreach (var file in files)
        {
            try
            {
                resources.Add(ReadResource(file));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                return LocalDanmuCacheReadResult.Unavailable(
                    $"核心本地弹幕缓存 {Path.GetFileName(file)} 无法解析（{error.Message}），已回退到核心接口");
            }
        }

        resources.Sort((left, right) =>
            string.Compare(right.UpdatedAt ?? string.Empty, left.UpdatedAt ?? string.Empty, StringComparison.Ordinal));
        return LocalDanmuCacheReadResult.Success(resources);
    }

    private static CoreLocalDanmuResource ReadResource(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = stream.Length;
        if (length <= 0)
        {
            throw new InvalidDataException("文件为空");
        }

        var headLength = (int)Math.Min(HeadBytes, length);
        var head = new byte[headLength];
        stream.ReadExactly(head, 0, headLength);
        var headText = Encoding.UTF8.GetString(head);
        var markerIndex = headText.IndexOf(CommentsMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            throw new InvalidDataException($"头部 {HeadBytes} 字节内没有找到 {CommentsMarker} 字段（核心落盘字段顺序可能已变化）");
        }

        string updatedAt;
        if (length <= HeadBytes)
        {
            // 小文件：头部已经覆盖整个文件，直接在完整文本里取 updatedAt。
            var tailMatch = UpdatedAtPattern().Match(headText);
            if (!tailMatch.Success)
            {
                throw new InvalidDataException("没有找到 updatedAt 字段");
            }

            updatedAt = tailMatch.Groups[1].Value;
        }
        else
        {
            var tailLength = (int)Math.Min(TailBytes, length);
            var tail = new byte[tailLength];
            stream.Seek(length - tailLength, SeekOrigin.Begin);
            stream.ReadExactly(tail, 0, tailLength);
            var tailMatch = UpdatedAtPattern().Match(Encoding.UTF8.GetString(tail));
            if (!tailMatch.Success)
            {
                throw new InvalidDataException("尾部 512 字节内没有找到 updatedAt 字段");
            }

            updatedAt = tailMatch.Groups[1].Value;
        }

        // 头部截到 comments 之前并补上右花括号，即得到一份只含元数据的合法 JSON。
        var metadata = headText[..markerIndex] + "}";
        using var document = JsonDocument.Parse(metadata);
        var resource = CoreLocalDanmuClient.ParseResource(document.RootElement);
        if (!string.Equals(ExpectedFileName(resource.ResourceKey), Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("文件名与 resourceKey 的 sha256 不一致（可能来自其它核心或旧版本）");
        }

        return resource with { UpdatedAt = updatedAt };
    }

    /// <summary>核心用 <c>sha256(resourceKey)</c> 的十六进制小写作为文件名（UTF-8 编码）。</summary>
    internal static string ExpectedFileName(string resourceKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resourceKey))).ToLowerInvariant() + ".json";

    [GeneratedRegex("\"updatedAt\":\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex UpdatedAtPattern();
}
