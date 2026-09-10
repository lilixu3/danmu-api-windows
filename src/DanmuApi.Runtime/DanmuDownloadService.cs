using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace DanmuApi.Runtime;

/// <summary>
/// 弹幕文件负载校验：下载写入前按格式验证结构，避免把回退的普通 JSON 当目标格式保存。
/// 语义对齐 Android DanmuPayloadInspector。
/// </summary>
public static class DanmuPayloadInspector
{
    private const int TextPrefixLimit = 4096;

    public static DanmuPayloadInspection Inspect(ReadOnlySpan<byte> payload, DanmuDownloadFormat format, string? contentType = null)
    {
        return format.PayloadKind() switch
        {
            DanmuPayloadKind.Xml => InspectXml(payload, format),
            DanmuPayloadKind.Binary => InspectBinary(payload, contentType),
            _ => InspectJson(payload, format),
        };
    }

    private static DanmuPayloadInspection InspectXml(ReadOnlySpan<byte> payload, DanmuDownloadFormat format)
    {
        var prefix = TextPrefix(payload);
        if (!prefix.StartsWith("<?xml", StringComparison.Ordinal) && !prefix.StartsWith("<i", StringComparison.Ordinal))
        {
            return Invalid(format, "返回内容不是 XML");
        }

        try
        {
            var preview = DanmuFilePreviewParser.Parse(payload.ToArray(), format, string.Empty, string.Empty, payload.Length, 1);
            return preview.ParseError is null
                ? new DanmuPayloadInspection(true, preview.Count)
                : Warning(format, preview.ParseError);
        }
        catch (Exception error) when (error is XmlException or IOException or ArgumentException)
        {
            return Warning(format, $"XML 解析失败：{error.Message}");
        }
    }

    private static DanmuPayloadInspection InspectJson(ReadOnlySpan<byte> payload, DanmuDownloadFormat format)
    {
        var text = TextPrefix(payload, full: true);
        if (text.Length == 0)
        {
            return Invalid(format, "返回内容为空");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return Invalid(format, "返回内容不是有效 JSON");
        }

        using (document)
        {
            var root = document.RootElement;
            var count = format switch
            {
                DanmuDownloadFormat.Json => InspectJsonFormat(root, "comments", "缺少 comments 数组，核心可能未支持该格式", "count"),
                DanmuDownloadFormat.DdplayJson => InspectJsonFormat(root, "comments", "缺少 comments 数组", "count"),
                DanmuDownloadFormat.ArtplayerJson => InspectJsonFormat(root, "danmuku", "缺少 danmuku 数组，核心可能回退到了普通 JSON", null),
                DanmuDownloadFormat.VodJson => InspectJsonFormat(root, "danmuku", "缺少 danmuku 数组", "danum"),
                DanmuDownloadFormat.DplayerJson => InspectJsonFormat(root, "data", "缺少 data 数组，核心可能回退到了普通 JSON", null),
                DanmuDownloadFormat.BahaJson => InspectBaha(root),
                DanmuDownloadFormat.DanuniJson => root.ValueKind == JsonValueKind.Array
                    ? root.GetArrayLength()
                    : (int?)null,
                _ => null,
            };

            return count is int value
                ? new DanmuPayloadInspection(true, value)
                : Invalid(format, format.Label() + " 结构不符合预期，核心可能回退到了普通 JSON");
        }
    }

    private static int? InspectJsonFormat(JsonElement root, string arrayName, string missingError, string? countKey)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(arrayName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        if (countKey is not null &&
            root.TryGetProperty(countKey, out var countElement) &&
            countElement.ValueKind == JsonValueKind.Number &&
            countElement.TryGetInt32(out var count) &&
            count >= 0)
        {
            return count;
        }

        return array.GetArrayLength();
    }

    private static int? InspectBaha(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("danmu", out var danmu) ||
            danmu.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        if (data.TryGetProperty("totalCount", out var total) &&
            total.ValueKind == JsonValueKind.Number &&
            total.TryGetInt32(out var count) &&
            count >= 0)
        {
            return count;
        }

        return danmu.GetArrayLength();
    }

    private static DanmuPayloadInspection InspectBinary(ReadOnlySpan<byte> payload, string? contentType)
    {
        if (payload.IsEmpty)
        {
            return Invalid(DanmuDownloadFormat.DanuniBinPb, "返回内容为空");
        }

        var normalizedType = contentType?.Split(';')[0].Trim().ToLowerInvariant() ?? string.Empty;
        var prefix = TextPrefix(payload);
        if (normalizedType is "application/json" or "application/xml" ||
            prefix.StartsWith('{') || prefix.StartsWith('[') ||
            prefix.StartsWith("<?xml", StringComparison.Ordinal) || prefix.StartsWith("<i", StringComparison.Ordinal))
        {
            return Invalid(DanmuDownloadFormat.DanuniBinPb, "核心返回了文本内容，可能尚未支持 DanUni Protobuf");
        }

        return new DanmuPayloadInspection(true);
    }

