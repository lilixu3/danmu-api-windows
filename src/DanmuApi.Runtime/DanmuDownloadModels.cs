using System.Text.Json.Serialization;

namespace DanmuApi.Runtime;

public enum DanmuPayloadKind
{
    Json,
    Xml,
    Binary,
}

[JsonConverter(typeof(JsonStringEnumConverter<DanmuDownloadFormat>))]
public enum DanmuDownloadFormat
{
    Json,
    Xml,
    ArtplayerJson,
    BahaJson,
    BiliXml,
    DanuniJson,
    DanuniBinPb,
    DdplayJson,
    DplayerJson,
    VodJson,
}

public static class DanmuDownloadFormatExtensions
{
    public static string Value(this DanmuDownloadFormat format) => format switch
    {
        DanmuDownloadFormat.Json => "json",
        DanmuDownloadFormat.Xml => "xml",
        DanmuDownloadFormat.ArtplayerJson => "artplayer.json",
        DanmuDownloadFormat.BahaJson => "baha.json",
        DanmuDownloadFormat.BiliXml => "bili.xml",
        DanmuDownloadFormat.DanuniJson => "danuni.json",
        DanmuDownloadFormat.DanuniBinPb => "danuni.binpb",
        DanmuDownloadFormat.DdplayJson => "ddplay.json",
        DanmuDownloadFormat.DplayerJson => "dplayer.json",
        DanmuDownloadFormat.VodJson => "vod.json",
        _ => "xml",
    };

    public static string Label(this DanmuDownloadFormat format) => format switch
    {
        DanmuDownloadFormat.Json => "JSON",
        DanmuDownloadFormat.Xml => "XML",
        DanmuDownloadFormat.ArtplayerJson => "ArtPlayer JSON",
        DanmuDownloadFormat.BahaJson => "Baha JSON",
        DanmuDownloadFormat.BiliXml => "哔哩哔哩 XML",
        DanmuDownloadFormat.DanuniJson => "DanUni JSON",
        DanmuDownloadFormat.DanuniBinPb => "DanUni Protobuf",
        DanmuDownloadFormat.DdplayJson => "弹弹play JSON",
        DanmuDownloadFormat.DplayerJson => "DPlayer JSON",
        DanmuDownloadFormat.VodJson => "VOD JSON",
        _ => "XML",
    };

    public static string Extension(this DanmuDownloadFormat format) => format switch
    {
        DanmuDownloadFormat.Json => "json",
        DanmuDownloadFormat.Xml => "xml",
        DanmuDownloadFormat.ArtplayerJson => "artplayer.json",
        DanmuDownloadFormat.BahaJson => "baha.json",
        DanmuDownloadFormat.BiliXml => "bili.xml",
        DanmuDownloadFormat.DanuniJson => "danuni.json",
        DanmuDownloadFormat.DanuniBinPb => "danuni.binpb",
        DanmuDownloadFormat.DdplayJson => "ddplay.json",
        DanmuDownloadFormat.DplayerJson => "dplayer.json",
        DanmuDownloadFormat.VodJson => "vod.json",
        _ => "xml",
    };

    public static string MimeType(this DanmuDownloadFormat format) => format switch
    {
        DanmuDownloadFormat.DanuniBinPb => "application/octet-stream",
        DanmuDownloadFormat.Xml or DanmuDownloadFormat.BiliXml => "application/xml",
        _ => "application/json",
    };

    public static DanmuPayloadKind PayloadKind(this DanmuDownloadFormat format) => format switch
    {
        DanmuDownloadFormat.Xml or DanmuDownloadFormat.BiliXml => DanmuPayloadKind.Xml,
        DanmuDownloadFormat.DanuniBinPb => DanmuPayloadKind.Binary,
        _ => DanmuPayloadKind.Json,
    };

    public static bool SupportsPreview(this DanmuDownloadFormat format) =>
        format.PayloadKind() != DanmuPayloadKind.Binary;

    public static DanmuDownloadFormat? FromValueOrNull(string? raw)
    {
        var value = raw?.Trim().ToLowerInvariant() ?? string.Empty;
        foreach (DanmuDownloadFormat format in Enum.GetValues(typeof(DanmuDownloadFormat)))
        {
            if (format.Value() == value)
            {
                return format;
            }
        }

        return null;
    }

    public static DanmuDownloadFormat FromValue(string? raw) => FromValueOrNull(raw) ?? DanmuDownloadFormat.Xml;

