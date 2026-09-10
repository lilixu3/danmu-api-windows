using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DanmuApi.App.Services;
using DanmuApi.Runtime;

namespace DanmuApi.App.ViewModels;

public enum DanmuDownloadSection
{
    Search,
    Queue,
    Records,
    Settings,
}

public enum DownloadSearchStage
{
    AnimeList,
    EpisodeList,
}

public enum DownloadRecordStage
{
    AnimeList,
    EpisodeList,
}

public enum DownloadRecordFilter
{
    All,
    Success,
    Failed,
    Skipped,
}

public sealed record DownloadRecordFilterOption(DownloadRecordFilter Value, string Title)
{
    public override string ToString() => Title;
}

public sealed record DanmuDownloadSectionOption(DanmuDownloadSection Value, string Title, string Description)
{
    public override string ToString() => Title;
}

public enum EpisodeDownloadState
{
    Idle,
    Queued,
    Running,
    Success,
    Failed,
    Skipped,
    Canceled,
}

public static class EpisodeDownloadStateExtensions
{
    public static string Label(this EpisodeDownloadState state) => state switch
    {
        EpisodeDownloadState.Idle => "未下载",
        EpisodeDownloadState.Queued => "排队中",
        EpisodeDownloadState.Running => "下载中",
        EpisodeDownloadState.Success => "已下载",
        EpisodeDownloadState.Failed => "失败",
        EpisodeDownloadState.Skipped => "已跳过",
        EpisodeDownloadState.Canceled => "已取消",
        _ => "未下载",
    };
}

public sealed partial class DanmuDownloadPageViewModel : ViewModelBase
{
    private const string RateLimitEnvKey = "RATE_LIMIT_MAX_REQUESTS";

    private readonly RuntimeApiContext _context;
    private readonly IDanmuApiClient _client;
    private readonly ICoreEnvClient _envClient;
    private readonly DanmuDownloadStore _store;
    private readonly DanmuDownloadFileService _service;
    private readonly IUiDialogService _dialogs;
    private readonly IAppDiagnostics _diagnostics;
    private readonly PosterImageService _posters;
    private readonly SynchronizationContext? _uiContext;
    private CancellationTokenSource? _queueCts;
    private volatile bool _cancelRequested;
    private bool _requireQueuePreparationBeforeRun;
    private bool _isDownloadingFlag;
    private sealed record RateLimitBypassSession(string? OriginalValue, bool Applied);

    [ObservableProperty]
    private DanmuDownloadSectionOption _selectedSectionOption = null!;

    [ObservableProperty]
    private object _currentSection = null!;

    [ObservableProperty]
    private string _keyword = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _hasSearched;

    [ObservableProperty]
    private bool _isLoadingEpisodes;

    [ObservableProperty]
    private string? _operationMessage;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _throttleHint;

    [ObservableProperty]
    private AnimeRowViewModel? _currentAnime;

    [ObservableProperty]
    private DownloadSearchStage _currentStage = DownloadSearchStage.AnimeList;

    [ObservableProperty]
    private DownloadRecordStage _currentRecordStage = DownloadRecordStage.AnimeList;

    [ObservableProperty]
    private RecordAnimeGroupViewModel? _selectedRecordGroup;

    [ObservableProperty]
    private DownloadRecordFilterOption _selectedRecordFilter = null!;

    [ObservableProperty]
    private string? _selectedSourceFilter;

    [ObservableProperty]
    private double _overallProgress;

    [ObservableProperty]
    private string _progressSummary = "等待开始";

    [ObservableProperty]
    private string _activeTaskText = "当前无运行中的任务";

    [ObservableProperty]
    private double _activeTaskProgress;

    [ObservableProperty]
    private bool _isPreviewOpen;

    [ObservableProperty]
    private DanmuFilePreview? _preview;

    [ObservableProperty]
    private string? _previewError;

    public DanmuDownloadPageViewModel(
        RuntimeApiContext context,
        IDanmuApiClient client,
        ICoreEnvClient envClient,
        DanmuDownloadStore store,
        DanmuDownloadFileService service,
        IUiDialogService dialogs,
        IAppDiagnostics diagnostics,
        PosterImageService? posters = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _envClient = envClient ?? throw new ArgumentNullException(nameof(envClient));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _posters = posters ?? new PosterImageService();
        _uiContext = SynchronizationContext.Current;
        _store.PersistFailed += OnStorePersistFailed;
        _context.RuntimeStateChanged += (_, _) => RunOnUi(() =>
        {
            OnPropertyChanged(nameof(IsServiceRunning));
            OnPropertyChanged(nameof(CanSearch));
            OnPropertyChanged(nameof(CanStartDownload));
        });
        SectionOptions =
        [
            new(DanmuDownloadSection.Search, "搜索下载", "搜索动漫、勾选剧集并开始批量下载"),
            new(DanmuDownloadSection.Queue, "下载队列", "查看队列进度，暂停、重试和排序"),
            new(DanmuDownloadSection.Records, "记录与库", "浏览下载记录、预览内容、扫描目录"),
            new(DanmuDownloadSection.Settings, "下载设置", "保存目录、格式、命名模板和流控"),
        ];
        _selectedSectionOption = SectionOptions[0];
        FormatOptions = Enum.GetValues<DanmuDownloadFormat>().ToArray();
        ConflictOptions = Enum.GetValues<DownloadConflictPolicy>().ToArray();
        ThrottleOptions = Enum.GetValues<DownloadThrottlePresetKind>().ToArray();
        PreviewFilterOptions =
        [
            new(DanmuPreviewFilterKind.All, "全部"),
            new(DanmuPreviewFilterKind.Scroll, "滚动"),
            new(DanmuPreviewFilterKind.Top, "顶部"),
            new(DanmuPreviewFilterKind.Bottom, "底部"),
        ];
        RecordFilterOptions =
        [
            new(DownloadRecordFilter.All, "全部"),
            new(DownloadRecordFilter.Success, "成功"),
            new(DownloadRecordFilter.Failed, "失败"),
            new(DownloadRecordFilter.Skipped, "跳过"),
        ];
        _selectedRecordFilter = RecordFilterOptions[0];
        SyncSettingsFromStore();
        var recovered = _store.MarkRunningTasksAsPending("应用启动时恢复");
        if (recovered > 0)
        {
            _requireQueuePreparationBeforeRun = true;
            OperationMessage = $"检测到上次未完成任务，已恢复到队列（{recovered}）";
        }

        RefreshQueue();
        StartupSyncTask = InitializeStartupSyncAsync();
    }

    /// <summary>构造期目录扫描任务；完成时记录分组已在 UI 上下文重建完毕。</summary>
    public Task StartupSyncTask { get; }

    private async Task InitializeStartupSyncAsync()
    {
        if (!string.IsNullOrWhiteSpace(_store.Settings.SaveDirectory))
        {
            try
            {
                await _service.SyncExistingFilesAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                _diagnostics.Record("弹幕下载目录静默扫描失败", error);
            }
        }

        await RefreshRecordsAsync().ConfigureAwait(false);
    }

    public IReadOnlyList<DanmuDownloadSectionOption> SectionOptions { get; }
    public IReadOnlyList<DanmuDownloadFormat> FormatOptions { get; }
    public IReadOnlyList<DownloadConflictPolicy> ConflictOptions { get; }
    public IReadOnlyList<DownloadThrottlePresetKind> ThrottleOptions { get; }
    public IReadOnlyList<DanmuPreviewFilterOption> PreviewFilterOptions { get; }
    public IReadOnlyList<DownloadRecordFilterOption> RecordFilterOptions { get; }

    public ObservableCollection<AnimeRowViewModel> AnimeRows { get; } = [];
    public ObservableCollection<string> SourceOptions { get; } = [];
    public ObservableCollection<EpisodeRowViewModel> EpisodeRows { get; } = [];
    public ObservableCollection<QueueGroupViewModel> QueueGroups { get; } = [];
    public ObservableCollection<RecordAnimeGroupViewModel> RecordGroups { get; } = [];
    public ObservableCollection<PreviewItemRowViewModel> PreviewRows { get; } = [];

    public bool IsServiceRunning => _context.Snapshot.State == DesktopRuntimeState.Running && _context.Snapshot.Port is not null;
    public bool IsDownloading => _isDownloadingFlag;
    public bool HasAnimeRows => AnimeRows.Count > 0;
    public bool ShowEmptySearch => HasSearched && !IsSearching && !HasAnimeRows;
    public bool HasEpisodeRows => VisibleRows().Any();
    public bool ShowEmptyEpisodes => CurrentStage == DownloadSearchStage.EpisodeList && !IsLoadingEpisodes && !HasEpisodeRows;
    public bool HasQueueGroups => QueueGroups.Count > 0;
    public bool HasRecordGroups => RecordGroups.Count > 0;
    public bool HasVisibleRecordGroups => VisibleRecordGroups.Count > 0;
    public bool HasSelectedRecordGroup => SelectedRecordGroup is not null;
    public bool HasSelection => EpisodeRows.Any(row => row.IsSelected);
    public bool HasFailedVisible => VisibleRows().Any(row => row.State.State == EpisodeDownloadState.Failed);
    public bool HasOperationMessage => !string.IsNullOrWhiteSpace(OperationMessage);
    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasPreviewError => !string.IsNullOrWhiteSpace(PreviewError);
    public bool HasThrottleHint => !string.IsNullOrWhiteSpace(ThrottleHint);
    public bool CanSearch => IsServiceRunning && !IsSearching && !IsLoadingEpisodes;
    public bool CanStartDownload => IsServiceRunning && !IsDownloading && HasSelection;
    public bool CanMutateRecords => !IsDownloading && !IsSyncing;
    public string SaveDirectoryDisplay => string.IsNullOrWhiteSpace(Settings.SaveDirectory) ? "尚未选择保存目录" : Settings.SaveDirectory;
    public string CompactConfigText => $"{SaveDirectoryDisplay} · {SelectedFormat.Label()} · {SelectedConflict.Label()}";
    public string QueueSummaryText => BuildQueueSummaryText();
    public string RecordsSummaryText => BuildRecordsSummaryText();
    public string EpisodeSummaryText => BuildEpisodeSummaryText();
    public IReadOnlyList<RecordAnimeGroupViewModel> VisibleRecordGroups => FilterRecordGroups(RecordGroups, SelectedRecordFilter.Value);
    public int QueuePendingCount => CountQueue(DownloadQueueStatus.Pending);
    public int QueueRunningCount => CountQueue(DownloadQueueStatus.Running);
    public int QueueSuccessCount => CountQueue(DownloadQueueStatus.Success);
    public int QueueFailedCount => CountQueue(DownloadQueueStatus.Failed);
    public int QueueSkippedCount => CountQueue(DownloadQueueStatus.Skipped);
    public string PreviewSummaryText => Preview is null ? string.Empty : $"共 {Preview.Count} 条，显示前 {Preview.Items.Count} 条";
    public string OverallProgressPercent => $"{(int)Math.Round(OverallProgress * 100)}%";
    public string TemplatePreview => DanmuFileNameTemplates.Render(
        TemplateText,
        SelectedFormat,
        "凡人修仙传",
        "再入星海",
        3,
        2334455,
        "bilibili1");

    // ── 设置（即改即存，无保存按钮；N-032 语义） ──────────────────

    [ObservableProperty]
    private DanmuDownloadSettings _settings = new();

    [ObservableProperty]
    private DanmuDownloadFormat _selectedFormat = DanmuDownloadFormat.Xml;

    [ObservableProperty]
    private DownloadConflictPolicy _selectedConflict = DownloadConflictPolicy.Rename;

    [ObservableProperty]
    private DownloadThrottlePresetKind _selectedThrottle = DownloadThrottlePresetKind.Conservative;

    [ObservableProperty]
    private string _templateText = DanmuDownloadDefaults.FileNameTemplate;