    private static string TextPrefix(ReadOnlySpan<byte> payload, bool full = false)
    {
        var slice = full ? payload : payload[..Math.Min(payload.Length, TextPrefixLimit)];
        var text = Encoding.UTF8.GetString(slice.ToArray()).TrimStart('\uFEFF');
        return full ? text.Trim() : text.TrimStart();
    }

    private static DanmuPayloadInspection Invalid(DanmuDownloadFormat format, string detail) =>
        new(false, null, $"{format.Label()} 格式校验失败：{detail}");

    private static DanmuPayloadInspection Warning(DanmuDownloadFormat format, string detail) =>
        new(true, null, string.Empty, $"{format.Label()} 格式检查警告：{detail}");
}

/// <summary>
/// 弹幕文件内容预览解析：JSON / XML 前缀条目抽取，语义对齐 Android DanmuFilePreviewParser。
/// </summary>
public static class DanmuFilePreviewParser
{
    public static DanmuFilePreview Parse(
        byte[] payload,
        DanmuDownloadFormat format,
        string fileName,
        string relativePath,
        long bytes,
        int previewLimit = DanmuDownloadDefaults.PreviewLimit)
    {
        var safeLimit = Math.Clamp(previewLimit, 1, 100_000);
        return format.PayloadKind() switch
        {
            DanmuPayloadKind.Xml => ParseXml(payload, fileName, relativePath, bytes, safeLimit) with { Format = format },
            DanmuPayloadKind.Json => ParseJson(payload, fileName, relativePath, bytes, safeLimit, format),
            _ => throw new ArgumentException($"{format.Label()} 是二进制格式，不支持内容预览", nameof(format)),
        };
    }

    private static DanmuFilePreview ParseXml(byte[] payload, string fileName, string relativePath, long bytes, int previewLimit)
    {
        var items = new List<DanmuPreviewItem>();
        var count = 0;
        string? parseError = null;

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };

