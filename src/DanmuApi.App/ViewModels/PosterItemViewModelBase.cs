using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using DanmuApi.App.Services;
using DanmuApi.Runtime;

namespace DanmuApi.App.ViewModels;

/// <summary>
/// 海报行基类：根据核心返回的 imageUrl 异步加载封面位图；
/// 加载失败或无地址时显示标题首字占位块（参考核心前端 anime-item/favorite-cover 布局）。
/// </summary>
public abstract partial class PosterItemViewModelBase : ViewModelBase
{
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;

    [ObservableProperty]
    private Bitmap? _poster;

    protected PosterItemViewModelBase(PosterImageService? posters, string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl) || posters is null)
        {
            return;
        }

        _ = LoadPosterAsync(posters, imageUrl.Trim());
    }

    public bool HasPoster => Poster is not null;
    public bool ShowPosterPlaceholder => Poster is null;

    partial void OnPosterChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(HasPoster));
        OnPropertyChanged(nameof(ShowPosterPlaceholder));
    }

    private async Task LoadPosterAsync(PosterImageService posters, string url)
    {
        var bitmap = await posters.LoadAsync(url).ConfigureAwait(false);
        if (bitmap is null)
        {
            return;
        }

        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
        {
            Poster = bitmap;
            return;
        }

        _uiContext.Post(_ => Poster = bitmap, null);
    }
}

/// <summary>核心前端 favorite-item 的桌面等价卡片数据：左海报、中信息、右操作。</summary>
public sealed partial class FavoriteItemViewModel : PosterItemViewModelBase
{
    [ObservableProperty]
    private bool _isSelected;

    public FavoriteItemViewModel(DanmuFavoriteItem item, PosterImageService? posters)
        : base(posters, item.ImageUrl)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
    }

    public DanmuFavoriteItem Item { get; }
    public string Keyword => Item.Keyword;
    public string Title => string.IsNullOrWhiteSpace(Item.AnimeTitle)
        ? (string.IsNullOrWhiteSpace(Item.Keyword) ? "未命名剧集" : Item.Keyword)
        : Item.AnimeTitle;
    public string PosterFallbackText => Title.Length == 0 ? "藏" : Title[..1];
    public string SourceText => string.IsNullOrWhiteSpace(Item.Source) ? "未知来源" : Item.Source;
    public string EpisodeText => Item.EpisodeCount > 0 ? $"{Item.EpisodeCount} 集" : "集数未知";
    public string ResultsText => $"{Item.ResultsCount} 个搜索结果";
    public string MetaLine => $"来源：{SourceText} · {EpisodeText} · {ResultsText}";
    public string CollectedText => $"收藏时间：{Item.TimestampText}";
    public string LastRefreshText => $"最近刷新时间：{Item.LastRefreshText}";
    public string ScheduleSummaryText => Item.RefreshSchedule is null
        ? "定时刷新：未设置"
        : $"定时刷新：{Item.ScheduleText}";
    public string ScheduleDetailText => Item.RefreshSchedule is null
        ? string.Empty
        : BuildScheduleDetail(Item.RefreshSchedule);
    public bool HasSchedule => Item.RefreshSchedule is not null;

    private static string BuildScheduleDetail(DanmuFavoriteSchedule schedule)
    {
        var parts = new List<string>();
        if (schedule.NextRunAt is long nextRun)
        {
            parts.Add($"下次 {FormatUnix(nextRun)}");
        }

        if (!string.IsNullOrWhiteSpace(schedule.LastStatus))
        {
            parts.Add(schedule.LastStatus!);
        }

        return parts.Count == 0 ? string.Empty : string.Join(" · ", parts);
    }

    private static string FormatUnix(long unixMilliseconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