    public static DanmuDownloadFormat? FromFileName(string? fileName)
    {
        var normalized = fileName?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length == 0)
        {
            return null;
        }

        DanmuDownloadFormat? best = null;
        var bestLength = 0;
        foreach (DanmuDownloadFormat format in Enum.GetValues(typeof(DanmuDownloadFormat)))
        {
            var extension = format.Extension();
            if (extension.Length <= bestLength)
            {
                continue;
            }

            if (normalized == extension || normalized.EndsWith("." + extension, StringComparison.Ordinal))
            {
                best = format;
                bestLength = extension.Length;
            }
        }

        return best;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<DownloadConflictPolicy>))]
public enum DownloadConflictPolicy
{
    Rename,
    Overwrite,
    Skip,
}

public static class DownloadConflictPolicyExtensions
{
    public static string Key(this DownloadConflictPolicy policy) => policy switch
    {
        DownloadConflictPolicy.Rename => "rename",
        DownloadConflictPolicy.Overwrite => "overwrite",
        DownloadConflictPolicy.Skip => "skip",
        _ => "rename",
    };

    public static string Label(this DownloadConflictPolicy policy) => policy switch
    {
        DownloadConflictPolicy.Rename => "重命名",
        DownloadConflictPolicy.Overwrite => "覆盖",
        DownloadConflictPolicy.Skip => "跳过",
        _ => "重命名",
    };

    public static DownloadConflictPolicy FromKey(string? raw)
    {
        var key = raw?.Trim().ToLowerInvariant() ?? string.Empty;
        return key switch
        {
            "overwrite" => DownloadConflictPolicy.Overwrite,
            "skip" => DownloadConflictPolicy.Skip,
            _ => DownloadConflictPolicy.Rename,
        };
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<DownloadRecordStatus>))]
public enum DownloadRecordStatus
{
    Success,
    Failed,
    Skipped,
}

public static class DownloadRecordStatusExtensions
{
    public static string Key(this DownloadRecordStatus status) => status switch
    {
        DownloadRecordStatus.Success => "success",
        DownloadRecordStatus.Failed => "failed",
        DownloadRecordStatus.Skipped => "skipped",
        _ => "failed",
    };

    public static string Label(this DownloadRecordStatus status) => status switch
    {
        DownloadRecordStatus.Success => "成功",
        DownloadRecordStatus.Failed => "失败",
        DownloadRecordStatus.Skipped => "跳过",
        _ => "失败",
    };

    public static DownloadRecordStatus FromKey(string? raw)
    {
        var key = raw?.Trim().ToLowerInvariant() ?? string.Empty;
        return key switch
        {
            "success" => DownloadRecordStatus.Success,
            "skipped" => DownloadRecordStatus.Skipped,
            "failed" => DownloadRecordStatus.Failed,
            _ => DownloadRecordStatus.Failed,
        };
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<DownloadQueueStatus>))]
public enum DownloadQueueStatus
{
    Pending,
    Running,
    Success,
    Failed,
    Skipped,
    Canceled,
}

public static class DownloadQueueStatusExtensions
{
    public static string Key(this DownloadQueueStatus status) => status switch
    {
        DownloadQueueStatus.Pending => "pending",
        DownloadQueueStatus.Running => "running",
        DownloadQueueStatus.Success => "success",
        DownloadQueueStatus.Failed => "failed",
        DownloadQueueStatus.Skipped => "skipped",
        DownloadQueueStatus.Canceled => "canceled",
        _ => "pending",
    };

    public static string Label(this DownloadQueueStatus status) => status switch
    {
        DownloadQueueStatus.Pending => "待处理",
        DownloadQueueStatus.Running => "下载中",
        DownloadQueueStatus.Success => "成功",
        DownloadQueueStatus.Failed => "失败",
        DownloadQueueStatus.Skipped => "跳过",
        DownloadQueueStatus.Canceled => "已取消",
        _ => "待处理",
    };

    public static DownloadQueueStatus FromKey(string? raw)
    {
        var key = raw?.Trim().ToLowerInvariant() ?? string.Empty;
        return key switch
        {
            "running" => DownloadQueueStatus.Running,
            "success" => DownloadQueueStatus.Success,
            "failed" => DownloadQueueStatus.Failed,
            "skipped" => DownloadQueueStatus.Skipped,
            "canceled" => DownloadQueueStatus.Canceled,
            _ => DownloadQueueStatus.Pending,
        };
    }
}