    [ObservableProperty]
    private string _customBaseDelayMs = "1400";

    [ObservableProperty]
    private string _customJitterMaxMs = "600";

    [ObservableProperty]
    private string _customBatchSize = "10";

    [ObservableProperty]
    private string _customBatchRestMs = "20000";

    [ObservableProperty]
    private string _customBackoffBaseMs = "10000";

    [ObservableProperty]
    private string _customBackoffMaxMs = "240000";

    [ObservableProperty]
    private DanmuPreviewFilterOption _selectedPreviewFilter = null!;

    partial void OnSelectedSectionOptionChanged(DanmuDownloadSectionOption value) => CurrentSection = value.Value switch
    {
        DanmuDownloadSection.Queue => QueueSection.Instance,
        DanmuDownloadSection.Records => RecordsSection.Instance,
        DanmuDownloadSection.Settings => SettingsSection.Instance,
        _ => SearchSection.Instance,
    };

    partial void OnIsSearchingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSearch));
        OnPropertyChanged(nameof(ShowEmptySearch));
    }

    partial void OnIsLoadingEpisodesChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSearch));
        OnPropertyChanged(nameof(ShowEmptyEpisodes));
    }

    partial void OnHasSearchedChanged(bool value) => OnPropertyChanged(nameof(ShowEmptySearch));

    partial void OnSelectedFormatChanged(DanmuDownloadFormat value)
    {
        UpdateSettings(settings => settings with { DefaultFormat = value.Value() });
        OnPropertyChanged(nameof(CompactConfigText));
    }

    partial void OnSelectedConflictChanged(DownloadConflictPolicy value)
    {
        UpdateSettings(settings => settings with { ConflictPolicy = value.Key() });
        OnPropertyChanged(nameof(CompactConfigText));
    }

    partial void OnSelectedThrottleChanged(DownloadThrottlePresetKind value) => UpdateSettings(settings => settings with { ThrottlePreset = DownloadThrottlePreset.Key(value) });
    partial void OnTemplateTextChanged(string value) => UpdateSettings(settings => settings with { FileNameTemplate = value }, notifyPreview: true);
    partial void OnPreviewChanged(DanmuFilePreview? value) => OnPropertyChanged(nameof(PreviewSummaryText));
    partial void OnSelectedRecordFilterChanged(DownloadRecordFilterOption value)
    {
        OnPropertyChanged(nameof(VisibleRecordGroups));
        OnPropertyChanged(nameof(HasVisibleRecordGroups));
    }

    partial void OnSelectedRecordGroupChanged(RecordAnimeGroupViewModel? value) =>
        OnPropertyChanged(nameof(HasSelectedRecordGroup));

    [RelayCommand]
    private void UsePreset(string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        TemplateText = template;
    }

    [RelayCommand]
    private void UseDefaultTemplate() => TemplateText = DanmuDownloadDefaults.FileNameTemplate;

    [RelayCommand]
    private void ApplyCustomThrottle()
    {
        var config = DownloadThrottleConfig.SanitizeCustom(
            ParseLong(CustomBaseDelayMs, 1400),
            ParseLong(CustomJitterMaxMs, 600),
            (int)ParseLong(CustomBatchSize, 10),
            ParseLong(CustomBatchRestMs, 20_000),
            ParseLong(CustomBackoffBaseMs, 10_000),
            ParseLong(CustomBackoffMaxMs, 240_000));
        CustomBaseDelayMs = config.BaseDelayMs.ToString(CultureInfo.InvariantCulture);
        CustomJitterMaxMs = config.JitterMaxMs.ToString(CultureInfo.InvariantCulture);
        CustomBatchSize = config.BatchSize.ToString(CultureInfo.InvariantCulture);
        CustomBatchRestMs = config.BatchRestMs.ToString(CultureInfo.InvariantCulture);
        CustomBackoffBaseMs = config.BackoffBaseMs.ToString(CultureInfo.InvariantCulture);
        CustomBackoffMaxMs = config.BackoffMaxMs.ToString(CultureInfo.InvariantCulture);
        UpdateSettings(settings => settings with
        {
            ThrottlePreset = "custom",
            CustomBaseDelayMs = config.BaseDelayMs,
            CustomJitterMaxMs = config.JitterMaxMs,
            CustomBatchSize = config.BatchSize,
            CustomBatchRestMs = config.BatchRestMs,
            CustomBackoffBaseMs = config.BackoffBaseMs,
            CustomBackoffMaxMs = config.BackoffMaxMs,
        });
        SelectedThrottle = DownloadThrottlePresetKind.Custom;
        OperationMessage = "自定义流控已保存。";
        OnPropertyChanged(nameof(HasOperationMessage));
    }

    [RelayCommand]
    private async Task PickDirectoryAsync()
    {
        try
        {
            var directory = await _dialogs.PickFolderAsync(Settings.SaveDirectory).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            UpdateSettings(settings => settings with { SaveDirectory = directory, SaveDirDisplayName = directory });
            OnPropertyChanged(nameof(SaveDirectoryDisplay));
            OnPropertyChanged(nameof(CompactConfigText));
            OperationMessage = "保存目录已更新。";
            OnPropertyChanged(nameof(HasOperationMessage));
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or ArgumentException)
        {
            ErrorMessage = $"选择保存目录失败：{error.Message}";
            OnPropertyChanged(nameof(HasErrorMessage));
            _diagnostics.Record("选择下载保存目录失败", error);
        }
    }

    [RelayCommand]
    private async Task OpenSaveDirectoryAsync()
    {
        if (string.IsNullOrWhiteSpace(Settings.SaveDirectory))
        {
            ErrorMessage = "尚未选择保存目录。";
            OnPropertyChanged(nameof(HasErrorMessage));
            return;
        }

        try
        {
            await _dialogs.OpenDirectoryAsync(Settings.SaveDirectory).ConfigureAwait(true);
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or ArgumentException)
        {
            ErrorMessage = $"打开保存目录失败：{error.Message}";
            OnPropertyChanged(nameof(HasErrorMessage));
            _diagnostics.Record("打开下载保存目录失败", error);
        }
    }

    private void UpdateSettings(Func<DanmuDownloadSettings, DanmuDownloadSettings> mutate, bool notifyPreview = false)
    {
        var next = mutate(_store.Settings);
        _store.SaveSettings(next);
        Settings = next;
        if (notifyPreview)
        {
            OnPropertyChanged(nameof(TemplatePreview));
        }
    }

    private void SyncSettingsFromStore()
    {
        Settings = _store.Settings;
        SelectedFormat = Settings.Format();
        SelectedConflict = Settings.Policy();
        SelectedThrottle = Settings.Throttle();
        SelectedPreviewFilter = PreviewFilterOptions[0];
        TemplateText = string.IsNullOrWhiteSpace(Settings.FileNameTemplate) ? DanmuDownloadDefaults.FileNameTemplate : Settings.FileNameTemplate;
        CustomBaseDelayMs = Settings.CustomBaseDelayMs.ToString(CultureInfo.InvariantCulture);
        CustomJitterMaxMs = Settings.CustomJitterMaxMs.ToString(CultureInfo.InvariantCulture);
        CustomBatchSize = Settings.CustomBatchSize.ToString(CultureInfo.InvariantCulture);
        CustomBatchRestMs = Settings.CustomBatchRestMs.ToString(CultureInfo.InvariantCulture);
        CustomBackoffBaseMs = Settings.CustomBackoffBaseMs.ToString(CultureInfo.InvariantCulture);
        CustomBackoffMaxMs = Settings.CustomBackoffMaxMs.ToString(CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(SaveDirectoryDisplay));
        OnPropertyChanged(nameof(CompactConfigText));
        OnPropertyChanged(nameof(TemplatePreview));
    }

    // ── 搜索与剧集 ──────────────────────────────────────────

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (IsSearching || IsLoadingEpisodes)
        {
            return;
        }

        if (!IsServiceRunning)
        {
            ErrorMessage = "服务未运行，无法搜索。";
            OnPropertyChanged(nameof(HasErrorMessage));
            return;
        }

        var query = Keyword.Trim();
        if (query.Length == 0)
        {
            ErrorMessage = "请输入搜索关键词。";
            OnPropertyChanged(nameof(HasErrorMessage));
            return;
        }

        try
        {
            IsSearching = true;
            ErrorMessage = null;
            OnPropertyChanged(nameof(HasErrorMessage));
            var token = _context.Token;
            IReadOnlyList<DanmuAnime> animes;
            using (DanmuScenes.Begin("弹幕下载/搜索动漫"))
            {
                var result = await _client.SearchAnimeAsync(_context.Host, _context.Port!.Value, token, query).ConfigureAwait(true);
                if (!result.Success)
                {
                    throw new DanmuApiException(DanmuApiFailureKind.Protocol, result.ErrorMessage ?? "核心搜索失败");
                }

                animes = result.Animes;
            }

            HasSearched = true;
            AnimeRows.Clear();
            var records = _store.Records;
            foreach (var anime in animes)
            {
                AnimeRows.Add(new AnimeRowViewModel(anime, _posters, BuildHistoryText(anime, records)));
            }

            OperationMessage = animes.Count == 0 ? "未搜索到匹配动漫" : $"已搜索到 {animes.Count} 个动漫";
            OnPropertyChanged(nameof(HasOperationMessage));
            OnPropertyChanged(nameof(HasAnimeRows));
            OnPropertyChanged(nameof(ShowEmptySearch));
            ClearSelection();
        }
        catch (Exception error) when (error is DanmuApiException or ArgumentException)
        {
            ErrorMessage = error.Message;
            OnPropertyChanged(nameof(HasErrorMessage));
            _diagnostics.Record("弹幕下载搜索失败", error);
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private async Task OpenAnimeAsync(AnimeRowViewModel anime)
    {
        ArgumentNullException.ThrowIfNull(anime);
        if (IsSearching || IsLoadingEpisodes)
        {
            return;
        }

        if (!IsServiceRunning)
        {
            ErrorMessage = "服务未运行，无法加载剧集。";
            OnPropertyChanged(nameof(HasErrorMessage));
            return;
        }

        try
        {
            IsLoadingEpisodes = true;
            CurrentAnime = anime;
            CurrentStage = DownloadSearchStage.EpisodeList;
            ClearSelection();
            EpisodeRows.Clear();
            SourceOptions.Clear();
            NotifyEpisodeSummaryChanged();
            OnPropertyChanged(nameof(HasEpisodeRows));
            OnPropertyChanged(nameof(ShowEmptyEpisodes));
            ErrorMessage = null;
            OnPropertyChanged(nameof(HasErrorMessage));
            IReadOnlyList<DanmuEpisodeCandidate> episodes;
            using (DanmuScenes.Begin("弹幕下载/加载剧集"))
            {
                var result = await _client.GetBangumiAsync(_context.Host, _context.Port!.Value, _context.Token, anime.AnimeId).ConfigureAwait(true);
                if (!result.Success || result.Bangumi is null)
                {
                    throw new DanmuApiException(DanmuApiFailureKind.Protocol, result.ErrorMessage ?? "番剧详情不可用");
                }

                var fallbackSource = DanmuDownloadParsing.ExtractSourceFromAnimeTitle(anime.Title);
                episodes = DanmuDownloadParsing.DeduplicateEpisodes(result.Bangumi.Episodes
                    .Select((episode, index) => DanmuDownloadParsing.ToEpisodeCandidate(episode, fallbackSource, index + 1))
                    .ToArray());
            }

            var states = BuildInitialEpisodeStates(anime, episodes);
            var sources = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var episode in episodes)
            {
                var state = states.TryGetValue(episode.EpisodeId, out var value) ? value : new EpisodeUiState();
                EpisodeRows.Add(new EpisodeRowViewModel(
                    episode.EpisodeId, episode.EpisodeNumber, episode.Title, episode.Source, episode.SourceUrl, state,
                    NotifySelectionChanged));
                sources.Add(episode.Source);
            }

            SourceOptions.Clear();
            SourceOptions.Add("全部来源");
            foreach (var source in sources)
            {
                SourceOptions.Add(source);
            }

            SelectedSourceFilter = "全部来源";
            OperationMessage = episodes.Count == 0 ? "该动漫暂无可下载剧集" : $"共加载 {episodes.Count} 集";
            OnPropertyChanged(nameof(HasOperationMessage));
            OnPropertyChanged(nameof(HasEpisodeRows));
            OnPropertyChanged(nameof(ShowEmptyEpisodes));
            NotifyEpisodeSummaryChanged();
        }
        catch (Exception error) when (error is DanmuApiException or ArgumentException)
        {
            ErrorMessage = error.Message;
            OnPropertyChanged(nameof(HasErrorMessage));
            _diagnostics.Record("弹幕下载加载剧集失败", error);
        }
        finally
        {
            IsLoadingEpisodes = false;
        }
    }

    [RelayCommand]
    private void BackToAnimeList()
    {
        CurrentStage = DownloadSearchStage.AnimeList;
        CurrentAnime = null;
        ClearSelection();
        EpisodeRows.Clear();
        SourceOptions.Clear();
        OnPropertyChanged(nameof(HasEpisodeRows));
        OnPropertyChanged(nameof(ShowEmptyEpisodes));
        NotifyEpisodeSummaryChanged();
    }

    partial void OnSelectedSourceFilterChanged(string? value) => ApplySourceFilter();

    private void ApplySourceFilter()
    {
        var filter = SelectedSourceFilter;
        foreach (var row in EpisodeRows)
        {
            row.IsVisible = filter is null || filter == "全部来源" ||
                string.Equals(row.Source, filter, StringComparison.OrdinalIgnoreCase);
        }

        if (filter is not null && filter != "全部来源")
        {
            foreach (var row in EpisodeRows.Where(row => !row.IsVisible))
            {
                row.IsSelected = false;
            }
        }

        NotifyEpisodeSummaryChanged();
        OnPropertyChanged(nameof(HasEpisodeRows));
        OnPropertyChanged(nameof(ShowEmptyEpisodes));
    }

    [RelayCommand]
    private void ToggleSelectAll()
    {
        var visible = VisibleRows().ToArray();
        if (visible.Length == 0)
        {
            return;
        }

        var allSelected = visible.All(row => row.IsSelected);
        foreach (var row in visible)
        {
            row.IsSelected = !allSelected;
        }
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var row in EpisodeRows)
        {
            row.IsSelected = false;
        }
    }

    [RelayCommand]
    private void SelectFailed()
    {
        if (IsDownloading)
        {
            return;
        }

        var failed = VisibleRows().Where(row => row.State.State is EpisodeDownloadState.Failed).ToArray();
        foreach (var row in EpisodeRows)
        {
            row.IsSelected = false;
        }

        foreach (var row in failed)
        {
            row.IsSelected = true;
        }

        OperationMessage = failed.Length == 0 ? "当前筛选下没有失败项" : $"已选择 {failed.Length} 个失败项";
        OnPropertyChanged(nameof(HasOperationMessage));
    }

    [RelayCommand]
    private void SelectUnfinished()
    {
        if (IsDownloading)
        {
            return;
        }

        var unfinished = VisibleRows().Where(row => row.State.State is not (EpisodeDownloadState.Success or EpisodeDownloadState.Skipped)).ToArray();
        foreach (var row in EpisodeRows)
        {
            row.IsSelected = false;
        }

        foreach (var row in unfinished)
        {
            row.IsSelected = true;
        }

        OperationMessage = unfinished.Length == 0 ? "当前筛选下没有未完成项" : $"已选择 {unfinished.Length} 个未完成项";
        OnPropertyChanged(nameof(HasOperationMessage));
    }

    [RelayCommand]
    private void RetryFailedVisible()
    {
        if (IsDownloading)
        {
            return;
        }

        SelectFailed();
        if (!HasSelection)
        {
            return;
        }

        StartDownload();
    }

    [RelayCommand]
    private void StartDownload()
    {
        if (IsDownloading || IsSearching || IsLoadingEpisodes)
        {
            return;
        }

        if (!IsServiceRunning)
        {
            ErrorMessage = "服务未运行，无法下载。";
            OnPropertyChanged(nameof(HasErrorMessage));
            return;
        }

        if (string.IsNullOrWhiteSpace(_store.Settings.SaveDirectory))
        {
            ErrorMessage = "请先在下载设置中选择保存目录。";
            OnPropertyChanged(nameof(HasErrorMessage));
            return;
        }

        var anime = CurrentAnime;
        if (anime is null)
        {
            ErrorMessage = "请先选择动漫。";
            OnPropertyChanged(nameof(HasErrorMessage));
            return;
        }

        var selected = VisibleRows().Where(row => row.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            ErrorMessage = "请至少选择一集。";
            OnPropertyChanged(nameof(HasErrorMessage));
            return;
        }

        var settings = _store.Settings;
        var inputs = selected.Select(row => new DanmuDownloadInput(
            string.Empty,
            anime.Title,
            row.Title,
            row.EpisodeId,
            row.EpisodeNumber,
            row.Source,
            SelectedFormat,
            TemplateText,
            SelectedConflict,
            anime.AnimeId)).ToArray();
        ErrorMessage = null;
        OnPropertyChanged(nameof(HasErrorMessage));
        var added = _store.EnqueueTasks(inputs);
        foreach (var row in selected)
        {
            row.UpdateState(new EpisodeUiState(EpisodeDownloadState.Queued, 0, "排队中"));
        }

        RefreshQueue();
        OperationMessage = added > 0
            ? $"已加入队列 {added} 集，正在进入下载状态"
            : "所选剧集已在队列中，继续执行队列";
        OnPropertyChanged(nameof(HasOperationMessage));
        SelectedSectionOption = SectionOptions.First(option => option.Value == DanmuDownloadSection.Queue);
        _requireQueuePreparationBeforeRun = true;
        _ = ProcessPendingQueueAsync();
    }

    // ── 队列执行 ──────────────────────────────────────────

    [RelayCommand]
    private void PauseDownload()
    {
        if (!IsDownloading)
        {
            OperationMessage = "当前没有正在执行的下载";
            OnPropertyChanged(nameof(HasOperationMessage));
            return;
        }

        _cancelRequested = true;
        _queueCts?.Cancel();
        ProgressSummary = "正在暂停...";
    }

    [RelayCommand]
    private void ResumeQueue()
    {
        if (IsDownloading)
        {
            return;
        }

        var pending = _store.QueueTasks.Count(task => task.StatusEnum == DownloadQueueStatus.Pending);
        if (pending <= 0)
        {
            OperationMessage = "当前没有待处理任务";
            OnPropertyChanged(nameof(HasOperationMessage));
            return;
        }

        _requireQueuePreparationBeforeRun = true;
        _ = ProcessPendingQueueAsync();
    }

    [RelayCommand]
    private void RetryFailedQueueTasks()
    {
        if (IsDownloading)
        {
            OperationMessage = "下载进行中，无法重试失败项";
            OnPropertyChanged(nameof(HasOperationMessage));
            return;
        }

        var failedIds = _store.QueueTasks
            .Where(task => task.StatusEnum == DownloadQueueStatus.Failed)
            .Select(task => task.TaskId)
            .ToHashSet();
        if (failedIds.Count == 0)
        {
            OperationMessage = "队列中没有失败项";
            OnPropertyChanged(nameof(HasOperationMessage));
            return;
        }

        var reset = _store.ResetTasks(failedIds, "等待重试");
        if (reset > 0)
        {
            _requireQueuePreparationBeforeRun = true;
            OperationMessage = $"已重试入队 {reset} 项";
            OnPropertyChanged(nameof(HasOperationMessage));
            RefreshQueue();
            _ = ProcessPendingQueueAsync();
        }
        else
        {
            OperationMessage = "失败项重试入队失败";
            OnPropertyChanged(nameof(HasOperationMessage));
        }
    }

    [RelayCommand]
    private void ClearQueueTasks()
    {
        if (IsDownloading)
        {
            OperationMessage = "下载进行中，无法清空队列";
            OnPropertyChanged(nameof(HasOperationMessage));
            return;
        }

        _store.ClearQueueTasks();
        RefreshQueue();
        OperationMessage = "队列已清空";
        OnPropertyChanged(nameof(HasOperationMessage));
    }

    [RelayCommand]
    private void ClearCompletedQueueTasks()
    {
        if (IsDownloading)
        {
            OperationMessage = "下载进行中，无法清理已完成任务";
            OnPropertyChanged(nameof(HasOperationMessage));
            return;
        }

        var removed = _store.ClearCompletedQueueTasks();
        RefreshQueue();
        OperationMessage = removed > 0 ? $"已清理 {removed} 个已完成任务" : "没有可清理的已完成任务";
        OnPropertyChanged(nameof(HasOperationMessage));
    }

    [RelayCommand]
    private void MoveQueueGroupUp(QueueGroupViewModel group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (IsDownloading)
        {
            OperationMessage = "下载进行中，请先暂停再调整顺序";
            OnPropertyChanged(nameof(HasOperationMessage));
            return;
        }

        MoveQueueGroup(group, -1);
    }

    [RelayCommand]
    private void MoveQueueGroupDown(QueueGroupViewModel group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (IsDownloading)
        {
            OperationMessage = "下载进行中，请先暂停再调整顺序";
            OnPropertyChanged(nameof(HasOperationMessage));
            return;
        }

        MoveQueueGroup(group, 1);
    }

    private void MoveQueueGroup(QueueGroupViewModel group, int direction)
    {
        var tasks = _store.QueueTasks;
        var grouped = tasks
            .GroupBy(task => task.AnimeTitle.Trim().Length == 0 ? "未命名剧集" : task.AnimeTitle.Trim())
            .ToList();
        var index = grouped.FindIndex(item => item.Key == group.AnimeTitle);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= grouped.Count)
        {
            return;
        }

        (grouped[index], grouped[target]) = (grouped[target], grouped[index]);
        _store.ReorderQueueTasks(grouped.SelectMany(item => item).ToArray());
        RefreshQueue();
    }

    private async Task ProcessPendingQueueAsync()
    {
        if (_isDownloadingFlag || IsSearching || IsLoadingEpisodes)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_store.Settings.SaveDirectory))
        {
            ErrorMessage = "请先在下载设置中选择保存目录。";
            OnPropertyChanged(nameof(HasErrorMessage));
            return;
        }

        var pendingCount = _store.QueueTasks.Count(task => task.StatusEnum == DownloadQueueStatus.Pending);
        if (pendingCount <= 0)
        {
            OperationMessage = "当前没有待处理任务";
            OnPropertyChanged(nameof(HasOperationMessage));
            return;
        }

        _isDownloadingFlag = true;
        _cancelRequested = false;
        _queueCts = new CancellationTokenSource();
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(CanStartDownload));
        OnPropertyChanged(nameof(CanMutateRecords));
        RateLimitBypassSession? bypassSession = null;
        var prepareChainForThisRun = _requireQueuePreparationBeforeRun;
        _requireQueuePreparationBeforeRun = false;
        var throttle = _store.Settings.ThrottleConfig();
        var sourceCooldownUntil = new Dictionary<string, long>(StringComparer.Ordinal);
        var sourceBackoffLevel = new Dictionary<string, int>(StringComparer.Ordinal);
        var random = new Random(Environment.TickCount);
        var processed = 0;
        var success = 0;
        var failed = 0;
        var skipped = 0;
        string? summaryMessage = null;
        SetProgressSummary($"队列执行中：待处理 {pendingCount} 集 · 流控{throttle.Label}");

        try
        {
            bypassSession = await PrepareRateLimitBypassAsync(_queueCts.Token).ConfigureAwait(false);
            while (true)
            {
                if (_cancelRequested)
                {
                    break;
                }

                var task = _store.QueueTasks.FirstOrDefault(item => item.StatusEnum == DownloadQueueStatus.Pending);
                if (task is null)
                {
                    break;
                }

                CancellationToken queueToken = _queueCts.Token;
                _store.SetTaskStatus(task.TaskId, DownloadQueueStatus.Running, "准备下载", incrementAttempt: true);
                UpdateEpisodeState(task.EpisodeId, EpisodeDownloadState.Running, 0, "准备下载");
                SetActiveTask(task.AnimeTitle, task.EpisodeNo, task.Source, "准备下载");
                RefreshQueue();
                var input = task.ToInput();
                var activeEpisodeId = task.EpisodeId;
                var taskSourceKey = SourceKey(task.Source);
                if (prepareChainForThisRun)
                {
                    const string prepareDetail = "准备下载中：优先尝试原链路，失效后自动重建";
                    _store.SetTaskStatus(task.TaskId, DownloadQueueStatus.Running, prepareDetail);
                    UpdateEpisodeState(activeEpisodeId, EpisodeDownloadState.Running, 0, prepareDetail);
                    SetActiveTask(task.AnimeTitle, task.EpisodeNo, task.Source, prepareDetail);
                }

                var persistedRetryWait = task.RetryNotBeforeAt - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (persistedRetryWait > 0 && !_cancelRequested)
                {
                    var waitSeconds = Math.Max(1, persistedRetryWait / 1000);
                    var waitDetail = $"上次失败退避等待 {waitSeconds}s";
                    _store.SetTaskStatus(task.TaskId, DownloadQueueStatus.Running, waitDetail);
                    UpdateEpisodeState(activeEpisodeId, EpisodeDownloadState.Running, 0, waitDetail);
                    SetActiveTask(task.AnimeTitle, task.EpisodeNo, task.Source, waitDetail);
                    SetProgressSummary($"等待上次失败退避结束（{waitSeconds}s）");
                    SetThrottleHint($"来源退避中：等待 {waitSeconds}s 后重试");
                    await InterruptibleDelayAsync(persistedRetryWait).ConfigureAwait(false);
                    SetThrottleHint(null);
                }

                if (_cancelRequested)
                {
                    RevertTaskToPending(task, activeEpisodeId);
                    break;
                }

                if (task.RetryNotBeforeAt > 0)
                {
                    _store.SetTaskRetryNotBefore(task.TaskId, 0);
                }

                var sourceWait = (sourceCooldownUntil.GetValueOrDefault(taskSourceKey) - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if (sourceWait > 0 && !_cancelRequested)
                {
                    var waitDetail = $"来源限流等待 {sourceWait / 1000}s";
                    _store.SetTaskStatus(task.TaskId, DownloadQueueStatus.Running, waitDetail);
                    UpdateEpisodeState(activeEpisodeId, EpisodeDownloadState.Running, 0, waitDetail);
                    SetActiveTask(task.AnimeTitle, task.EpisodeNo, task.Source, waitDetail);
                    SetProgressSummary($"来源 {SourceDisplay(task.Source)} 限流等待 {sourceWait / 1000}s");
                    SetThrottleHint($"流控中：来源限流等待 {sourceWait / 1000}s，请耐心等待");
                    await InterruptibleDelayAsync(sourceWait).ConfigureAwait(false);
                    SetThrottleHint(null);
                }

                if (_cancelRequested)
                {
                    RevertTaskToPending(task, activeEpisodeId);
                    break;
                }

                if (processed > 0 && !_cancelRequested)
                {
                    var reqWait = throttle.BaseDelayMs + NextJitterMs(random, throttle.JitterMaxMs);
                    if (reqWait > 0)
                    {
                        var waitDetail = $"节流等待 {reqWait}ms";
                        _store.SetTaskStatus(task.TaskId, DownloadQueueStatus.Running, waitDetail);
                        UpdateEpisodeState(activeEpisodeId, EpisodeDownloadState.Running, 0, waitDetail);
                        SetActiveTask(task.AnimeTitle, task.EpisodeNo, task.Source, waitDetail);
                        SetProgressSummary($"流控等待 {reqWait}ms 后继续");
                        await InterruptibleDelayAsync(reqWait).ConfigureAwait(false);
                    }
                }

                if (_cancelRequested)
                {
                    RevertTaskToPending(task, activeEpisodeId);
                    break;
                }

                const int maxAutoRetry = 2;
                var retryCount = 0;
                DanmuDownloadResult? finalResult = null;
                Exception? finalError = null;
                var staleRebuildDone = false;
                var canceled = false;

                while (!_cancelRequested)
                {
                    var attemptNo = retryCount + 1;
                    var totalAttempts = maxAutoRetry + 1;
                    var startDetail = attemptNo == 1 ? "开始下载" : $"开始重试（{attemptNo}/{totalAttempts}）";
                    _store.SetTaskStatus(task.TaskId, DownloadQueueStatus.Running, startDetail, incrementAttempt: attemptNo > 1);
                    UpdateEpisodeState(activeEpisodeId, EpisodeDownloadState.Running, 0, startDetail);
                    SetActiveTask(task.AnimeTitle, task.EpisodeNo, task.Source, startDetail);

                    DanmuDownloadResult attemptResult;
                    try
                    {
                        if (!IsServiceRunning)
                        {
                            throw new DanmuApiException(DanmuApiFailureKind.ServiceNotRunning, "服务未运行，无法下载弹幕");
                        }

                        var progress = new Progress<DanmuDownloadProgress>(value =>
                            RunOnUi(() =>
                            {
                                SetActiveTask(task.AnimeTitle, task.EpisodeNo, task.Source, value.Detail);
                                ActiveTaskProgress = value.Progress;
                                UpdateEpisodeProgress(activeEpisodeId, value.Progress, value.Detail);
                            }));
                        using (DanmuScenes.Begin("弹幕下载/下载弹幕"))
                        {
                            attemptResult = await _service.DownloadAsync(
                                input,
                                _context.Host,
                                _context.Port!.Value,
                                _context.Token,
                                progress,
                                queueToken).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (_cancelRequested)
                    {
                        canceled = true;
                        break;
                    }
                    catch (DanmuApiException error)
                    {
                        attemptResult = new DanmuDownloadResult(
                            DownloadRecordStatus.Failed, string.Empty, string.Empty, string.Empty, 0, 0,
                            null, error.StatusCode, error.Message);
                    }

                    if (canceled)
                    {
                        break;
                    }

                    finalResult = attemptResult;
                    finalError = null;
                    var rawDetail = attemptResult.Status == DownloadRecordStatus.Failed
                        ? string.IsNullOrWhiteSpace(attemptResult.ErrorMessage) ? "下载失败" : attemptResult.ErrorMessage!
                        : string.Empty;
                    var failureHttpCode = attemptResult.HttpCode ?? ExtractHttpCodeFromDetail(rawDetail);
                    var failedResult = attemptResult.Status == DownloadRecordStatus.Failed;
                    var canRetry = retryCount < maxAutoRetry;
                    if (prepareChainForThisRun && failedResult && canRetry && !staleRebuildDone &&
                        DanmuDownloadRetryPolicy.ShouldRebuildChainForStaleFailure(failureHttpCode, rawDetail))
                    {
                        const string rebuildingDetail = "检测到旧链路可能失效，正在重建弹幕链路";
                        _store.SetTaskStatus(task.TaskId, DownloadQueueStatus.Running, rebuildingDetail);
                        UpdateEpisodeState(activeEpisodeId, EpisodeDownloadState.Running, 0, rebuildingDetail);
                        SetActiveTask(task.AnimeTitle, task.EpisodeNo, task.Source, rebuildingDetail);
                        try
                        {
                            DanmuDownloadInput rebuilt;
                            using (DanmuScenes.Begin("弹幕下载/重建链路"))
                            {
                                rebuilt = await RebuildInputAsync(task, queueToken).ConfigureAwait(false);
                            }

                            var oldEpisodeId = input.EpisodeId;
                            var rebuiltDetail = rebuilt.EpisodeId != oldEpisodeId
                                ? $"链路重建完成：已刷新弹幕ID（{oldEpisodeId}→{rebuilt.EpisodeId}）"
                                : "链路重建完成：已刷新映射";
                            if (!_store.UpdateTaskInput(task.TaskId, rebuilt, rebuiltDetail))
                            {
                                finalResult = null;
                                finalError = new InvalidOperationException("链路重建失败：无法保存新的弹幕链路");
                                break;
                            }

                            input = rebuilt;
                            activeEpisodeId = rebuilt.EpisodeId;
                            taskSourceKey = SourceKey(rebuilt.Source);
                            staleRebuildDone = true;
                            SetActiveTask(rebuilt.AnimeTitle, rebuilt.EpisodeNo, rebuilt.Source, rebuiltDetail);
                            _store.SetTaskStatus(task.TaskId, DownloadQueueStatus.Running, rebuiltDetail);
                            UpdateEpisodeState(activeEpisodeId, EpisodeDownloadState.Running, 0, rebuiltDetail);
                            RefreshQueue();
                            retryCount++;
                            continue;
                        }
                        catch (Exception error) when (error is DanmuApiException or InvalidOperationException or ArgumentException)
                        {
                            var rebuildFailDetail = $"链路重建失败：{error.Message}";
                            _store.SetTaskStatus(task.TaskId, DownloadQueueStatus.Running, rebuildFailDetail);
                            UpdateEpisodeState(activeEpisodeId, EpisodeDownloadState.Running, 0, rebuildFailDetail);
                            finalResult = null;
                            finalError = new InvalidOperationException(rebuildFailDetail);
                            break;
                        }
                    }

                    var shouldRetry = failedResult && canRetry && DanmuDownloadRetryPolicy.ShouldRetryFailure(failureHttpCode, rawDetail);
                    if (!shouldRetry)
                    {
                        break;
                    }

                    var shouldBackoff = DanmuDownloadRetryPolicy.ShouldTriggerBackoff(failureHttpCode, rawDetail);
                    var nextAttemptNo = attemptNo + 1;
                    var retryDetail = rawDetail.Length == 0
                        ? $"下载失败，准备重试（{nextAttemptNo}/{totalAttempts}）"
                        : $"下载失败，准备重试（{nextAttemptNo}/{totalAttempts}）：{rawDetail}";
                    long backoffMs = 0;
                    if (shouldBackoff)
                    {
                        var level = (sourceBackoffLevel.GetValueOrDefault(taskSourceKey)) + 1;
                        sourceBackoffLevel[taskSourceKey] = level;
                        backoffMs = BackoffDelayMs(throttle, level, random);
                        sourceCooldownUntil[taskSourceKey] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + backoffMs;
                        retryDetail += $"（退避{backoffMs / 1000}s）";
                    }

                    UpdateEpisodeState(activeEpisodeId, EpisodeDownloadState.Running, 0, retryDetail);
                    _store.SetTaskStatus(task.TaskId, DownloadQueueStatus.Running, retryDetail);
                    SetActiveTask(task.AnimeTitle, task.EpisodeNo, task.Source, retryDetail);
                    if (backoffMs > 0 && !_cancelRequested)
                    {
                        SetProgressSummary($"来源 {SourceDisplay(task.Source)} 重试退避 {backoffMs / 1000}s");
                        SetThrottleHint($"流控中：下载失败自动重试，等待 {backoffMs / 1000}s");
                        await InterruptibleDelayAsync(backoffMs).ConfigureAwait(false);
                        SetThrottleHint(null);
                    }

                    retryCount++;
                }

                if (canceled || _cancelRequested)
                {
                    RevertTaskToPending(task, activeEpisodeId);
                    break;
                }

                processed++;
                if (finalResult is not null)
                {
                    var result = finalResult;
                    switch (result.Status)
                    {
                        case DownloadRecordStatus.Success:
                        {
                            success++;
                            sourceBackoffLevel[taskSourceKey] = 0;
                            sourceCooldownUntil.Remove(taskSourceKey);
                            var detail = string.IsNullOrWhiteSpace(result.ErrorMessage)
                                ? $"已保存：{result.RelativePath}"
                                : $"已保存：{result.RelativePath}（{result.ErrorMessage}）";
                            UpdateEpisodeState(activeEpisodeId, EpisodeDownloadState.Success, 1, detail);
                            _store.SetTaskStatus(task.TaskId, DownloadQueueStatus.Success, detail);
                            _store.SetTaskRetryNotBefore(task.TaskId, 0);
                            break;
                        }
                        case DownloadRecordStatus.Skipped:
                        {
                            skipped++;
                            sourceBackoffLevel[taskSourceKey] = 0;
                            sourceCooldownUntil.Remove(taskSourceKey);
                            var detail = string.IsNullOrWhiteSpace(result.ErrorMessage) ? "已跳过" : result.ErrorMessage!;
                            UpdateEpisodeState(activeEpisodeId, EpisodeDownloadState.Skipped, 1, detail);
                            _store.SetTaskStatus(task.TaskId, DownloadQueueStatus.Skipped, detail);
                            _store.SetTaskRetryNotBefore(task.TaskId, 0);
                            break;
                        }
                        default:
                        {
                            failed++;
                            var rawFail = string.IsNullOrWhiteSpace(result.ErrorMessage) ? "下载失败" : result.ErrorMessage!;
                            var failPrefix = retryCount > 0 ? $"重试{retryCount}次后仍失败：" : "下载失败：";
                            long retryNotBeforeAt = 0;
                            var shouldBackoff = DanmuDownloadRetryPolicy.ShouldTriggerBackoff(result.HttpCode ?? ExtractHttpCodeFromDetail(rawFail), rawFail);
                            var detail = rawFail;
                            if (shouldBackoff)
                            {
                                var level = (sourceBackoffLevel.GetValueOrDefault(taskSourceKey)) + 1;
                                sourceBackoffLevel[taskSourceKey] = level;
                                var backoffMs = BackoffDelayMs(throttle, level, random);
                                retryNotBeforeAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + backoffMs;
                                sourceCooldownUntil[taskSourceKey] = retryNotBeforeAt;
                                detail = $"{failPrefix}{rawFail}（退避{backoffMs / 1000}s）";
                            }
                            else
                            {
                                detail = $"{failPrefix}{rawFail}";
                            }

                            UpdateEpisodeState(activeEpisodeId, EpisodeDownloadState.Failed, 1, detail);
                            _store.SetTaskStatus(task.TaskId, DownloadQueueStatus.Failed, detail);
                            _store.SetTaskRetryNotBefore(task.TaskId, retryNotBeforeAt);
                            break;
                        }
                    }
                }
                else
                {
                    failed++;
                    var message = finalError?.Message ?? "下载任务执行异常";
                    UpdateEpisodeState(activeEpisodeId, EpisodeDownloadState.Failed, 1, message);
                    _store.SetTaskStatus(task.TaskId, DownloadQueueStatus.Failed, message);
                }

                RefreshQueue();
                var remaining = _store.QueueTasks.Count(item => item.StatusEnum == DownloadQueueStatus.Pending);
                var totalForRun = processed + remaining;
                SetOverallProgress(totalForRun <= 0 ? 1 : (double)processed / totalForRun);
                SetProgressSummary($"队列执行：已完成 {processed}，待处理 {remaining}（成功 {success}，失败 {failed}，跳过 {skipped}）");

                if (!_cancelRequested && remaining > 0 && throttle.BatchSize > 0 && processed % throttle.BatchSize == 0)
                {
                    var batchWait = throttle.BatchRestMs + NextJitterMs(random, Math.Min(2000, throttle.JitterMaxMs));
                    if (batchWait > 0)
                    {
                        SetProgressSummary($"已完成 {processed} 集，批次休息 {batchWait / 1000}s");
                        SetThrottleHint($"流控中：每 {throttle.BatchSize} 集休息 {batchWait / 1000}s，防止被封禁");
                        await InterruptibleDelayAsync(batchWait).ConfigureAwait(false);
                        SetThrottleHint(null);
                    }
                }
            }

            var remain = _store.QueueTasks.Count(item => item.StatusEnum == DownloadQueueStatus.Pending);
            SetProgressSummary(_cancelRequested
                ? $"队列已暂停，待处理 {remain} 集"
                : $"队列执行完成：成功 {success}，失败 {failed}，跳过 {skipped}");
            summaryMessage = _cancelRequested
                ? $"队列已暂停，待处理 {remain} 集"
                : $"队列执行完成：成功 {success}，失败 {failed}，跳过 {skipped}";
        }
        catch (OperationCanceledException)
        {
            SetProgressSummary("队列已暂停");
            summaryMessage = "队列已暂停";
        }
        catch (Exception error)
        {
            SetProgressSummary($"队列执行失败：{error.Message}");
            summaryMessage = $"队列执行失败：{error.Message}";
            _diagnostics.Record("弹幕下载队列执行失败", error);
        }
        finally
        {
            var restoreError = await RestoreRateLimitBypassAsync(bypassSession).ConfigureAwait(false);
            RunOnUi(() =>
            {
                OperationMessage = CombineMessages(summaryMessage, restoreError);
                ErrorMessage = null;
                OnPropertyChanged(nameof(HasErrorMessage));
                OnPropertyChanged(nameof(HasOperationMessage));
                ClearActiveTask();
                ThrottleHint = null;
                OnPropertyChanged(nameof(ThrottleHint));
                OnPropertyChanged(nameof(HasThrottleHint));
                _isDownloadingFlag = false;
                OnPropertyChanged(nameof(IsDownloading));
                OnPropertyChanged(nameof(CanStartDownload));
                OnPropertyChanged(nameof(CanMutateRecords));
                RefreshQueue();
                _ = RefreshRecordsAsync();
            });
            _queueCts.Dispose();
            _queueCts = null;
        }
    }

    private async Task<DanmuDownloadInput> RebuildInputAsync(DanmuDownloadTask task, CancellationToken cancellationToken)
    {
        if (!IsServiceRunning)
        {
            throw new DanmuApiException(DanmuApiFailureKind.ServiceNotRunning, "服务未运行，无法重建链路");
        }

        return await _service.RebuildChainAsync(task, _context.Host, _context.Port!.Value, _context.Token, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RateLimitBypassSession?> PrepareRateLimitBypassAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!IsServiceRunning)
            {
                return null;
            }

            var admin = _context.AdminToken();
            var read = await _envClient.ReadConfigValueAsync(
                _context.Host, _context.Port!.Value, _context.Token, admin, RateLimitEnvKey, cancellationToken).ConfigureAwait(false);
            if (!read.Succeeded)
            {
                return null;
            }

            var original = read.Value?.Trim() ?? string.Empty;
            if (original == "0")
            {
                return new RateLimitBypassSession(original, false);
            }

            var set = await _envClient.SetAsync(
                _context.Host, _context.Port.Value, _context.Token, admin, RateLimitEnvKey, "0", cancellationToken).ConfigureAwait(false);
            if (!set.Succeeded)
            {
                return null;
            }

            return new RateLimitBypassSession(original, true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is DanmuApiException or ArgumentException)
        {
            _diagnostics.Record("下载限流旁路准备失败", error);
            return null;
        }
    }

    private async Task<string?> RestoreRateLimitBypassAsync(RateLimitBypassSession? session)
    {
        if (session is null || !session.Applied)
        {
            return null;
        }

        try
        {
            if (!IsServiceRunning)
            {
                return "下载限流旁路恢复失败：服务未运行；RATE_LIMIT_MAX_REQUESTS 仍为 0，请在配置页手动恢复。";
            }

            var admin = _context.AdminToken();
            var set = await _envClient.SetAsync(
                _context.Host, _context.Port!.Value, _context.Token, admin, RateLimitEnvKey, session.OriginalValue ?? "0").ConfigureAwait(false);
            return set.Succeeded ? null : $"下载限流旁路恢复失败：{set.Diagnostic}";
        }
        catch (Exception error) when (error is DanmuApiException or ArgumentException)
        {
            return $"下载限流旁路恢复失败：{error.Message}";
        }
    }

    private async Task InterruptibleDelayAsync(long totalMs)
    {
        const int step = 300;
        var remaining = totalMs;
        while (remaining > 0 && !_cancelRequested)
        {
            await Task.Delay((int)Math.Min(step, remaining), _queueCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
            remaining -= step;
        }
    }

    private void RevertTaskToPending(DanmuDownloadTask task, long episodeId)
    {
        _store.SetTaskStatus(task.TaskId, DownloadQueueStatus.Pending, "等待恢复");
        UpdateEpisodeState(episodeId, EpisodeDownloadState.Queued, 0, "等待恢复");
        ClearActiveTask();
        RefreshQueue();
    }

    private static long NextJitterMs(Random random, long maxMs) =>
        maxMs <= 0 ? 0 : random.NextInt64(maxMs + 1);

    private static long BackoffDelayMs(DownloadThrottleConfig config, int level, Random random)
    {
        var delayMs = Math.Max(0, config.BackoffBaseMs);
        var rounds = Math.Clamp(level - 1, 0, 10);
        for (var index = 0; index < rounds; index++)
        {
            delayMs = Math.Min(delayMs * 2, config.BackoffMaxMs);
        }

        delayMs = Math.Min(delayMs + NextJitterMs(random, Math.Min(1500, config.JitterMaxMs)), config.BackoffMaxMs);
        return Math.Max(1000, delayMs);
    }

    private static int? ExtractHttpCodeFromDetail(string detail)
    {
        var match = System.Text.RegularExpressions.Regex.Match(detail, @"http\s*([0-9]{3})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var code) ? code : null;
    }

    private void SetActiveTask(string animeTitle, int episodeNo, string source, string detail)
    {
        RunOnUi(() =>
        {
            var sourceText = string.IsNullOrWhiteSpace(source) ? "unknown" : source;
            var episodeText = episodeNo > 0 ? $"第{episodeNo}集" : "当前集";
            ActiveTaskText = $"{animeTitle} · {episodeText} · {sourceText} · {detail}";
        });
    }

    private void SetProgressSummary(string value) => RunOnUi(() => ProgressSummary = value);

    private void SetThrottleHint(string? value) => RunOnUi(() =>
    {
        ThrottleHint = value;
        OnPropertyChanged(nameof(HasThrottleHint));
    });

    private void SetOverallProgress(double value) => RunOnUi(() =>
    {
        OverallProgress = value;
        OnPropertyChanged(nameof(OverallProgressPercent));
    });

    private void ClearActiveTask() => RunOnUi(() =>
    {
        ActiveTaskText = "当前无运行中的任务";
        ActiveTaskProgress = 0;
    });

    private static string SourceKey(string raw) =>
        string.IsNullOrWhiteSpace(raw) ? "unknown" : DanmuDownloadParsing.CanonicalSourceKey(raw);

    private static string SourceDisplay(string raw) => string.IsNullOrWhiteSpace(raw) ? "unknown" : raw;

    private static string CombineMessages(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return second ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(second))
        {
            return first;
        }

        return $"{first}（{second}）";
    }

    private static long ParseLong(string text, long fallback) =>
        long.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private void UpdateEpisodeProgress(long episodeId, double progress, string detail)
    {
        RunOnUi(() =>
        {
            var row = EpisodeRows.FirstOrDefault(item => item.EpisodeId == episodeId);
            if (row is null || row.State.State != EpisodeDownloadState.Running)
            {
                return;
            }

            row.UpdateState(new EpisodeUiState(EpisodeDownloadState.Running, progress, detail));
            NotifyEpisodeSummaryChanged();
        });
    }

    private void UpdateEpisodeState(long episodeId, EpisodeDownloadState state, double progress, string detail)
    {
        RunOnUiAsync(() =>
        {
            var row = EpisodeRows.FirstOrDefault(item => item.EpisodeId == episodeId);
            row?.UpdateState(new EpisodeUiState(state, progress, detail));
            NotifyEpisodeSummaryChanged();
        }).GetAwaiter().GetResult();
    }

    // ── 记录与库 ──────────────────────────────────────────

    [ObservableProperty]
    private bool _isSyncing;

    partial void OnIsSyncingChanged(bool value) => OnPropertyChanged(nameof(CanMutateRecords));

    [RelayCommand]
    private async Task SyncDirectoryAsync()
    {
        if (IsSyncing)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_store.Settings.SaveDirectory))
        {
            ErrorMessage = "请先在下载设置中选择保存目录。";
            OnPropertyChanged(nameof(HasErrorMessage));
            return;
        }

        try
        {
            IsSyncing = true;
            var result = await _service.SyncExistingFilesAsync().ConfigureAwait(true);
            await RefreshRecordsAsync().ConfigureAwait(true);
            if (result.ImportedRecords > 0 || result.ScannedFiles > 0)
            {
                OperationMessage = $"已扫描 {result.ScannedFiles} 个文件，新增 {result.ImportedRecords} 条记录" +
                    (result.Truncated ? "（已达到扫描上限）" : string.Empty);
                OnPropertyChanged(nameof(HasOperationMessage));
            }
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            ErrorMessage = $"目录扫描失败：{error.Message}";
            OnPropertyChanged(nameof(HasErrorMessage));
            _diagnostics.Record("弹幕下载目录扫描失败", error);
        }
        finally
        {
            IsSyncing = false;
        }
    }

    private async Task SyncDirectoryQuietlyAsync()
    {
        try
        {
            await _service.SyncExistingFilesAsync().ConfigureAwait(true);
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _diagnostics.Record("弹幕下载目录静默扫描失败", error);
        }

        await RefreshRecordsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenRecordGroup(RecordAnimeGroupViewModel group)
    {
        ArgumentNullException.ThrowIfNull(group);
        SelectedRecordGroup = group;
        CurrentRecordStage = DownloadRecordStage.EpisodeList;
    }

    [RelayCommand]
    private void BackToRecordAnimeList()
    {
        CurrentRecordStage = DownloadRecordStage.AnimeList;
        SelectedRecordGroup = null;
        ClosePreview();
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var selectedGroups = (SelectedRecordGroup?.Episodes ?? RecordGroups.SelectMany(group => group.Episodes))
            .Where(item => item.IsSelected)
            .ToArray();
        var recordIds = selectedGroups.SelectMany(item => item.RecordIds).ToHashSet();
        if (recordIds.Count == 0)
        {
            OperationMessage = "请先勾选要删除的记录";
            OnPropertyChanged(nameof(HasOperationMessage));
            return;
        }

        await DeleteRecordsByIdsAsync(
            recordIds,
            "删除下载记录",
            $"确定删除 {recordIds.Count} 条下载记录吗？勾选项将同时清理对应文件。").ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteRecordGroupAsync(RecordAnimeGroupViewModel group)
    {
        ArgumentNullException.ThrowIfNull(group);
        await DeleteRecordsByIdsAsync(
            group.Records.Select(record => record.Id).ToHashSet(),
            $"删除《{group.Title}》的记录",
            $"将清理这部剧的 {group.Episodes.Count} 集、{group.Records.Count} 条记录，并删除对应本地文件。").ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteRecordEpisodeAsync(RecordEpisodeGroupViewModel episode)
    {
        ArgumentNullException.ThrowIfNull(episode);
        var label = episode.EpisodeNo > 0 ? $"第 {episode.EpisodeNo} 集" : episode.Title;
        await DeleteRecordsByIdsAsync(
            episode.RecordIds.ToHashSet(),
            $"删除{label}的记录",
            $"将清理这一集的 {episode.Records.Count} 条记录，并删除对应本地文件。").ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteAllRecordsAsync()
    {
        var recordIds = _store.Records.Select(record => record.Id).ToHashSet();
        if (recordIds.Count == 0)
        {
            OperationMessage = "暂无下载记录可清空";
            OnPropertyChanged(nameof(HasOperationMessage));
            return;
        }

        await DeleteRecordsByIdsAsync(
            recordIds,
            "清空全部下载记录",
            $"将清理 App 中的 {recordIds.Count} 条下载记录，并删除对应本地文件。").ConfigureAwait(true);
    }

    private async Task DeleteRecordsByIdsAsync(HashSet<long> recordIds, string title, string description)
    {
        if (recordIds.Count == 0)
        {
            OperationMessage = "所选记录已不存在";
            OnPropertyChanged(nameof(HasOperationMessage));
            return;
        }

        if (IsDownloading)
        {
            OperationMessage = "下载进行中，请暂停后再删除记录";
            OnPropertyChanged(nameof(HasOperationMessage));
            return;
        }

        if (IsSyncing)
        {
            OperationMessage = "正在扫描目录，请稍后再删除";
            OnPropertyChanged(nameof(HasOperationMessage));
            return;
        }

        if (!await _dialogs.ConfirmAsync(title, description, "删除").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            var summary = _store.DeleteRecords(recordIds, deleteLocalFiles: true);
            await RefreshRecordsAsync().ConfigureAwait(true);
            RefreshQueue();
            OperationMessage = summary.RemovedRecords <= 0
                ? "所选记录已不存在"
                : $"已清理 {summary.RemovedRecords} 条记录，删除 {summary.DeletedFiles} 个本地文件" +
                    (summary.MissingFiles > 0 ? $"，{summary.MissingFiles} 个文件已不存在" : string.Empty) +
                    (summary.FailedFiles > 0 ? $"，{summary.FailedFiles} 个文件删除失败" : string.Empty);
            OnPropertyChanged(nameof(HasOperationMessage));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ErrorMessage = $"删除下载记录失败：{error.Message}";
            OnPropertyChanged(nameof(HasErrorMessage));
            _diagnostics.Record("删除下载记录失败", error);
        }
    }

    [RelayCommand]
    private async Task OpenPreviewAsync(RecordEpisodeGroupViewModel group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (IsPreviewOpen)
        {
            return;
        }

        try
        {
            var record = group.Records.FirstOrDefault(item => item.StatusEnum == DownloadRecordStatus.Success && item.FormatOrNull?.SupportsPreview() == true);
            if (record is null)
            {
                PreviewError = "该剧集组没有可预览的成功记录";
                Preview = null;
                IsPreviewOpen = true;
                OnPropertyChanged(nameof(HasPreviewError));
                return;
            }

            var preview = await _service.LoadPreviewAsync(record).ConfigureAwait(true);
            Preview = preview;
            PreviewError = null;
            SelectedPreviewFilter = PreviewFilterOptions[0];
            PreviewRows.Clear();
            foreach (var item in preview.Items)
            {
                PreviewRows.Add(new PreviewItemRowViewModel(item));
            }

            IsPreviewOpen = true;
            OnPropertyChanged(nameof(HasPreviewError));
            ApplyPreviewFilter();
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or System.Xml.XmlException)
        {
            PreviewError = error.Message;
            Preview = null;
            IsPreviewOpen = true;
            OnPropertyChanged(nameof(HasPreviewError));
        }
    }

    [RelayCommand]
    private void ClosePreview()
    {
        IsPreviewOpen = false;
        Preview = null;
        PreviewError = null;
        PreviewRows.Clear();
        OnPropertyChanged(nameof(HasPreviewError));
    }

    partial void OnSelectedPreviewFilterChanged(DanmuPreviewFilterOption value) => ApplyPreviewFilter();

    private void ApplyPreviewFilter()
    {
        var filter = SelectedPreviewFilter.Value;
        foreach (var row in PreviewRows)
        {
            row.IsVisible = filter switch
            {
                DanmuPreviewFilterKind.Top => row.Mode == "5",
                DanmuPreviewFilterKind.Bottom => row.Mode == "4",
                DanmuPreviewFilterKind.Scroll => row.Mode is not ("4" or "5"),
                _ => true,
            };
        }
    }

    private void RefreshRecords()
    {
        _ = RefreshRecordsAsync();
    }

    private async Task RefreshRecordsAsync()
    {
        var records = _store.Records;
        await RunOnUiAsync(() =>
        {
            RecordGroups.Clear();
            foreach (var group in BuildRecordAnimeGroups(records))
            {
                RecordGroups.Add(group);
            }

            OnPropertyChanged(nameof(HasRecordGroups));
            OnPropertyChanged(nameof(RecordsSummaryText));
            OnPropertyChanged(nameof(VisibleRecordGroups));
            OnPropertyChanged(nameof(HasVisibleRecordGroups));
            RestoreSelectedRecordGroup();
        }).ConfigureAwait(false);
    }

    private void RestoreSelectedRecordGroup()
    {
        if (SelectedRecordGroup is null)
        {
            return;
        }

        var restored = RecordGroups.FirstOrDefault(group => group.Key == SelectedRecordGroup.Key);
        if (restored is null)
        {
            CurrentRecordStage = DownloadRecordStage.AnimeList;
            SelectedRecordGroup = null;
            return;
        }

        SelectedRecordGroup = restored;
    }

    internal string BuildRecordsSummaryText()
    {
        var records = _store.Records;
        if (records.Count == 0)
        {
            return "暂无下载记录";
        }

        var groups = RecordGroups;
        var downloadedEpisodes = groups.Sum(group => group.DownloadedEpisodeCount);
        return $"{groups.Count} 部剧 · {downloadedEpisodes} 集已下载 · {records.Count} 条记录";
    }

    private IReadOnlyList<RecordAnimeGroupViewModel> BuildRecordAnimeGroups(IReadOnlyList<DanmuDownloadRecord> records)
    {
        return records
            .GroupBy(AnimeGroupKey)
            .Select(group =>
            {
                var sorted = group.OrderByDescending(record => record.CreatedAt).ThenByDescending(record => record.Id).ToArray();
                var episodeGroups = BuildRecordEpisodeGroups(sorted);
                return new RecordAnimeGroupViewModel(
                    group.Key,
                    sorted[0].AnimeTitle.StripFromSuffix(),
                    sorted,
                    episodeGroups,
                    sorted[0],
                    sorted.Count(record => record.StatusEnum == DownloadRecordStatus.Failed),
                    sorted.Count(record => record.StatusEnum == DownloadRecordStatus.Skipped),
                    sorted.Where(record => record.StatusEnum == DownloadRecordStatus.Success)
                        .DistinctBy(record => string.IsNullOrWhiteSpace(record.FilePath) ? $"record-{record.Id}" : record.FilePath)
                        .Sum(record => Math.Max(0, record.Bytes)));
            })
            .OrderByDescending(group => group.LatestAt)
            .ToArray();
    }

    private static IReadOnlyList<RecordEpisodeGroupViewModel> BuildRecordEpisodeGroups(IReadOnlyList<DanmuDownloadRecord> records)
    {
        return records
            .GroupBy(EpisodeGroupKey)
            .Select(group =>
            {
                var sorted = group.OrderByDescending(record => record.CreatedAt).ThenByDescending(record => record.Id).ToArray();
                var representative = sorted.FirstOrDefault(record => record.StatusEnum == DownloadRecordStatus.Success) ?? sorted[0];
                var status = sorted.Any(record => record.StatusEnum == DownloadRecordStatus.Success)
                    ? DownloadRecordStatus.Success
                    : sorted[0].StatusEnum;
                var successfulFiles = sorted
                    .Where(record => record.StatusEnum == DownloadRecordStatus.Success)
                    .Select(record => string.IsNullOrWhiteSpace(record.FilePath) ? $"record-{record.Id}" : record.FilePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                return new RecordEpisodeGroupViewModel(
                    representative.EpisodeNo,
                    string.IsNullOrWhiteSpace(representative.EpisodeTitle)
                        ? (string.IsNullOrWhiteSpace(representative.FileName) ? "未命名剧集" : representative.FileName)
                        : representative.EpisodeTitle,
                    status,
                    successfulFiles,
                    sorted,
                    sorted.Max(record => record.CreatedAt));
            })
            .OrderBy(group => group.EpisodeNo > 0 ? 0 : 1)
            .ThenBy(group => group.EpisodeNo)
            .ThenByDescending(group => group.LatestAt)
            .ToArray();
    }

    private static string AnimeGroupKey(DanmuDownloadRecord record) =>
        DanmuDownloadParsing.NormalizeAnimeTitleForMatch(record.AnimeTitle) is { Length: > 0 } key
            ? key
            : record.AnimeId > 0 ? $"anime-{record.AnimeId}" : (record.AnimeTitle.Trim().ToLowerInvariant() is { Length: > 0 } raw ? raw : "unknown-anime");

    private static string EpisodeGroupKey(DanmuDownloadRecord record)
    {
        if (record.EpisodeNo > 0)
        {
            return $"number-{record.EpisodeNo}";
        }

        var titleKey = DanmuDownloadParsing.NormalizeEpisodeTitleForMatch(record.EpisodeTitle);
        if (titleKey.Length > 0)
        {
            return $"title-{titleKey}";
        }

        return record.EpisodeId > 0 ? $"id-{record.EpisodeId}" : $"record-{record.Id}";
    }

    // ── 队列展示 ──────────────────────────────────────────

    private void RefreshQueue()
    {
        var tasks = _store.QueueTasks;
        RunOnUi(() =>
        {
            QueueGroups.Clear();
            var order = 0;
            foreach (var group in tasks.GroupBy(task => task.AnimeTitle.Trim().Length == 0 ? "未命名剧集" : task.AnimeTitle.Trim()))
            {
                var items = group.ToList();
                var success = items.Count(item => item.StatusEnum == DownloadQueueStatus.Success);
                var failedCount = items.Count(item => item.StatusEnum == DownloadQueueStatus.Failed);
                var skipped = items.Count(item => item.StatusEnum == DownloadQueueStatus.Skipped);
                var canceled = items.Count(item => item.StatusEnum == DownloadQueueStatus.Canceled);
                var pending = items.Count(item => item.StatusEnum == DownloadQueueStatus.Pending);
                var running = items.Count(item => item.StatusEnum == DownloadQueueStatus.Running);
                var latest = items.Max(item => item.UpdatedAt);
                var runningTask = items.Where(item => item.StatusEnum == DownloadQueueStatus.Running)
                    .OrderByDescending(item => item.UpdatedAt)
                    .FirstOrDefault();
                var episodeItems = items
                    .OrderBy(item => item.EpisodeNo)
                    .ThenBy(item => item.Source.ToLowerInvariant())
                    .ThenBy(item => item.EpisodeId)
                    .Select(item => new QueueEpisodeItemViewModel(
                        item.EpisodeNo,
                        item.EpisodeTitle,
                        string.IsNullOrWhiteSpace(item.Source) ? "unknown" : item.Source,
                        item.StatusEnum,
                        item.LastDetail))
                    .ToArray();
                QueueGroups.Add(new QueueGroupViewModel(
                    group.Key,
                    items.Count,
                    success + failedCount + skipped + canceled,
                    pending,
                    running,
                    success,
                    failedCount,
                    skipped,
                    canceled,
                    runningTask?.EpisodeNo,
                    runningTask?.LastDetail ?? items.OrderBy(item => item.UpdatedAt).Last().LastDetail,
                    latest,
                    order++,
                    episodeItems));
            }

            OnPropertyChanged(nameof(HasQueueGroups));
            OnPropertyChanged(nameof(QueueSummaryText));
            OnPropertyChanged(nameof(QueuePendingCount));
            OnPropertyChanged(nameof(QueueRunningCount));
            OnPropertyChanged(nameof(QueueSuccessCount));
            OnPropertyChanged(nameof(QueueFailedCount));
            OnPropertyChanged(nameof(QueueSkippedCount));
        });
    }

    private string BuildQueueSummaryText()
    {
        var tasks = _store.QueueTasks;
        if (tasks.Count == 0)
        {
            return "队列为空";
        }

        var pending = tasks.Count(item => item.StatusEnum == DownloadQueueStatus.Pending);
        var running = tasks.Count(item => item.StatusEnum == DownloadQueueStatus.Running);
        var success = tasks.Count(item => item.StatusEnum == DownloadQueueStatus.Success);
        var failed = tasks.Count(item => item.StatusEnum == DownloadQueueStatus.Failed);
        var skipped = tasks.Count(item => item.StatusEnum == DownloadQueueStatus.Skipped);
        return $"共 {tasks.Count} 项：待处理 {pending} · 下载中 {running} · 成功 {success} · 失败 {failed} · 跳过 {skipped}";
    }

    // ── 剧集状态构建 ──────────────────────────────────────────

    private Dictionary<long, EpisodeUiState> BuildInitialEpisodeStates(AnimeRowViewModel anime, IReadOnlyList<DanmuEpisodeCandidate> episodes)
    {
        var result = new Dictionary<long, EpisodeUiState>();
        if (episodes.Count == 0)
        {
            return result;
        }

        var animeKey = DanmuDownloadParsing.NormalizeAnimeTitleForMatch(anime.Title);
        var queueTasks = _store.QueueTasks.Where(task => animeKey.Length == 0 || DanmuDownloadParsing.AnimeIdentityMatches(task.AnimeTitle, task.AnimeId, anime.Title, anime.AnimeId)).ToArray();
        var records = _store.Records.Where(record => animeKey.Length == 0 || DanmuDownloadParsing.AnimeIdentityMatches(record.AnimeTitle, record.AnimeId, anime.Title, anime.AnimeId)).ToArray();
        foreach (var episode in episodes)
        {
            var sourceKey = DanmuDownloadParsing.CanonicalSourceKey(episode.Source);
            var titleKey = DanmuDownloadParsing.NormalizeEpisodeTitleForMatch(episode.Title);
            var matchingRecords = records
                .Where(record =>
                {
                    var sourceMatches = DanmuDownloadParsing.HistorySourceMatches(episode.Source, record.Source);
                    var numberMatches = record.EpisodeNo > 0 && record.EpisodeNo == episode.EpisodeNumber;
                    var idMatches = record.EpisodeId > 0 && record.EpisodeId == episode.EpisodeId;
                    var titleMatches = titleKey.Length > 0 && DanmuDownloadParsing.NormalizeEpisodeTitleForMatch(record.EpisodeTitle) == titleKey;
                    return sourceMatches && (numberMatches || idMatches || titleMatches);
                })
                .OrderByDescending(record => record.CreatedAt)
                .ToArray();
            var successfulRecords = matchingRecords.Where(record => record.StatusEnum == DownloadRecordStatus.Success).ToArray();
            var downloadedBefore = successfulRecords.Length > 0;
            var lastDownloadedAt = successfulRecords.Length == 0 ? (long?)null : successfulRecords.Max(record => record.CreatedAt);
            var queueTask = queueTasks
                .Where(task =>
                {
                    var sourceMatches = sourceKey == "unknown" || DanmuDownloadParsing.CanonicalSourceKey(task.Source) == sourceKey;
                    var numberMatches = task.EpisodeNo == episode.EpisodeNumber;
                    var idMatches = task.EpisodeId == episode.EpisodeId;
                    var titleMatches = titleKey.Length > 0 && DanmuDownloadParsing.NormalizeEpisodeTitleForMatch(task.EpisodeTitle) == titleKey;
                    return sourceMatches && (numberMatches || idMatches || titleMatches);
                })
                .OrderByDescending(task => task.UpdatedAt)
                .FirstOrDefault();
            if (queueTask is not null)
            {
                var mappedState = queueTask.StatusEnum switch
                {
                    DownloadQueueStatus.Pending => EpisodeDownloadState.Queued,
                    DownloadQueueStatus.Running => EpisodeDownloadState.Running,
                    DownloadQueueStatus.Success => EpisodeDownloadState.Success,
                    DownloadQueueStatus.Failed => EpisodeDownloadState.Failed,
                    DownloadQueueStatus.Skipped => EpisodeDownloadState.Skipped,
                    DownloadQueueStatus.Canceled => EpisodeDownloadState.Canceled,
                    _ => EpisodeDownloadState.Queued,
                };
                var progress = mappedState is EpisodeDownloadState.Success or EpisodeDownloadState.Failed or EpisodeDownloadState.Skipped or EpisodeDownloadState.Canceled
                    ? 1
                    : mappedState == EpisodeDownloadState.Running ? 0.15 : 0;
                result[episode.EpisodeId] = new EpisodeUiState(
                    mappedState, progress, queueTask.LastDetail, downloadedBefore, successfulRecords.Length, lastDownloadedAt);
                continue;
            }

            var record0 = matchingRecords.FirstOrDefault();
            if (record0 is not null)
            {
                var mapped = record0.StatusEnum switch
                {
                    DownloadRecordStatus.Success => EpisodeDownloadState.Success,
                    DownloadRecordStatus.Failed => EpisodeDownloadState.Failed,
                    DownloadRecordStatus.Skipped => EpisodeDownloadState.Skipped,
                    _ => EpisodeDownloadState.Idle,
                };
                result[episode.EpisodeId] = new EpisodeUiState(
                    mapped, 1, string.IsNullOrWhiteSpace(record0.RelativePath) ? record0.ErrorMessage ?? string.Empty : record0.RelativePath,
                    downloadedBefore, successfulRecords.Length, lastDownloadedAt);
            }
        }

        return result;
    }

    private static string BuildHistoryText(DanmuAnime anime, IReadOnlyList<DanmuDownloadRecord> records)
    {
        var successful = records
            .Where(record => record.StatusEnum == DownloadRecordStatus.Success)
            .Where(record => DanmuDownloadParsing.AnimeIdentityMatches(record.AnimeTitle, record.AnimeId, anime.AnimeTitle, anime.AnimeId))
            .Where(record => DanmuDownloadParsing.HistorySourceMatches(
                string.IsNullOrWhiteSpace(anime.Source) ? DanmuDownloadParsing.ExtractSourceFromAnimeTitle(anime.AnimeTitle) : anime.Source,
                record.Source))
            .ToArray();
        if (successful.Length == 0)
        {
            return "尚无下载记录";
        }

        var episodeCount = successful.Select(EpisodeKey).Distinct().Count();
        var latest = successful.Max(record => record.CreatedAt);
        var latestText = DateTimeOffset.FromUnixTimeMilliseconds(latest).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return $"已下载 {episodeCount} 集 / {successful.Length} 条 · 最近 {latestText}";
    }

    private static string EpisodeKey(DanmuDownloadRecord record)
    {
        if (record.EpisodeNo > 0)
        {
            return $"number-{record.EpisodeNo}";
        }

        var titleKey = DanmuDownloadParsing.NormalizeEpisodeTitleForMatch(record.EpisodeTitle);
        if (titleKey.Length > 0)
        {
            return $"title-{titleKey}";
        }

        return record.EpisodeId > 0 ? $"id-{record.EpisodeId}" : $"record-{record.Id}";
    }

    private void OnStorePersistFailed(string diagnostic)
    {
        _diagnostics.Record("弹幕下载数据持久化失败：" + diagnostic);
        RunOnUi(() =>
        {
            ErrorMessage = diagnostic;
            OnPropertyChanged(nameof(HasErrorMessage));
        });
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanStartDownload));
        NotifyEpisodeSummaryChanged();
    }

    private void NotifyEpisodeSummaryChanged()
    {
        OnPropertyChanged(nameof(EpisodeSummaryText));
        OnPropertyChanged(nameof(HasFailedVisible));
    }

    private IEnumerable<EpisodeRowViewModel> VisibleRows() =>
        EpisodeRows.Where(row => row.IsVisible);

    private int CountQueue(DownloadQueueStatus status) =>
        _store.QueueTasks.Count(task => task.StatusEnum == status);

    private static IReadOnlyList<RecordAnimeGroupViewModel> FilterRecordGroups(
        IEnumerable<RecordAnimeGroupViewModel> groups,
        DownloadRecordFilter filter) =>
        groups.Where(group => filter switch
        {
            DownloadRecordFilter.Success => group.Episodes.Any(episode => episode.Status == DownloadRecordStatus.Success),
            DownloadRecordFilter.Failed => group.FailedCount > 0,
            DownloadRecordFilter.Skipped => group.SkippedCount > 0,
            _ => true,
        }).ToArray();

    private string BuildEpisodeSummaryText()
    {
        var visible = VisibleRows().ToArray();
        if (CurrentAnime is null)
        {
            return string.Empty;
        }

        var success = visible.Count(row => row.State.State == EpisodeDownloadState.Success);
        var failed = visible.Count(row => row.State.State == EpisodeDownloadState.Failed);
        var skipped = visible.Count(row => row.State.State == EpisodeDownloadState.Skipped);
        var queued = visible.Count(row => row.State.State == EpisodeDownloadState.Queued);
        var running = visible.Count(row => row.State.State == EpisodeDownloadState.Running);
        var unfinished = visible.Count(row => row.State.State is not (EpisodeDownloadState.Success or EpisodeDownloadState.Skipped));
        return $"AnimeID: {CurrentAnime.AnimeId} · 官方 {CurrentAnime.EpisodeCount} 集 · 当前可见 {visible.Length} 集 · 成功 {success} · 失败 {failed} · 跳过 {skipped} · 排队 {queued} · 下载中 {running} · 未完成 {unfinished}";
    }

    private Task RunOnUiAsync(Action action)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiContext.Post(_ =>
        {
            try
            {
                action();
                completion.TrySetResult(true);
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        }, null);
        return completion.Task;
    }

    private void RunOnUi(Action action)
    {
        _ = RunOnUiAsync(action);
    }
}

public sealed record DanmuPreviewFilterOption(DanmuPreviewFilterKind Value, string Title)
{
    public override string ToString() => Title;
}

public enum DanmuPreviewFilterKind
{
    All,
    Scroll,
    Top,
    Bottom,
}

public sealed partial class PreviewItemRowViewModel : ViewModelBase
{
    [ObservableProperty]
    private bool _isVisible = true;

    public PreviewItemRowViewModel(DanmuPreviewItem item)
    {
        Index = item.Index;
        TimeText = FormatTime(item.TimeSeconds);
        Mode = item.Mode;
        ModeLabel = item.ModeLabel;
        Color = item.Color;
        Source = item.Source;
        Text = item.Text;
    }

    public int Index { get; }
    public string TimeText { get; }
    public string Mode { get; }
    public string ModeLabel { get; }
    public string Color { get; }
    public string Source { get; }
    public string Text { get; }

    private static string FormatTime(double? seconds)
    {
        if (seconds is null || !double.IsFinite(seconds.Value))
        {
            return "--:--.--";
        }

        var totalCentis = (long)Math.Round(Math.Max(0, seconds.Value) * 100);
        var minutes = totalCentis / 6000;
        var sec = totalCentis % 6000 / 100;
        var centis = totalCentis % 100;
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}.{2:00}", minutes, sec, centis);
    }
}

public sealed record SearchSection
{
    public static SearchSection Instance { get; } = new();
}

public sealed record QueueSection
{
    public static QueueSection Instance { get; } = new();
}

public sealed record RecordsSection
{
    public static RecordsSection Instance { get; } = new();
}

public sealed record SettingsSection
{
    public static SettingsSection Instance { get; } = new();
}

public sealed record EpisodeUiState(
    EpisodeDownloadState State = EpisodeDownloadState.Idle,
    double Progress = 0,
    string Detail = "",
    bool DownloadedBefore = false,
    int DownloadedRecordCount = 0,
    long? LastDownloadedAt = null);

public sealed partial class AnimeRowViewModel : PosterItemViewModelBase
{
    public AnimeRowViewModel(DanmuAnime anime, PosterImageService? posters, string historyText)
        : base(posters, anime.ImageUrl)
    {
        Anime = anime ?? throw new ArgumentNullException(nameof(anime));
        HistoryText = historyText;
    }

    public DanmuAnime Anime { get; }
    public int AnimeId => Anime.AnimeId;
    public string Title => Anime.AnimeTitle;
    public string DisplayTitle => AnimeSearchPresentation.DisplayTitle(Anime.AnimeTitle);
    public string SourceText => $"来源：{AnimeSearchPresentation.SourceText(Anime.Source, Anime.AnimeTitle)}";
    public string MetaText => AnimeSearchPresentation.MetaText(Anime.AnimeId, Anime.EpisodeCount);
    public string TypeDescription => Anime.TypeDescription;
    public int EpisodeCount => Anime.EpisodeCount;
    public string HistoryText { get; }
    public bool HasHistory => !string.IsNullOrWhiteSpace(HistoryText) && HistoryText != "尚无下载记录";
    public string PosterFallbackText => AnimeSearchPresentation.PosterFallback(Anime.AnimeTitle);
}

public sealed partial class EpisodeRowViewModel : ViewModelBase
{
    private readonly Action _selectionChanged;
    private EpisodeUiState _state;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isVisible = true;

    public EpisodeRowViewModel(long episodeId, int episodeNumber, string title, string source, string sourceUrl, EpisodeUiState state, Action selectionChanged)
    {
        EpisodeId = episodeId;
        EpisodeNumber = episodeNumber;
        Title = title;
        Source = string.IsNullOrWhiteSpace(source) ? "unknown" : source;
        SourceUrl = sourceUrl;
        _state = state;
        _selectionChanged = selectionChanged;
    }

    public long EpisodeId { get; }
    public int EpisodeNumber { get; }
    public string Title { get; }
    public string Source { get; }
    public string SourceUrl { get; }

    public EpisodeUiState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StateLabel));
                OnPropertyChanged(nameof(Detail));
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(IsSelectedDisabled));
            }
        }
    }

    public string StateLabel => State.State.Label();
    public string Detail => State.Detail;
    public string ProgressPercent => $"{(int)Math.Round(State.Progress * 100)}%";
    public bool IsSelectedDisabled => State.State is EpisodeDownloadState.Running;

    partial void OnIsSelectedChanged(bool value) => _selectionChanged();

    public void UpdateState(EpisodeUiState state) => State = state;
}

