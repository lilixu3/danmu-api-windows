using System.Globalization;
using System.Text.RegularExpressions;

namespace DanmuApi.Core;

public enum LogLevel
{
    Debug,
    Info,
    Success,
    Warn,
    Error,
    Other,
}

public enum LogSourceKind
{
    CoreApi,
    CoreOutput,
    CoreError,
    Host,
}

public sealed record LogEntry(
    long Sequence,
    LogSourceKind Source,
    string? Timestamp,
    LogLevel Level,
    string Message,
    string Category,
    string Raw)
{
    public bool IsContinuation { get; init; }

    public string LevelText => LogLineParser.FormatLevel(Level);
    public string TimestampText => LogLineParser.FormatTimestamp(this);
    public string SequenceText => Sequence.ToString(CultureInfo.InvariantCulture);
}

public sealed record ParsedLogLine(
    string? Timestamp,
    LogLevel? Level,
    string Message,
    string? Category)
{
    public bool IsContinuation => Level is null;
}

/// <summary>
/// 解析核心日志行。核心日志文本格式为 <c>[时间戳] level: 消息</c>
/// （见本地核心源码 <c>danmu_api/apis/system-api.js</c> 的 handleLogs），
/// 消息前缀可包含若干 <c>[标签]</c>，用于归类到系统/工具/视频源分类；
/// 无前缀的续行（含 JSON 缩进行）继承上一行级别与分类，保证筛选时上下文完整。
/// </summary>
public static partial class LogLineParser
{
    [GeneratedRegex(@"^\[(?<timestamp>[^\]]+)\]\s+(?<level>[A-Za-z]+)\s*:?\s?(?<rest>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex HeaderPattern();

    [GeneratedRegex(@"^(?:\s*\[[^\]]*\])+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingTagsPattern();

    [GeneratedRegex(@"\[([^\]]+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[T ]", RegexOptions.CultureInvariant)]
    private static partial Regex IsoTimestampPattern();

    [GeneratedRegex(@"^\d{2}:\d{2}(:\d{2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex ClockTimestampPattern();

    private static readonly Dictionary<string, string> NormalizationMap =
        new(StringComparer.Ordinal)
        {
            ["vod fastest mode"] = "vod",
            ["custom source"] = "custom",
            ["bilibili-proxy"] = "bilibili",
            ["tmdb-source"] = "tmdb",
            ["path check"] = "system",
            ["path fix"] = "system",
            ["base"] = "system",
            ["fongmi"] = "system",
        };

    private static readonly string[] MergeKeys = ["匹配", "落单", "补全", "合集", "略过", "merge-check"];

    public static ParsedLogLine Parse(string line, LogSourceKind source)
    {
        ArgumentNullException.ThrowIfNull(line);
        var match = HeaderPattern().Match(line);
        if (!match.Success)
        {
            return new ParsedLogLine(null, null, line, null);
        }

        var level = ParseLevel(match.Groups["level"].Value);
        if (level == LogLevel.Other && source == LogSourceKind.CoreError)
        {
            level = LogLevel.Error;
        }

        var rest = match.Groups["rest"].Value;
        return new ParsedLogLine(
            match.Groups["timestamp"].Value.Trim(),
            level,
            rest.Length == 0 ? line : rest,
            ExtractCategory(rest));
    }

    public static LogLevel ParseLevel(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "error" or "err" or "fatal" or "critical" => LogLevel.Error,
            "warn" or "warning" => LogLevel.Warn,
            "info" or "log" => LogLevel.Info,
            "success" or "ok" or "done" => LogLevel.Success,
            "debug" or "trace" or "verbose" => LogLevel.Debug,
            _ => LogLevel.Other,
        };
    }

    public static string? ExtractCategory(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var prefix = LeadingTagsPattern().Match(message);
        if (!prefix.Success)
        {
            return null;
        }

        var tags = TagPattern().Matches(prefix.Value)
            .Select(match => match.Groups[1].Value.Trim())
            .Where(tag => tag.Length > 0)
            .ToArray();
        if (tags.Length == 0)
        {
            return null;
        }

        if (tags.Any(tag => string.Equals(tag, "merge", StringComparison.OrdinalIgnoreCase) ||
                            MergeKeys.Any(key => tag.Contains(key, StringComparison.OrdinalIgnoreCase))))
        {
            return "merge";
        }

        var businessTags = tags
            .Where(tag => !IsoTimestampPattern().IsMatch(tag) &&
                          !ClockTimestampPattern().IsMatch(tag) &&
                          !tag.Contains("08:00", StringComparison.Ordinal) &&
                          !string.Equals(tag, "请求模拟", StringComparison.Ordinal) &&
                          !string.Equals(tag, "网络请求", StringComparison.Ordinal))
            .ToArray();
        if (businessTags.Length == 0)
        {
            return null;
        }

        var category = businessTags[0].ToLowerInvariant();
        return NormalizationMap.TryGetValue(category, out var normalized)
            ? normalized
            : category;
    }

    public static string FormatLevel(LogLevel level) => level switch
    {
        LogLevel.Error => "ERROR",
        LogLevel.Warn => "WARN",
        LogLevel.Info => "INFO",
        LogLevel.Success => "OK",
        LogLevel.Debug => "DEBUG",
        _ => "LOG",
    };

    public static string FormatTimestamp(LogEntry entry) =>
        string.IsNullOrWhiteSpace(entry.Timestamp)
            ? string.Empty
            : entry.Timestamp.Length > 19
                ? entry.Timestamp[..19].Replace('T', ' ')
                : entry.Timestamp;

    public static string FormatLine(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var timestamp = FormatTimestamp(entry);
        var prefix = timestamp.Length == 0
            ? string.Empty
            : $"{timestamp}  ";
        var level = FormatLevel(entry.Level);
        return $"{prefix}{level,-5}  {entry.Message}";
    }
}
