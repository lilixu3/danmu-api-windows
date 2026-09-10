using System.Text.RegularExpressions;
using DanmuApi.Runtime;

namespace DanmuApi.App.ViewModels;

/// <summary>
/// 搜索结果展示文案：对齐核心前端/Android 弹幕测试结果（去 from 后缀、来源、集数、ID）。
/// </summary>
internal static class AnimeSearchPresentation
{
    private static readonly Regex FromSuffix = new(@"\s+from\s+.+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string DisplayTitle(string title)
    {
        var trimmed = (title ?? string.Empty).Trim();
        var withoutSource = FromSuffix.Replace(trimmed, string.Empty).Trim();
        return withoutSource.Length == 0 ? trimmed : withoutSource;
    }

    public static string SourceText(string? source, string title)
    {
        var resolved = string.IsNullOrWhiteSpace(source)
            ? DanmuDownloadParsing.ExtractSourceFromAnimeTitle(title)
            : source.Trim();
        return string.IsNullOrWhiteSpace(resolved) ? "来源未知" : resolved;
    }

    public static string EpisodeCountText(int episodeCount) =>
        episodeCount > 0 ? $"{episodeCount} 集" : "集数未知";

    public static string IdText(int animeId) => $"ID：{animeId}";

    public static string MetaText(int animeId, int episodeCount) =>
        $"{IdText(animeId)} · {EpisodeCountText(episodeCount)}";

    public static string PosterFallback(string title)
    {
        var display = DisplayTitle(title);
        return display.Length == 0 ? "搜" : display[..1];
    }
}
