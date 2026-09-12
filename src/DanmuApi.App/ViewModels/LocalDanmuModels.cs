using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DanmuApi.App.Services;
using DanmuApi.Runtime;

namespace DanmuApi.App.ViewModels;

/// <summary>本地弹幕的写权限三态。默认（未开管理员模式、也没开 LOCAL_DANMU_NOT_REQUIRE_ADMIN）
/// 只能看列表——与核心的 403 规则一致。</summary>
public enum LocalDanmuWriteAccess
{
    /// <summary>只能查看列表：需要配置 ADMIN_TOKEN 或开启 LOCAL_DANMU_NOT_REQUIRE_ADMIN。</summary>
    ReadOnly,
    /// <summary>已配置管理员密码但还没进入管理员模式，引导用户先开管理员模式。</summary>
    AdminRequired,
    /// <summary>可上传/删除。</summary>
    Writable,
}

public enum LocalDanmuTypeFilter
{
    All,
    Tv,
    Movie,
}

public sealed record LocalDanmuTypeFilterOption(LocalDanmuTypeFilter Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>类型筛选按钮：自身持有选中态（与核心页的变体卡片同一手法，便于自动化回读）。</summary>
public sealed partial class LocalDanmuFilterChip : ObservableObject
{
    public LocalDanmuFilterChip(LocalDanmuTypeFilter value, string label)
    {
        Value = value;
        Label = label;
    }

    public LocalDanmuTypeFilter Value { get; }
    public string Label { get; }

    [ObservableProperty]
    private bool _isActive;

    public override string ToString() => Label;
}

/// <summary>上传表单的类型选项（核心只接受 tv / movie）。</summary>
public sealed record LocalDanmuTypeOption(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>预览里的一行弹幕（时间 + 内容）。</summary>
public sealed record LocalDanmuPreviewLine(string TimeText, string Text);

/// <summary>分组里的一个弹幕文件（一行）。行本身是纯数据，动作按钮由视图绑定父 VM 的命令。</summary>
public sealed record LocalDanmuEpisodeRow(
    CoreLocalDanmuResource Resource,
    string EpisodeLabel,
    string MetaText,
    string UpdatedText)
{
    public string ResourceKey => Resource.ResourceKey;
    public string FileName => Resource.Filename;
    public string Title => Resource.Title;
}

/// <summary>按「标题 + 年份 + 类型 + 季」聚合出来的一个资源组。</summary>
public sealed partial class LocalDanmuGroupRow : ObservableObject
{
    public LocalDanmuGroupRow(
        string groupKey,
        string title,
        int? year,
        string type,
        int season,
        IReadOnlyList<LocalDanmuEpisodeRow> episodes)
    {
        GroupKey = groupKey;
        Title = title;
        Year = year;
        Type = type;
        Season = season;
        Episodes = episodes;
        EpisodeCountText = type == LocalDanmuTypes.Movie
            ? $"{episodes.Count} 个文件"
            : $"已上传 {episodes.Count} 集";
        CountText = $"{episodes.Sum(item => item.Resource.Count)} 条弹幕";
        SizeText = LocalDanmuFormatters.FormatBytes(episodes.Sum(item => item.Resource.Size));
        SubtitleText = LocalDanmuFormatters.GroupSubtitle(year, type, season);
        UpdatedText = episodes
            .Select(item => item.Resource.UpdatedAt)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .OrderByDescending(value => value, StringComparer.Ordinal)
            .Select(value => LocalDanmuFormatters.FormatUpdated(value))
            .FirstOrDefault() ?? "时间未知";
        IsExpanded = false;
    }

    public string GroupKey { get; }
    public string Title { get; }
    public int? Year { get; }
    public string Type { get; }
    public int Season { get; }
    public IReadOnlyList<LocalDanmuEpisodeRow> Episodes { get; }
    public string SubtitleText { get; }
    public string EpisodeCountText { get; }
    public string CountText { get; }
    public string SizeText { get; }
    public string UpdatedText { get; }
    public string ExpandText => IsExpanded ? "收起" : "展开";

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandText));

    /// <summary>整组（整季）的 resourceKey 列表，供「删除本季」使用。</summary>
    public IReadOnlyList<string> ResourceKeys => Episodes.Select(item => item.ResourceKey).ToArray();
}