public sealed class QueueGroupViewModel
{
    public QueueGroupViewModel(
        string animeTitle,
        int total,
        int completed,
        int pending,
        int running,
        int success,
        int failed,
        int skipped,
        int canceled,
        int? runningEpisodeNo,
        string detail,
        long latestUpdatedAt,
        int order,
        IReadOnlyList<QueueEpisodeItemViewModel> episodes)
    {
        AnimeTitle = animeTitle;
        Total = total;
        Completed = completed;
        Pending = pending;
        Running = running;
        Success = success;
        Failed = failed;
        Skipped = skipped;
        Canceled = canceled;
        RunningEpisodeNo = runningEpisodeNo;
        Detail = detail;
        LatestUpdatedAt = latestUpdatedAt;
        Order = order;
        Episodes = episodes;
    }

    public string AnimeTitle { get; }
    public int Total { get; }
    public int Completed { get; }
    public int Pending { get; }
    public int Running { get; }
    public int Success { get; }
    public int Failed { get; }
    public int Skipped { get; }
    public int Canceled { get; }
    public int? RunningEpisodeNo { get; }
    public string Detail { get; }
    public long LatestUpdatedAt { get; }
    public int Order { get; }
    public IReadOnlyList<QueueEpisodeItemViewModel> Episodes { get; }
    public double Progress => Total <= 0 ? 0 : (double)Completed / Total;
    public string ProgressPercent => $"{(int)Math.Round(Progress * 100)}%";
}