public enum DownloadThrottlePresetKind
{
    Conservative,
    Balanced,
    Fast,
    Custom,
}

public static class DownloadThrottlePreset
{
    public static DownloadThrottlePresetKind FromKey(string? raw)
    {
        var key = raw?.Trim().ToLowerInvariant() ?? string.Empty;
        return key switch
        {
            "balanced" => DownloadThrottlePresetKind.Balanced,
            "fast" => DownloadThrottlePresetKind.Fast,
            "custom" => DownloadThrottlePresetKind.Custom,
            _ => DownloadThrottlePresetKind.Conservative,
        };
    }

    public static string Key(DownloadThrottlePresetKind preset) => preset switch
    {
        DownloadThrottlePresetKind.Balanced => "balanced",
        DownloadThrottlePresetKind.Fast => "fast",
        DownloadThrottlePresetKind.Custom => "custom",
        _ => "conservative",
    };

    public static string Label(DownloadThrottlePresetKind preset) => preset switch
    {
        DownloadThrottlePresetKind.Conservative => "保守",
        DownloadThrottlePresetKind.Balanced => "均衡",
        DownloadThrottlePresetKind.Fast => "快速",
        DownloadThrottlePresetKind.Custom => "自定义",
        _ => "保守",
    };

    public static DownloadThrottleConfig ToConfig(DownloadThrottlePresetKind preset) => preset switch
    {
        DownloadThrottlePresetKind.Balanced => new DownloadThrottleConfig(
            preset, 1400, 600, 10, 20_000, 10_000, 240_000),
        DownloadThrottlePresetKind.Fast => new DownloadThrottleConfig(
            preset, 900, 350, 12, 12_000, 8_000, 180_000),
        DownloadThrottlePresetKind.Custom => new DownloadThrottleConfig(
            preset, 1400, 600, 10, 20_000, 10_000, 240_000),
        _ => new DownloadThrottleConfig(
            DownloadThrottlePresetKind.Conservative, 2000, 900, 8, 30_000, 15_000, 300_000),
    };
}

public sealed record DownloadThrottleConfig(
    DownloadThrottlePresetKind Preset,
    long BaseDelayMs,
    long JitterMaxMs,
    int BatchSize,
    long BatchRestMs,
    long BackoffBaseMs,
    long BackoffMaxMs)
{
    public string Label => DownloadThrottlePreset.Label(Preset);

    public static DownloadThrottleConfig SanitizeCustom(
        long baseDelayMs,
        long jitterMaxMs,
        int batchSize,
        long batchRestMs,
        long backoffBaseMs,
        long backoffMaxMs)
    {
        var baseDelay = Math.Clamp(baseDelayMs, 100, 120_000);
        var jitter = Math.Clamp(jitterMaxMs, 0, 20_000);
        var batch = Math.Clamp(batchSize, 1, 500);
        var batchRest = Math.Clamp(batchRestMs, 0, 900_000);
        var backoffBase = Math.Clamp(backoffBaseMs, 1_000, 900_000);
        var backoffMax = Math.Clamp(backoffMaxMs, backoffBase, 1_800_000);
        return new DownloadThrottleConfig(
            DownloadThrottlePresetKind.Custom, baseDelay, jitter, batch, batchRest, backoffBase, backoffMax);
    }
}

public static class DanmuDownloadDefaults
{
    public const string FileNameTemplate = "{animeTitle}_E{episodeNo2}_{episodeTitle}_{source}.{ext}";
    public const int MaximumRecords = 500;
    public const int MaximumQueueTasks = 1200;
    public const int MaximumScanFiles = 2_000;
    public const int MaximumScanDepth = 8;
    public const long MaximumScanInspectBytes = 16L * 1024L * 1024L;
    public const int PreviewLimit = 500;
}

public static class DanmuFileNameTemplates
{
    public static IReadOnlyList<(string Name, string Template)> Presets { get; } =
    [
        ("标准", "{animeTitle}_E{episodeNo2}_{episodeTitle}_{source}.{ext}"),
        ("简洁", "{animeTitle}-第{episodeNo}集.{ext}"),
        ("按来源", "{animeTitle}_[{source}]_E{episodeNo2}_{episodeTitle}.{ext}"),
        ("带日期", "{animeTitle}_E{episodeNo2}_{date}_{source}.{ext}"),
    ];

