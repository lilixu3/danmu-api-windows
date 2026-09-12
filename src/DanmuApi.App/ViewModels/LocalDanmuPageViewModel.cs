using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DanmuApi.App.Services;
using DanmuApi.Core;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.App.ViewModels;

/// <summary>
/// 工具页「本地弹幕」分类：对齐核心 <c>/api/v2/local-danmu</c> 的上传/列表/删除，
/// 以及移动端 v1.0.5.101 的主要能力（文件名解析、批量导入、分组列表、详情、预览、删除、
/// SOURCE_ORDER 提示、管理员写门禁）。
///
/// 数据来源：先读核心落盘目录（快路径，见 <see cref="ILocalDanmuCacheReader"/>），
/// 快路径不可用时回退核心接口，并把原因写进诊断——不静默兜底。
/// </summary>
public sealed partial class LocalDanmuPageViewModel : ViewModelBase, IAsyncDisposable
{
    /// <summary>快照新鲜窗口：与移动端一致，避免每次进页面都打核心接口。</summary>
    private static readonly TimeSpan SnapshotFreshWindow = TimeSpan.FromSeconds(60);

    /// <summary>预览最多展示多少条弹幕（核心可能返回几十万条）。</summary>
    private const int PreviewLimit = 500;

    /// <summary>文件夹导入一次最多扫描多少个候选文件。</summary>
    private const int ImportScanLimit = 500;

    private static readonly string[] SupportedExtensions = [".xml", ".json", ".ass", ".ssa", ".csv", ".txt"];

    private readonly RuntimeApiContext _context;
    private readonly ICoreLocalDanmuClient _client;
    private readonly ILocalDanmuCacheReader _cacheReader;
    private readonly IDanmuApiClient _danmuClient;
    private readonly ICoreEnvClient _envClient;
    private readonly IUiDialogService _dialogs;
    private readonly IAdminWriteGate _writeGate;
    private readonly IAdminSessionService _adminSession;
    private readonly IAppDiagnostics _diagnostics;
    private readonly ShellNavigationAccessor _navigation;
    private readonly AppPaths _paths;
    private readonly SynchronizationContext? _uiContext;

    private IReadOnlyList<CoreLocalDanmuResource> _resources = [];
    private DateTimeOffset _snapshotAt = DateTimeOffset.MinValue;
    private string? _snapshotFingerprint;
    private CancellationTokenSource? _batchCancellation;
    private string? _sourceOrderValue;

