using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DanmuApi.App.Services;
using DanmuApi.Runtime;

namespace DanmuApi.App.ViewModels;

public enum DanmuTestTab
{
    Auto,
    Manual,
    Favorites,
}

public sealed record DanmuTestTabOption(DanmuTestTab Value, string Title, string Description)
{
    public override string ToString() => Title;
}

public sealed record DanmuExportFormatOption(DanmuDownloadFormat Value, string Title)
{
    public override string ToString() => Title;
}

public enum DanmuPageRoute
{
    Entry,
    SearchResults,
    BangumiDetails,
    DanmuDetails,
}

public enum DanmuReturnTarget
{
    Auto,
    ManualSearch,
    SearchResults,
    BangumiDetails,
    Favorites,
}

internal static class DanmuScenes
{
    public static IDisposable Begin(string scene) => DanmuRequestSceneContext.Push(scene);
}

public sealed partial class DanmuTestPageViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly RuntimeApiContext _context;
    private readonly IDanmuApiClient _client;
    private readonly IUiDialogService _dialogs;
    private readonly IAppDiagnostics _diagnostics;
    private readonly PosterImageService? _posters;
    private readonly ILocalRequestRecordStore? _requestRecords;
    private CancellationTokenSource _lifetime = new();
    private readonly Stack<object> _history = new();

    [ObservableProperty]
    private DanmuTestTabOption _selectedTabOption = null!;

    [ObservableProperty]
    private object _currentPage = null!;

    public DanmuTestPageViewModel(
        RuntimeApiContext context,
        IDanmuApiClient client,
        IUiDialogService dialogs,
        IAppDiagnostics diagnostics,
        ILocalRequestRecordStore? requestRecords = null,
        PosterImageService? posters = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _requestRecords = requestRecords;
        _posters = posters;
        TabOptions =
        [
            new(DanmuTestTab.Auto, "自动匹配", "输入文件名，调用核心自动选择剧集"),
            new(DanmuTestTab.Manual, "手动匹配", "搜索动漫、查看番剧和选择剧集"),
            new(DanmuTestTab.Favorites, "收藏", "管理核心收藏和刷新能力。"),
        ];
        _selectedTabOption = TabOptions[0];
        _currentPage = CreateEntryPage(DanmuTestTab.Auto);
        StartInitialLoad(_currentPage);
    }

    private static void StartInitialLoad(object page)
    {
        switch (page)
        {
            case SearchResultsPageViewModel results:
                _ = results.LoadCommand.ExecuteAsync(null);
                break;
            case BangumiDetailsPageViewModel bangumi:
                _ = bangumi.LoadCommand.ExecuteAsync(null);
                break;
            case DanmuDetailsPageViewModel danmu:
                _ = danmu.LoadCommand.ExecuteAsync(null);
                break;
            case FavoritesPageViewModel favorites:
                _ = favorites.LoadCommand.ExecuteAsync(null);
                break;
        }
    }

    public IReadOnlyList<DanmuTestTabOption> TabOptions { get; }
    internal PosterImageService? Posters => _posters;
    public DanmuPageRoute Route => CurrentPage switch
    {
        SearchResultsPageViewModel => DanmuPageRoute.SearchResults,
        BangumiDetailsPageViewModel => DanmuPageRoute.BangumiDetails,
        DanmuDetailsPageViewModel => DanmuPageRoute.DanmuDetails,
        _ => DanmuPageRoute.Entry,
    };

    partial void OnSelectedTabOptionChanged(DanmuTestTabOption value)
    {
        _history.Clear();
        CancelPage();
        CurrentPage = CreateEntryPage(value.Value);
        OnPropertyChanged(nameof(Route));
        StartInitialLoad(CurrentPage);
    }

    private object CreateEntryPage(DanmuTestTab tab) => tab switch
    {
        DanmuTestTab.Auto => new AutoMatchPageViewModel(this, _context, _client, _dialogs, _diagnostics),
        DanmuTestTab.Manual => new ManualSearchPageViewModel(this, _context, _client, _dialogs, _diagnostics),
        DanmuTestTab.Favorites => new FavoritesPageViewModel(this, _context, _client, _dialogs, _diagnostics, _posters),
        _ => throw new ArgumentOutOfRangeException(nameof(tab)),
    };

    internal CancellationToken BeginPageRequest()
    {
        CancelPage();
        _lifetime = new CancellationTokenSource();
        return _lifetime.Token;
    }

    internal void Navigate(object page)
    {
        ArgumentNullException.ThrowIfNull(page);
        _history.Push(CurrentPage);
        CancelPage();
        CurrentPage = page;
        OnPropertyChanged(nameof(Route));
        StartInitialLoad(page);
    }

    internal void Back()
    {
        if (_history.Count == 0)
        {
            return;
        }

        CancelPage();
        CurrentPage = _history.Pop();
        OnPropertyChanged(nameof(Route));
    }

    internal void BackToTab(DanmuTestTab tab)
    {
        _history.Clear();
        SelectedTabOption = TabOptions.First(item => item.Value == tab);
    }

    internal void RecordExport(string format, long startedTimestamp, bool success, string? errorMessage)
    {
        if (_requestRecords is null)
        {
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(startedTimestamp);
        _requestRecords.Add(new LocalRequestRecord(
            _requestRecords.AllocateId(),
            DateTimeOffset.UtcNow,
            $"弹幕测试/导出/{format}",
            "WRITE",
            RequestRecordRedactor.MaskInterface($"local://danmu/export?format={Uri.EscapeDataString(format)}"),
            string.Empty,
            null,
            Math.Max(0, (long)Math.Round(elapsed.TotalMilliseconds)),
            success,
            errorMessage,
            string.Empty));
    }

    internal async Task ShowFailureAsync(string title, Exception error)
    {
        if (error is DanmuApiException { Kind: DanmuApiFailureKind.Cancelled })
        {
            return;
        }

        _diagnostics.Record($"{title}失败", error);
        await _dialogs.ShowMessageAsync(title, error.Message, isError: true).ConfigureAwait(true);
    }

    private void CancelPage()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        _lifetime = new CancellationTokenSource();
    }

    public ValueTask DisposeAsync()
    {
        CancelPage();
        return ValueTask.CompletedTask;
    }
}

public sealed partial class AutoMatchPageViewModel : ViewModelBase
{
    private readonly DanmuTestPageViewModel _owner;
    private readonly RuntimeApiContext _context;
    private readonly IDanmuApiClient _client;
    private readonly IUiDialogService _dialogs;
    private readonly IAppDiagnostics _diagnostics;
    private bool _isBusy;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<DanmuAnimeMatch> _matches = [];

    [ObservableProperty]
    private string? _diagnostic;

    public AutoMatchPageViewModel(DanmuTestPageViewModel owner, RuntimeApiContext context, IDanmuApiClient client, IUiDialogService dialogs, IAppDiagnostics diagnostics)
    {
        _owner = owner;
        _context = context;
        _client = client;
        _dialogs = dialogs;
        _diagnostics = diagnostics;
    }