    public static string Render(
        string? template,
        DanmuDownloadFormat format,
        string animeTitle,
        string episodeTitle,
        int episodeNo,
        long episodeId,
        string source,
        DateTime? now = null)
    {
        var moment = now ?? DateTime.Now;
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["animeTitle"] = animeTitle,
            ["episodeTitle"] = episodeTitle,
            ["episodeNo"] = episodeNo.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["episodeNo2"] = episodeNo.ToString("00", System.Globalization.CultureInfo.InvariantCulture),
            ["episodeNo3"] = episodeNo.ToString("000", System.Globalization.CultureInfo.InvariantCulture),
            ["episodeId"] = episodeId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["source"] = string.IsNullOrWhiteSpace(source) ? "unknown" : source,
            ["format"] = format.Value(),
            ["ext"] = format.Extension(),
            ["date"] = moment.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture),
            ["datetime"] = moment.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture),
        };

        var output = string.IsNullOrWhiteSpace(template) ? DanmuDownloadDefaults.FileNameTemplate : template.Trim();
        foreach (var pair in mapping)
        {
            output = output.Replace("{" + pair.Key + "}", pair.Value, StringComparison.Ordinal);
        }

        var sanitized = SanitizeFileComponent(output);
        if (sanitized.Length == 0)
        {
            sanitized = "episode_" + episodeId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + format.Extension();
        }

        if (!sanitized.Contains('.', StringComparison.Ordinal))
        {
            sanitized += "." + format.Extension();
        }

        return sanitized;
    }

    public static string SanitizeFileComponent(string raw)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(raw.Length);
        foreach (var character in raw)
        {
            builder.Append(Array.IndexOf(invalidChars, character) >= 0 ? '_' : character);
        }

        var replaced = builder.ToString();
        foreach (var control in new[] { '\n', '\r' })
        {
            replaced = replaced.Replace(control, '_');
        }

        replaced = replaced.Trim().Trim('.');
        var collapsed = string.Join(' ', replaced.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= 120 ? collapsed : collapsed[..120];
    }
}

public sealed record DanmuDownloadSettings(
    string SaveDirectory = "",
    string SaveDirDisplayName = "",
    string DefaultFormat = "xml",
    string FileNameTemplate = DanmuDownloadDefaults.FileNameTemplate,
    string ConflictPolicy = "rename",
    string ThrottlePreset = "conservative",
    long CustomBaseDelayMs = 1400,
    long CustomJitterMaxMs = 600,
    int CustomBatchSize = 10,
    long CustomBatchRestMs = 20_000,
    long CustomBackoffBaseMs = 10_000,
    long CustomBackoffMaxMs = 240_000)
{
    public DanmuDownloadFormat Format() => DanmuDownloadFormatExtensions.FromValue(DefaultFormat);
    public DownloadConflictPolicy Policy() => DownloadConflictPolicyExtensions.FromKey(ConflictPolicy);
    public DownloadThrottlePresetKind Throttle() => DownloadThrottlePreset.FromKey(ThrottlePreset);

    public DownloadThrottleConfig ThrottleConfig()
    {
        var preset = Throttle();
        if (preset != DownloadThrottlePresetKind.Custom)
        {
            return DownloadThrottlePreset.ToConfig(preset);
        }

        return DownloadThrottleConfig.SanitizeCustom(
            CustomBaseDelayMs,
            CustomJitterMaxMs,
            CustomBatchSize,
            CustomBatchRestMs,
            CustomBackoffBaseMs,
            CustomBackoffMaxMs);
    }
}

public sealed record DanmuDownloadRecord(
    long Id,
    long CreatedAt,
    string AnimeTitle,
    string EpisodeTitle,
    long EpisodeId,
    int EpisodeNo,
    string Source,
    string Format,
    string Status,
    string FileName = "",
    string RelativePath = "",
    string FilePath = "",
    long DurationMs = 0,
    long Bytes = 0,
    int? DanmuCount = null,
    int? HttpCode = null,
    string? ErrorMessage = null,
    long AnimeId = 0)
{
    [JsonIgnore]
    public DownloadRecordStatus StatusEnum => DownloadRecordStatusExtensions.FromKey(Status);

    [JsonIgnore]
    public DanmuDownloadFormat? FormatOrNull => DanmuDownloadFormatExtensions.FromValueOrNull(Format);

    [JsonIgnore]
    public DanmuDownloadFormat FormatEnum => FormatOrNull ?? DanmuDownloadFormat.Xml;

    [JsonIgnore]
    public string FormatLabel => FormatOrNull?.Label() ?? (Format.Length == 0 ? "未知格式" : Format);
}