    public LocalDanmuPageViewModel(
        RuntimeApiContext context,
        ICoreLocalDanmuClient client,
        ILocalDanmuCacheReader cacheReader,
        IDanmuApiClient danmuClient,
        ICoreEnvClient envClient,
        IUiDialogService dialogs,
        IAdminWriteGate writeGate,
        IAdminSessionService adminSession,
        IAppDiagnostics diagnostics,
        ShellNavigationAccessor navigation,
        AppPaths paths)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _cacheReader = cacheReader ?? throw new ArgumentNullException(nameof(cacheReader));
        _danmuClient = danmuClient ?? throw new ArgumentNullException(nameof(danmuClient));
        _envClient = envClient ?? throw new ArgumentNullException(nameof(envClient));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _writeGate = writeGate ?? throw new ArgumentNullException(nameof(writeGate));
        _adminSession = adminSession ?? throw new ArgumentNullException(nameof(adminSession));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _uiContext = SynchronizationContext.Current;
        FilterOptions =
        [
            new(LocalDanmuTypeFilter.All, "全部"),
            new(LocalDanmuTypeFilter.Tv, "电视剧"),
            new(LocalDanmuTypeFilter.Movie, "电影"),
        ];
        TypeOptions =
        [
            new(LocalDanmuTypes.Tv, "电视剧"),
            new(LocalDanmuTypes.Movie, "电影"),
        ];
        _selectedFilter = FilterOptions[0];
        _selectedUploadTypeOption = TypeOptions[0];
        SyncFilterChips();
        DetailDialog = new LocalDanmuDetailDialogViewModel(LoadPreviewAsync, DeleteFromDialogAsync);
        _context.RuntimeStateChanged += OnRuntimeStateChanged;
    }

    /// <summary>由外壳注入的页面跳转（管理员设置 / 核心配置）。</summary>
    public IReadOnlyList<LocalDanmuFilterChip> FilterOptions { get; }

    public IReadOnlyList<LocalDanmuTypeOption> TypeOptions { get; }

    [ObservableProperty]
    private IReadOnlyList<LocalDanmuGroupRow> _groups = [];

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private LocalDanmuFilterChip _selectedFilter;

    [ObservableProperty]
    private LocalDanmuTypeOption _selectedUploadTypeOption;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _diagnostic = string.Empty;

    [ObservableProperty]
    private LocalDanmuWriteAccess _writeAccess = LocalDanmuWriteAccess.ReadOnly;

    [ObservableProperty]
    private bool _isLocalSourceEnabled;

    [ObservableProperty]
    private bool _isSourceOrderKnown;

    // ── 上传表单（内联面板） ────────────────────────────────────────────
    [ObservableProperty]
    private bool _isUploadPanelOpen;

    [ObservableProperty]
    private string _uploadFilePath = string.Empty;

    [ObservableProperty]
    private string _uploadFileName = string.Empty;

    [ObservableProperty]
    private string _uploadTitle = string.Empty;

    [ObservableProperty]
    private int? _uploadYear = DateTimeOffset.Now.Year;

    [ObservableProperty]
    private int _uploadSeason = 1;

    [ObservableProperty]
    private string _uploadEpisodeText = "1";

    [ObservableProperty]
    private string _uploadNotes = string.Empty;

    // ── 批量导入（内联面板） ────────────────────────────────────────────
    [ObservableProperty]
    private bool _isBatchPanelOpen;

    [ObservableProperty]
    private bool _isBatchRunning;

    /// <summary>正在扫描导入目录：面板显示不确定进度条，避免文件多时看起来像卡死。</summary>
    [ObservableProperty]
    private bool _isScanningImport;

    /// <summary>批量导入进度（0..1）：按「已完成文件数 + 当前文件字节比例」推进。</summary>
    [ObservableProperty]
    private double _batchProgress;

    [ObservableProperty]
    private string _batchSummaryText = string.Empty;

    public ObservableCollection<LocalDanmuPendingItem> PendingItems { get; } = [];

    // ── 详情弹窗 ────────────────────────────────────────────────────────
    /// <summary>弹幕文件详情（弹窗形态）：列表独占整列高度，详情不再受窗口高度挤压。</summary>
    public LocalDanmuDetailDialogViewModel DetailDialog { get; }

    /// <summary>批量面板右下角那颗按钮：导入中它是「取消导入」，闲时它是「返回列表」。</summary>
    public string BatchActionText => IsBatchRunning ? "取消导入" : "返回列表";
    public bool HasFailedItems => PendingItems.Any(item => item.IsFailed);
    public bool CanSubmitBatch => !IsBatchRunning && !IsScanningImport && PendingItems.Count > 0;

    public bool CanWrite => WriteAccess == LocalDanmuWriteAccess.Writable;
    public bool IsReadOnly => WriteAccess != LocalDanmuWriteAccess.Writable;
    public bool NeedsAdminMode => WriteAccess == LocalDanmuWriteAccess.AdminRequired;
    public bool NeedsAdminConfiguration => WriteAccess == LocalDanmuWriteAccess.ReadOnly;

    public string WriteAccessText => WriteAccess switch
    {
        LocalDanmuWriteAccess.Writable => "可上传和删除本地弹幕",
        LocalDanmuWriteAccess.AdminRequired => "只读模式：上传和删除需要先进入管理员模式",
        _ => "只读模式：上传和删除需要配置 ADMIN_TOKEN，或在核心配置里开启 LOCAL_DANMU_NOT_REQUIRE_ADMIN",
    };

    public bool HasDiagnostic => Diagnostic.Length > 0;
    public bool HasGroups => Groups.Count > 0;
    public bool IsEmpty => !IsBusy && Groups.Count == 0;

    /// <summary>有资源但 SOURCE_ORDER 里没有 local：搜索/自动匹配不会命中这些弹幕。</summary>
    public bool ShowSourceOrderHint => IsSourceOrderKnown && !IsLocalSourceEnabled && _resources.Count > 0;
    public string SourceOrderHintText => _sourceOrderValue is { Length: > 0 } current
        ? $"当前 SOURCE_ORDER 是 {current}；把 local 加进去，核心才会在搜索和自动匹配时检索已导入的弹幕。"
        : "还没配置 SOURCE_ORDER；把 local 加进去，核心才会在搜索和自动匹配时检索已导入的弹幕。";

    public string StatsText => LocalDanmuFormatters.StatsSummary(
        Groups.Count,
        FileCount,
        Groups.Sum(group => group.Episodes.Sum(item => item.Resource.Size)));

    public int FileCount => Groups.Sum(group => group.Episodes.Count);
    public int CommentCount => Groups.Sum(group => group.Episodes.Sum(item => item.Resource.Count));
    public string SizeText => LocalDanmuFormatters.FormatBytes(
        Groups.Sum(group => group.Episodes.Sum(item => item.Resource.Size)));

    public bool HasUploadNotes => UploadNotes.Length > 0;

    /// <summary>落盘位置与「换运行目录不迁移」的提示（对齐移动端工作目录页的说明）。</summary>
    public string StorageHintText =>
        $"文件保存在 {Path.Combine(_paths.NodeProjectDirectory, ".cache", LocalDanmuCacheReader.CacheDirectoryName)}；" +
        "切换运行目录不会自动迁移，需要保留原目录或重新导入。清理缓存不会删除这些文件。";

    public string BatchProgressText => BatchSummaryText;

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(force: true);

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private void SelectFilter(LocalDanmuFilterChip? option)
    {
        if (option is not null)
        {
            SelectedFilter = option;
        }
    }

    [RelayCommand]
    private void ToggleGroup(LocalDanmuGroupRow? group)
    {
        if (group is not null)
        {
            group.IsExpanded = !group.IsExpanded;
        }
    }

    // ── 上传 ────────────────────────────────────────────────────────────
    [RelayCommand]
    private void ToggleUploadPanel()
    {
        if (IsUploadPanelOpen)
        {
            IsUploadPanelOpen = false;
            return;
        }

        ResetUploadForm();
        IsBatchPanelOpen = false;
        IsUploadPanelOpen = true;
    }

    [RelayCommand]
    private async Task PickUploadFileAsync()
    {
        var files = await _dialogs.PickFilesAsync(
            "选择弹幕文件（XML / JSON / ASS / SSA / CSV / TXT，单个不超过 10 MB）",
            [new UiFileFilter("弹幕文件", SupportedExtensions.Select(extension => "*" + extension).ToArray())]);
        if (files.Count == 0)
        {
            return;
        }

        ApplyUploadFile(files[0]);
    }

    [RelayCommand]
    private async Task SubmitUploadAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (UploadFilePath.Length == 0)
        {
            await _dialogs.ShowMessageAsync("上传本地弹幕", "请选择要上传的弹幕文件。", isError: true).ConfigureAwait(true);
            return;
        }

        var currentYear = DateTimeOffset.Now.Year;
        var validation = LocalDanmuValidation.ValidateTitle(UploadTitle)
            ?? LocalDanmuValidation.ValidateYear(UploadYear, currentYear)
            ?? LocalDanmuValidation.ValidateType(UploadType)
            ?? LocalDanmuValidation.ValidateSeason(UploadSeason)
            ?? LocalDanmuValidation.ValidateEpisode(UploadEpisode, UploadType == LocalDanmuTypes.Movie)
            ?? LocalDanmuValidation.ValidateFileSize(new FileInfo(UploadFilePath).Length);
        if (validation is not null)
        {
            await _dialogs.ShowMessageAsync("上传本地弹幕", validation, isError: true).ConfigureAwait(true);
            return;
        }

        if (!await EnsureWriteAccessAsync("上传本地弹幕").ConfigureAwait(true))
        {
            return;
        }

        var upload = new PendingUpload(
            UploadFilePath,
            UploadFileName,
            UploadTitle.Trim(),
            UploadYear!.Value,
            UploadType,
            UploadSeason,
            UploadEpisode);
        var attempt = await UploadOnceAsync(upload, progressTitle: "上传本地弹幕", interactive: true).ConfigureAwait(true);
        if (!attempt.Succeeded)
        {
            return;
        }

        IsUploadPanelOpen = false;
        ResetUploadForm();
        await LoadAsync(force: true).ConfigureAwait(true);
    }

    [RelayCommand]
    private void CloseUploadPanel()
    {
        IsUploadPanelOpen = false;
        ResetUploadForm();
    }

    // ── 批量导入 ────────────────────────────────────────────────────────
    [RelayCommand]
    private async Task ImportFolderAsync()
    {
        var folder = await _dialogs.PickFolderWithTitleAsync("选择弹幕文件所在文件夹", null).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        await BuildPendingItemsAsync(folder).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task PickFilesForBatchAsync()
    {
        var files = await _dialogs.PickFilesAsync(
            "选择多个弹幕文件（XML / JSON / ASS / SSA / CSV / TXT）",
            [new UiFileFilter("弹幕文件", SupportedExtensions.Select(extension => "*" + extension).ToArray())],
            allowMultiple: true).ConfigureAwait(true);
        if (files.Count == 0)
        {
            return;
        }

        BuildPendingItems(files);
    }

    [RelayCommand]
    private async Task SubmitBatchAsync()
    {
        if (IsBatchRunning || PendingItems.Count == 0)
        {
            return;
        }

        var currentYear = DateTimeOffset.Now.Year;
        var selected = PendingItems.Where(item => item.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            await _dialogs.ShowMessageAsync("批量导入", "没有勾选任何文件。", isError: true).ConfigureAwait(true);
            return;
        }

        var invalid = selected
            .Select(item => (item, error: item.Validate(currentYear)))
            .Where(pair => pair.error is not null)
            .ToArray();
        if (invalid.Length > 0)
        {
            await _dialogs.ShowMessageAsync(
                "批量导入",
                $"有 {invalid.Length} 条信息不完整：{invalid[0].item.FileName}（{invalid[0].error}）。请先补全或取消勾选。",
                isError: true).ConfigureAwait(true);
            return;
        }

        if (!await EnsureWriteAccessAsync("批量导入本地弹幕").ConfigureAwait(true))
        {
            return;
        }

        await RunBatchAsync(selected).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RetryFailedAsync()
    {
        if (IsBatchRunning)
        {
            return;
        }

        var failed = PendingItems.Where(item => item.IsFailed).ToArray();
        if (failed.Length == 0)
        {
            return;
        }

        foreach (var item in failed)
        {
            item.IsFailed = false;
            item.IsSelected = true;
        }

        await RunBatchAsync(failed).ConfigureAwait(true);
    }

    [RelayCommand]
    private void CancelBatch()
    {
        if (IsBatchRunning)
        {
            _batchCancellation?.Cancel();
            return;
        }

        IsBatchPanelOpen = false;
        PendingItems.Clear();
        BatchSummaryText = string.Empty;
    }

    /// <summary>打开某一集的详情弹窗。</summary>
    [RelayCommand]
    private async Task OpenDetailAsync(LocalDanmuEpisodeRow? row)
    {
        if (row is null)
        {
            return;
        }

        RefreshWriteAccess();
        DetailDialog.Show(row, CanWrite);
        await _dialogs.ShowLocalDanmuDetailAsync(DetailDialog).ConfigureAwait(true);
    }

    /// <summary>弹窗里点了预览：按 <c>local:&lt;resourceKey&gt;</c> 取核心既有 comment 接口。</summary>
    private async Task LoadPreviewAsync()
    {
        var resourceKey = DetailDialog.ResourceKey;
        if (resourceKey.Length == 0 || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            _context.EnsureRunning();
            var response = await _danmuClient.SendRawAsync(
                _context.Host,
                _context.Port!.Value,
                _context.Token,
                "getCommentByUrl",
                new Dictionary<string, string?>
                {
                    ["url"] = $"local:{resourceKey}",
                    ["format"] = "json",
                }).ConfigureAwait(true);
            var parsed = DanmuApiClient.ParseCommentJson(response.Body);
            DetailDialog.PreviewLines = parsed.Comments
                .Take(PreviewLimit)
                .Select(comment => new LocalDanmuPreviewLine(FormatTime(comment.TimeSeconds), comment.Text))
                .ToArray();
            DetailDialog.PreviewSummaryText = parsed.Count > PreviewLimit
                ? $"共 {parsed.Count} 条，预览前 {PreviewLimit} 条"
                : $"共 {parsed.Count} 条";
            DetailDialog.IsPreviewOpen = true;
            DetailDialog.Diagnostic = string.Empty;
        }
        catch (Exception error) when (error is DanmuApiException or InvalidDataException or IOException)
        {
            DetailDialog.Diagnostic = $"读取本地弹幕内容失败：{Describe(error)}";
            _diagnostics.Record("读取本地弹幕内容失败", error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>弹窗里点了删除：走与行内删除同一条确认 + 删除流程，成功后关闭弹窗。</summary>
    private async Task DeleteFromDialogAsync()
    {
        var resourceKey = DetailDialog.ResourceKey;
        var title = DetailDialog.Title;
        var episodeLabel = DetailDialog.EpisodeLabel;
        if (resourceKey.Length == 0)
        {
            return;
        }

        await DeleteKeysAsync(
            [resourceKey],
            "确认删除本地弹幕",
            $"将删除「{title} {episodeLabel}」。删除后如果仍需要，只能重新上传。").ConfigureAwait(true);
        DetailDialog.RequestClose();
    }

    // ── 删除 ────────────────────────────────────────────────────────────
    [RelayCommand]
    private Task DeleteEpisodeAsync(LocalDanmuEpisodeRow? row) =>
        row is null
            ? Task.CompletedTask
            : DeleteKeysAsync(
                [row.ResourceKey],
                "确认删除本地弹幕",
                $"将删除「{row.Title} {row.EpisodeLabel}」。删除后如果仍需要，只能重新上传。");

    [RelayCommand]
    private Task DeleteSeasonAsync(LocalDanmuGroupRow? group) =>
        group is null
            ? Task.CompletedTask
            : DeleteKeysAsync(
                group.ResourceKeys,
                "确认删除本地弹幕",
                $"将删除「{group.Title} {group.SubtitleText}」的 {group.Episodes.Count} 个文件，共 {group.CountText}。删除后如果仍需要，只能重新上传。");

    [RelayCommand]
    private Task DeleteTitleAsync(LocalDanmuGroupRow? group)
    {
        if (group is null)
        {
            return Task.CompletedTask;
        }

        // 整部 = 同一标题/年份/类型下的所有季（含当前列表里被筛选掉的）。
        var keys = _resources
            .Where(resource =>
                string.Equals(resource.Title, group.Title, StringComparison.Ordinal) &&
                resource.Year == group.Year &&
                string.Equals(resource.Type, group.Type, StringComparison.Ordinal))
            .Select(resource => resource.ResourceKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var count = keys.Length;
        return DeleteKeysAsync(
            keys,
            "确认批量删除",
            $"将删除「{group.Title}」的全部 {count} 个文件（含所有季）。删除后如果仍需要，只能重新上传。");
    }

    // ── 本地源开关（写核心 .env 的 SOURCE_ORDER，不直接改文件） ─────────
    [RelayCommand]
    private Task EnableLocalSourceFirstAsync() => UpdateSourceOrderAsync(prepend: true);

    [RelayCommand]
    private Task EnableLocalSourceLastAsync() => UpdateSourceOrderAsync(prepend: false);

    // ── 权限与导航 ──────────────────────────────────────────────────────
    [RelayCommand]
    private async Task OpenAdminSettingsAsync()
    {
        if (await _writeGate.EnsureAsync("本地弹幕的上传与删除").ConfigureAwait(true))
        {
            RefreshWriteAccess();
            Diagnostic = string.Empty;
        }
    }

    [RelayCommand]
    private void OpenCoreConfiguration() => _navigation.NavigateTo?.Invoke("configuration");

    public async Task LoadAsync(bool force)
    {
        if (IsBusy)
        {
            return;
        }

        var fingerprint = $"{_paths.NodeProjectDirectory}|{ReadVariant()}";
        if (!force &&
            _snapshotFingerprint == fingerprint &&
            DateTimeOffset.UtcNow - _snapshotAt < SnapshotFreshWindow &&
            _resources.Count > 0)
        {
            return;
        }

        IsBusy = true;
        Diagnostic = string.Empty;
        try
        {
            _context.EnsureRunning();
            RefreshWriteAccess();

            var cached = _cacheReader.Read(_paths.NodeProjectDirectory);
            if (cached.Diagnostic is { Length: > 0 } reason)
            {
                // 快路径不可用不是「静默降级」：原因要留在诊断里（活动页可见）。
                _diagnostics.Record($"本地弹幕缓存快路径未命中：{reason}");
            }

            if (cached.Available)
            {
                ApplyResources(cached.Resources);
            }
            else
            {
                var result = await _client
                    .ListAsync(_context.Host, _context.Port!.Value, _context.Token)
                    .ConfigureAwait(true);
                if (!result.Succeeded)
                {
                    _resources = [];
                    Rebuild();
                    Diagnostic = result.Diagnostic;
                    return;
                }

                ApplyResources(result.Resources);
            }

            _snapshotFingerprint = fingerprint;
            _snapshotAt = DateTimeOffset.UtcNow;
            await RefreshSourceOrderAsync().ConfigureAwait(true);
        }
        catch (DanmuApiException error)
        {
            _resources = [];
            Rebuild();
            Diagnostic = error.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyResources(IReadOnlyList<CoreLocalDanmuResource> resources)
    {
        _resources = resources;
        Rebuild();
    }

    /// <summary>按核心的规则重建分组：组键 = resourceKey 把集号段换成 all；组内集号升序、null 最后。</summary>
    private void Rebuild()
    {
        var culture = CultureInfo.GetCultureInfo("zh-CN");
        var filtered = _resources.Where(MatchesFilter).ToArray();
        var groups = new List<LocalDanmuGroupRow>();
        foreach (var group in filtered.GroupBy(GroupKeyOf, StringComparer.Ordinal))
        {
            var first = group.First();
            var episodes = group
                .OrderBy(resource => resource.Episode ?? int.MaxValue)
                .ThenBy(resource => resource.Filename, StringComparer.Ordinal)
                .Select(resource => new LocalDanmuEpisodeRow(
                    resource,
                    LocalDanmuFormatters.EpisodeLabel(resource.Episode, resource.Type),
                    LocalDanmuFormatters.EpisodeMeta(resource),
                    LocalDanmuFormatters.FormatUpdated(resource.UpdatedAt)))
                .ToArray();
            groups.Add(new LocalDanmuGroupRow(
                group.Key,
                first.Title,
                first.Year,
                first.Type,
                first.Season,
                episodes));
        }

        Groups = groups
            .OrderBy(group => group.Title, StringComparer.Create(culture, ignoreCase: true))
            .ThenByDescending(group => group.Year ?? 0)
            .ThenBy(group => group.Type, StringComparer.Ordinal)
            .ThenBy(group => group.Season)
            .ToArray();

        OnPropertyChanged(nameof(HasGroups));
        OnPropertyChanged(nameof(IsEmpty));
        // 统计带的四格分别绑在 Groups.Count 与这三个派生属性上：漏 raise 任何一个，
        // 就会出现「资源有数、文件/弹幕/占用还是 0」而右侧汇总（StatsText）却正常的现象。
        OnPropertyChanged(nameof(FileCount));
        OnPropertyChanged(nameof(CommentCount));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(StatsText));
        OnPropertyChanged(nameof(ShowSourceOrderHint));
    }

    /// <summary>组键 = 去掉 resourceKey 最后一段（集号）再补 all，等价于核心用 episode=null 重建的键。</summary>
    private static string GroupKeyOf(CoreLocalDanmuResource resource)
    {
        var key = resource.ResourceKey;
        var lastSeparator = key.LastIndexOf('|');
        return lastSeparator > 0 ? key[..lastSeparator] + "|all" : key + "|all";
    }

    private bool MatchesFilter(CoreLocalDanmuResource resource)
    {
        if (SelectedFilter.Value == LocalDanmuTypeFilter.Tv && resource.Type != LocalDanmuTypes.Tv)
        {
            return false;
        }

        if (SelectedFilter.Value == LocalDanmuTypeFilter.Movie && resource.Type != LocalDanmuTypes.Movie)
        {
            return false;
        }

        var keyword = SearchText.Trim();
        if (keyword.Length == 0)
        {
            return true;
        }

        return resource.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               resource.Filename.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               LocalDanmuFormatters.EpisodeLabel(resource.Episode, resource.Type)
                   .Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               (resource.Episode?.ToString(CultureInfo.InvariantCulture) ?? string.Empty) == keyword;
    }

    private void RefreshWriteAccess()
    {
        _adminSession.Refresh();
        var state = _adminSession.State;
        var relaxed = ReadLocalDanmuRelaxedFlag();
        WriteAccess = state.IsAdminMode || relaxed
            ? LocalDanmuWriteAccess.Writable
            : state.HasAdminTokenConfigured
                ? LocalDanmuWriteAccess.AdminRequired
                : LocalDanmuWriteAccess.ReadOnly;
        OnPropertyChanged(nameof(CanWrite));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(NeedsAdminMode));
        OnPropertyChanged(nameof(NeedsAdminConfiguration));
        OnPropertyChanged(nameof(WriteAccessText));
    }

    /// <summary>核心侧的放宽开关：LOCAL_DANMU_NOT_REQUIRE_ADMIN=true 时普通 TOKEN 也能写。</summary>
    private bool ReadLocalDanmuRelaxedFlag()
    {
        try
        {
            var value = DotEnvFile.ReadValue(
                Path.Combine(_paths.NodeProjectDirectory, "config", ".env"),
                "LOCAL_DANMU_NOT_REQUIRE_ADMIN");
            return value is not null &&
                   value.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _diagnostics.Record("读取 LOCAL_DANMU_NOT_REQUIRE_ADMIN 失败", error);
            return false;
        }
    }

    private async Task<bool> EnsureWriteAccessAsync(string action)
    {
        RefreshWriteAccess();
        if (CanWrite)
        {
            return true;
        }

        if (NeedsAdminMode)
        {
            return await _writeGate.EnsureAsync(action).ConfigureAwait(true);
        }

        await _dialogs.ShowMessageAsync(
            "需要写权限",
            $"{WriteAccessText}。可以先进入管理员模式，或在核心配置里把 LOCAL_DANMU_NOT_REQUIRE_ADMIN 设为 true。",
            isError: true).ConfigureAwait(true);
        return false;
    }

    /// <summary>
    /// 上传一个文件。<paramref name="interactive"/> = true 走单文件那套：进度弹窗 + 成功/失败提示。
    /// 批量导入必须传 false —— 否则 N 个文件会弹 N 次窗（用户实测反馈），批量进度改为在面板里用进度条展示。
    /// </summary>
    private async Task<UploadAttempt> UploadOnceAsync(
        PendingUpload upload,
        string progressTitle,
        bool interactive,
        IProgress<double>? fileFraction = null)
    {
        CoreLocalDanmuResourceResult? result = null;

        async Task RunAsync(IProgress<CoreInstallProgress> progress, CancellationToken token)
        {
            await using var stream = new FileStream(upload.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = stream.Length;
            var request = new CoreLocalDanmuUploadRequest(
                upload.Title,
                upload.Year,
                upload.Type,
                upload.Season,
                upload.Episode,
                upload.FileName,
                stream,
                length);
            // CoreInstallProgress 是安装流程的进度契约，这里借它的字节/文案字段展示上传进度
            // （ProgressDialogWindow 只渲染 Detail 与字节数，不映射阶段枚举）。命名债记在 HANDOFF。
            var sent = new Progress<long>(bytes =>
            {
                fileFraction?.Report(length > 0 ? Math.Clamp((double)bytes / length, 0, 1) : 0);
                progress.Report(new CoreInstallProgress(
                    CoreInstallStage.Downloading,
                    $"正在上传 {upload.FileName}",
                    bytes,
                    length));
            });
            result = await _client
                .UploadAsync(_context.Host, _context.Port!.Value, _context.Token, _context.AdminToken(), request, sent, token)
                .ConfigureAwait(false);
        }

        if (interactive)
        {
            var outcome = await _dialogs.RunWithProgressDialogAsync(progressTitle, RunAsync).ConfigureAwait(true);
            if (outcome.Outcome == ProgressOperationOutcome.Canceled)
            {
                return UploadAttempt.CanceledAttempt;
            }

            if (result is null)
            {
                var diagnostic = outcome.Diagnostic ?? "上传没有返回结果";
                Diagnostic = diagnostic;
                await _dialogs.ShowMessageAsync("上传本地弹幕", diagnostic, isError: true).ConfigureAwait(true);
                return new UploadAttempt(false, diagnostic);
            }
        }
        else
        {
            await RunAsync(NullProgress.Instance, CancellationToken.None).ConfigureAwait(true);
            if (result is null)
            {
                return new UploadAttempt(false, "上传没有返回结果");
            }
        }

        if (!result.Succeeded)
        {
            Diagnostic = result.Diagnostic;
            if (interactive)
            {
                await _dialogs.ShowMessageAsync("上传本地弹幕", result.Diagnostic, isError: true).ConfigureAwait(true);
            }

            return new UploadAttempt(false, result.Diagnostic);
        }

        Diagnostic = string.Empty;
        if (interactive)
        {
            await _dialogs.ShowMessageAsync(
                "上传本地弹幕",
                $"上传成功：{result.Resource!.Title} {LocalDanmuFormatters.EpisodeLabel(result.Resource.Episode, result.Resource.Type)} · {result.Resource.Count} 条弹幕",
                isError: false).ConfigureAwait(true);
        }

        return new UploadAttempt(true, null);
    }

    private async Task BuildPendingItemsAsync(string folder)
    {
        // 扫描与文件名解析放到线程池：目录里文件多时同步枚举会把 UI 冻住，
        // 用户会以为程序卡死（实测反馈）。扫描期间面板先打开并显示不确定进度条。
        IsBatchPanelOpen = true;
        IsScanningImport = true;
        BatchSummaryText = "正在扫描目录…";
        PendingItems.Clear();
        OnPropertyChanged(nameof(CanSubmitBatch));
        try
        {
            var scan = await Task.Run(() => ScanFolder(folder)).ConfigureAwait(true);
            if (scan.Error is { Length: > 0 } error)
            {
                Diagnostic = error;
                BatchSummaryText = string.Empty;
                IsBatchPanelOpen = false;
                await _dialogs.ShowMessageAsync("批量导入", error, isError: true).ConfigureAwait(true);
                return;
            }

            if (scan.Files.Count == 0)
            {
                BatchSummaryText = string.Empty;
                IsBatchPanelOpen = false;
                await _dialogs.ShowMessageAsync(
                    "批量导入",
                    $"这个文件夹里没有受支持且不超过 10 MB 的弹幕文件（{string.Join(" / ", SupportedExtensions)}）。",
                    isError: true).ConfigureAwait(true);
                return;
            }

            BuildPendingItems(scan.Files);
            BatchSummaryText = scan.Truncated
                ? $"共 {PendingItems.Count} 个文件（目录里更多，只取了前 {ImportScanLimit} 个）"
                : $"共 {PendingItems.Count} 个文件";
        }
        finally
        {
            IsScanningImport = false;
            OnPropertyChanged(nameof(CanSubmitBatch));
        }
    }

    private static (IReadOnlyList<string> Files, bool Truncated, string? Error) ScanFolder(string folder)
    {
        try
        {
            var all = Directory
                .EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(path => SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .Where(path => !Path.GetFileName(path).StartsWith('.'))
                .Where(path => new FileInfo(path).Length <= LocalDanmuValidation.MaximumFileBytes)
                .ToArray();
            var truncated = all.Length > ImportScanLimit;
            return (truncated ? all.Take(ImportScanLimit).ToArray() : all, truncated, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return ([], false, $"读取文件夹失败：{error.Message}");
        }
    }

    private void BuildPendingItems(IReadOnlyList<string> files)
    {
        var currentYear = DateTimeOffset.Now.Year;
        PendingItems.Clear();
        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            PendingItems.Add(new LocalDanmuPendingItem(file, fileName, LocalDanmuFilenameParser.Guess(fileName, currentYear)));
        }

        IsUploadPanelOpen = false;
        IsBatchPanelOpen = true;
    }

    private async Task RunBatchAsync(IReadOnlyList<LocalDanmuPendingItem> items)
    {
        IsBatchRunning = true;
        _batchCancellation = new CancellationTokenSource();
        BatchProgress = 0;
        var succeeded = 0;
        var failed = 0;
        string? firstFailure = null;
        var canceled = false;
        try
        {
            for (var index = 0; index < items.Count; index++)
            {
                if (_batchCancellation.IsCancellationRequested)
                {
                    canceled = true;
                    break;
                }

                var item = items[index];
                BatchSummaryText = $"正在导入 {index + 1}/{items.Count} · 成功 {succeeded} · 失败 {failed}";
                item.StatusText = "上传中";
                var upload = new PendingUpload(
                    item.FilePath,
                    item.FileName,
                    item.Title.Trim(),
                    item.Year!.Value,
                    item.Type,
                    item.Season,
                    item.Episode);
                // 批量路径不弹窗（每个文件弹一次进度窗/成功窗正是用户反馈的问题）：
                // 进度走面板里的进度条，失败逐条标记在行上。
                var attempt = await UploadOnceAsync(
                    upload,
                    progressTitle: string.Empty,
                    interactive: false,
                    fileFraction: new Progress<double>(fraction =>
                        BatchProgress = (index + fraction) / items.Count)).ConfigureAwait(true);
                if (attempt.Succeeded)
                {
                    succeeded++;
                    item.StatusText = "已导入";
                    item.IsFailed = false;
                }
                else
                {
                    failed++;
                    item.IsFailed = true;
                    item.StatusText = "失败";
                    firstFailure ??= attempt.Diagnostic;
                }

                BatchProgress = (double)(index + 1) / items.Count;
                OnPropertyChanged(nameof(HasFailedItems));
            }

            BatchSummaryText = BuildBatchSummary(succeeded, failed, canceled);
            // 只要有成功的就刷新列表；失败项留在面板里，方便「重试失败」。
            if (succeeded > 0)
            {
                await LoadAsync(force: true).ConfigureAwait(true);
            }

            if (canceled)
            {
                await _dialogs.ShowMessageAsync("批量导入", BatchSummaryText, isError: failed > 0).ConfigureAwait(true);
                return;
            }

            // 全部成功就直接回到列表（用户反馈：成功后停在导入界面、必须点取消才能回去）。
            if (failed == 0)
            {
                IsBatchPanelOpen = false;
                PendingItems.Clear();
                BatchSummaryText = string.Empty;
                await _dialogs.ShowMessageAsync("批量导入", $"导入完成：成功 {succeeded} 个，已回到列表。").ConfigureAwait(true);
                return;
            }

            var message = firstFailure is null
                ? $"导入完成：成功 {succeeded} 个，失败 {failed} 个。可以点「重试失败」再试。"
                : $"导入完成：成功 {succeeded} 个，失败 {failed} 个。可以点「重试失败」再试。\n首个失败原因：{firstFailure}";
            await _dialogs.ShowMessageAsync("批量导入", message, isError: true).ConfigureAwait(true);
        }
        finally
        {
            IsBatchRunning = false;
            _batchCancellation.Dispose();
            _batchCancellation = null;
            OnPropertyChanged(nameof(BatchActionText));
            OnPropertyChanged(nameof(HasFailedItems));
        }
    }

    private static string BuildBatchSummary(int succeeded, int failed, bool canceled)
    {
        var prefix = canceled ? "已取消导入" : "批量导入完成";
        return failed == 0
            ? $"{prefix}：成功 {succeeded} 个"
            : $"{prefix}：成功 {succeeded} 个，失败 {failed} 个（可重试失败项）";
    }

    private async Task DeleteKeysAsync(IReadOnlyList<string> keys, string title, string message)
    {
        if (keys.Count == 0 || IsBusy)
        {
            return;
        }

        if (!await EnsureWriteAccessAsync("删除本地弹幕").ConfigureAwait(true))
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync(title, message, "确认删除").ConfigureAwait(true))
        {
            return;
        }

        string? failure = null;
        var succeeded = 0;
        var outcome = await _dialogs.RunWithProgressDialogAsync("删除本地弹幕", async (progress, token) =>
        {
            for (var index = 0; index < keys.Count; index++)
            {
                progress.Report(new CoreInstallProgress(
                    CoreInstallStage.Downloading,
                    $"正在删除 {index + 1}/{keys.Count}",
                    0,
                    0));
                var result = await _client
                    .DeleteAsync(_context.Host, _context.Port!.Value, _context.Token, _context.AdminToken(), keys[index], token)
                    .ConfigureAwait(false);
                if (result.Succeeded)
                {
                    succeeded++;
                }
                else
                {
                    failure ??= result.Diagnostic;
                }
            }
        }).ConfigureAwait(true);

        if (outcome.Outcome == ProgressOperationOutcome.Canceled)
        {
            return;
        }

        if (succeeded == 0)
        {
            Diagnostic = failure ?? outcome.Diagnostic ?? "删除失败，请稍后重试";
            _diagnostics.Record(Diagnostic);
            await _dialogs.ShowMessageAsync("删除本地弹幕", Diagnostic, isError: true).ConfigureAwait(true);
            return;
        }

        Diagnostic = failure is null ? string.Empty : $"部分删除失败：{failure}";
        await _dialogs.ShowMessageAsync(
            "删除本地弹幕",
            failure is null ? $"已删除 {succeeded} 个文件" : $"已删除 {succeeded} 个文件，另有失败：{failure}",
            isError: failure is not null).ConfigureAwait(true);
        await LoadAsync(force: true).ConfigureAwait(true);
    }

    private async Task RefreshSourceOrderAsync()
    {
        try
        {
            var result = await _envClient
                .ReadConfigValueAsync(_context.Host, _context.Port!.Value, _context.Token, _context.AdminToken(), "SOURCE_ORDER")
                .ConfigureAwait(true);
            if (!result.Succeeded)
            {
                IsSourceOrderKnown = false;
                _diagnostics.Record($"读取 SOURCE_ORDER 失败：{result.Diagnostic}");
                return;
            }

            _sourceOrderValue = result.Value?.Trim();
            IsLocalSourceEnabled = (_sourceOrderValue ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains("local", StringComparer.OrdinalIgnoreCase);
            IsSourceOrderKnown = true;
            OnPropertyChanged(nameof(ShowSourceOrderHint));
            OnPropertyChanged(nameof(SourceOrderHintText));
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or DanmuApiException)
        {
            IsSourceOrderKnown = false;
            _diagnostics.Record("读取 SOURCE_ORDER 失败", error);
        }
    }

    private async Task UpdateSourceOrderAsync(bool prepend)
    {
        if (IsBusy)
        {
            return;
        }

        var current = (_sourceOrderValue ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.Equals(item, "local", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (prepend)
        {
            current.Insert(0, "local");
        }
        else
        {
            current.Add("local");
        }

        var next = string.Join(',', current);
        if (!await _writeGate.EnsureAsync("修改核心的 SOURCE_ORDER").ConfigureAwait(true))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _envClient
                .SetAsync(_context.Host, _context.Port!.Value, _context.Token, _context.AdminToken(), "SOURCE_ORDER", next)
                .ConfigureAwait(true);
            if (!result.Succeeded)
            {
                Diagnostic = result.Diagnostic;
                await _dialogs.ShowMessageAsync("修改 SOURCE_ORDER", result.Diagnostic, isError: true).ConfigureAwait(true);
                return;
            }

            await _dialogs.ShowMessageAsync("修改 SOURCE_ORDER", $"已启用本地弹幕源：{next}").ConfigureAwait(true);
            await RefreshSourceOrderAsync().ConfigureAwait(true);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or DanmuApiException)
        {
            Diagnostic = $"写入 SOURCE_ORDER 失败：{error.Message}";
            _diagnostics.Record("写入 SOURCE_ORDER 失败", error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyUploadFile(string path)
    {
        var info = new FileInfo(path);
        var sizeError = LocalDanmuValidation.ValidateFileSize(info.Exists ? info.Length : null);
        if (sizeError is not null)
        {
            _ = _dialogs.ShowMessageAsync("上传本地弹幕", $"{sizeError}：{info.Name}", isError: true);
            return;
        }

        UploadFilePath = path;
        UploadFileName = info.Name;
        var guess = LocalDanmuFilenameParser.Guess(info.Name, DateTimeOffset.Now.Year);
        UploadTitle = guess.Title;
        UploadYear = guess.Year ?? DateTimeOffset.Now.Year;
        SelectedUploadTypeOption = TypeOptions.First(option => option.Value == guess.Type);
        UploadSeason = guess.Season ?? 1;
        UploadEpisodeText = guess.Episode?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        UploadNotes = guess.Notes.Count == 0 ? string.Empty : string.Join("；", guess.Notes);
    }

    private void ResetUploadForm()
    {
        UploadFilePath = string.Empty;
        UploadFileName = string.Empty;
        UploadTitle = string.Empty;
        UploadYear = DateTimeOffset.Now.Year;
        SelectedUploadTypeOption = TypeOptions[0];
        UploadSeason = 1;
        UploadEpisodeText = "1";
        UploadNotes = string.Empty;
    }

    public string UploadType => SelectedUploadTypeOption.Value;

    public int? UploadEpisode =>
        int.TryParse(UploadEpisodeText?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : null;

    /// <summary>快照指纹里的核心变体（切变体/换运行目录都让快照失效，与移动端一致）。</summary>
    private string ReadVariant()
    {
        try
        {
            return DotEnvFile.ReadValue(Path.Combine(_paths.NodeProjectDirectory, "config", ".env"), "DANMU_API_VARIANT")
                   ?? "stable";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _diagnostics.Record("本地弹幕读取核心变体失败", error);
            return "unknown";
        }
    }

    private void OnRuntimeStateChanged(object? sender, RuntimeSnapshot snapshot) =>
        RunOnUi(() =>
        {
            RefreshWriteAccess();
            OnPropertyChanged(nameof(StorageHintText));
        });

    private void RunOnUi(Action action)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
        {
            action();
            return;
        }

        _uiContext.Post(_ => action(), null);
    }

    partial void OnSearchTextChanged(string value) => Rebuild();

    partial void OnSelectedFilterChanged(LocalDanmuFilterChip value)
    {
        SyncFilterChips();
        Rebuild();
    }

    private void SyncFilterChips()
    {
        foreach (var chip in FilterOptions)
        {
            chip.IsActive = chip.Value == SelectedFilter.Value;
        }
    }

    partial void OnDiagnosticChanged(string value) => OnPropertyChanged(nameof(HasDiagnostic));

    partial void OnIsBatchRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(BatchActionText));
        OnPropertyChanged(nameof(CanSubmitBatch));
    }

    partial void OnIsScanningImportChanged(bool value) => OnPropertyChanged(nameof(CanSubmitBatch));

    partial void OnIsBatchPanelOpenChanged(bool value) => OnPropertyChanged(nameof(CanSubmitBatch));

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        RefreshCommand.NotifyCanExecuteChanged();
    }

    partial void OnUploadEpisodeTextChanged(string value) => OnPropertyChanged(nameof(UploadEpisode));

    partial void OnSelectedUploadTypeOptionChanged(LocalDanmuTypeOption value)
    {
        OnPropertyChanged(nameof(UploadType));
        OnPropertyChanged(nameof(UploadEpisode));
    }

    partial void OnUploadNotesChanged(string value) => OnPropertyChanged(nameof(HasUploadNotes));

    public ValueTask DisposeAsync()
    {
        _context.RuntimeStateChanged -= OnRuntimeStateChanged;
        _batchCancellation?.Cancel();
        _batchCancellation?.Dispose();
        _batchCancellation = null;
        return ValueTask.CompletedTask;
    }

    private static string FormatTime(double seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return duration.TotalHours >= 1
            ? duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    private static string Describe(Exception error) =>
        string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message;

    /// <summary>一次上传的输入快照（表单与批量项都归一到这个结构）。</summary>
    private sealed record PendingUpload(
        string FilePath,
        string FileName,
        string Title,
        int Year,
        string Type,
        int Season,
        int? Episode);

    private sealed record UploadAttempt(bool Succeeded, string? Diagnostic, bool Canceled = false)
    {
        public static UploadAttempt CanceledAttempt { get; } = new(false, null, true);
    }

    /// <summary>批量路径不需要进度弹窗的进度对象，但上传流程要求一个 <see cref="IProgress{T}"/>。</summary>
    private sealed class NullProgress : IProgress<CoreInstallProgress>
    {
        public static NullProgress Instance { get; } = new();
        public void Report(CoreInstallProgress value) { }
    }
}