public sealed class QueueEpisodeItemViewModel(
    int episodeNo,
    string episodeTitle,
    string source,
    DownloadQueueStatus status,
    string detail)
{
    public int EpisodeNo { get; } = episodeNo;
    public string EpisodeTitle { get; } = episodeTitle;
    public string Source { get; } = source;
    public DownloadQueueStatus Status { get; } = status;
    public string StatusLabel { get; } = status.Label();
    public string Detail { get; } = detail;
}

public sealed class RecordAnimeGroupViewModel
{
    public RecordAnimeGroupViewModel(
        string key,
        string title,
        IReadOnlyList<DanmuDownloadRecord> records,
        IReadOnlyList<RecordEpisodeGroupViewModel> episodes,
        DanmuDownloadRecord latest,
        int failedCount,
        int skippedCount,
        long totalBytes)
    {
        Key = key;
        Title = title;
        Records = records;
        Episodes = episodes;
        Latest = latest;
        FailedCount = failedCount;
        SkippedCount = skippedCount;
        TotalBytes = totalBytes;
    }

    public string Key { get; }
    public string Title { get; }
    public IReadOnlyList<DanmuDownloadRecord> Records { get; }
    public IReadOnlyList<RecordEpisodeGroupViewModel> Episodes { get; }
    public DanmuDownloadRecord Latest { get; }
    public int FailedCount { get; }
    public int SkippedCount { get; }
    public long TotalBytes { get; }
    public long LatestAt => Latest.CreatedAt;
    public int DownloadedEpisodeCount => Episodes.Count(episode => episode.Status == DownloadRecordStatus.Success);
    public string SummaryText => $"{Episodes.Count} 集 · {Records.Count} 条记录 · 最近 {LatestText}";
    public string DetailSummaryText => $"{Episodes.Count} 集 · {Records.Count} 条记录" + (string.IsNullOrWhiteSpace(TotalBytesText) ? string.Empty : $" · {TotalBytesText}");
    public string LatestText => DateTimeOffset.FromUnixTimeMilliseconds(LatestAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    public string TotalBytesText => TotalBytes switch
    {
        >= 1_048_576 => $"{TotalBytes / 1_048_576.0:0.#} MB",
        >= 1024 => $"{TotalBytes / 1024.0:0.#} KB",
        > 0 => $"{TotalBytes} B",
        _ => string.Empty,
    };
}

public sealed partial class RecordEpisodeGroupViewModel : ViewModelBase
{
    [ObservableProperty]
    private bool _isSelected;