public sealed record DanmuDownloadTask(
    long TaskId,
    long CreatedAt,
    long UpdatedAt,
    string ApiBaseUrl = "",
    string AnimeTitle = "",
    string EpisodeTitle = "",
    long EpisodeId = 0,
    int EpisodeNo = 0,
    string Source = "",
    string Format = "xml",
    string FileNameTemplate = DanmuDownloadDefaults.FileNameTemplate,
    string ConflictPolicy = "rename",
    string Status = "pending",
    int Attempts = 0,
    string LastDetail = "",
    long RetryNotBeforeAt = 0,
    long AnimeId = 0)
{
    [JsonIgnore]
    public DownloadQueueStatus StatusEnum => DownloadQueueStatusExtensions.FromKey(Status);

    public DanmuDownloadInput ToInput() => new(
        ApiBaseUrl,
        AnimeTitle,
        EpisodeTitle,
        EpisodeId,
        EpisodeNo,
        Source,
        DanmuDownloadFormatExtensions.FromValue(Format),
        FileNameTemplate,
        DownloadConflictPolicyExtensions.FromKey(ConflictPolicy),
        AnimeId);

    public DanmuDownloadTask With(
        DownloadQueueStatus? status = null,
        string? lastDetail = null,
        bool incrementAttempt = false,
        long? retryNotBeforeAt = null,
        DanmuDownloadInput? input = null) => new(
        TaskId,
        CreatedAt,
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        input?.ApiBaseUrl ?? ApiBaseUrl,
        input?.AnimeTitle ?? AnimeTitle,
        input?.EpisodeTitle ?? EpisodeTitle,
        input?.EpisodeId ?? EpisodeId,
        input?.EpisodeNo ?? EpisodeNo,
        input?.Source ?? Source,
        input?.Format.Value() ?? Format,
        input?.FileNameTemplate ?? FileNameTemplate,
        input?.ConflictPolicy.Key() ?? ConflictPolicy,
        status is null ? Status : status.Value.Key(),
        incrementAttempt ? Attempts + 1 : Attempts,
        lastDetail is null ? LastDetail : lastDetail,
        retryNotBeforeAt is null ? RetryNotBeforeAt : Math.Max(0, retryNotBeforeAt.Value),
        input?.AnimeId ?? AnimeId);
}

public sealed record DanmuDownloadInput(
    string ApiBaseUrl,
    string AnimeTitle,
    string EpisodeTitle,
    long EpisodeId,
    int EpisodeNo,
    string Source,
    DanmuDownloadFormat Format,
    string FileNameTemplate,
    DownloadConflictPolicy ConflictPolicy,
    long AnimeId = 0);

public sealed record DanmuDownloadResult(
    DownloadRecordStatus Status,
    string FileName,
    string RelativePath,
    string FilePath,
    long Bytes,
    long DurationMs,
    int? DanmuCount = null,
    int? HttpCode = null,
    string? ErrorMessage = null);

public sealed record DownloadDirectorySyncResult(
    int ScannedFiles,
    int ImportedRecords,
    int SkippedFiles,
    bool Truncated);

public sealed record DownloadRecordDeleteResult(
    int RemovedRecords,
    int RequestedFiles,
    int DeletedFiles,
    int MissingFiles,
    int FailedFiles,
    int RetainedSharedFiles);

public sealed record DanmuPreviewItem(
    int Index,
    double? TimeSeconds = null,
    string Mode = "",
    string Color = "",
    string Source = "",
    string Text = "")
{
    public string ModeLabel => Mode switch
    {
        "4" => "底部",
        "5" => "顶部",
        "" => "滚动",
        _ => "滚动",
    };
}

public sealed record DanmuFilePreview(
    DanmuDownloadFormat Format,
    string FileName,
    string RelativePath,
    long Bytes,
    int Count,
    int PreviewLimit,
    bool Truncated,
    IReadOnlyList<DanmuPreviewItem> Items,
    string? ParseError = null);

public sealed record DanmuPayloadInspection(
    bool Valid,
    int? Count = null,
    string Error = "",
    string? Warning = null);

public sealed record DanmuDownloadPayload(
    int StatusCode,
    string ContentType,
    byte[] Body,
    string? DanmuFormat,
    int? DanmuCount);

public sealed record DanmuDownloadProgress(double Progress, string Detail);
