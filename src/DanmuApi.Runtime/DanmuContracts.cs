using System.Net;
using System.Text.Json;

namespace DanmuApi.Runtime;

public sealed record DanmuAnime(
    int AnimeId,
    string BangumiId,
    string AnimeTitle,
    string Type,
    string TypeDescription,
    string ImageUrl,
    string StartDate,
    int EpisodeCount,
    double Rating,
    bool IsFavorited,
    string Source,
    IReadOnlyList<DanmuLink> Links);

public sealed record DanmuLink(string Name, string Url, string Title, int Id);

public sealed record DanmuAnimeMatch(
    int EpisodeId,
    int AnimeId,
    string AnimeTitle,
    string EpisodeTitle,
    string Type,
    string TypeDescription,
    double Shift,
    string ImageUrl,
    string Url);

public sealed record DanmuSearchAnimeResult(
    bool Success,
    IReadOnlyList<DanmuAnime> Animes,
    string? ErrorMessage = null);

public sealed record DanmuSearchEpisode(
    int EpisodeId,
    string EpisodeTitle,
    string Url);

public sealed record DanmuSearchEpisodesAnime(
    int AnimeId,
    string AnimeTitle,
    string Type,
    string TypeDescription,
    IReadOnlyList<DanmuSearchEpisode> Episodes);

public sealed record DanmuSearchEpisodesResult(
    bool Success,
    IReadOnlyList<DanmuSearchEpisodesAnime> Animes,
    string? ErrorMessage = null);

public sealed record DanmuMatchResult(
    bool IsMatched,
    IReadOnlyList<DanmuAnimeMatch> Matches,
    string? ErrorMessage = null);

public sealed record DanmuSeason(string Id, string AirDate, string Name, int EpisodeCount);

public sealed record DanmuEpisode(
    string SeasonId,
    int EpisodeId,
    string EpisodeTitle,
    string EpisodeNumber,
    string AirDate,
    string Url);

public sealed record DanmuBangumi(
    int AnimeId,
    string BangumiId,
    string AnimeTitle,
    string ImageUrl,
    bool IsOnAir,
    int AirDay,
    bool IsFavorited,
    double Rating,
    string Type,
    string TypeDescription,
    IReadOnlyList<DanmuSeason> Seasons,
    IReadOnlyList<DanmuEpisode> Episodes);

public sealed record DanmuBangumiResult(bool Success, DanmuBangumi? Bangumi, string? ErrorMessage = null);

public sealed record DanmuComment(
    string Text,
    double TimeSeconds,
    int Mode,
    int Color,
    string? RawPosition,
    JsonElement? Extra = null,
    string? Source = null,
    long? Cid = null,
    long? Like = null,
    string? ColorV2 = null)
{
    public string ModeLabel => Mode switch
    {
        4 => "底部",
        5 => "顶部",
        _ => "滚动",
    };

    public string ColorHex => $"#{Color:X6}";
    public string SourceLabel => string.IsNullOrWhiteSpace(Source) ? "来源未知" : $"[{Source}]";
    public string CidText => Cid is long cid ? $"cid：{cid}" : "cid：未提供";
    public string LikeText => Like is long like ? $"like：{like}" : "like：未提供";
    public string ColorV2Text => string.IsNullOrWhiteSpace(ColorV2) ? "color_v2：未提供" : $"color_v2：{ColorV2}";
    public string RawPositionText => string.IsNullOrWhiteSpace(RawPosition) ? "p：未提供" : $"p：{RawPosition}";
}

public sealed record DanmuHeatmapBucket(
    int Index,
    double StartSeconds,
    double EndSeconds,
    int Count,
    double Intensity)
{
    public string RangeText => $"{Format(StartSeconds)} - {Format(EndSeconds)}";
    public string CountText => $"{Count} 条";
    public string IntensityText => $"热度 {Intensity:P0}";

    private static string Format(double seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return duration.TotalHours >= 1 ? duration.ToString("hh\\:mm\\:ss") : duration.ToString("mm\\:ss");
    }
}

public sealed record DanmuStatistics(
    int Count,
    double DurationSeconds,
    double AveragePerMinute,
    double HotStartSeconds,
    double HotEndSeconds,
    int HotCount,
    double ColoredRatio,
    double RequestSeconds)
{
    public string DurationText => TimeSpan.FromSeconds(Math.Max(0, DurationSeconds)).ToString("hh\\:mm\\:ss");
    public string AverageText => $"{AveragePerMinute:0.0} 条/分钟";
    public string HotRangeText => $"{Format(HotStartSeconds)} - {Format(HotEndSeconds)}";
    public string HotCountText => $"{HotCount} 条";
    public string ColoredRatioText => $"{ColoredRatio:P1}";
    public string RequestText => $"{RequestSeconds:0.00} 秒";

    private static string Format(double seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return duration.TotalHours >= 1 ? duration.ToString("hh\\:mm\\:ss") : duration.ToString("mm\\:ss");
    }
}

public sealed record DanmuResult(
    int Count,
    IReadOnlyList<DanmuComment> Comments,
    double? VideoDuration,
    string ContentType);

public sealed record DanmuRawApiResponse(
    int StatusCode,
    string ContentType,
    byte[] Body,
    string? BodyText,
    string RequestPath,
    TimeSpan Duration)
{
    public bool IsSuccessStatusCode => StatusCode is >= 200 and <= 299;
    public int ByteCount => Body.Length;
}