        try
        {
            using var stream = new MemoryStream(payload);
            using var reader = XmlReader.Create(stream, settings);
            string? currentP = null;
            var text = new StringBuilder();
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "d")
                {
                    count++;
                    currentP = reader.GetAttribute("p") ?? string.Empty;
                    text.Clear();
                    if (reader.IsEmptyElement)
                    {
                        AppendItem(items, count, currentP, text.ToString(), previewLimit);
                        currentP = null;
                    }
                }
                else if (reader.NodeType == XmlNodeType.Text && currentP is not null)
                {
                    text.Append(reader.Value);
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "d" && currentP is not null)
                {
                    AppendItem(items, count, currentP, text.ToString().Trim(), previewLimit);
                    currentP = null;
                }
            }
        }
        catch (XmlException error)
        {
            parseError = $"XML 解析失败：{error.Message}";
        }
        catch (Exception error) when (error is IOException or ArgumentException)
        {
            parseError = $"读取 XML 文件失败: {error.Message}";
        }

        return new DanmuFilePreview(
            DanmuDownloadFormat.Xml, fileName, relativePath, bytes, count, previewLimit,
            count > items.Count, items, parseError);
    }

    private static void AppendItem(List<DanmuPreviewItem> items, int index, string? p, string text, int previewLimit)
    {
        if (items.Count < previewLimit)
        {
            items.Add(BuildPreviewItem(index, p ?? string.Empty, text));
        }
    }

    private static DanmuFilePreview ParseJson(
        byte[] payload, string fileName, string relativePath, long bytes, int previewLimit, DanmuDownloadFormat format)
    {
        var raw = Encoding.UTF8.GetString(payload).TrimStart('\uFEFF').Trim();
        if (raw.Length == 0)
        {
            return new DanmuFilePreview(format, fileName, relativePath, bytes, 0, previewLimit, false, []);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException error)
        {
            return new DanmuFilePreview(format, fileName, relativePath, bytes, 0, previewLimit, false, [], $"JSON 解析失败: {error.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            var comments = ExtractCommentsArray(root);
            if (comments is null)
            {
                return new DanmuFilePreview(format, fileName, relativePath, bytes, 0, previewLimit, false, []);
            }

            var explicitCount = ExtractExplicitCount(root, format);
            var itemCount = comments.Value.GetArrayLength();
            var count = explicitCount ?? itemCount;
            var items = new List<DanmuPreviewItem>();
            var index = 0;
            foreach (var item in comments.Value.EnumerateArray())
            {
                index++;
                if (items.Count >= previewLimit)
                {
                    break;
                }

                items.Add(JsonPreviewItem(index, item, format));
            }

            return new DanmuFilePreview(
                format, fileName, relativePath, bytes, count, previewLimit,
                itemCount > items.Count || count > items.Count, items);
        }
    }

    private static JsonElement? ExtractCommentsArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in new[] { "comments", "danmus", "danmaku", "danmuku", "d", "danmu", "data" })
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
            {
                return value;
            }
        }

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "comments", "danmus", "danmaku", "danmuku", "danmu" })
            {
                if (data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static int? ExtractExplicitCount(JsonElement root, DanmuDownloadFormat format)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var key = format switch
        {
            DanmuDownloadFormat.BahaJson => null,
            DanmuDownloadFormat.VodJson => "danum",
            DanmuDownloadFormat.Json or DanmuDownloadFormat.DdplayJson => "count",
            _ => null,
        };
        if (key is null)
        {
            if (format == DanmuDownloadFormat.BahaJson &&
                root.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("totalCount", out var total) &&
                total.ValueKind == JsonValueKind.Number &&
                total.TryGetInt32(out var bahaCount) &&
                bahaCount >= 0)
            {
                return bahaCount;
            }

            return null;
        }

        if (root.TryGetProperty(key, out var element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out var count) &&
            count >= 0)
        {
            return count;
        }

        return null;
    }

    private static DanmuPreviewItem JsonPreviewItem(int index, JsonElement item, DanmuDownloadFormat format)
    {
        if (item.ValueKind == JsonValueKind.Array)
        {
            var values = new List<string>();
            foreach (var value in item.EnumerateArray())
            {
                values.Add(ValueToString(value));
            }

            return TuplePreviewItem(index, values, format);
        }

        if (item.ValueKind != JsonValueKind.Object)
        {
            return new DanmuPreviewItem(index, Text: ValueToString(item));
        }

        var p = TryGetString(item, "p").Trim();
        var text = FirstNonBlank(
            TryGetString(item, "m"),
            TryGetString(item, "content"),
            TryGetString(item, "text"),
            TryGetString(item, "message"));
        if (p.Length > 0)
        {
            return BuildPreviewItem(index, p, text);
        }

        double? timeSeconds = null;
        if (TryGetNumber(item, "timepoint") is { } timepoint)
        {
            timeSeconds = timepoint;
        }
        else if (TryGetNumber(item, "time") is { } time)
        {
            timeSeconds = format == DanmuDownloadFormat.BahaJson ? time / 10d : time;
        }
        else if (TryGetNumber(item, "progress") is { } progress)
        {
            timeSeconds = progress / 1000d;
        }

        return new DanmuPreviewItem(
            index,
            timeSeconds,
            NormalizeMode(FirstNonBlank(TryGetString(item, "mode"), TryGetString(item, "ct"), TryGetString(item, "position")), format),
            TryGetString(item, "color").Trim(),
            FirstNonBlank(TryGetString(item, "source"), TryGetString(item, "platform"), TryGetString(item, "type")),
            text);
    }

    private static DanmuPreviewItem TuplePreviewItem(int index, IReadOnlyList<string> values, DanmuDownloadFormat format)
    {
        if (format is not (DanmuDownloadFormat.DplayerJson or DanmuDownloadFormat.VodJson))
        {
            return new DanmuPreviewItem(index, Text: string.Join(",", values));
        }

        return new DanmuPreviewItem(
            index,
            values.Count > 0 ? double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var time) ? time : null : null,
            NormalizeMode(values.Count > 1 ? values[1] : string.Empty, format),
            values.Count > 2 ? values[2] : string.Empty,
            format == DanmuDownloadFormat.DplayerJson && values.Count > 3 ? values[3] : string.Empty,
            values.Count > 4 ? values[4] : string.Empty);
    }

    private static string NormalizeMode(string raw, DanmuDownloadFormat format)
    {
        var value = raw.Trim().ToLowerInvariant();
        return format switch
        {
            DanmuDownloadFormat.ArtplayerJson or DanmuDownloadFormat.BahaJson or DanmuDownloadFormat.DplayerJson => value switch
            {
                "1" => "5",
                "2" => "4",
                _ => value,
            },
            DanmuDownloadFormat.DanuniJson => value switch
            {
                "1" or "bottom" => "4",
                "2" or "top" => "5",
                _ => value,
            },
            DanmuDownloadFormat.VodJson => value switch
            {
                "top" => "5",
                "bottom" => "4",
                _ => "1",
            },
            _ => value,
        };
    }

    internal static DanmuPreviewItem BuildPreviewItem(int index, string p, string text)
    {
        var parts = p.Split(',');
        var timeSeconds = parts.Length > 0 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var time)
            ? (double?)time
            : null;
        var mode = parts.Length > 1 ? parts[1] : string.Empty;
        var color = parts.Length switch
        {
            4 => parts[2],
            >= 8 => parts[3],
            _ => parts.Length > 3 ? parts[3] : parts.Length > 2 ? parts[2] : string.Empty,
        };
        var source = parts.Length > 0 ? parts[^1].Trim().Trim('[', ']') : string.Empty;
        return new DanmuPreviewItem(index, timeSeconds, mode, color, source, text);
    }

    private static string ValueToString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => string.Empty,
        _ => value.GetRawText(),
    };

    private static string TryGetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static double? TryGetNumber(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
            double.IsFinite(parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string FirstNonBlank(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}

public static class DanmuDownloadParsing
{
    public static string ParseSource(string rawTitle)
    {
        var match = System.Text.RegularExpressions.Regex.Match(rawTitle, @"^\s*【([^】]+】?)\s*");
        var value = match.Success ? match.Groups[1].Value.Trim().TrimEnd('】') : string.Empty;
        return value.Length == 0 ? "unknown" : value;
    }

    public static string StripSourceTag(string rawTitle) =>
        System.Text.RegularExpressions.Regex.Replace(rawTitle, @"^\s*【[^】]+】\s*", string.Empty).Trim();

    public static string NormalizeAnimeTitleForMatch(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var noFrom = System.Text.RegularExpressions.Regex.Replace(raw, @"\s*from\s+.*$", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var noType = System.Text.RegularExpressions.Regex.Replace(noFrom, @"【[^】]*】", string.Empty);
        var noYear = System.Text.RegularExpressions.Regex.Replace(noType, @"[（(]\d{4}[)）]", string.Empty);
        return NormalizeKey(noYear);
    }

    public static string NormalizeEpisodeTitleForMatch(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return NormalizeKey(StripSourceTag(raw));
    }

    private static string NormalizeKey(string raw) =>
        System.Text.RegularExpressions.Regex
            .Replace(raw, @"[\s\p{P}　【】（）\[\]「」]", string.Empty)
            .ToLowerInvariant()
            .Trim();

    public static string ExtractSourceFromAnimeTitle(string raw)
    {
        var match = System.Text.RegularExpressions.Regex.Match(raw, @"from\s+(.+?)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim().Trim('【', '】', '[', ']') : string.Empty;
    }

    public static string CanonicalSourceKey(string raw)
    {
        var key = System.Text.RegularExpressions.Regex.Replace(raw.Trim(), @"^from\s+", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Trim()
            .ToLowerInvariant();
        if (key.Length == 0)
        {
            return "unknown";
        }

        return key switch
        {
            "qq" or "tencent" => "tencent",
            "bilibili" or "bilibili1" or "bili" or "b23" => "bilibili",
            "iqiyi" or "qiyi" => "iqiyi",
            "imgo" or "mango" or "mgtv" or "hunantv" => "imgo",
            "douyin" or "xigua" => "xigua",
            _ => key,
        };
    }

    public static string ExtractAnimeKeywordForSearch(string rawAnimeTitle)
    {
        var noFrom = System.Text.RegularExpressions.Regex.Replace(rawAnimeTitle, @"\s*from\s+.*$", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var noType = System.Text.RegularExpressions.Regex.Replace(noFrom, @"【[^】]*】", string.Empty);
        var noYear = System.Text.RegularExpressions.Regex.Replace(noType, @"[（(]\d{4}[)）]", string.Empty);
        var keyword = noYear.Trim();
        return keyword.Length > 0 ? keyword : noFrom.Trim();
    }

    public static int ExtractEpisodeNumber(string fileName)
    {
        var patterns = new[]
        {
            new System.Text.RegularExpressions.Regex(@"(?i)E(?:P)?(\d{1,4})"),
            new System.Text.RegularExpressions.Regex(@"第\s*(\d{1,4})\s*[集话]"),
        };
        foreach (var pattern in patterns)
        {
            var match = pattern.Match(fileName);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var number))
            {
                return number;
            }
        }

        return 0;
    }

    public static DanmuEpisodeCandidate ToEpisodeCandidate(DanmuEpisode episode, string fallbackSource, int fallbackNumber)
    {
        var rawTitle = episode.EpisodeTitle;
        var parsedSource = ParseSource(rawTitle);
        var source = parsedSource == "unknown"
            ? string.IsNullOrWhiteSpace(fallbackSource) ? "unknown" : fallbackSource
            : parsedSource;
        var title = StripSourceTag(rawTitle);
        if (title.Length == 0)
        {
            title = $"第{episode.EpisodeNumber}集";
        }

        var number = int.TryParse(episode.EpisodeNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : fallbackNumber;
        return new DanmuEpisodeCandidate(
            episode.EpisodeId,
            number,
            title,
            source,
            string.IsNullOrWhiteSpace(episode.Url) ? string.Empty : episode.Url);
    }

    public static IReadOnlyList<DanmuEpisodeCandidate> DeduplicateEpisodes(IReadOnlyList<DanmuEpisodeCandidate> episodes)
    {
        var map = new Dictionary<string, DanmuEpisodeCandidate>(StringComparer.Ordinal);
        foreach (var episode in episodes)
        {
            var titleKey = NormalizeEpisodeTitleForMatch(episode.Title);
            var key = titleKey.Length > 0
                ? $"{episode.EpisodeNumber}|{titleKey}"
                : $"id-{episode.EpisodeId}";
            if (!map.ContainsKey(key))
            {
                map[key] = episode;
            }
        }

        return map.Values
            .OrderBy(episode => episode.EpisodeNumber)
            .ThenBy(episode => episode.EpisodeId)
            .ToArray();
    }

    public static DanmuEpisodeCandidate? PickEpisodeForTask(DanmuDownloadTask task, IReadOnlyList<DanmuEpisodeCandidate> episodes)
    {
        if (episodes.Count == 0)
        {
            return null;
        }

        var taskSource = CanonicalSourceKey(task.Source);
        var sameSourceEpisodes = taskSource == "unknown"
            ? episodes
            : episodes.Where(episode => CanonicalSourceKey(episode.Source) == taskSource).ToArray();
        var taskTitleKey = NormalizeEpisodeTitleForMatch(task.EpisodeTitle);
        var pool = sameSourceEpisodes.Count > 0 ? sameSourceEpisodes : episodes;
        var match = pool.FirstOrDefault(episode => episode.EpisodeNumber == task.EpisodeNo) ??
            (taskTitleKey.Length > 0 ? pool.FirstOrDefault(episode => NormalizeEpisodeTitleForMatch(episode.Title) == taskTitleKey) : null) ??
            episodes.FirstOrDefault(episode => episode.EpisodeNumber == task.EpisodeNo) ??
            (taskTitleKey.Length > 0 ? episodes.FirstOrDefault(episode => NormalizeEpisodeTitleForMatch(episode.Title) == taskTitleKey) : null);
        return match;
    }

    public static bool AnimeIdentityMatches(string storedTitle, long storedAnimeId, string candidateTitle, long candidateAnimeId)
    {
        if (storedAnimeId > 0 && candidateAnimeId > 0)
        {
            return storedAnimeId == candidateAnimeId;
        }

        var candidateKey = NormalizeAnimeTitleForMatch(candidateTitle);
        if (candidateKey.Length == 0 || NormalizeAnimeTitleForMatch(storedTitle) != candidateKey)
        {
            return false;
        }

        var storedYear = ExtractAnimeYear(storedTitle);
        var candidateYear = ExtractAnimeYear(candidateTitle);
        return storedYear.Length == 0 || candidateYear.Length == 0 || storedYear == candidateYear;
    }

    public static bool HistorySourceMatches(string candidateSource, string recordSource)
    {
        var candidateKey = CanonicalSourceKey(candidateSource);
        var recordKey = CanonicalSourceKey(recordSource);
        return candidateKey == "unknown" || IsNeutralHistorySource(recordSource) || candidateKey == recordKey;
    }

    public static bool IsNeutralHistorySource(string raw)
    {
        var normalized = raw.Trim().ToLowerInvariant();
        return normalized.Length == 0 || normalized is "unknown" or "目录同步" or "目录导入";
    }

    private static string ExtractAnimeYear(string raw)
    {
        var match = System.Text.RegularExpressions.Regex.Match(raw, @"[（(](\d{4})[)）]");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }
}

public sealed record DanmuEpisodeCandidate(
    long EpisodeId,
    int EpisodeNumber,
    string Title,
    string Source,
    string SourceUrl);