/// <summary>本地弹幕列表里的一条待导入项（批量导入面板用）。</summary>
public sealed partial class LocalDanmuPendingItem : ObservableObject
{
    public LocalDanmuPendingItem(string filePath, string fileName, LocalDanmuFileGuess guess)
    {
        FilePath = filePath;
        FileName = fileName;
        _title = guess.Title;
        _year = guess.Year;
        _type = guess.Type;
        _season = guess.Season ?? 1;
        _episodeText = guess.Episode?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        Notes = guess.Notes;
    }

    public string FilePath { get; }
    public string FileName { get; }
    public IReadOnlyList<string> Notes { get; }
    public string NotesText => Notes.Count == 0 ? string.Empty : string.Join("；", Notes);
    public bool HasNotes => Notes.Count > 0;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private int? _year;

    [ObservableProperty]
    private string _type;

    [ObservableProperty]
    private int _season;

    [ObservableProperty]
    private string _episodeText;

    [ObservableProperty]
    private string _statusText = "待导入";

    [ObservableProperty]
    private bool _isSelected = true;

    [ObservableProperty]
    private bool _isFailed;

    public bool IsTv => Type == LocalDanmuTypes.Tv;

    public int? Episode =>
        int.TryParse(EpisodeText?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : null;

    /// <summary>与核心一致的字段校验；返回 null 表示这一条可以上传。</summary>
    public string? Validate(int currentYear) =>
        LocalDanmuValidation.ValidateTitle(Title)
        ?? LocalDanmuValidation.ValidateYear(Year, currentYear)
        ?? LocalDanmuValidation.ValidateType(Type)
        ?? LocalDanmuValidation.ValidateSeason(Season)
        ?? LocalDanmuValidation.ValidateEpisode(Episode, Type == LocalDanmuTypes.Movie);

    partial void OnTypeChanged(string value) => OnPropertyChanged(nameof(IsTv));
}

public static class LocalDanmuFormatters
{
    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L)
        {
            return $"{(bytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture)} MB";
        }

        if (bytes >= 1024)
        {
            return $"{(bytes / 1024d).ToString("0.0", CultureInfo.InvariantCulture)} KB";
        }

        return $"{bytes.ToString(CultureInfo.InvariantCulture)} B";
    }

    public static string FormatUpdated(string? updatedAt)
    {
        if (string.IsNullOrWhiteSpace(updatedAt) ||
            !DateTimeOffset.TryParse(updatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value))
        {
            return "时间未知";
        }

        return value.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>集标签：电影是「正片」，电视剧缺集号时是「全集」。</summary>
    public static string EpisodeLabel(int? episode, string type)
    {
        if (episode is int value)
        {
            return $"第 {value} 集";
        }

        return type == LocalDanmuTypes.Movie ? "正片" : "全集";
    }

    /// <summary>组的副标题：`2026 · 电视剧 · 第 2 季`；电影不显示季。</summary>
    public static string GroupSubtitle(int? year, string type, int season)
    {
        var yearText = year is int value ? value.ToString(CultureInfo.InvariantCulture) : "年份未知";
        var typeText = LocalDanmuTypes.ToLabel(type);
        return type == LocalDanmuTypes.Movie || season <= 1
            ? $"{yearText} · {typeText}"
            : $"{yearText} · {typeText} · 第 {season} 季";
    }

    public static string EpisodeMeta(CoreLocalDanmuResource resource) =>
        $"{resource.Count} 条 · {FormatBytes(resource.Size)} · {FormatFormat(resource.Format)} · {FormatUpdated(resource.UpdatedAt)}";

    private static string FormatFormat(string format) => string.IsNullOrWhiteSpace(format) ? "未知格式" : format;

    public static string StatsSummary(int groupCount, int fileCount, long totalBytes) =>
        $"{groupCount} 个资源 · {fileCount} 个文件 · {FormatBytes(totalBytes)}";
}