    public bool IsBusy => _isBusy;
    public bool HasMatches => Matches.Count > 0;

    [RelayCommand]
    private async Task MatchAsync()
    {
        if (_isBusy || string.IsNullOrWhiteSpace(FileName))
        {
            Diagnostic = "请输入文件名或播放链接 URL。";
            return;
        }

        try
        {
            _context.EnsureRunning();
            _isBusy = true;
            OnPropertyChanged(nameof(IsBusy));
            var input = FileName.Trim();
            var token = _owner.BeginPageRequest();
            if (TryGetHttpUrl(input, out var directUrl))
            {
                Diagnostic = "正在按播放链接解析弹幕…";
                _owner.Navigate(new DanmuDetailsPageViewModel(
                    _owner,
                    _context,
                    _client,
                    _dialogs,
                    _diagnostics,
                    0,
                    "URL 弹幕 · " + directUrl,
                    directUrl,
                    DanmuReturnTarget.Auto));
                return;
            }

            using var scene = DanmuScenes.Begin("弹幕测试/自动匹配");
            var result = await _client.MatchAsync(_context.Host, _context.Port!.Value, _context.Token, input, token).ConfigureAwait(true);
            Matches = result.Matches;
            Diagnostic = result.IsMatched ? $"核心返回 {Matches.Count} 个匹配，默认按核心顺序展示。" : result.ErrorMessage ?? "未匹配到结果。";
            if (result.IsMatched && Matches.Count > 0)
            {
                await OpenMatchAsync(Matches[0]).ConfigureAwait(true);
            }
        }
        catch (Exception error) when (error is DanmuApiException or ArgumentException)
        {
            Diagnostic = error.Message;
            await _owner.ShowFailureAsync("自动匹配失败", error).ConfigureAwait(true);
        }
        finally
        {
            _isBusy = false;
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    [RelayCommand]
    private async Task OpenMatchAsync(DanmuAnimeMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        _owner.Navigate(new DanmuDetailsPageViewModel(_owner, _context, _client, _dialogs, _diagnostics, match.EpisodeId, $"{match.AnimeTitle} {match.EpisodeTitle}", match.Url, DanmuReturnTarget.Auto));
        await Task.CompletedTask;
    }

    private static bool TryGetHttpUrl(string value, out string url)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https")
        {
            url = parsed.ToString();
            return true;
        }

        url = string.Empty;
        return false;
    }
}

public sealed partial class ManualSearchPageViewModel : ViewModelBase
{
    private readonly DanmuTestPageViewModel _owner;
    private readonly RuntimeApiContext _context;
    private readonly IDanmuApiClient _client;
    private readonly IUiDialogService _dialogs;
    private readonly IAppDiagnostics _diagnostics;
    private bool _isBusy;

    [ObservableProperty]
    private string _keyword = string.Empty;

    [ObservableProperty]
    private string? _diagnostic;

    public ManualSearchPageViewModel(DanmuTestPageViewModel owner, RuntimeApiContext context, IDanmuApiClient client, IUiDialogService dialogs, IAppDiagnostics diagnostics)
    {
        _owner = owner;
        _context = context;
        _client = client;
        _dialogs = dialogs;
        _diagnostics = diagnostics;
    }

    public bool IsBusy => _isBusy;

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (_isBusy || string.IsNullOrWhiteSpace(Keyword))
        {
            Diagnostic = "请输入搜索关键字。";
            return;
        }

        try
        {
            _context.EnsureRunning();
            _isBusy = true;
            OnPropertyChanged(nameof(IsBusy));
            var input = Keyword.Trim();
            if (TryGetHttpUrl(input, out var directUrl))
            {
                Diagnostic = "正在按播放链接解析弹幕…";
                _owner.Navigate(new DanmuDetailsPageViewModel(
                    _owner,
                    _context,
                    _client,
                    _dialogs,
                    _diagnostics,
                    0,
                    "URL 弹幕 · " + directUrl,
                    directUrl,
                    DanmuReturnTarget.ManualSearch));
                return;
            }

            var token = _owner.BeginPageRequest();
            using var scene = DanmuScenes.Begin("弹幕测试/手动搜索动漫");
            var result = await _client.SearchAnimeAsync(_context.Host, _context.Port!.Value, _context.Token, input, token).ConfigureAwait(true);
            if (!result.Success || result.Animes.Count == 0)
            {
                Diagnostic = result.ErrorMessage ?? "未找到相关动漫。";
                return;
            }

            _owner.Navigate(new SearchResultsPageViewModel(_owner, _context, _client, _dialogs, _diagnostics, Keyword.Trim(), result.Animes, _owner.Posters));
        }
        catch (Exception error) when (error is DanmuApiException or ArgumentException)
        {
            Diagnostic = error.Message;
            await _owner.ShowFailureAsync("搜索失败", error).ConfigureAwait(true);
        }
        finally
        {
            _isBusy = false;
            OnPropertyChanged(nameof(IsBusy));
        }
    }
    private static bool TryGetHttpUrl(string value, out string url)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https")
        {
            url = parsed.ToString();
            return true;
        }

        url = string.Empty;
        return false;
    }
}

public sealed class SearchResultRowViewModel : PosterItemViewModelBase
{
    public SearchResultRowViewModel(DanmuAnime anime, PosterImageService? posters)
        : base(posters, anime.ImageUrl)
    {
        Anime = anime ?? throw new ArgumentNullException(nameof(anime));
    }

    public DanmuAnime Anime { get; }
    public string DisplayTitle => AnimeSearchPresentation.DisplayTitle(Anime.AnimeTitle);
    public string SourceText => $"来源：{AnimeSearchPresentation.SourceText(Anime.Source, Anime.AnimeTitle)}";
    public string MetaText => AnimeSearchPresentation.MetaText(Anime.AnimeId, Anime.EpisodeCount);
    public string PosterFallbackText => AnimeSearchPresentation.PosterFallback(Anime.AnimeTitle);
}

public sealed partial class SearchResultsPageViewModel : ViewModelBase
{
    private readonly DanmuTestPageViewModel _owner;
    private readonly RuntimeApiContext _context;
    private readonly IDanmuApiClient _client;
    private readonly IUiDialogService _dialogs;
    private readonly IAppDiagnostics _diagnostics;
    private readonly PosterImageService? _posters;
    private bool _isLoadingFavorites;
    private string? _favoriteKeyword;

    [ObservableProperty]
    private string? _diagnostic;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isFavorite;

    public SearchResultsPageViewModel(DanmuTestPageViewModel owner, RuntimeApiContext context, IDanmuApiClient client, IUiDialogService dialogs, IAppDiagnostics diagnostics, string keyword, IReadOnlyList<DanmuAnime> results, PosterImageService? posters = null)
    {
        _owner = owner;
        _context = context;
        _client = client;
        _dialogs = dialogs;
        _diagnostics = diagnostics;
        _posters = posters;
        Keyword = keyword;
        Results = results;
        foreach (var anime in results)
        {
            Rows.Add(new SearchResultRowViewModel(anime, posters));
        }
    }

