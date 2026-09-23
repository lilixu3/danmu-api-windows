using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DanmuApi.App.Services;
using DanmuApi.Core;
using DanmuApi.Platform;

namespace DanmuApi.App.ViewModels;

public enum CorePageMode
{
    Overview,
    Commits,
    PullRequests,
    History,
}

/// <summary>
/// PR 列表的一行。队列位次是行的状态而不是列表的状态：放进这里，
/// 行模板就能直接绑定「已选第几个 / 加入还是移出」，不必用转换器去查字典。
/// </summary>
public sealed partial class PullRequestRow(GithubPullRequest pullRequest) : ObservableObject
{
    public GithubPullRequest PullRequest { get; } = pullRequest ?? throw new ArgumentNullException(nameof(pullRequest));
    public int Number => PullRequest.Number;
    public string Title => PullRequest.Title;
    public string HeadRepository => PullRequest.HeadRepository;

    [ObservableProperty]
    private int _queuePosition;

    /// <summary>该 PR 已经在当前已安装的本地组合里（移动端同样在列表行上标"已包含"）。</summary>
    [ObservableProperty]
    private bool _isIncludedInCore;

    public bool IsQueued => QueuePosition > 0;
    public bool CanQueue => !IsIncludedInCore;
    public string QueueActionText => IsQueued ? "移出队列" : "加入队列";
    public string QueueBadgeText => IsQueued ? $"队列第 {QueuePosition} 位" : "未加入队列";
    /// <summary>已并入当前核心的行不再让用户排队：重复并入同一个 PR 没有意义。</summary>
    public string InclusionBadgeText => "已并入当前核心";

    partial void OnQueuePositionChanged(int value)
    {
        OnPropertyChanged(nameof(IsQueued));
        OnPropertyChanged(nameof(QueueActionText));
        OnPropertyChanged(nameof(QueueBadgeText));
        OnPropertyChanged(nameof(CanQueue));
    }

    partial void OnIsIncludedInCoreChanged(bool value)
    {
        OnPropertyChanged(nameof(CanQueue));
    }
}

/// <summary>运行变体的展示信息。图标、上游仓库与一行说明在这里声明，供变体卡片在不展开的情况下
/// 说明「这个变体是什么」，并为每个变体建立类型层面的身份标识——GUI 自动化选中它之后会回读
/// IsSelected，从而确认切换真的生效。</summary>
public sealed partial class CoreVariantOption : ObservableObject
{
    public CoreVariantOption(ManagedCoreVariant value, string label, string icon, string repository, string summary)
    {
        Value = value;
        Label = label;
        Icon = icon;
        Repository = repository;
        Summary = summary;
    }

    public ManagedCoreVariant Value { get; }
    public string Label { get; }
    public string Icon { get; }
    public string Repository { get; }
    public string Summary { get; }

    [ObservableProperty]
    private bool _isSelected;

    public override string ToString() => Label;
}

/// <summary>
/// 核心页：所有操作提示统一走弹窗（进度弹窗 + 结果弹窗），不再使用常驻横幅。
/// 线路选择只在未确认或线路类失败时弹出；取消永远不改动磁盘。
/// </summary>
public sealed partial class CorePageViewModel : ViewModelBase
{
    private readonly Func<ManagedCoreVariant, CancellationToken, Task<CoreDependencyHealth>>? _verifyDependencies;
    private readonly ICoreManagementService _management;
    private readonly IGithubCoreRemote _remote;
    private readonly IGithubRoutePreferenceStore _routePreferences;
    private readonly IGithubProxySpeedTester _speedTester;
    private readonly ICoreUpdateScheduler _updateScheduler;
    private readonly IUiDialogService _dialogService;
    private readonly IAppDiagnostics _diagnostics;
    private readonly IGithubTokenConfigurationService _githubTokenConfiguration;
    private readonly SynchronizationContext? _uiContext;
    private readonly RuntimePreparationService? _preparation;
    private readonly ICorePullRequestManagementService? _pullRequestManagement;
    private readonly IActiveCoreVariantStore? _activeVariant;
    private readonly Func<ManagedCoreVariant, CancellationToken, Task<RuntimeVariantSwitchResult>>? _runtimeVariantSwitch;
    private readonly List<GithubPullRequest> _selectedPullRequests = [];
    private CoreDependencyHealth _coreDependencyHealth = CoreDependencyHealth.Unknown;
    private int _preparationUpdateQueued;
    private CoreInstallationInfo _installation;
    private CoreUpdateCheckResult? _updateResult;
    /// <summary>记录 <see cref="_updateResult"/> 是哪一次检查时、对着哪个已安装提交得出的结论。</summary>
    private string? _updateResultCommit;
    private bool _isBusy;

    /// <summary>「安装后切换」开关：与移动端 PR 实验室的构建对话框同义，默认开启。</summary>
    [ObservableProperty]
    private bool _activateAfterBuild = true;

    [ObservableProperty]
    private ManagedCoreVariant _selectedVariant = ManagedCoreVariant.Stable;

    [ObservableProperty]
    private CoreVariantOption _selectedVariantOption = null!;

    [ObservableProperty]
    private bool _isDependencyMaintenanceExpanded;

    [ObservableProperty]
    private CorePageMode _pageMode = CorePageMode.Overview;

    [ObservableProperty]
    private string _repositoryText = "https://github.com/owner/repository";

    [ObservableProperty]
    private string _branchText = string.Empty;

    [ObservableProperty]
    private string _displayName = "官方核心";

    [ObservableProperty]
    private IReadOnlyList<GithubBranch> _branches = [];

    [ObservableProperty]
    private GithubBranch? _selectedBranch;

    [ObservableProperty]
    private IReadOnlyList<GithubCommit> _commits = [];

    [ObservableProperty]
    private IReadOnlyList<GithubPullRequest> _pullRequests = [];

    /// <summary>带队列状态的行模型，供 PR 实验室列表绑定。</summary>
    public IReadOnlyList<PullRequestRow> PullRequestRows
    {
        get
        {
            var merged = MergedPullRequestNumbers.ToHashSet();
            return PullRequests.Select(item => new PullRequestRow(item)
            {
                QueuePosition = PullRequestQueuePosition(item),
                IsIncludedInCore = merged.Contains(item.Number),
            }).ToArray();
        }
    }

    [ObservableProperty]
    private IReadOnlyList<CoreVersionRecord> _history = [];

    [ObservableProperty]
    private bool _hasCommitNextPage;

    [ObservableProperty]
    private bool _hasPullRequestNextPage;

    public CorePageViewModel(
        ICoreManagementService management,
        IGithubCoreRemote remote,
        IGithubRoutePreferenceStore routePreferences,
        IGithubProxySpeedTester speedTester,
        ICoreUpdateScheduler updateScheduler,
        IUiDialogService dialogService,
        IAppDiagnostics diagnostics,
        IGithubTokenStore tokenStore)
        : this(
            management,
            remote,
            routePreferences,
            speedTester,
            updateScheduler,
            dialogService,
            diagnostics,
            new GithubTokenConfigurationService(tokenStore, remote, diagnostics))
    {
    }

    public CorePageViewModel(
        ICoreManagementService management,
        IGithubCoreRemote remote,
        IGithubRoutePreferenceStore routePreferences,
        IGithubProxySpeedTester speedTester,
        ICoreUpdateScheduler updateScheduler,
        IUiDialogService dialogService,
        IAppDiagnostics diagnostics,
        IGithubTokenConfigurationService githubTokenConfiguration,
        Func<ManagedCoreVariant, CancellationToken, Task<CoreDependencyHealth>>? verifyDependencies = null,
        RuntimePreparationService? preparation = null,
        ICorePullRequestManagementService? pullRequestManagement = null,
        IActiveCoreVariantStore? activeVariant = null,
        Func<ManagedCoreVariant, CancellationToken, Task<RuntimeVariantSwitchResult>>? runtimeVariantSwitch = null)
    {
        _verifyDependencies = verifyDependencies;
        _management = management ?? throw new ArgumentNullException(nameof(management));
        // 托盘「立即更新核心」和后台自动更新都不经过本页，订阅安装状态变更后
        // 版本号、提交与安装时间才会在更新完成的瞬间自动回读，不需要用户手动刷新。
        _management.InstallationChanged += OnInstallationChanged;
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _routePreferences = routePreferences ?? throw new ArgumentNullException(nameof(routePreferences));
        _speedTester = speedTester ?? throw new ArgumentNullException(nameof(speedTester));
        _updateScheduler = updateScheduler ?? throw new ArgumentNullException(nameof(updateScheduler));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _githubTokenConfiguration = githubTokenConfiguration ?? throw new ArgumentNullException(nameof(githubTokenConfiguration));
        _pullRequestManagement = pullRequestManagement;
        _activeVariant = activeVariant;
        _runtimeVariantSwitch = runtimeVariantSwitch;
        _uiContext = SynchronizationContext.Current;
        if (preparation is not null)
        {
            _preparation = preparation;
            preparation.Changed += OnPreparationChanged;
        }

        VariantOptions =
        [
            new(ManagedCoreVariant.Stable, "稳定核心", "\uE950", "huangxd-/danmu_api", "官方主线，跟随上游发布"),
            new(ManagedCoreVariant.Dev, "开发核心", "\uE896", "lilixu3/danmu_api", "开发上游，新特性验证用"),
            new(ManagedCoreVariant.Custom, "自定义核心", "\uE943", "自选仓库", "自选仓库的提交或本地 PR 组合"),
        ];
        SyncVariantSelection();
        _selectedVariantOption = VariantOptions[0];
        _installation = _management.Inspect(_selectedVariant);
        _history = _management.GetHistory(_selectedVariant);
    }