    public RecordEpisodeGroupViewModel(
        int episodeNo,
        string title,
        DownloadRecordStatus status,
        int successfulFileCount,
        IReadOnlyList<DanmuDownloadRecord> records,
        long latestAt)
    {
        EpisodeNo = episodeNo;
        Title = title;
        Status = status;
        SuccessfulFileCount = successfulFileCount;
        Records = records;
        LatestAt = latestAt;
    }

    public int EpisodeNo { get; }
    public string Title { get; }
    public DownloadRecordStatus Status { get; }
    public string StatusLabel => Status.Label();
    public int SuccessfulFileCount { get; }
    public IReadOnlyList<DanmuDownloadRecord> Records { get; }
    public long LatestAt { get; }
    public string TimeText => DateTimeOffset.FromUnixTimeMilliseconds(LatestAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    public IReadOnlyList<long> RecordIds => Records.Select(record => record.Id).ToArray();
    public bool CanPreview => Records.Any(record => record.StatusEnum == DownloadRecordStatus.Success && record.FormatOrNull?.SupportsPreview() == true);
}

internal static class DanmuTitleExtensions
{
    public static string StripFromSuffix(this string raw) =>
        System.Text.RegularExpressions.Regex.Replace(raw ?? string.Empty, @"\s*from\s+.*$", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
}