    public string Keyword { get; }
    public IReadOnlyList<DanmuAnime> Results { get; }
    public ObservableCollection<SearchResultRowViewModel> Rows { get; } = [];
    public bool IsLoadingFavorites => _isLoadingFavorites;
    public bool HasFavoriteCapability { get; private set; }
    public bool HasDiagnostic => !string.IsNullOrWhiteSpace(Diagnostic);
    public string FavoriteCapabilityText { get; private set; } = "正在读取收藏能力…";
    public string FavoriteActionText => IsBusy ? "处理中…" : IsFavorite ? "取消收藏快照" : "收藏当前搜索快照";
    public bool CanToggleFavorite => HasFavoriteCapability && !IsLoadingFavorites && !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(FavoriteActionText));
        OnPropertyChanged(nameof(CanToggleFavorite));
    }

    partial void OnIsFavoriteChanged(bool value) => OnPropertyChanged(nameof(FavoriteActionText));

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_isLoadingFavorites)
        {
            return;
        }

        try
        {
            _context.EnsureRunning();
            _isLoadingFavorites = true;
            OnPropertyChanged(nameof(IsLoadingFavorites));
            OnPropertyChanged(nameof(CanToggleFavorite));
            using var scene = DanmuScenes.Begin("弹幕测试/收藏列表");
            var result = await _client.GetFavoritesAsync(_context.Host, _context.Port!.Value, _context.Token, _owner.BeginPageRequest()).ConfigureAwait(true);
            var matchingFavorite = result.Favorites.FirstOrDefault(item =>
                Results.Any(anime => FavoriteMatches(item, Keyword, anime.AnimeTitle)));
            _favoriteKeyword = matchingFavorite?.Keyword;
            IsFavorite = matchingFavorite is not null;
            HasFavoriteCapability = result.Capabilities.FavoriteSupported;
            FavoriteCapabilityText = result.Capabilities.FavoriteSupported
                ? "收藏会保存当前关键词对应的整组搜索结果快照。"
                : result.Capabilities.SupportMessage ?? "当前部署不支持收藏";
            OnPropertyChanged(nameof(FavoriteCapabilityText));
            OnPropertyChanged(nameof(HasFavoriteCapability));
            OnPropertyChanged(nameof(CanToggleFavorite));
        }
        catch (Exception error) when (error is DanmuApiException or ArgumentException)
        {
            FavoriteCapabilityText = error.Message;
            Diagnostic = error.Message;
            OnPropertyChanged(nameof(HasDiagnostic));
            OnPropertyChanged(nameof(FavoriteCapabilityText));
            await _owner.ShowFailureAsync("读取收藏能力失败", error).ConfigureAwait(true);
        }
        finally
        {
            _isLoadingFavorites = false;
            OnPropertyChanged(nameof(IsLoadingFavorites));
            OnPropertyChanged(nameof(CanToggleFavorite));
        }
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (!CanToggleFavorite)
        {
            Diagnostic = FavoriteCapabilityText;
            OnPropertyChanged(nameof(HasDiagnostic));
            return;
        }

        try
        {
            _context.EnsureRunning();
            var admin = _context.AdminToken();
            if (admin is null)
            {
                Diagnostic = "请先在 设置 > 安全 开启管理员模式。";
                OnPropertyChanged(nameof(HasDiagnostic));
                return;
            }

            IsBusy = true;
            var requestKeyword = IsFavorite ? _favoriteKeyword ?? Keyword.Trim() : Keyword.Trim();
            string message;
            using (DanmuScenes.Begin(IsFavorite ? "弹幕测试/收藏/remove" : "弹幕测试/收藏/add"))
            {
                message = IsFavorite
                    ? await _client.RemoveFavoriteAsync(_context.Host, _context.Port!.Value, _context.Token, admin, requestKeyword, _owner.BeginPageRequest()).ConfigureAwait(true)
                    : await _client.AddFavoriteAsync(_context.Host, _context.Port!.Value, _context.Token, admin, requestKeyword, _owner.BeginPageRequest()).ConfigureAwait(true);
            }

            Diagnostic = message;
            OnPropertyChanged(nameof(HasDiagnostic));
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception error) when (error is DanmuApiException or ArgumentException)
        {
            Diagnostic = error.Message;
            OnPropertyChanged(nameof(HasDiagnostic));
            await _owner.ShowFailureAsync("收藏操作失败", error).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SelectAnime(DanmuAnime anime)
    {
        ArgumentNullException.ThrowIfNull(anime);
        _owner.Navigate(new BangumiDetailsPageViewModel(_owner, _context, _client, _dialogs, _diagnostics, anime, DanmuReturnTarget.SearchResults));
    }

    [RelayCommand]
    private void Back() => _owner.Back();

    private static bool FavoriteMatches(DanmuFavoriteItem favorite, string keyword, string animeTitle)
    {
        var queryTitles = new[] { keyword, animeTitle }
            .Select(StripSeasonSuffix)
            .Where(value => value.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (queryTitles.Length == 0)
        {
            return false;
        }

        var favoriteTitles = new[] { favorite.Keyword, favorite.AnimeTitle }
            .Select(StripSeasonSuffix)
            .Where(value => value.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return favoriteTitles.Any(favoriteTitle => queryTitles.Any(queryTitle =>
            string.Equals(favoriteTitle, queryTitle, StringComparison.OrdinalIgnoreCase) ||
            queryTitle.Contains(favoriteTitle, StringComparison.OrdinalIgnoreCase) ||
            favoriteTitle.Contains(queryTitle, StringComparison.OrdinalIgnoreCase)));
    }

    private static string StripSeasonSuffix(string value)
    {
        var trimmed = value.Trim();
        var suffixStart = trimmed.LastIndexOf("_S", StringComparison.OrdinalIgnoreCase);
        if (suffixStart >= 0 && suffixStart + 2 < trimmed.Length &&
            trimmed[(suffixStart + 2)..].All(char.IsDigit))
        {
            return trimmed[..suffixStart].Trim();
        }

        return trimmed;
    }
}

public sealed partial class BangumiDetailsPageViewModel : ViewModelBase
{
    private readonly DanmuTestPageViewModel _owner;
    private readonly RuntimeApiContext _context;
    private readonly IDanmuApiClient _client;
    private readonly IUiDialogService _dialogs;
    private readonly IAppDiagnostics _diagnostics;
    private bool _isBusy;

    [ObservableProperty]
    private DanmuBangumi? _bangumi;

    [ObservableProperty]
    private string? _diagnostic;

    [ObservableProperty]
    private string _episodeQuery = string.Empty;

    public BangumiDetailsPageViewModel(DanmuTestPageViewModel owner, RuntimeApiContext context, IDanmuApiClient client, IUiDialogService dialogs, IAppDiagnostics diagnostics, DanmuAnime anime, DanmuReturnTarget returnTarget)
    {
        _owner = owner;
        _context = context;
        _client = client;
        _dialogs = dialogs;
        _diagnostics = diagnostics;
        Anime = anime;
        ReturnTarget = returnTarget;
    }

    public DanmuAnime Anime { get; }
    public DanmuReturnTarget ReturnTarget { get; }
    public bool IsBusy => _isBusy;
    public IReadOnlyList<DanmuEpisode> Episodes => Bangumi?.Episodes ?? [];
    public IReadOnlyList<DanmuEpisode> VisibleEpisodes
    {
        get
        {
            var query = EpisodeQuery.Trim();
            if (query.Length == 0)
            {
                return Episodes;
            }

            if (int.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out var requestedNumber) && requestedNumber > 0)
            {
                return Episodes.Where(episode => EpisodeNumberMatches(episode, requestedNumber)).ToArray();
            }

            return Episodes.Where(episode =>
                episode.EpisodeTitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                episode.EpisodeNumber.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
    }
    public string EpisodeSummaryText => string.IsNullOrWhiteSpace(EpisodeQuery)
        ? $"共 {Episodes.Count} 集"
        : $"筛选结果 {VisibleEpisodes.Count} / {Episodes.Count}";

    partial void OnEpisodeQueryChanged(string value)
    {
        OnPropertyChanged(nameof(VisibleEpisodes));
        OnPropertyChanged(nameof(EpisodeSummaryText));
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_isBusy || Bangumi is not null)
        {
            return;
        }

        try
        {
            _context.EnsureRunning();
            _isBusy = true;
            OnPropertyChanged(nameof(IsBusy));
            var token = _owner.BeginPageRequest();
            using var scene = DanmuScenes.Begin("弹幕测试/加载剧集");
            var result = await _client.GetBangumiAsync(_context.Host, _context.Port!.Value, _context.Token, Anime.AnimeId, token).ConfigureAwait(true);
            Bangumi = result.Bangumi;
            Diagnostic = result.Success ? null : result.ErrorMessage ?? "番剧详情不可用。";
            OnPropertyChanged(nameof(Episodes));
            OnPropertyChanged(nameof(VisibleEpisodes));
            OnPropertyChanged(nameof(EpisodeSummaryText));
        }
        catch (Exception error) when (error is DanmuApiException or ArgumentException)
        {
            Diagnostic = error.Message;
            await _owner.ShowFailureAsync("获取番剧详情失败", error).ConfigureAwait(true);
        }
        finally
        {
            _isBusy = false;
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    [RelayCommand]
    private void JumpToEpisode()
    {
        if (!int.TryParse(EpisodeQuery.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var episodeNumber) || episodeNumber <= 0)
        {
            Diagnostic = "请输入有效的正整数集数。";
            return;
        }

        var episode = Episodes.FirstOrDefault(item => EpisodeNumberMatches(item, episodeNumber));
        if (episode is null)
        {
            Diagnostic = $"找不到第 {episodeNumber} 集。";
            return;
        }

        EpisodeQuery = episodeNumber.ToString(CultureInfo.InvariantCulture);
        Diagnostic = $"已定位第 {episodeNumber} 集，请点击该行的“获取弹幕”。";
    }

    private static bool EpisodeNumberMatches(DanmuEpisode episode, int requestedNumber) =>
        int.TryParse(episode.EpisodeNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
        number == requestedNumber;

    [RelayCommand]
    private void SelectEpisode(DanmuEpisode episode)
    {
        ArgumentNullException.ThrowIfNull(episode);
        _owner.Navigate(new DanmuDetailsPageViewModel(_owner, _context, _client, _dialogs, _diagnostics, episode.EpisodeId, $"{Bangumi?.AnimeTitle ?? Anime.AnimeTitle} {episode.EpisodeTitle}", episode.Url, DanmuReturnTarget.BangumiDetails));
    }

    [RelayCommand]
    private void Back() => _owner.Back();
}

public enum DanmuFilter
{
    All,
    Scroll,
    Top,
    Bottom,
}

public sealed partial class DanmuDetailsPageViewModel : ViewModelBase
{
    private const int PageSize = 500;
    private readonly DanmuTestPageViewModel _owner;
    private readonly RuntimeApiContext _context;
    private readonly IDanmuApiClient _client;
    private readonly IUiDialogService _dialogs;
    private readonly IAppDiagnostics _diagnostics;
    private bool _isBusy;
    private bool _loadCompleted;
    private bool _usedSourceUrlFallback;
    private bool _loadFailed;
    private int _requestSequence;

    [ObservableProperty]
    private IReadOnlyList<DanmuComment> _allComments = [];

    [ObservableProperty]
    private IReadOnlyList<DanmuComment> _filteredComments = [];

    [ObservableProperty]
    private DanmuFilter _currentFilter = DanmuFilter.All;

    [ObservableProperty]
    private int _filterIndex;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private int _displayedCount;

    [ObservableProperty]
    private string? _diagnostic;

    [ObservableProperty]
    private string _loadingText = string.Empty;

    [ObservableProperty]
    private DanmuExportFormatOption _selectedExportFormatOption = null!;

    [ObservableProperty]
    private double? _videoDuration;

    [ObservableProperty]
    private double _requestSeconds;

    [ObservableProperty]
    private DanmuStatistics? _statistics;

    [ObservableProperty]
    private IReadOnlyList<DanmuHeatmapBucket> _heatmapBuckets = [];

    [ObservableProperty]
    private IReadOnlyList<DanmuHeatmapBucket> _highMoments = [];

    [ObservableProperty]
    private int _selectedHeatmapIndex = -1;

    public DanmuDetailsPageViewModel(DanmuTestPageViewModel owner, RuntimeApiContext context, IDanmuApiClient client, IUiDialogService dialogs, IAppDiagnostics diagnostics, int episodeId, string title, string sourceUrl, DanmuReturnTarget returnTarget)
    {
        _owner = owner;
        _context = context;
        _client = client;
        _dialogs = dialogs;
        _diagnostics = diagnostics;
        EpisodeId = episodeId;
        Title = title;
        SourceUrl = sourceUrl;
        ReturnTarget = returnTarget;
        _selectedExportFormatOption = ExportFormatOptions[0];
    }

    public int EpisodeId { get; }
    public string Title { get; }
    public string SourceUrl { get; }
    public DanmuReturnTarget ReturnTarget { get; }
    public IReadOnlyList<DanmuExportFormatOption> ExportFormatOptions { get; } =
    [
        new(DanmuDownloadFormat.Json, DanmuDownloadFormat.Json.Label()),
        new(DanmuDownloadFormat.Xml, DanmuDownloadFormat.Xml.Label()),
        new(DanmuDownloadFormat.DdplayJson, DanmuDownloadFormat.DdplayJson.Label()),
        new(DanmuDownloadFormat.DplayerJson, DanmuDownloadFormat.DplayerJson.Label()),
        new(DanmuDownloadFormat.ArtplayerJson, DanmuDownloadFormat.ArtplayerJson.Label()),
        new(DanmuDownloadFormat.VodJson, DanmuDownloadFormat.VodJson.Label()),
        new(DanmuDownloadFormat.BahaJson, DanmuDownloadFormat.BahaJson.Label()),
        new(DanmuDownloadFormat.BiliXml, DanmuDownloadFormat.BiliXml.Label()),
        new(DanmuDownloadFormat.DanuniJson, DanmuDownloadFormat.DanuniJson.Label()),
        new(DanmuDownloadFormat.DanuniBinPb, DanmuDownloadFormat.DanuniBinPb.Label()),
    ];
    public int PageSizeValue => PageSize;
    public bool IsBusy => _isBusy;
    public bool HasLoadedComments => _loadCompleted;
    public bool CanExport => _loadCompleted && EpisodeId > 0;
    public bool HasDiagnostic => !string.IsNullOrWhiteSpace(Diagnostic);
    public bool CanRetry => !IsBusy && _loadFailed;
    public IReadOnlyList<DanmuComment> DisplayedComments => FilteredComments.Take(DisplayedCount).ToArray();
    public bool CanLoadMore => DisplayedCount < FilteredComments.Count;
    public string CountText => $"{FilteredComments.Count} 条";
    public string DurationText => VideoDuration is > 0 ? TimeSpan.FromSeconds(VideoDuration.Value).ToString("hh\\:mm\\:ss") : "未提供";
    public string SourceUrlText => string.IsNullOrWhiteSpace(SourceUrl) ? "未提供来源地址" : SourceUrl;
    public bool HasSourceUrl => Uri.TryCreate(SourceUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
    public bool HasStatistics => Statistics is not null;
    public bool HasHeatmap => HeatmapBuckets.Count > 0;
    public bool HasHighMoments => HighMoments.Count > 0;
    public bool HasDisplayedComments => DisplayedComments.Count > 0;
    public bool ShowEmptyFilterState => _loadCompleted && AllComments.Count > 0 && FilteredComments.Count == 0;
    public DanmuHeatmapBucket? SelectedHeatmapBucket =>
        SelectedHeatmapIndex >= 0 && SelectedHeatmapIndex < HeatmapBuckets.Count
            ? HeatmapBuckets[SelectedHeatmapIndex]
            : null;
    public string SelectedHeatmapRangeText => SelectedHeatmapBucket?.RangeText ?? "拖动时间轴或使用方向键查看区间";
    public string SelectedHeatmapCountText => SelectedHeatmapBucket?.CountText ?? "未选择";

    partial void OnStatisticsChanged(DanmuStatistics? value) => OnPropertyChanged(nameof(HasStatistics));
    partial void OnHeatmapBucketsChanged(IReadOnlyList<DanmuHeatmapBucket> value)
    {
        SelectedHeatmapIndex = value.Count == 0 ? -1 : Math.Clamp(SelectedHeatmapIndex, 0, value.Count - 1);
        OnPropertyChanged(nameof(HasHeatmap));
        OnPropertyChanged(nameof(SelectedHeatmapBucket));
        OnPropertyChanged(nameof(SelectedHeatmapRangeText));
        OnPropertyChanged(nameof(SelectedHeatmapCountText));
    }
    partial void OnHighMomentsChanged(IReadOnlyList<DanmuHeatmapBucket> value) => OnPropertyChanged(nameof(HasHighMoments));
    partial void OnSelectedHeatmapIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedHeatmapBucket));
        OnPropertyChanged(nameof(SelectedHeatmapRangeText));
        OnPropertyChanged(nameof(SelectedHeatmapCountText));
    }

    partial void OnFilterIndexChanged(int value)
    {
        CurrentFilter = value switch
        {
            1 => DanmuFilter.Scroll,
            2 => DanmuFilter.Top,
            3 => DanmuFilter.Bottom,
            _ => DanmuFilter.All,
        };
        ApplyFilter();
    }

    partial void OnCurrentFilterChanged(DanmuFilter value)
    {
        if (FilterIndex != (int)value)
        {
            FilterIndex = value switch
            {
                DanmuFilter.Scroll => 1,
                DanmuFilter.Top => 2,
                DanmuFilter.Bottom => 3,
                _ => 0,
            };
        }
        ApplyFilter();
    }

    partial void OnSearchQueryChanged(string value) => ApplyFilter();

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_isBusy || _loadCompleted)
        {
            return;
        }

        var sequence = Interlocked.Increment(ref _requestSequence);
        var started = Stopwatch.GetTimestamp();
        try
        {
            _context.EnsureRunning();
            _isBusy = true;
            _usedSourceUrlFallback = false;
            _loadCompleted = false;
            OnPropertyChanged(nameof(HasLoadedComments));
            OnPropertyChanged(nameof(CanExport));
            _loadFailed = false;
            LoadingText = "正在从核心拉取弹幕，可能需要一点时间…";
            Diagnostic = null;
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(LoadingText));
            OnPropertyChanged(nameof(CanRetry));
            var token = _owner.BeginPageRequest();
            using var scene = DanmuScenes.Begin(EpisodeId > 0
                ? (ReturnTarget == DanmuReturnTarget.Auto ? "弹幕测试/自动匹配弹幕" : "弹幕测试/手动获取弹幕")
                : (ReturnTarget == DanmuReturnTarget.Auto ? "弹幕测试/自动URL解析" : "弹幕测试/手动URL解析"));
            DanmuResult result;
            try
            {
                result = EpisodeId > 0
                    ? await _client.GetCommentAsync(_context.Host, _context.Port!.Value, _context.Token, EpisodeId, true, "json", token).ConfigureAwait(true)
                    : await _client.GetCommentByUrlAsync(_context.Host, _context.Port!.Value, _context.Token, SourceUrl, true, "json", token).ConfigureAwait(true);
            }
            catch (Exception primaryError) when (EpisodeId > 0 && HasSourceUrl && ShouldTrySourceFallback(primaryError))
            {
                _usedSourceUrlFallback = true;
                LoadingText = "按剧集 ID 失败，正在尝试来源 URL…";
                OnPropertyChanged(nameof(LoadingText));
                try
                {
                    using var fallbackScene = DanmuScenes.Begin("弹幕测试/来源URL兜底");
                    result = await _client.GetCommentByUrlAsync(_context.Host, _context.Port!.Value, _context.Token, SourceUrl, true, "json", token).ConfigureAwait(true);
                }
                catch (Exception fallbackError) when (fallbackError is DanmuApiException or ArgumentException)
                {
                    if (fallbackError is DanmuApiException apiFailure)
                    {
                        throw new DanmuApiException(
                            apiFailure.Kind,
                            $"剧集 ID 请求失败；来源 URL 请求也失败：{apiFailure.Message}",
                            apiFailure,
                            apiFailure.StatusCode);
                    }

                    throw new ArgumentException(
                        $"剧集 ID 请求失败；来源 URL 请求参数无效：{fallbackError.Message}",
                        fallbackError);
                }
            }

            if (sequence != _requestSequence || token.IsCancellationRequested)
            {
                return;
            }

            AllComments = result.Comments;
            VideoDuration = result.VideoDuration;
            _loadCompleted = true;
            OnPropertyChanged(nameof(HasLoadedComments));
            OnPropertyChanged(nameof(CanExport));
            RequestSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            Statistics = CalculateStatistics(AllComments, VideoDuration, RequestSeconds);
            HeatmapBuckets = CalculateHeatmap(AllComments, Statistics.DurationSeconds);
            HighMoments = CalculateHighMoments(HeatmapBuckets);
            SelectedHeatmapIndex = HeatmapBuckets.Count == 0 ? -1 : 0;
            ApplyFilter();
            Diagnostic = AllComments.Count == 0
                ? "该剧集暂无弹幕数据。"
                : _usedSourceUrlFallback
                    ? "剧集 ID 请求失败，已通过来源 URL 获取弹幕。"
                    : null;
        }
        catch (Exception error) when (error is DanmuApiException or ArgumentException)
        {
            if (sequence == _requestSequence)
            {
                _loadFailed = true;
                Diagnostic = error.Message;
                OnPropertyChanged(nameof(CanRetry));
                await _owner.ShowFailureAsync("获取弹幕失败", error).ConfigureAwait(true);
            }
        }
        finally
        {
            _isBusy = false;
            LoadingText = string.Empty;
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(LoadingText));
            OnPropertyChanged(nameof(CanRetry));
        }
    }

    [RelayCommand]
    private void SelectHeatmapBucket(DanmuHeatmapBucket bucket)
    {
        ArgumentNullException.ThrowIfNull(bucket);
        if (bucket.Index >= 0 && bucket.Index < HeatmapBuckets.Count)
        {
            SelectedHeatmapIndex = bucket.Index;
        }
    }

    [RelayCommand]
    private void ApplyFilter()
    {
        var terms = SearchQuery.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        FilteredComments = AllComments.Where(comment => MatchesFilter(comment) && (terms.Length == 0 || terms.All(term => comment.Text.Contains(term, StringComparison.OrdinalIgnoreCase)))).ToArray();
        DisplayedCount = Math.Min(PageSize, FilteredComments.Count);
        NotifyListChanged();
    }

    [RelayCommand]
    private void LoadMore()
    {
        DisplayedCount = Math.Min(DisplayedCount + PageSize, FilteredComments.Count);
        NotifyListChanged();
    }

    [RelayCommand]
    private async Task CopySourceUrlAsync()
    {
        if (!HasSourceUrl)
        {
            Diagnostic = "来源地址不是合法的 http 或 https URL。";
            return;
        }

        await _dialogs.CopyTextAsync(SourceUrl).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task OpenSourceUrlAsync()
    {
        if (!HasSourceUrl)
        {
            Diagnostic = "来源地址不是合法的 http 或 https URL。";
            return;
        }

        try
        {
            await _dialogs.OpenExternalUrlAsync(SourceUrl).ConfigureAwait(true);
        }
        catch (Exception error) when (error is ArgumentException or IOException or System.ComponentModel.Win32Exception)
        {
            Diagnostic = $"打开来源地址失败：{error.Message}";
            OnPropertyChanged(nameof(HasDiagnostic));
            _diagnostics.Record("打开弹幕来源地址失败", error);
        }
    }

    [RelayCommand]
    private Task ExportJsonAsync() => ExportCoreAsync(DanmuDownloadFormat.Json);

    [RelayCommand]
    private Task ExportXmlAsync() => ExportCoreAsync(DanmuDownloadFormat.Xml);

    [RelayCommand]
    private Task ExportAsync() => ExportCoreAsync(SelectedExportFormatOption.Value);

    private async Task ExportCoreAsync(DanmuDownloadFormat format)
    {
        if (!HasLoadedComments)
        {
            Diagnostic = "暂无可导出的弹幕数据。";
            OnPropertyChanged(nameof(HasDiagnostic));
            return;
        }

        if (EpisodeId <= 0)
        {
            Diagnostic = "当前内容由播放链接直接解析，核心未返回可用于原生格式导出的剧集 ID。";
            OnPropertyChanged(nameof(HasDiagnostic));
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var formatValue = format.Value();
        try
        {
            _context.EnsureRunning();
            var payload = await _client.DownloadCommentAsync(
                _context.Host,
                _context.Port!.Value,
                _context.Token,
                EpisodeId,
                formatValue,
                cancellationToken: _owner.BeginPageRequest()).ConfigureAwait(true);
            var returnedFormat = payload.DanmuFormat?.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(returnedFormat) && !string.Equals(returnedFormat, formatValue, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"核心实际返回格式为 {returnedFormat}，与请求的 {formatValue} 不一致");
            }

            var inspection = DanmuPayloadInspector.Inspect(payload.Body, format, payload.ContentType);
            if (!inspection.Valid)
            {
                throw new InvalidOperationException(inspection.Error);
            }

            var fileName = DanmuFileNameTemplates.SanitizeFileComponent(Title);
            if (fileName.Length == 0)
            {
                fileName = $"danmu_{EpisodeId}";
            }

            var body = payload.Body;
            if (format.PayloadKind() == DanmuPayloadKind.Json)
            {
                using var document = JsonDocument.Parse(payload.Body);
                using var formatted = new MemoryStream();
                using (var writer = new Utf8JsonWriter(formatted, new JsonWriterOptions { Indented = true }))
                {
                    document.RootElement.WriteTo(writer);
                }

                body = formatted.ToArray();
            }

            var suggestedFileName = fileName + "." + format.Extension();
            var savedPath = await _dialogs.SaveBytesFileAsync(suggestedFileName, body, payload.ContentType).ConfigureAwait(true);
            _owner.RecordExport(formatValue, started, savedPath is not null, savedPath is null ? "用户取消导出" : null);
            Diagnostic = savedPath is null ? null : $"已导出 {format.Label()}：{Path.GetFileName(savedPath)}";
            OnPropertyChanged(nameof(HasDiagnostic));
        }
        catch (Exception error) when (error is DanmuApiException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException)
        {
            Diagnostic = $"导出 {format.Label()} 失败：{error.Message}";
            OnPropertyChanged(nameof(HasDiagnostic));
            _diagnostics.Record($"导出弹幕 {format.Label()} 失败", error);
            _owner.RecordExport(formatValue, started, false, "导出保存失败");
        }
    }

    [RelayCommand]
    private void Back() => _owner.Back();

    private string BuildJsonExport()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("count", AllComments.Count);
            if (VideoDuration is double duration)
            {
                writer.WriteNumber("videoDuration", duration);
            }
            else
            {
                writer.WriteNull("videoDuration");
            }

            writer.WriteStartArray("comments");
            foreach (var comment in AllComments)
            {
                writer.WriteStartObject();
                if (comment.RawPosition is not null) writer.WriteString("p", comment.RawPosition);
                writer.WriteString("m", comment.Text);
                writer.WriteNumber("t", comment.TimeSeconds);
                if (comment.Cid is long cid) writer.WriteNumber("cid", cid);
                if (comment.Like is long like) writer.WriteNumber("like", like);
                if (comment.ColorV2 is not null) writer.WriteString("color_v2", comment.ColorV2);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private string BuildXmlExport()
    {
        var builder = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<i>\n");
        foreach (var comment in AllComments)
        {
            var p = comment.RawPosition ?? $"{comment.TimeSeconds.ToString("0.###", CultureInfo.InvariantCulture)},{comment.Mode},{comment.Color},[{comment.Source ?? "unknown"}]";
            builder.Append("  <d p=\"")
                .Append(System.Security.SecurityElement.Escape(p))
                .Append("\">")
                .Append(System.Security.SecurityElement.Escape(comment.Text))
                .AppendLine("</d>");
        }
        return builder.Append("</i>").ToString();
    }

    private static DanmuStatistics CalculateStatistics(IReadOnlyList<DanmuComment> comments, double? duration, double requestSeconds)
    {
        var effectiveDuration = duration is > 0 ? duration.Value : comments.Count == 0 ? 0 : comments.Max(item => item.TimeSeconds);
        var bucketCount = Math.Max(1, (int)Math.Ceiling(effectiveDuration / 30));
        var counts = new int[bucketCount];
        foreach (var comment in comments)
        {
            if (comment.TimeSeconds < 0 || effectiveDuration <= 0) continue;
            counts[Math.Min((int)(comment.TimeSeconds / 30), counts.Length - 1)]++;
        }
        var hotIndex = counts.Length == 0 ? 0 : Array.IndexOf(counts, counts.Max());
        var colored = comments.Count(item => item.Color != 16_777_215);
        return new(
            comments.Count,
            effectiveDuration,
            effectiveDuration > 0 ? comments.Count / (effectiveDuration / 60) : 0,
            hotIndex * 30,
            Math.Min((hotIndex + 1) * 30, effectiveDuration),
            counts.Length == 0 ? 0 : counts[hotIndex],
            comments.Count == 0 ? 0 : (double)colored / comments.Count,
            requestSeconds);
    }

    private static IReadOnlyList<DanmuHeatmapBucket> CalculateHeatmap(IReadOnlyList<DanmuComment> comments, double duration)
    {
        if (duration <= 0) return [];
        var count = Math.Clamp((int)Math.Ceiling(duration / 30), 20, 60);
        var bucketLength = duration / count;
        var counts = new int[count];
        foreach (var comment in comments)
        {
            if (comment.TimeSeconds < 0 || comment.TimeSeconds > duration) continue;
            counts[Math.Min((int)(comment.TimeSeconds / bucketLength), count - 1)]++;
        }
        var max = Math.Max(1, counts.Max());
        return Enumerable.Range(0, count)
            .Select(index => new DanmuHeatmapBucket(index, index * bucketLength, Math.Min(duration, (index + 1) * bucketLength), counts[index], (double)counts[index] / max))
            .ToArray();
    }

    private static IReadOnlyList<DanmuHeatmapBucket> CalculateHighMoments(IReadOnlyList<DanmuHeatmapBucket> buckets)
    {
        var selected = new List<DanmuHeatmapBucket>(5);
        foreach (var bucket in buckets.OrderByDescending(item => item.Count).ThenBy(item => item.Index))
        {
            if (bucket.Count <= 0)
            {
                continue;
            }

            if (selected.All(item => Math.Abs(item.Index - bucket.Index) > 1))
            {
                selected.Add(bucket);
                if (selected.Count == 5)
                {
                    break;
                }
            }
        }

        return selected.OrderBy(item => item.StartSeconds).ToArray();
    }

    private static bool ShouldTrySourceFallback(Exception error) => error switch
    {
        DanmuApiException { Kind: DanmuApiFailureKind.NotFound or DanmuApiFailureKind.Http or DanmuApiFailureKind.Protocol } => true,
        _ => false,
    };

    private bool MatchesFilter(DanmuComment comment) => CurrentFilter switch
    {
        DanmuFilter.Top => comment.Mode == 5,
        DanmuFilter.Bottom => comment.Mode == 4,
        DanmuFilter.Scroll => comment.Mode is not (4 or 5),
        _ => true,
    };

    private void NotifyListChanged()
    {
        OnPropertyChanged(nameof(DisplayedComments));
        OnPropertyChanged(nameof(HasDisplayedComments));
        OnPropertyChanged(nameof(ShowEmptyFilterState));
        OnPropertyChanged(nameof(CanLoadMore));
        OnPropertyChanged(nameof(CountText));
    }
}

public sealed partial class FavoritesPageViewModel : ViewModelBase
{
    private readonly DanmuTestPageViewModel _owner;
    private readonly RuntimeApiContext _context;
    private readonly IDanmuApiClient _client;
    private readonly IUiDialogService _dialogs;
    private readonly IAppDiagnostics _diagnostics;
    private readonly PosterImageService? _posters;
    private readonly Dictionary<string, FavoriteItemViewModel> _wrappers = new(StringComparer.Ordinal);
    private bool _isBusy;
    private string? _busyKeyword;

    [ObservableProperty]
    private IReadOnlyList<DanmuFavoriteItem> _favorites = [];

    [ObservableProperty]
    private DanmuFavoriteCapabilities _capabilities = new(false, false, null);

    [ObservableProperty]
    private string? _diagnostic;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<FavoriteItemViewModel> _visibleFavorites = [];

    public FavoritesPageViewModel(DanmuTestPageViewModel owner, RuntimeApiContext context, IDanmuApiClient client, IUiDialogService dialogs, IAppDiagnostics diagnostics, PosterImageService? posters = null)
    {
        _owner = owner;
        _context = context;
        _client = client;
        _dialogs = dialogs;
        _diagnostics = diagnostics;
        _posters = posters;
        _context.RuntimeStateChanged += (_, _) => RunOnUi(() =>
        {
            OnPropertyChanged(nameof(CanWrite));
            OnPropertyChanged(nameof(CanSchedule));
        });
    }

    public bool IsBusy => _isBusy;
    public bool HasSupportMessage => !string.IsNullOrWhiteSpace(Capabilities.SupportMessage);
    public bool HasDiagnostic => !string.IsNullOrWhiteSpace(Diagnostic);
    public bool CanWrite => Capabilities.FavoriteSupported && _context.Snapshot.State == DesktopRuntimeState.Running;
    public bool CanSchedule => Capabilities.ScheduledRefreshSupported && CanWrite;
    public bool HasFavorites => VisibleFavorites.Count > 0;
    public bool IsItemBusy(DanmuFavoriteItem item) => _busyKeyword == item.Keyword;

    private void RunOnUi(Action action)
    {
        var context = SynchronizationContext.Current;
        if (context is null)
        {
            action();
            return;
        }

        context.Post(_ => action(), null);
    }

    partial void OnSearchTextChanged(string value) => RebuildVisibleFavorites();

    partial void OnFavoritesChanged(IReadOnlyList<DanmuFavoriteItem> value) => RebuildVisibleFavorites();

    partial void OnVisibleFavoritesChanged(IReadOnlyList<FavoriteItemViewModel> value) =>
        OnPropertyChanged(nameof(HasFavorites));

    private void RebuildVisibleFavorites()
    {
        var query = SearchText.Trim();
        var next = new List<FavoriteItemViewModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Favorites)
        {
            seen.Add(item.Keyword);
            if (!_wrappers.TryGetValue(item.Keyword, out var wrapper) || !ReferenceEquals(wrapper.Item, item))
            {
                wrapper = new FavoriteItemViewModel(item, _posters);
                _wrappers[item.Keyword] = wrapper;
            }

            if (query.Length > 0 &&
                !item.Keyword.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !item.AnimeTitle.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            next.Add(wrapper);
        }

        foreach (var stale in _wrappers.Keys.Where(key => !seen.Contains(key)).ToArray())
        {
            _wrappers.Remove(stale);
        }

        VisibleFavorites = next;
    }

    partial void OnCapabilitiesChanged(DanmuFavoriteCapabilities value)
    {
        OnPropertyChanged(nameof(HasSupportMessage));
        OnPropertyChanged(nameof(CanWrite));
        OnPropertyChanged(nameof(CanSchedule));
    }

    partial void OnDiagnosticChanged(string? value) => OnPropertyChanged(nameof(HasDiagnostic));

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            _context.EnsureRunning();
            _isBusy = true;
            OnPropertyChanged(nameof(IsBusy));
            var token = _owner.BeginPageRequest();
            using var scene = DanmuScenes.Begin("弹幕测试/收藏列表");
            var result = await _client.GetFavoritesAsync(_context.Host, _context.Port!.Value, _context.Token, token).ConfigureAwait(true);
            Favorites = result.Favorites;
            Capabilities = result.Capabilities;
            Diagnostic = result.ErrorMessage;
        }
        catch (Exception error) when (error is DanmuApiException or ArgumentException)
        {
            Diagnostic = error.Message;
            await _owner.ShowFailureAsync("加载收藏失败", error).ConfigureAwait(true);
        }
        finally
        {
            _isBusy = false;
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    [RelayCommand]
    private async Task RefreshAsync(FavoriteItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!CanWrite || !await _dialogs.ConfirmAsync("刷新收藏", $"立即重新搜索“{item.Title}”吗？", "刷新").ConfigureAwait(true))
        {
            return;
        }

        await MutateAsync(item.Item, "刷新收藏", "弹幕测试/收藏/refresh", (admin, token) => _client.RefreshFavoriteAsync(_context.Host, _context.Port!.Value, _context.Token, admin, item.Keyword, token)).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RemoveAsync(FavoriteItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!CanWrite || !await _dialogs.ConfirmAsync("删除收藏", $"确定删除“{item.Title}”吗？", "删除").ConfigureAwait(true))
        {
            return;
        }

        await MutateAsync(item.Item, "删除收藏", "弹幕测试/收藏/remove", (admin, token) => _client.RemoveFavoriteAsync(_context.Host, _context.Port!.Value, _context.Token, admin, item.Keyword, token)).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ScheduleAsync(FavoriteItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!CanSchedule)
        {
            Diagnostic = Capabilities.SupportMessage ?? "当前部署不支持定时刷新。";
            return;
        }

        var schedule = await _dialogs.PromptFavoriteScheduleAsync(item.Item.RefreshSchedule).ConfigureAwait(true);
        if (schedule is null)
        {
            return;
        }

        await MutateAsync(item.Item, "设置定时刷新", "弹幕测试/收藏/schedule", (admin, token) => _client.SetFavoriteScheduleAsync(_context.Host, _context.Port!.Value, _context.Token, admin, item.Keyword, schedule, token)).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DisableScheduleAsync(FavoriteItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!CanSchedule || item.Item.RefreshSchedule is null)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync("关闭定时刷新", $"确定关闭“{item.Title}”的定时刷新吗？", "关闭").ConfigureAwait(true))
        {
            return;
        }

        await MutateAsync(item.Item, "关闭定时刷新", "弹幕测试/收藏/schedule", (admin, token) => _client.SetFavoriteScheduleAsync(_context.Host, _context.Port!.Value, _context.Token, admin, item.Keyword, null, token)).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (!CanWrite)
        {
            Diagnostic = Capabilities.SupportMessage ?? "当前部署不支持收藏写操作，或服务未运行。";
            return;
        }

        var keyword = await _dialogs.PromptTextAsync("添加收藏", "输入动漫名称，核心会保存其搜索结果。", string.Empty, "添加").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return;
        }

        try
        {
            _context.EnsureRunning();
            var admin = _context.AdminToken();
            if (admin is null)
            {
                Diagnostic = "请先在 设置 > 安全 开启管理员模式。";
                return;
            }

            using var scene = DanmuScenes.Begin("弹幕测试/收藏/add");
            Diagnostic = await _client.AddFavoriteAsync(_context.Host, _context.Port!.Value, _context.Token, admin, keyword.Trim(), _owner.BeginPageRequest()).ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception error) when (error is DanmuApiException or ArgumentException)
        {
            Diagnostic = error.Message;
            await _owner.ShowFailureAsync("添加收藏失败", error).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task OpenAsync(FavoriteItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var manual = new ManualSearchPageViewModel(_owner, _context, _client, _dialogs, _diagnostics)
        {
            Keyword = item.Keyword,
        };
        _owner.Navigate(manual);
        await manual.SearchCommand.ExecuteAsync(null).ConfigureAwait(true);
    }

    private async Task MutateAsync(DanmuFavoriteItem item, string title, string scene, Func<string?, CancellationToken, Task<string>> mutation)
    {
        if (!CanWrite)
        {
            Diagnostic = Capabilities.SupportMessage ?? "当前部署不支持收藏写操作，或服务未运行。";
            return;
        }

        try
        {
            _context.EnsureRunning();
            var admin = _context.AdminToken();
            if (admin is null)
            {
                Diagnostic = "请先在 设置 > 安全 开启管理员模式。";
                return;
            }

            _busyKeyword = item.Keyword;
            OnPropertyChanged(nameof(IsItemBusy));
            string message;
            using (DanmuScenes.Begin(scene))
            {
                message = await mutation(admin, _owner.BeginPageRequest()).ConfigureAwait(true);
            }

            Diagnostic = message;
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception error) when (error is DanmuApiException or ArgumentException)
        {
            Diagnostic = error.Message;
            await _owner.ShowFailureAsync(title + "失败", error).ConfigureAwait(true);
        }
        finally
        {
            _busyKeyword = null;
            OnPropertyChanged(nameof(IsItemBusy));
        }
    }
}