    public IReadOnlyList<CoreVariantOption> VariantOptions { get; }
    public CoreInstallationInfo Installation => _installation;
    public CoreInstallationManifest? Manifest => _installation.Manifest;
    public bool IsInstalled => _installation.IsInstalled && _installation.IsValid && _installation.Manifest is not null;
    public bool IsNotInstalled => !IsInstalled;
    public bool IsBusy => _isBusy;
    /// <summary>
    /// 缓存里的远端提交是否就是当前已安装的提交。更新刚应用完成时两者相同：这时不能再显示
    /// 「有新版本」，否则用户必须手动再检查一次界面才会恢复正常。
    /// </summary>
    private bool RemoteMatchesInstalled => _updateResult?.Remote is { Sha.Length: > 0 } remote &&
        Manifest is { CommitSha.Length: > 0 } manifest &&
        (manifest.CommitSha.StartsWith(remote.Sha, StringComparison.OrdinalIgnoreCase) ||
         remote.Sha.StartsWith(manifest.CommitSha, StringComparison.OrdinalIgnoreCase));
    public bool HasUpdate => _updateResult is { Status: CoreUpdateCheckStatus.Checked, UpdateAvailable: true } && !RemoteMatchesInstalled;
    public string VariantLabel => SelectedVariant.ToLabel();
    public string InstallationStatusText => !Installation.IsInstalled
        ? "尚未安装"
        : !Installation.IsValid
            ? "安装不完整"
            : "已安装";
    public string InstallationDetailText => Installation.Diagnostic
        ?? (Manifest is null ? "缺少来源 manifest，需重新安装" : "来源和入口校验通过");
    public string RepositoryDisplay => Manifest?.Repository ??
        SelectedVariant.DefaultRepositoryFullName() ?? "尚未配置";
    public string BranchDisplay => Manifest?.Branch ?? "—";
    public string CommitDisplay => Manifest?.CommitSha ?? "—";
    public string VersionDisplay => Manifest?.Version ?? Installation.Version ?? "未知";
    public string InstalledAtDisplay => Manifest?.InstalledAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";
    /// <summary>有更新时显示的胶囊文案：目标提交的短 SHA，与更新详情里的对照一致。</summary>
    public string UpdateLabelText => _updateResult?.Remote is { } remote
        ? $"发现 {remote.ShortSha}"
        : "发现新版本";
    public string DisplayNameValue => Manifest?.DisplayName ?? DisplayName;
    /// <summary>身份带第一行（眉标）：所选变体的安装状态、名称、来源仓库，用 · 连成一句。
    /// 必须是一个字符串：拆成多个控件放进横向 StackPanel 时子控件拿不到宽度约束，
    /// 窄窗口下会直接压到右边的版本号上（TextTrimming 不会生效）。</summary>
    public string IdentityEyebrow => string.Join(" · ",
        new[] { IsInstalled ? "已安装" : "尚未安装", VariantLabel, DisplayNameValue, IsInstalled ? RepositoryDisplay : null }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
    /// <summary>身份带第二行（次级信息）：分支、短提交、安装时间，或校验未通过时的诊断原因。
    /// 当前核心是本地 PR 组合时把「已合并 PR」摆在这一行的最前面 —— 与移动端核心卡一致，
    /// 用户不用再进 PR 实验室才能确认组合到底装上了没有。</summary>
    public string IdentityMetaText => IsInstalled && Manifest is { } manifest
        ? (HasMergedPullRequests
            ? $"本地 PR 组合 {MergedPullRequestText} · 基线 {manifest.ShortSha} · 安装于 {manifest.InstalledAt.ToLocalTime():yyyy-MM-dd HH:mm}"
            : $"分支 {manifest.Branch} · 提交 {manifest.ShortSha} · 安装于 {manifest.InstalledAt.ToLocalTime():yyyy-MM-dd HH:mm}")
        : InstallationDetailText;
    public string UpdateStatusText => _updateResult switch
    {
        null => "尚未检查",
        { Status: CoreUpdateCheckStatus.SkippedCooldown } => _updateResult.Diagnostic,
        { Status: CoreUpdateCheckStatus.NotInstalled } => "核心尚未安装",
        { Status: CoreUpdateCheckStatus.MissingSource } => "缺少来源信息",
        { Status: CoreUpdateCheckStatus.Failed } => _updateResult.Diagnostic,
        // 刚把这次更新装完：远端提交已经是本地提交，不能再报「有新提交」。
        { UpdateAvailable: true, Remote: not null } when RemoteMatchesInstalled => "当前已是最新提交",
        { UpdateAvailable: true, Remote: not null } => $"发现新提交 {_updateResult.Remote.ShortSha}",
        _ => "当前已是最新提交",
    };
    public string UpdateDetailText => _updateResult?.Remote is not { } remote
        ? UpdateStatusText
        : RemoteMatchesInstalled
            ? $"已更新到 {remote.ShortSha} · {remote.Title}"
            : $"远端 {remote.ShortSha} · {remote.Title}";
    public string RateLimitText => _remote.LastRateLimit is { } limit
        ? $"剩余 {limit.Remaining}/{limit.Limit} · {limit.ResetAt.ToLocalTime():HH:mm} 重置"
        : "剩余次数未读取";

    /// <summary>运行环境（随包 Node、宿主脚本与生产依赖）的当前状态，来自启动准备流程的真实快照。</summary>
    public string RuntimeEnvironmentStatusText => _preparation?.Snapshot.State switch
    {
        RuntimePreparationState.Ready => "就绪",
        RuntimePreparationState.Failed => "失败",
        RuntimePreparationState.Canceled => "已取消",
        RuntimePreparationState.Pending => "等待准备",
        _ => "准备中",
    };
    public bool IsRuntimeEnvironmentHealthy => _preparation?.Snapshot.State == RuntimePreparationState.Ready;
    public bool IsRuntimeEnvironmentFailed =>
        _preparation?.Snapshot.State is RuntimePreparationState.Failed or RuntimePreparationState.Canceled;
    /// <summary>核心依赖的核对结论，由核心安装/更新成功后的自动核对写入，不再是"永远未检查"。</summary>
    public string CoreDependencyStatusText => _coreDependencyHealth switch
    {
        { Checked: false } => "尚未核对",
        { Healthy: true } => "正常",
        _ => $"缺失 {_coreDependencyHealth.MissingCount} 项",
    };
    public bool IsCoreDependencyHealthy => _coreDependencyHealth is { Checked: true, Healthy: true };
    public bool IsCoreDependencyFailed => _coreDependencyHealth is { Checked: true, Healthy: false };
    /// <summary>只有真正配置了 LOCAL_REDIS_URL 才显示这一项，避免给不用 Redis 的用户加噪音。</summary>
    public bool ShowOptionalRedisStatus => _preparation?.OptionalRedis is { Configured: true };
    public string OptionalRedisStatusText => _preparation?.OptionalRedis is { Staged: true } ? "已启用" : "铺开失败";
    public bool IsOptionalRedisHealthy => _preparation?.OptionalRedis is { Staged: true };
    public bool IsOptionalRedisFailed => _preparation?.OptionalRedis is { Configured: true, Staged: false };
    public bool IsOverviewPage => PageMode == CorePageMode.Overview;
    public bool IsCommitsPage => PageMode == CorePageMode.Commits;
    public bool IsPullRequestsPage => PageMode == CorePageMode.PullRequests;
    public bool IsHistoryPage => PageMode == CorePageMode.History;
    public bool HasLocalHistory => History.Count > 0;
    partial void OnHistoryChanged(IReadOnlyList<CoreVersionRecord> value) => OnPropertyChanged(nameof(HasLocalHistory));

    [RelayCommand]
    private async Task OpenHistoryPageAsync()
    {
        if (_isBusy) return;
        try
        {
            LoadHistory();
            PageMode = CorePageMode.History;
        }
        catch (Exception error)
        {
            _diagnostics.Record("读取本地核心历史失败", error);
            await _dialogService.ShowMessageAsync("本地历史读取失败", error.Message, true);
        }
    }
    public bool HasBranches => Branches.Count > 0;
    /// <summary>分支行的辅助文字（正在刷新 / 刷新失败 / 没有分支），为空时不占位。</summary>
    [ObservableProperty]
    private string _branchStatusText = string.Empty;
    public bool HasBranchStatus => BranchStatusText.Length > 0;
    private bool _branchRefreshInFlight;
    /// <summary>只有选中的分支与当前安装的分支不同才允许切换：否则会白跑一次核心重装。</summary>
    public bool CanSwitchSelectedBranch => !_isBusy && IsInstalled && SelectedBranch is { } branch &&
        !string.Equals(branch.Name, Manifest?.Branch, StringComparison.Ordinal);
    public string GithubTokenStatusText => GetGithubTokenStatusText();
    public bool CanInstallOfficial => !_isBusy && SelectedVariant == ManagedCoreVariant.Stable;
    public bool CanInstallDev => !_isBusy && SelectedVariant == ManagedCoreVariant.Dev;
    public bool CanInstallCustom => !_isBusy && SelectedVariant == ManagedCoreVariant.Custom;
    public bool CanOperateInstalled => !_isBusy && IsInstalled;

    /// <summary>所选变体是不是当前的运行核心。读不出覆盖值时按"不是"处理并给出诊断，
    /// 不能让界面显示一个可能不成立的运行归属。</summary>
    public bool CanActivateSelectedVariant
    {
        get
        {
            if (_activeVariant is null || _runtimeVariantSwitch is null || !IsInstalled)
            {
                return false;
            }

            try
            {
                return _activeVariant.Read() != SelectedVariant;
            }
            catch (IOException error)
            {
                _diagnostics.Record("读取运行核心选择失败", error);
                return false;
            }
        }
    }
    public bool CanApplyUpdate => !_isBusy && HasUpdate;
    public bool HasCommitPreviousPage => CommitPage > 1;
    public bool HasPullRequestPreviousPage => PullRequestPage > 1;
    public bool HasCommits => Commits.Count > 0;
    public bool HasPullRequests => PullRequests.Count > 0;
    public IReadOnlyList<GithubPullRequest> SelectedPullRequests => _selectedPullRequests.ToArray();
    public bool HasSelectedPullRequests => _selectedPullRequests.Count > 0;
    public string SelectedPullRequestSummary => _selectedPullRequests.Count == 0
        ? "未选择 PR"
        : $"已选 {_selectedPullRequests.Count} 个 PR：{string.Join("、", _selectedPullRequests.Select(item => $"#{item.Number}"))}";
    /// <summary>队列摘要按合并顺序展开（移动端同样把顺序摆在最显眼处）。</summary>
    public string PullRequestQueueSummary => _selectedPullRequests.Count == 0
        ? "队列为空：从下方列表加入要并入当前核心的 PR"
        : $"合并顺序：{string.Join(" → ", _selectedPullRequests.Select((item, index) => $"{index + 1}. #{item.Number}"))}";
    public bool CanBuildPullRequestStack => !_isBusy && _pullRequestManagement is not null && IsInstalled && HasSelectedPullRequests;
    /// <summary>已装在该变体上的本地 PR 组合（合并结果），来自安装 manifest。</summary>
    public IReadOnlyList<int> MergedPullRequestNumbers =>
        Manifest is { IsLocalPullRequestStack: true } manifest
            ? manifest.PullRequests.Select(source => source.Number).ToArray()
            : [];
    public bool HasMergedPullRequests => MergedPullRequestNumbers.Count > 0;
    public string MergedPullRequestText => HasMergedPullRequests
        ? string.Join(" ", MergedPullRequestNumbers.Select(number => $"#{number}"))
        : "无";
    public int CommitPage { get; private set; } = 1;
    public int PullRequestPage { get; private set; } = 1;
    public string PageSummaryText => $"第 {CommitPage} 页";
    public string PullRequestPageSummaryText => $"第 {PullRequestPage} 页";
    public string InstallDescription => SelectedVariant switch
    {
        ManagedCoreVariant.Stable =>
            "从官方上游安装稳定核心；默认分支由 GitHub API 返回，不猜固定分支名。",
        ManagedCoreVariant.Dev =>
            "从开发上游 lilixu3/danmu_api 安装开发核心；默认分支同样以 GitHub API 返回为准。",
        _ => "从你输入的 GitHub 仓库安装自定义核心；分支可写在 /tree/branch 地址中。",
    };

    [RelayCommand]
    private void RefreshInstallation()
    {
        RefreshInstallationState();
    }

    /// <summary>变体卡片与下方工作台是同一个选择开关：点击卡片即切换 SelectedVariant。</summary>
    [RelayCommand]
    private void SelectVariant(CoreVariantOption? option)
    {
        if (option is not null && option.Value != SelectedVariant)
        {
            SelectedVariantOption = option;
        }
    }

    [RelayCommand]
    private async Task InstallOfficialAsync()
    {
        if (!CanInstallOfficial)
        {
            return;
        }

        var repository = GithubRepositoryReference.Official();
        if (!string.IsNullOrWhiteSpace(BranchText))
        {
            repository = repository.WithBranch(GithubRepositoryReference.ValidateBranch(BranchText));
        }

        await RunDownloadOperationAsync(
            "安装稳定核心",
            "首次安装稳定核心前，请先测速并选择 GitHub 线路。",
            (route, progress, token) => _management.InstallBranchAsync(
                ManagedCoreVariant.Stable,
                repository,
                NormalizeDisplayName("官方核心"),
                route,
                progress,
                token)).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task InstallDevAsync()
    {
        if (!CanInstallDev)
        {
            return;
        }

        var repository = GithubRepositoryReference.Dev();
        if (!string.IsNullOrWhiteSpace(BranchText))
        {
            repository = repository.WithBranch(GithubRepositoryReference.ValidateBranch(BranchText));
        }

        await RunDownloadOperationAsync(
            "安装开发核心",
            "首次安装开发核心前，请先测速并选择 GitHub 线路。",
            (route, progress, token) => _management.InstallBranchAsync(
                ManagedCoreVariant.Dev,
                repository,
                NormalizeDisplayName("开发核心"),
                route,
                progress,
                token)).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task InstallCustomAsync()
    {
        if (!CanInstallCustom)
        {
            return;
        }

        GithubRepositoryReference repository;
        try
        {
            repository = GithubRepositoryReference.Parse(RepositoryText);
            if (!repository.HasExplicitBranch && !string.IsNullOrWhiteSpace(BranchText))
            {
                repository = repository.WithBranch(GithubRepositoryReference.ValidateBranch(BranchText));
            }
        }
        catch (Exception error) when (error is ArgumentException or FormatException)
        {
            await _dialogService.ShowMessageAsync("仓库地址无效", error.Message, isError: true).ConfigureAwait(true);
            return;
        }

        await RunDownloadOperationAsync(
            "安装自定义核心",
            "首次安装自定义核心前，请先测速并选择 GitHub 线路。",
            (route, progress, token) => _management.InstallBranchAsync(
                ManagedCoreVariant.Custom,
                repository,
                NormalizeDisplayName("自定义核心"),
                route,
                progress,
                token)).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        if (_isBusy)
        {
            return;
        }

        CoreUpdateCheckResult? pendingApply = null;
        _isBusy = true;
        NotifyBusyChanged();
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var route = await EnsureRouteSelectedAsync(
                    attempt == 0
                        ? "检查核心更新前，请先测速并选择 GitHub 线路。"
                        : "当前 GitHub 线路不可达，请重新测速并选择线路。",
                    CancellationToken.None).ConfigureAwait(true);
                if (route is null)
                {
                    return;
                }

                var result = await ReadWithProgressAsync("检查核心更新", token => _updateScheduler.CheckManualAsync(SelectedVariant, token)).ConfigureAwait(true);
                // 先回读磁盘再采信检查结论：核心若被本应用之外改动过，会出现
                // 「弹窗说当前已是最新提交、页头还挂着旧版本号」的自相矛盾。
                RefreshInstallationState();
                _updateResult = result;
                _updateResultCommit = Manifest?.CommitSha;
                NotifyUpdateChanged();
                NotifyRateLimitChanged();
                if (result.FailureKind is GithubFailureKind.RouteSelectionRequired or GithubFailureKind.Network && attempt == 0)
                {
                    _routePreferences.Invalidate();
                    continue;
                }

                switch (result.Status)
                {
                    case CoreUpdateCheckStatus.Checked when result.UpdateAvailable && result.Remote is not null:
                        // 这里不能直接升级：本方法还握着 _isBusy，应用更新的入口会被自己的忙标志挡掉
                        // （RunDownloadOperationAsync 在入口处直接返回）。先记下来，等忙标志释放后再问用户。
                        pendingApply = result;
                        break;
                    case CoreUpdateCheckStatus.Checked:
                        await _dialogService.ShowMessageAsync("检查更新", "当前已是最新提交。").ConfigureAwait(true);
                        break;
                    default:
                        await _dialogService.ShowMessageAsync(
                            "检查更新失败",
                            result.Diagnostic ?? "未能完成更新检查。",
                            isError: true).ConfigureAwait(true);
                        break;
                }

                break;
            }
        }
        catch (Exception error)
        {
            await ShowRemoteReadFailureAsync("核心更新", error).ConfigureAwait(true);
            return;
        }
        finally
        {
            _isBusy = false;
            NotifyBusyChanged();
        }

        if (pendingApply is { Remote: { } remote })
        {
            // 发现新版本时直接问「是否现在更新」，选是就立刻走线路确认 + 进度 + 结果对话框，
            // 不再让用户先关掉提示、再回总览点「应用更新」。
            var applyNow = await _dialogService.ConfirmAsync(
                "发现新提交",
                $"{VariantLabel}有新版本：{remote.ShortSha} · {remote.Title}\n"
                    + "立即更新会先停止正在运行的服务，完成后自动重启；也可以稍后在总览点「应用更新」。",
                "立即更新").ConfigureAwait(true);
            if (applyNow)
            {
                await ApplyUpdateAsync().ConfigureAwait(true);
            }
        }
    }

    [RelayCommand]
    private async Task ShowUpdateDetailsAsync()
    {
        if (_isBusy || Manifest is null || _updateResult is not { UpdateAvailable: true, Remote: not null } update)
        {
            await _dialogService.ShowMessageAsync(
                "查看更新详情",
                "请先检查更新；发现新提交后可以查看逐文件变更详情。",
                isError: true).ConfigureAwait(true);
            return;
        }

        try
        {
            var repository = GithubRepositoryReference.Parse(Manifest.Repository).WithBranch(Manifest.Branch);
            var route = await EnsureRouteSelectedAsync(
                "读取更新详情需要访问 GitHub，请先测速并选择线路。",
                CancellationToken.None).ConfigureAwait(true);
            if (route is null)
            {
                return;
            }

            var comparison = await ReadWithProgressAsync("读取更新详情", token => _remote.GetCompareAsync(repository, Manifest.CommitSha, update.Remote.Sha, token)).ConfigureAwait(true);
            NotifyRateLimitChanged();
            var remoteTitle = update.Remote.Title.Length > 40 ? update.Remote.Title[..40] + "…" : update.Remote.Title;
            var apply = await _dialogService.ShowUpdateDetailsAsync(
                comparison,
                $"{VersionDisplay} · {Manifest.ShortSha}",
                $"{remoteTitle} · {update.Remote.ShortSha}").ConfigureAwait(true);
            if (apply)
            {
                await ApplyUpdateAsync().ConfigureAwait(true);
            }
        }
        catch (Exception error)
        {
            await ShowRemoteReadFailureAsync("更新详情", error).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private Task ApplyUpdateAsync()
    {
        if (!CanApplyUpdate || _updateResult is null)
        {
            return Task.CompletedTask;
        }

        return ApplyUpdateAsync(_updateResult);
    }

    /// <summary>
    /// 侧栏「有更新」卡片走的正是这条：检查结论可能来自后台或进前台的静默检查，
    /// 用户从没进过核心页，所以这里直接吃调用方给的结果，而不是页面上缓存的那份。
    /// 线路确认、进度弹窗、失败原因与取消处理全部复用同一段下载编排，不另立流程。
    /// </summary>
    public Task<CoreManagementOperationResult?> ApplyUpdateAsync(CoreUpdateCheckResult update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update is not { Status: CoreUpdateCheckStatus.Checked, UpdateAvailable: true, Remote: not null })
        {
            throw new ArgumentException("这份检查结论里没有可应用的新提交。", nameof(update));
        }

        return RunDownloadOperationAsync(
            "应用核心更新",
            "应用核心更新前，请确认一条 GitHub 下载线路。",
            (route, progress, token) => _management.ApplyUpdateAsync(update, route, progress, token));
    }

    [RelayCommand]
    private async Task ReinstallAsync()
    {
        if (!CanOperateInstalled || Manifest is null)
        {
            return;
        }

        var confirmed = await _dialogService.ConfirmAsync(
            "重新安装",
            $"将按当前来源（{RepositoryDisplay}@{Manifest.ShortSha}）重新下载并替换{VariantLabel}；配置与日志不受影响，运行中的服务会先停止再恢复。",
            "重新安装").ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        await RunDownloadOperationAsync(
            "重新安装核心",
            "重新安装核心前，请确认一条 GitHub 下载线路。",
            (route, progress, token) => _management.ReinstallAsync(SelectedVariant, route, progress, token)).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RollbackAsync(CoreVersionRecord? record)
    {
        if (_isBusy || record is null)
        {
            return;
        }

        var confirmed = await _dialogService.ConfirmAsync(
            "回退版本",
            $"将用历史版本 {record.Manifest.ShortSha}（{record.Manifest.InstalledAt.ToLocalTime():yyyy-MM-dd HH:mm} 安装）替换当前核心。运行中的服务会先安全停止，完成后自动重启。",
            "确认回退").ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        await RunLocalMutationAsync(
            "回退核心",
            () => _management.RollbackAsync(SelectedVariant, record.Id),
            "核心已回退到所选历史版本。").ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (!CanOperateInstalled)
        {
            return;
        }

        var confirmed = await _dialogService.ConfirmAsync(
            "删除核心",
            $"将删除{VariantLabel}文件，但不会删除配置、日志、Node 或下载缓存。此操作会停止运行中的服务。",
            "停止服务并删除核心").ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        await RunLocalMutationAsync(
            "删除核心",
            () => _management.DeleteAsync(SelectedVariant),
            "核心已删除；服务需要重新安装核心后才能启动。").ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RenameCoreAsync()
    {
        if (!CanOperateInstalled || Manifest is null)
        {
            return;
        }

        var input = await _dialogService.PromptTextAsync(
            "核心设置",
            "修改显示名称（仅影响桌面端展示，不改变运行目录与核心配置）。",
            Manifest.DisplayName,
            "保存").ConfigureAwait(true);
        if (input is null)
        {
            return;
        }

        await RunLocalMutationAsync(
            "保存核心设置",
            () => _management.RenameAsync(SelectedVariant, input),
            "核心显示名称已更新。").ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task LoadBranchesAsync()
    {
        if (_isBusy || !IsInstalled)
        {
            return;
        }

        var succeeded = await RunRemoteReadAsync(
            "分支",
            async cancellationToken =>
            {
                var branches = await _remote.GetAllBranchesAsync(GetInstalledRepository(), cancellationToken).ConfigureAwait(true);
                Branches = branches;
                SelectedBranch = branches.FirstOrDefault(branch =>
                    string.Equals(branch.Name, Manifest?.Branch, StringComparison.Ordinal));
                OnPropertyChanged(nameof(HasBranches));
                OnPropertyChanged(nameof(CanSwitchSelectedBranch));
                SwitchSelectedBranchCommand.NotifyCanExecuteChanged();
            }).ConfigureAwait(true);
        if (succeeded)
        {
            await _dialogService.ShowMessageAsync("分支已读取", $"共读取 {Branches.Count} 个分支，点「切换」可安装分支最新提交。").ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 点开分支下拉时调用：静默刷新列表。**不能走 LoadBranchesCommand**——它会弹模态进度对话框，
    /// 用户还没来得及选，下拉就被打断；而且它每次都会把 SelectedBranch 重置回当前安装的分支，
    /// 用户刚选好的目标分支会被抹掉，"切换"按钮随之变灰——这正是"点一下就被刷新、根本切不了"的原因。
    /// 这里不占用 _isBusy（否则整页按钮都会闪灰），改用独立的在途标志；失败写诊断并显示在分支行下方。
    /// </summary>
    public async Task RefreshBranchesQuietlyAsync()
    {
        if (!IsInstalled || _branchRefreshInFlight)
        {
            return;
        }

        _branchRefreshInFlight = true;
        BranchStatusText = "正在刷新分支…";
        try
        {
            // 记住用户选的目标分支，刷新后按名字还原，避免列表换了实例就把选择清掉。
            var wanted = SelectedBranch?.Name ?? Manifest?.Branch;
            var branches = await _remote.GetAllBranchesAsync(GetInstalledRepository(), CancellationToken.None).ConfigureAwait(true);
            Branches = branches;
            SelectedBranch = branches.FirstOrDefault(branch => string.Equals(branch.Name, wanted, StringComparison.Ordinal))
                ?? branches.FirstOrDefault(branch => string.Equals(branch.Name, Manifest?.Branch, StringComparison.Ordinal));
            BranchStatusText = branches.Count == 0 ? "该仓库没有可用分支" : string.Empty;
        }
        catch (Exception error)
        {
            var reason = error is GithubRemoteException github ? github.Message : $"{error.GetType().Name}: {error.Message}";
            _diagnostics.Record($"刷新分支失败：{reason}");
            BranchStatusText = $"分支列表刷新失败：{reason}";
        }
        finally
        {
            _branchRefreshInFlight = false;
            OnPropertyChanged(nameof(HasBranches));
            OnPropertyChanged(nameof(CanSwitchSelectedBranch));
            OnPropertyChanged(nameof(BranchStatusText));
            OnPropertyChanged(nameof(HasBranchStatus));
        }
    }

    [RelayCommand(CanExecute = nameof(CanSwitchSelectedBranch))]
    private async Task SwitchSelectedBranchAsync()
    {
        await SwitchBranchAsync(SelectedBranch).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SwitchBranchAsync(GithubBranch? branch)
    {
        if (_isBusy || branch is null || Manifest is null)
        {
            return;
        }

        var confirmed = await _dialogService.ConfirmAsync(
            "切换分支",
            $"将安装分支 {branch.Name} 的最新提交并替换当前核心。运行中的服务会先停止，完成后自动重启。",
            "切换并重装").ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        var repository = GithubRepositoryReference.Parse(Manifest.Repository).WithBranch(branch.Name);
        var displayName = Manifest.DisplayName;
        await RunDownloadOperationAsync(
            $"切换分支 {branch.Name}",
            "切换分支需要下载核心包，请确认一条 GitHub 线路。",
            (route, progress, token) => _management.InstallBranchAsync(
                SelectedVariant, repository, displayName, route, progress, token)).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task LoadCommitsAsync() => LoadCommitPageAsync(1);

    [RelayCommand]
    private Task NextCommitPageAsync() => LoadCommitPageAsync(CommitPage + 1);

    [RelayCommand]
    private Task PreviousCommitPageAsync() => LoadCommitPageAsync(Math.Max(1, CommitPage - 1));

    [RelayCommand]
    private async Task ShowCommitDetailsAsync(GithubCommit? commit)
    {
        if (_isBusy || commit is null || Manifest is null)
        {
            return;
        }

        try
        {
            var repository = GithubRepositoryReference.Parse(Manifest.Repository).WithBranch(Manifest.Branch);
            var route = await EnsureRouteSelectedAsync(
                "读取提交详情需要访问 GitHub，请先测速并选择线路。",
                CancellationToken.None).ConfigureAwait(true);
            if (route is null)
            {
                return;
            }

            _isBusy = true;
            NotifyBusyChanged();
            var details = await ReadWithProgressAsync("读取提交详情", token => _remote.GetCommitDetailsAsync(repository, commit.Sha, token)).ConfigureAwait(true);
            NotifyRateLimitChanged();
            var rollback = await _dialogService.ShowCommitDetailsAsync(details).ConfigureAwait(true);
            if (rollback)
            {
                _isBusy = false;
                NotifyBusyChanged();
                await RollbackToCommitAsync(commit).ConfigureAwait(true);
            }
        }
        catch (Exception error)
        {
            await ShowRemoteReadFailureAsync("提交详情", error).ConfigureAwait(true);
        }
        finally
        {
            _isBusy = false;
            NotifyBusyChanged();
        }
    }

    [RelayCommand]
    private async Task RollbackToCommitAsync(GithubCommit? commit)
    {
        if (_isBusy || commit is null || !IsInstalled || Manifest is null)
        {
            return;
        }

        var confirmed = await _dialogService.ConfirmAsync(
            $"回退到 {commit.ShortSha}？",
            $"将用提交 {commit.ShortSha}（{commit.Title}）替换当前核心。运行中的服务会先安全停止，完成后自动重启。",
            "确认回退").ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        var repository = GithubRepositoryReference.Parse(Manifest.Repository).WithBranch(Manifest.Branch);
        var branch = Manifest.Branch;
        var displayName = Manifest.DisplayName;
        await RunDownloadOperationAsync(
            $"回退到 {commit.ShortSha}",
            "回退到指定提交需要下载该提交的核心包，请确认一条 GitHub 线路。",
            (route, progress, token) => _management.InstallCommitAsync(
                SelectedVariant, repository, branch, commit.Sha, displayName, route, progress, token)).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task LoadPullRequestsAsync() => LoadPullRequestPageAsync(1);

    [RelayCommand]
    private Task NextPullRequestPageAsync() => LoadPullRequestPageAsync(PullRequestPage + 1);

    [RelayCommand]
    private Task PreviousPullRequestPageAsync() => LoadPullRequestPageAsync(Math.Max(1, PullRequestPage - 1));

    [RelayCommand]
    private async Task ShowPullRequestDetailsAsync(GithubPullRequest? pullRequest)
    {
        if (_isBusy || pullRequest is null || Manifest is null)
        {
            return;
        }

        try
        {
            var repository = GithubRepositoryReference.Parse(Manifest.Repository).WithBranch(Manifest.Branch);
            var route = await EnsureRouteSelectedAsync(
                "读取 PR 详情需要访问 GitHub，请先测速并选择线路。",
                CancellationToken.None).ConfigureAwait(true);
            if (route is null)
            {
                return;
            }

            _isBusy = true;
            NotifyBusyChanged();
            var details = await ReadWithProgressAsync("读取 PR 详情", token => _remote.GetPullRequestAsync(repository, pullRequest.Number, token)).ConfigureAwait(true);
            var files = await ReadWithProgressAsync("读取 PR 文件", token => _remote.GetAllPullRequestFilesAsync(repository, pullRequest.Number, token)).ConfigureAwait(true);
            NotifyRateLimitChanged();
            await _dialogService.ShowPullRequestDetailsAsync(details, files).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            await ShowRemoteReadFailureAsync("PR 详情", error).ConfigureAwait(true);
        }
        finally
        {
            _isBusy = false;
            NotifyBusyChanged();
        }
    }

    [RelayCommand]
    private void TogglePullRequestSelection(GithubPullRequest? pullRequest)
    {
        if (_isBusy || pullRequest is null)
        {
            return;
        }

        var existing = _selectedPullRequests.FindIndex(item => item.Number == pullRequest.Number);
        if (existing >= 0)
        {
            _selectedPullRequests.RemoveAt(existing);
            NotifyPullRequestSelectionChanged();
            return;
        }

        // 已并入当前核心的 PR 不允许再排队：合并的语义是「当前核心 + 这些 PR」，重复并入没有意义。
        if (MergedPullRequestNumbers.Contains(pullRequest.Number))
        {
            return;
        }

        _selectedPullRequests.Add(pullRequest);
        NotifyPullRequestSelectionChanged();
    }

    /// <summary>调整队列顺序：合并按这个顺序逐个进行，所以顺序必须由用户可改。</summary>
    [RelayCommand]
    private void MoveSelectedPullRequestUp(GithubPullRequest? pullRequest) => MoveSelectedPullRequest(pullRequest, -1);

    [RelayCommand]
    private void MoveSelectedPullRequestDown(GithubPullRequest? pullRequest) => MoveSelectedPullRequest(pullRequest, 1);

    private void MoveSelectedPullRequest(GithubPullRequest? pullRequest, int offset)
    {
        if (_isBusy || pullRequest is null)
        {
            return;
        }

        var index = _selectedPullRequests.FindIndex(item => item.Number == pullRequest.Number);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= _selectedPullRequests.Count)
        {
            return;
        }

        var item = _selectedPullRequests[index];
        _selectedPullRequests.RemoveAt(index);
        _selectedPullRequests.Insert(target, item);
        NotifyPullRequestSelectionChanged();
    }

    public bool IsPullRequestSelected(GithubPullRequest? pullRequest) =>
        pullRequest is not null && _selectedPullRequests.Any(item => item.Number == pullRequest.Number);

    /// <summary>该 PR 已在队列中的位次（从 1 开始）；0 表示未选择。
    /// 与移动端一致：行上直接显示顺序号，用户不用回头数队列。</summary>
    public int PullRequestQueuePosition(GithubPullRequest? pullRequest)
    {
        if (pullRequest is null)
        {
            return 0;
        }

        var index = _selectedPullRequests.FindIndex(item => item.Number == pullRequest.Number);
        return index < 0 ? 0 : index + 1;
    }

    private void NotifyPullRequestSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedPullRequests));
        OnPropertyChanged(nameof(HasSelectedPullRequests));
        OnPropertyChanged(nameof(SelectedPullRequestSummary));
        OnPropertyChanged(nameof(PullRequestQueueSummary));
        OnPropertyChanged(nameof(CanBuildPullRequestStack));
        OnPropertyChanged(nameof(PullRequestRows));
        OnPropertyChanged(nameof(MergedPullRequestNumbers));
        OnPropertyChanged(nameof(HasMergedPullRequests));
        OnPropertyChanged(nameof(MergedPullRequestText));
        BuildPullRequestStackCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 构建本地 PR 组合：对齐移动端 PR 实验室的唯一动作 ——
    /// 在当前已安装核心的基线提交之上按顺序真实合并所选 PR，然后原子替换同一变体。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanBuildPullRequestStack))]
    private async Task BuildPullRequestStackAsync()
    {
        if (_pullRequestManagement is null || !CanBuildPullRequestStack || Manifest is null)
        {
            return;
        }

        var variant = SelectedVariant;
        var source = Manifest;
        var selected = _selectedPullRequests.ToArray();
        var inherited = source.IsLocalPullRequestStack
            ? source.PullRequests.Where(existing => selected.All(item => item.Number != existing.Number))
                .Select(existing => existing.Number).ToArray()
            : [];
        PullRequestBuildConfirmation confirmation;
        try
        {
            confirmation = await _dialogService.ConfirmPullRequestBuildAsync(new PullRequestBuildPrompt(
                VariantLabel,
                source.DisplayName,
                source.Repository,
                source.Branch,
                source.BaseCommitSha ?? source.CommitSha,
                selected,
                inherited,
                ActivateAfterBuild)).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            // 确认窗口本身出问题也必须显式报出来：静默返回会让用户以为"点了没反应"。
            _diagnostics.Record("PR 组合构建确认失败", error);
            await _dialogService.ShowMessageAsync("PR 组合构建失败", error.Message, true).ConfigureAwait(true);
            return;
        }
        if (!confirmation.Confirmed)
        {
            return;
        }

        ActivateAfterBuild = confirmation.ActivateAfterInstall;
        _isBusy = true;
        NotifyBusyChanged();
        CorePreparedInstallRequest? prepared = null;
        try
        {
            var route = await EnsureRouteSelectedAsync(
                "构建本地 PR 组合需要访问 GitHub Git，请先测速并选择线路。",
                CancellationToken.None).ConfigureAwait(true);
            if (route is null)
            {
                return;
            }
            if (SelectedVariant != variant || Manifest != source ||
                !_selectedPullRequests.Select(item => item.Number).SequenceEqual(selected.Select(item => item.Number)))
            {
                await _dialogService.ShowMessageAsync("构建已失效", "当前核心或 PR 队列已改变，请重新确认。", true).ConfigureAwait(true);
                return;
            }

            var outcome = await _dialogService.RunWithProgressDialogAsync(
                "构建本地 PR 组合",
                async (progress, token) =>
                {
                    prepared = await _pullRequestManagement.PreparePullRequestMergeAsync(
                        variant,
                        selected,
                        route,
                        progress,
                        token).ConfigureAwait(true);
                }).ConfigureAwait(true);
            if (outcome.Outcome == ProgressOperationOutcome.Canceled)
            {
                await _dialogService.ShowMessageAsync("操作已取消", "PR 组合构建已取消，当前核心未变更。").ConfigureAwait(true);
                return;
            }
            if (outcome.Outcome != ProgressOperationOutcome.Completed || prepared is null)
            {
                await _dialogService.ShowMessageAsync("PR 组合构建失败", outcome.Diagnostic ?? "没有生成可应用的组合结果。", true).ConfigureAwait(true);
                return;
            }

            CoreManagementOperationResult? result = null;
            var applyOutcome = await _dialogService.RunWithProgressDialogAsync(
                "应用 PR 组合",
                async (progress, token) =>
                {
                    result = await _pullRequestManagement.ApplyPreparedPullRequestMergeAsync(prepared, progress, token).ConfigureAwait(true);
                }).ConfigureAwait(true);
            if (applyOutcome.Outcome == ProgressOperationOutcome.Canceled)
            {
                await _dialogService.ShowMessageAsync("操作已取消", "PR 组合未应用，当前核心未变更。", true).ConfigureAwait(true);
                return;
            }
            var effective = result ?? new CoreManagementOperationResult(false, false, false, null, applyOutcome.Diagnostic ?? "应用 PR 组合失败");
            if (applyOutcome.Outcome != ProgressOperationOutcome.Completed || !effective.Succeeded)
            {
                await _dialogService.ShowMessageAsync("PR 组合应用失败", effective.Diagnostic, true).ConfigureAwait(true);
                return;
            }

            ClearPullRequestSelection();
            RefreshInstallationState();
            await _dialogService.ShowMessageAsync("PR 组合已安装", BuildSuccessMessage("PR 组合构建", effective)).ConfigureAwait(true);
            await ApplyActivateAfterBuildAsync(variant, confirmation.ActivateAfterInstall).ConfigureAwait(true);
            await VerifyDependenciesAfterMutationAsync().ConfigureAwait(true);
        }
        catch (Exception error)
        {
            _diagnostics.Record("PR 组合构建失败", error);
            await _dialogService.ShowMessageAsync("PR 组合构建失败", error.Message, true).ConfigureAwait(true);
        }
        finally
        {
            if (prepared is not null)
            {
                try
                {
                    _pullRequestManagement.DiscardPreparedPullRequestMerge(prepared);
                }
                catch (Exception error)
                {
                    _diagnostics.Record("清理 PR 组合暂存目录失败", error);
                    await _dialogService.ShowMessageAsync("暂存目录清理失败", error.Message, true).ConfigureAwait(true);
                }
            }
            _isBusy = false;
            NotifyBusyChanged();
        }
    }

    /// <summary>
    /// 「安装后切换」：把运行核心换成刚装好组合的那个变体。失败时恢复原来的选择并重启原核心，
    /// 与移动端 finishInstallation 的语义一致 —— 不让用户在"服务起不来"的状态里自己猜。
    /// </summary>
    private async Task ApplyActivateAfterBuildAsync(ManagedCoreVariant variant, bool activate)
    {
        if (!activate)
        {
            return;
        }

        // 构建对话框里的「安装后切换」就是这次切换的确认，不再二次追问。
        await ActivateVariantAsync(variant, confirm: false).ConfigureAwait(true);
    }

    /// <summary>把运行核心切换到指定变体；已在该变体上时什么也不做。</summary>
    private async Task ActivateVariantAsync(ManagedCoreVariant variant, bool confirm)
    {
        if (_activeVariant is null || _runtimeVariantSwitch is null)
        {
            return;
        }

        try
        {
            if (_activeVariant.Read() == variant)
            {
                return;
            }
        }
        catch (IOException error)
        {
            _diagnostics.Record("读取运行核心选择失败", error);
            await _dialogService.ShowMessageAsync("切换运行核心失败", error.Message, true).ConfigureAwait(true);
            return;
        }

        try
        {
            if (confirm)
            {
                var confirmed = await _dialogService.ConfirmAsync(
                    $"切换到{variant.ToLabel()}？",
                    "运行中的服务会先安全停止，切换后自动启动；核心与配置不受影响。",
                    "切换并重启").ConfigureAwait(true);
                if (!confirmed)
                {
                    return;
                }
            }

            var switchResult = await _runtimeVariantSwitch(variant, CancellationToken.None).ConfigureAwait(true);
            await _dialogService.ShowMessageAsync(
                switchResult.Succeeded ? "已切换运行核心" : "切换运行核心失败",
                switchResult.Diagnostic,
                isError: !switchResult.Succeeded).ConfigureAwait(true);
            NotifyActivationChanged();
        }
        catch (Exception error)
        {
            _diagnostics.Record("切换运行核心失败", error);
            await _dialogService.ShowMessageAsync("切换运行核心失败", error.Message, true).ConfigureAwait(true);
        }
    }

    /// <summary>核心页的「切换到此核心」：所选变体不是运行变体时把它设为运行核心。</summary>
    [RelayCommand]
    private async Task ActivateSelectedVariantAsync()
    {
        if (_isBusy || !CanOperateInstalled || !CanActivateSelectedVariant)
        {
            return;
        }

        _isBusy = true;
        NotifyBusyChanged();
        try
        {
            await ActivateVariantAsync(SelectedVariant, confirm: true).ConfigureAwait(true);
        }
        finally
        {
            _isBusy = false;
            NotifyBusyChanged();
        }
    }

    private void NotifyActivationChanged()
    {
        OnPropertyChanged(nameof(CanActivateSelectedVariant));
        ActivateSelectedVariantCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void LoadHistory()
    {
        History = _management.GetHistory(SelectedVariant);
    }

    /// <summary>
    /// 核心安装/更新/回退成功后自动核对新核心的依赖声明：上游新增依赖时随包依赖不可能覆盖，
    /// 这里只做只读探测并把缺失项一次性提醒给用户，不再提供手动检查与修复入口。
    /// 核对失败不改写核心操作结果，只记录诊断。
    /// </summary>
    private async Task VerifyDependenciesAfterMutationAsync()
    {
        if (_verifyDependencies is null || !IsInstalled)
        {
            return;
        }

        try
        {
            _coreDependencyHealth = await _verifyDependencies(SelectedVariant, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            // 核对失败要显式反映到状态带上，不能悄悄留着一个"未核对"。
            _coreDependencyHealth = CoreDependencyHealth.Unknown;
            _diagnostics.Record($"核心依赖核对失败：{error.GetType().Name} / {DependencyMaintenanceDiagnostics.Describe(error)}");
        }

        NotifyDependencyHealthChanged();
    }

    private void NotifyDependencyHealthChanged()
    {
        OnPropertyChanged(nameof(CoreDependencyStatusText));
        OnPropertyChanged(nameof(IsCoreDependencyHealthy));
        OnPropertyChanged(nameof(IsCoreDependencyFailed));
    }

    /// <summary>核心在别处被换掉后（托盘「立即更新核心」、后台自动更新、PR 安装），
    /// 本页持有的安装快照就过期了，必须回读磁盘。</summary>
    private void OnInstallationChanged(object? sender, CoreInstallationChangedEventArgs args)
    {
        if (args.Variant != SelectedVariant)
        {
            return;
        }

        void Update() => RefreshInstallationState();
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
        {
            Update();
            return;
        }

        _uiContext.Post(_ => Update(), null);
    }

    /// <summary>启动准备在后台线程推进，状态带必须回到 UI 线程再通知，并合并高频进度回调。</summary>
    private void OnPreparationChanged(object? sender, RuntimePreparationSnapshot snapshot)
    {
        void Update()
        {
            Interlocked.Exchange(ref _preparationUpdateQueued, 0);
            foreach (var property in new[]
            {
                nameof(RuntimeEnvironmentStatusText), nameof(IsRuntimeEnvironmentHealthy), nameof(IsRuntimeEnvironmentFailed),
                nameof(ShowOptionalRedisStatus), nameof(OptionalRedisStatusText),
                nameof(IsOptionalRedisHealthy), nameof(IsOptionalRedisFailed),
            })
            {
                OnPropertyChanged(property);
            }
        }

        if (_uiContext is null)
        {
            Update();
            return;
        }

        // 完整准备会报出上万次按文件进度；只保留一个待执行的 UI 回调，它渲染最新快照即可。
        if (Interlocked.Exchange(ref _preparationUpdateQueued, 1) == 0)
        {
            _uiContext.Post(_ => Update(), null);
        }
    }


    [RelayCommand]
    private async Task ConfigureGithubTokenAsync()
    {
        if (_isBusy)
        {
            return;
        }

        GithubTokenConfigurationState state;
        try
        {
            state = _githubTokenConfiguration.GetState();
        }
        catch (Exception error)
        {
            _diagnostics.Record($"读取 GitHub Token 状态失败：{error.GetType().Name}");
            await _dialogService.ShowMessageAsync("GitHub Token", error.Message, isError: true).ConfigureAwait(true);
            return;
        }

        GithubTokenDialogResult dialogResult;
        try
        {
            dialogResult = await _dialogService.PromptGithubTokenAsync(state.Configured, state.Hint).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            _diagnostics.Record("打开 GitHub Token 弹窗失败", error);
            await _dialogService.ShowMessageAsync("GitHub Token", error.Message, isError: true).ConfigureAwait(true);
            return;
        }
        if (dialogResult.Cancelled)
        {
            return;
        }

        try
        {
            GithubTokenConfigurationResult result;
            if (dialogResult.Clear)
            {
                result = await _githubTokenConfiguration.ClearAsync().ConfigureAwait(true);
            }
            else
            {
                var token = dialogResult.Token?.Trim();
                if (string.IsNullOrWhiteSpace(token))
                {
                    throw new ArgumentException("GitHub Token 不能为空");
                }

                result = await _githubTokenConfiguration
                    .ValidateAndSaveAsync(token)
                    .ConfigureAwait(true);
            }

            NotifyRateLimitChanged();
            OnPropertyChanged(nameof(GithubTokenStatusText));
            await _dialogService.ShowMessageAsync(
                result.Succeeded ? "GitHub Token" : "配置 GitHub Token 失败",
                result.Diagnostic,
                isError: !result.Succeeded).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            _diagnostics.Record($"配置 GitHub Token 失败：{error.GetType().Name}");
            await _dialogService.ShowMessageAsync("配置 GitHub Token 失败", error.Message, isError: true).ConfigureAwait(true);
        }
    }

    partial void OnCommitsChanged(IReadOnlyList<GithubCommit> value)
    {
        OnPropertyChanged(nameof(HasCommits));
    }

    partial void OnPullRequestsChanged(IReadOnlyList<GithubPullRequest> value)
    {
        OnPropertyChanged(nameof(HasPullRequests));
        OnPropertyChanged(nameof(PullRequestRows));
    }

    private void ClearPullRequestSelection()
    {
        _selectedPullRequests.Clear();
        NotifyPullRequestSelectionChanged();
    }

    private void ClearPullRequestPage()
    {
        PullRequests = [];
        ClearPullRequestSelection();
        PullRequestPage = 1;
        HasPullRequestNextPage = false;
        OnPropertyChanged(nameof(PullRequestRows));
        OnPropertyChanged(nameof(PullRequestPageSummaryText));
        OnPropertyChanged(nameof(HasPullRequestPreviousPage));
        PreviousPullRequestPageCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedVariantChanged(ManagedCoreVariant value)
    {
        SelectedVariantOption = VariantOptions.First(option => option.Value == value);
        SyncVariantSelection();
        OnPropertyChanged(nameof(VariantLabel));
        OnPropertyChanged(nameof(IdentityEyebrow));
        _updateResult = null;
        SelectedBranch = null;
        Branches = [];
        Commits = [];
        ClearPullRequestPage();
        CommitPage = 1;
        RefreshInstallationState();
        NotifyActivationChanged();
        NotifyUpdateChanged();
    }

    /// <summary>把选中态推给每个变体卡片，使卡片自身持有可回读的选中状态。</summary>
    private void SyncVariantSelection()
    {
        foreach (var option in VariantOptions)
        {
            option.IsSelected = option.Value == SelectedVariant;
        }
    }

    partial void OnSelectedVariantOptionChanged(CoreVariantOption value)
    {
        if (value is not null && value.Value != SelectedVariant)
        {
            SelectedVariant = value.Value;
        }
    }

    partial void OnSelectedBranchChanged(GithubBranch? value)
    {
        OnPropertyChanged(nameof(CanSwitchSelectedBranch));
        SwitchSelectedBranchCommand.NotifyCanExecuteChanged();
    }

    partial void OnBranchStatusTextChanged(string value) => OnPropertyChanged(nameof(HasBranchStatus));

    [RelayCommand]
    private async Task OpenCommitsPageAsync()
    {
        if (_isBusy || !IsInstalled)
        {
            return;
        }

        PageMode = CorePageMode.Commits;
        await LoadCommitPageAsync(1).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task OpenPullRequestsPageAsync()
    {
        if (_isBusy || !IsInstalled)
        {
            return;
        }

        PageMode = CorePageMode.PullRequests;
        await LoadPullRequestPageAsync(1).ConfigureAwait(true);
    }

    [RelayCommand]
    private void BackToOverview()
    {
        PageMode = CorePageMode.Overview;
    }

    partial void OnPageModeChanged(CorePageMode value)
    {
        OnPropertyChanged(nameof(IsOverviewPage));
        OnPropertyChanged(nameof(IsCommitsPage));
        OnPropertyChanged(nameof(IsPullRequestsPage));
        OnPropertyChanged(nameof(IsHistoryPage));
        OpenCommitsPageCommand.NotifyCanExecuteChanged();
        OpenPullRequestsPageCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasCommitNextPageChanged(bool value)
    {
        OnPropertyChanged(nameof(HasCommitPreviousPage));
        NextCommitPageCommand.NotifyCanExecuteChanged();
        PreviousCommitPageCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasPullRequestNextPageChanged(bool value)
    {
        OnPropertyChanged(nameof(HasPullRequestPreviousPage));
        NextPullRequestPageCommand.NotifyCanExecuteChanged();
        PreviousPullRequestPageCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadCommitPageAsync(int page)
    {
        if (_isBusy || !IsInstalled || Manifest is null || page < 1)
        {
            return;
        }

        try
        {
            var repository = GetInstalledRepository();
            var route = await EnsureRouteSelectedAsync(
                "读取提交列表需要访问 GitHub，请先测速并选择线路。",
                CancellationToken.None).ConfigureAwait(true);
            if (route is null)
            {
                return;
            }

            _isBusy = true;
            NotifyBusyChanged();
            var result = await ReadWithProgressAsync("读取提交列表", token => _remote.GetCommitsAsync(repository, repository.Branch!, page, cancellationToken: token)).ConfigureAwait(true);
            Commits = result.Items;
            CommitPage = result.Page;
            HasCommitNextPage = result.HasNextPage;
            NotifyRateLimitChanged();
            OnPropertyChanged(nameof(PageSummaryText));
            OnPropertyChanged(nameof(HasCommitPreviousPage));
        }
        catch (Exception error)
        {
            await ShowRemoteReadFailureAsync("提交", error).ConfigureAwait(true);
        }
        finally
        {
            _isBusy = false;
            NotifyBusyChanged();
        }
    }

    private async Task LoadPullRequestPageAsync(int page)
    {
        if (_isBusy || !IsInstalled || Manifest is null || page < 1)
        {
            return;
        }

        try
        {
            var variant = SelectedVariant;
            var repository = GetInstalledRepository();
            var route = await EnsureRouteSelectedAsync(
                "读取 PR 列表需要访问 GitHub，请先测速并选择线路。",
                CancellationToken.None).ConfigureAwait(true);
            if (route is null)
            {
                return;
            }

            _isBusy = true;
            NotifyBusyChanged();
            var result = await ReadWithProgressAsync("读取 PR 列表", token => _remote.GetPullRequestsAsync(repository, repository.Branch!, page: page, cancellationToken: token)).ConfigureAwait(true);
            if (SelectedVariant != variant || !IsInstalled || Manifest is null ||
                !string.Equals(Manifest.Repository, repository.FullName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Manifest.Branch, repository.Branch, StringComparison.Ordinal))
            {
                return;
            }
            PullRequests = result.Items;
            PullRequestPage = result.Page;
            HasPullRequestNextPage = result.HasNextPage;
            NotifyRateLimitChanged();
            OnPropertyChanged(nameof(PullRequestPageSummaryText));
            OnPropertyChanged(nameof(HasPullRequestPreviousPage));
        }
        catch (Exception error)
        {
            await ShowRemoteReadFailureAsync("PR", error).ConfigureAwait(true);
        }
        finally
        {
            _isBusy = false;
            NotifyBusyChanged();
        }
    }

    private string GetGithubTokenStatusText()
    {
        try
        {
            var state = _githubTokenConfiguration.GetState();
            return state.Configured
                ? $"Token 已配置（{state.Hint}）"
                : "未配置 Token";
        }
        catch (Exception error)
        {
            _diagnostics.Record($"读取 GitHub Token 状态失败：{error.GetType().Name}");
            return "Token 状态读取失败";
        }
    }

    private async Task ShowRemoteReadFailureAsync(string label, Exception error)
    {
        if (error is OperationCanceledException)
        {
            _diagnostics.Record($"读取{label}已取消", error);
            await _dialogService.ShowMessageAsync($"读取{label}已取消", "本次读取已取消，现有数据未变更。", isError: true).ConfigureAwait(true);
            return;
        }

        var message = error is GithubRemoteException github
            ? github.Message
            : $"{error.GetType().Name}: {error.Message}";
        _diagnostics.Record($"读取{label}失败", error);
        await _dialogService.ShowMessageAsync($"读取{label}失败", message, isError: true).ConfigureAwait(true);
    }

    /// <summary>
    /// 需要下载的核心操作统一流程：线路确认（仅未确认或线路类失败时弹窗）→
    /// 进度弹窗（可取消）→ 结果弹窗；线路类失败自动重新选线重试一次。
    /// </summary>
    private async Task<CoreManagementOperationResult?> RunDownloadOperationAsync(
        string progressTitle,
        string routeReason,
        Func<string, IProgress<CoreInstallProgress>, CancellationToken, Task<CoreManagementOperationResult>> operation)
    {
        if (_isBusy)
        {
            return null;
        }

        _isBusy = true;
        NotifyBusyChanged();
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var route = await EnsureRouteSelectedAsync(
                    attempt == 0 ? routeReason : "所选 GitHub 线路下载失败或超时，请重新测速并选择线路。",
                    CancellationToken.None).ConfigureAwait(true);
                if (route is null)
                {
                    return null;
                }

                CoreManagementOperationResult? result = null;
                var outcome = await _dialogService.RunWithProgressDialogAsync(
                    progressTitle,
                    async (progress, token) =>
                    {
                        try
                        {
                            result = await operation(route, progress, token).ConfigureAwait(true);
                        }
                        catch (Exception error) when (error is GithubRouteException or GithubRouteSelectionRequiredException)
                        {
                            result = new CoreManagementOperationResult(false, false, false, null, error.Message, true);
                        }
                    }).ConfigureAwait(true);

                if (outcome.Outcome == ProgressOperationOutcome.Canceled)
                {
                    await _dialogService.ShowMessageAsync("操作已取消", "下载或替换已取消，当前核心未变更。").ConfigureAwait(true);
                    return null;
                }

                var effective = result
                    ?? (outcome.Outcome == ProgressOperationOutcome.Failed
                        ? new CoreManagementOperationResult(false, false, false, null, outcome.Diagnostic ?? "核心操作失败", false)
                        : new CoreManagementOperationResult(false, false, false, null, "操作没有返回结果", false));
                if (effective.RouteInvalid && attempt == 0)
                {
                    _routePreferences.Invalidate();
                    continue;
                }

                if (!effective.Succeeded)
                {
                    await _dialogService.ShowMessageAsync(
                        "核心操作失败",
                        effective.RouteInvalid
                            ? $"{effective.Diagnostic}；重新选择的线路仍然不可用，请稍后重试。"
                            : effective.Diagnostic,
                        isError: true).ConfigureAwait(true);
                    return effective;
                }

                RefreshInstallationState();
                NotifyUpdateChanged();
                await _dialogService.ShowMessageAsync(
                    "操作完成",
                    BuildSuccessMessage(progressTitle, effective)).ConfigureAwait(true);
                await VerifyDependenciesAfterMutationAsync().ConfigureAwait(true);
                return effective;
            }

            await _dialogService.ShowMessageAsync(
                "核心操作失败",
                "GitHub 下载线路连续失败，请稍后重新选择线路。",
                isError: true).ConfigureAwait(true);
            return null;
        }
        finally
        {
            _isBusy = false;
            NotifyBusyChanged();
        }
    }

    private async Task<CoreManagementOperationResult?> RunLocalMutationAsync(
        string progressTitle,
        Func<Task<CoreManagementOperationResult>> operation,
        string successMessage)
    {
        if (_isBusy)
        {
            return null;
        }

        _isBusy = true;
        NotifyBusyChanged();
        try
        {
            CoreManagementOperationResult? result = null;
            var outcome = await _dialogService.RunWithProgressDialogAsync(
                progressTitle,
                async (_, _) =>
                {
                    result = await operation().ConfigureAwait(true);
                }).ConfigureAwait(true);
            var effective = result
                ?? (outcome.Outcome == ProgressOperationOutcome.Failed
                    ? new CoreManagementOperationResult(false, false, false, null, outcome.Diagnostic ?? "核心操作失败", false)
                    : new CoreManagementOperationResult(false, false, false, null, "操作没有返回结果", false));
            if (!effective.Succeeded)
            {
                await _dialogService.ShowMessageAsync("操作失败", effective.Diagnostic, isError: true).ConfigureAwait(true);
                return effective;
            }

            RefreshInstallationState();
            NotifyUpdateChanged();
            await _dialogService.ShowMessageAsync("操作完成", successMessage).ConfigureAwait(true);
            await VerifyDependenciesAfterMutationAsync().ConfigureAwait(true);
            return effective;
        }
        finally
        {
            _isBusy = false;
            NotifyBusyChanged();
        }
    }

    private async Task<T> ReadWithProgressAsync<T>(string label, Func<CancellationToken, Task<T>> operation, bool maySelectRoute = true)
    {
        T result = default!;
        Exception? failure = null;
        var outcome = await _dialogService.RunWithProgressDialogAsync(label, async (_, token) =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                result = await operation(deadline.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException error) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                failure = new GithubRemoteException(GithubFailureKind.Network, $"{label}超过 60 秒，已终止本次请求。", innerException: error);
                throw failure;
            }
            catch (Exception error)
            {
                failure = error;
                throw;
            }
        }).ConfigureAwait(true);
        if (outcome.Outcome == ProgressOperationOutcome.Canceled)
        {
            throw new OperationCanceledException($"{label}已取消");
        }
        if (failure is GithubRemoteException { Kind: GithubFailureKind.Network or GithubFailureKind.RouteSelectionRequired } && maySelectRoute)
        {
            _diagnostics.Record($"{label}线路失败，等待重新选择线路", failure);
            _routePreferences.Invalidate();
            var route = await EnsureRouteSelectedAsync($"{label}失败：{failure.Message}\n请重新选择线路，或取消本次操作。", CancellationToken.None).ConfigureAwait(true);
            if (route is null)
            {
                throw new OperationCanceledException($"{label}已取消");
            }
            return await ReadWithProgressAsync(label, operation, maySelectRoute: false).ConfigureAwait(true);
        }
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        if (outcome.Outcome != ProgressOperationOutcome.Completed)
        {
            throw new InvalidOperationException(outcome.Diagnostic ?? $"{label}失败");
        }
        return result;
    }

    private async Task<bool> RunRemoteReadAsync(string label, Func<CancellationToken, Task> operation)
    {
        if (_isBusy)
        {
            return false;
        }

        _isBusy = true;
        NotifyBusyChanged();
        try
        {
            var route = await EnsureRouteSelectedAsync(
                $"读取{label}需要访问 GitHub，请先测速并选择线路。",
                CancellationToken.None).ConfigureAwait(true);
            if (route is null)
            {
                return false;
            }

            await ReadWithProgressAsync(label, async token =>
            {
                await operation(token).ConfigureAwait(true);
                return true;
            }).ConfigureAwait(true);
            NotifyRateLimitChanged();
            return true;
        }
        catch (Exception error)
        {
            await ShowRemoteReadFailureAsync(label, error).ConfigureAwait(true);
            return false;
        }
        finally
        {
            _isBusy = false;
            NotifyBusyChanged();
        }
    }

    private async Task<string?> EnsureRouteSelectedAsync(string reason, CancellationToken cancellationToken)
    {
        var preference = _routePreferences.Read();
        if (preference.Confirmed)
        {
            return preference.ProxyId;
        }

        var selected = await _dialogService.ChooseGithubRouteAsync(
            reason,
            preference.ProxyId,
            _speedTester,
            cancellationToken).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(selected))
        {
            return null;
        }

        _routePreferences.Confirm(selected);
        return selected;
    }

    private static string BuildSuccessMessage(string progressTitle, CoreManagementOperationResult result)
    {
        var version = result.Installation?.Manifest is { } manifest
            ? $"当前版本：{manifest.Version ?? "未知"} · {manifest.ShortSha}（{manifest.Branch}）"
            : null;
        return version is null
            ? $"{progressTitle}已完成。"
            : $"{progressTitle}已完成。\n{version}";
    }

    private CoreInstallationInfo RefreshInstallationState()
    {
        var previousSource = Manifest is { } previous ? (previous.Repository, previous.Branch) : ((string, string)?)null;
        _installation = _management.Inspect(SelectedVariant);
        if (previousSource != (Manifest is { } current ? (current.Repository, current.Branch) : ((string, string)?)null))
        {
            ClearPullRequestPage();
        }
        History = _management.GetHistory(SelectedVariant);
        DisplayName = Manifest?.DisplayName ?? DisplayName;
        OnPropertyChanged(nameof(IdentityEyebrow));
        ReconcileUpdateResult();
        foreach (var property in new[]
        {
            nameof(Installation), nameof(Manifest), nameof(IsInstalled), nameof(IsNotInstalled),
            nameof(InstallationStatusText), nameof(InstallationDetailText), nameof(RepositoryDisplay),
            nameof(BranchDisplay), nameof(CommitDisplay), nameof(VersionDisplay), nameof(InstalledAtDisplay),
            nameof(DisplayNameValue), nameof(IdentityEyebrow), nameof(IdentityMetaText),
            nameof(MergedPullRequestNumbers), nameof(HasMergedPullRequests), nameof(MergedPullRequestText),
            nameof(CanInstallOfficial), nameof(CanInstallDev), nameof(CanInstallCustom),
            nameof(CanOperateInstalled), nameof(InstallDescription),
            // 切换分支的可用性依赖"当前安装的分支"，安装态一变就要重算。
            nameof(CanSwitchSelectedBranch),
        })
        {
            OnPropertyChanged(property);
        }

        return _installation;
    }

    /// <summary>
    /// 安装的提交变了以后，缓存里的更新结论可能已经不成立：
    /// - 新装的提交就是缓存里的远端提交 → 这是"刚应用完这次更新"，保留结果，界面显示"已是最新"；
    /// - 否则（重装、回退、切分支）缓存结论已失效，直接丢弃，回到"尚未检查"而不是继续报有新版本。
    /// </summary>
    private void ReconcileUpdateResult()
    {
        var installedCommit = Manifest?.CommitSha;
        if (_updateResult is null || string.Equals(_updateResultCommit, installedCommit, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _updateResultCommit = installedCommit;
        if (!RemoteMatchesInstalled)
        {
            _updateResult = null;
        }
    }

    private GithubRepositoryReference GetInstalledRepository()
    {
        return Manifest is null
            ? throw new InvalidOperationException("当前核心缺少来源 manifest")
            : GithubRepositoryReference.Parse(Manifest.Repository).WithBranch(Manifest.Branch);
    }

    private string NormalizeDisplayName(string fallback)
    {
        var value = DisplayName.Trim();
        return value.Length == 0 ? fallback : value;
    }

    private void NotifyBusyChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanInstallOfficial));
        OnPropertyChanged(nameof(CanInstallDev));
        OnPropertyChanged(nameof(CanInstallCustom));
        OnPropertyChanged(nameof(CanOperateInstalled));
        OnPropertyChanged(nameof(CanActivateSelectedVariant));
        OnPropertyChanged(nameof(CanBuildPullRequestStack));
        OnPropertyChanged(nameof(CanApplyUpdate));
        ActivateSelectedVariantCommand.NotifyCanExecuteChanged();
        InstallOfficialCommand.NotifyCanExecuteChanged();
        InstallDevCommand.NotifyCanExecuteChanged();
        InstallCustomCommand.NotifyCanExecuteChanged();
        CheckUpdateCommand.NotifyCanExecuteChanged();
        ShowUpdateDetailsCommand.NotifyCanExecuteChanged();
        ApplyUpdateCommand.NotifyCanExecuteChanged();
        ReinstallCommand.NotifyCanExecuteChanged();
        RollbackCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        RenameCoreCommand.NotifyCanExecuteChanged();
        LoadBranchesCommand.NotifyCanExecuteChanged();
        SwitchBranchCommand.NotifyCanExecuteChanged();
        LoadCommitsCommand.NotifyCanExecuteChanged();
        NextCommitPageCommand.NotifyCanExecuteChanged();
        PreviousCommitPageCommand.NotifyCanExecuteChanged();
        ShowCommitDetailsCommand.NotifyCanExecuteChanged();
        RollbackToCommitCommand.NotifyCanExecuteChanged();
        LoadPullRequestsCommand.NotifyCanExecuteChanged();
        NextPullRequestPageCommand.NotifyCanExecuteChanged();
        PreviousPullRequestPageCommand.NotifyCanExecuteChanged();
        ShowPullRequestDetailsCommand.NotifyCanExecuteChanged();
        TogglePullRequestSelectionCommand.NotifyCanExecuteChanged();
        MoveSelectedPullRequestUpCommand.NotifyCanExecuteChanged();
        MoveSelectedPullRequestDownCommand.NotifyCanExecuteChanged();
        BuildPullRequestStackCommand.NotifyCanExecuteChanged();
        SwitchSelectedBranchCommand.NotifyCanExecuteChanged();
        ConfigureGithubTokenCommand.NotifyCanExecuteChanged();
        OpenCommitsPageCommand.NotifyCanExecuteChanged();
        OpenPullRequestsPageCommand.NotifyCanExecuteChanged();
        BackToOverviewCommand.NotifyCanExecuteChanged();
    }

    private void NotifyUpdateChanged()
    {
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(UpdateStatusText));
        OnPropertyChanged(nameof(UpdateDetailText));
        OnPropertyChanged(nameof(CanApplyUpdate));
        ApplyUpdateCommand.NotifyCanExecuteChanged();
        ShowUpdateDetailsCommand.NotifyCanExecuteChanged();
    }

    private void NotifyRateLimitChanged()
    {
        OnPropertyChanged(nameof(RateLimitText));
    }
}