public sealed record DanmuSegment(
    string Type,
    double SegmentStart,
    double SegmentEnd,
    string Url,
    string? Data);

public sealed record DanmuFavoriteSchedule(
    string Frequency,
    string Time,
    int? Weekday,
    string Timezone,
    long? NextRunAt,
    long? RetryAt,
    long? LastRunAt,
    string? LastStatus,
    string? LastError);

public sealed record DanmuFavoriteItem(
    string Keyword,
    string AnimeTitle,
    string Source,
    IReadOnlyList<string> Sources,
    string ImageUrl,
    int EpisodeCount,
    int ResultsCount,
    long Timestamp,
    long LastRefreshAt,
    DanmuFavoriteSchedule? RefreshSchedule)
{
    public string TimestampText => FormatTimestamp(Timestamp);
    public string LastRefreshText => FormatTimestamp(LastRefreshAt);
    public string ScheduleText => RefreshSchedule is null
        ? "未设置定时刷新"
        : $"{(RefreshSchedule.Frequency == "weekly" ? "每周" : "每天")} {RefreshSchedule.Time} ({RefreshSchedule.Timezone})";
    public string NextRunText => RefreshSchedule?.NextRunAt is long value ? FormatTimestamp(value) : "未安排";
    public string RetryText => RefreshSchedule?.RetryAt is long value ? FormatTimestamp(value) : "无";
    public string LastRunText => RefreshSchedule?.LastRunAt is long value ? FormatTimestamp(value) : "未执行";
    public string LastStatusText => RefreshSchedule?.LastStatus switch
    {
        "success" => "上次执行成功",
        "failed" => $"上次执行失败：{RefreshSchedule.LastError}",
        _ => "尚无执行记录",
    };

    private static string FormatTimestamp(long value) => value <= 0
        ? "未知时间"
        : DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}

public sealed record DanmuFavoriteCapabilities(
    bool FavoriteSupported,
    bool ScheduledRefreshSupported,
    string? SupportMessage);

public sealed record DanmuFavoriteListResult(
    bool Success,
    DanmuFavoriteCapabilities Capabilities,
    IReadOnlyList<DanmuFavoriteItem> Favorites,
    string? ErrorMessage = null);

public enum DanmuApiFailureKind
{
    InvalidRequest,
    ServiceNotRunning,
    Cancelled,
    Timeout,
    Connection,
    Http,
    Authentication,
    NotFound,
    Protocol,
    ResponseTooLarge,
    Encoding,
}

public sealed class DanmuApiException : IOException
{
    public DanmuApiException(
        DanmuApiFailureKind kind,
        string message,
        Exception? innerException = null,
        int? statusCode = null)
        : base(message, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    public DanmuApiFailureKind Kind { get; }
    public int? StatusCode { get; }
}

public interface IDanmuApiClient
{
    Task<DanmuRawApiResponse> SendRawAsync(string host, int port, string? token, string apiKey, IReadOnlyDictionary<string, string?> parameters, string? jsonBody = null, CancellationToken cancellationToken = default);
    Task<DanmuDownloadPayload> DownloadCommentAsync(string host, int port, string? token, long episodeId, string formatValue, TimeSpan? requestTimeout = null, CancellationToken cancellationToken = default);
    Task<DanmuSearchAnimeResult> SearchAnimeAsync(string host, int port, string? token, string keyword, CancellationToken cancellationToken = default);
    Task<DanmuSearchEpisodesResult> SearchEpisodesAsync(string host, int port, string? token, string anime, string? episode = null, CancellationToken cancellationToken = default);
    Task<DanmuMatchResult> MatchAsync(string host, int port, string? token, string fileName, CancellationToken cancellationToken = default);
    Task<DanmuBangumiResult> GetBangumiAsync(string host, int port, string? token, int animeId, CancellationToken cancellationToken = default);
    Task<DanmuResult> GetCommentAsync(string host, int port, string? token, int commentId, bool includeDuration = true, string format = "json", CancellationToken cancellationToken = default, bool segmentFlag = false);
    Task<DanmuResult> GetCommentByUrlAsync(string host, int port, string? token, string videoUrl, bool includeDuration = true, string format = "json", CancellationToken cancellationToken = default, bool segmentFlag = false);
    Task<DanmuResult> GetSegmentCommentAsync(string host, int port, string? token, JsonElement segment, string format = "json", CancellationToken cancellationToken = default);
    Task<DanmuFavoriteListResult> GetFavoritesAsync(string host, int port, string? token, CancellationToken cancellationToken = default);
    Task<string> AddFavoriteAsync(string host, int port, string? token, string? adminToken, string keyword, CancellationToken cancellationToken = default);
    Task<string> RemoveFavoriteAsync(string host, int port, string? token, string? adminToken, string keyword, CancellationToken cancellationToken = default);
    Task<string> RefreshFavoriteAsync(string host, int port, string? token, string? adminToken, string keyword, CancellationToken cancellationToken = default);
    Task<string> SetFavoriteScheduleAsync(string host, int port, string? token, string? adminToken, string keyword, DanmuFavoriteSchedule? schedule, CancellationToken cancellationToken = default);
}
