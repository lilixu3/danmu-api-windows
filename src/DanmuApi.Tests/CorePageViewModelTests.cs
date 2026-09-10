using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
using DanmuApi.Core;

namespace DanmuApi.Tests;

public sealed partial class CorePageViewModelTests
{
    [Fact]
    public async Task UnconfirmedRoutePromptsOnceThenInstallsWithSelectedRoute()
    {
        var routeStore = new RecordingRoutePreferenceStore(confirmed: false);
        var management = new RecordingManagementService
        {
            InstallResult = new CoreManagementOperationResult(true, true, true, Installed(), "核心操作已完成"),
        };
        var dialogs = new RecordingDialogService { RouteSelection = "gh_proxy_org" };
        var viewModel = CreateViewModel(management, routeStore, dialogs);

        await viewModel.InstallOfficialCommand.ExecuteAsync(null);

        Assert.Equal(["gh_proxy_org"], dialogs.SelectedRoutes);
        Assert.Single(dialogs.RoutePrompts);
        Assert.Equal(["gh_proxy_org"], routeStore.ConfirmedIds);
        Assert.Equal("gh_proxy_org", management.LastInstallProxyId);
        Assert.Equal(["安装稳定核心"], dialogs.ProgressTitles);
        var success = Assert.Single(dialogs.Messages);
        Assert.False(success.IsError);
        Assert.Contains("安装稳定核心已完成", success.Message, StringComparison.Ordinal);
        Assert.Contains("1.0.0", success.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RoutePromptKeepsReusingConfirmedRouteWithoutAskingAgain()
    {
        var routeStore = new RecordingRoutePreferenceStore(confirmed: true) { ProxyId = "cdn_gh_proxy" };
        var management = new RecordingManagementService
        {
            InstallResult = new CoreManagementOperationResult(true, true, true, Installed(), "核心操作已完成"),
        };
        var dialogs = new RecordingDialogService();
        var viewModel = CreateViewModel(management, routeStore, dialogs);

        await viewModel.InstallOfficialCommand.ExecuteAsync(null);

        Assert.Empty(dialogs.RoutePrompts);
        Assert.Equal("cdn_gh_proxy", management.LastInstallProxyId);
    }

    [Fact]
    public async Task CancelledRoutePromptSkipsInstallSilently()
    {
        var routeStore = new RecordingRoutePreferenceStore(confirmed: false);
        var management = new RecordingManagementService();
        var dialogs = new RecordingDialogService { RouteSelection = null };
        var viewModel = CreateViewModel(management, routeStore, dialogs);

        await viewModel.InstallOfficialCommand.ExecuteAsync(null);

        Assert.Empty(routeStore.ConfirmedIds);
        Assert.Null(management.LastInstallProxyId);
        Assert.Empty(dialogs.ProgressTitles);
        Assert.Empty(dialogs.Messages);
    }

    [Fact]
    public async Task InvalidRouteInvalidatesOnceThenRetriesWithFreshSelection()
    {
        var routeStore = new RecordingRoutePreferenceStore(confirmed: true) { ProxyId = "gh_proxy_org" };
        var management = new RecordingManagementService
        {
            InstallResults =
            {
                new CoreManagementOperationResult(false, false, false, null, "线路超时", true),
                new CoreManagementOperationResult(true, true, true, Installed(), "核心操作已完成"),
            },
        };
        var dialogs = new RecordingDialogService { RouteSelection = "hk_gh_proxy" };
        var viewModel = CreateViewModel(management, routeStore, dialogs);

        await viewModel.InstallOfficialCommand.ExecuteAsync(null);

        Assert.Equal(1, routeStore.InvalidateCalls);
        Assert.Equal(["hk_gh_proxy"], dialogs.SelectedRoutes);
        Assert.Equal("hk_gh_proxy", management.LastInstallProxyId);
        Assert.Equal(2, dialogs.ProgressTitles.Count);
        var success = Assert.Single(dialogs.Messages);
        Assert.False(success.IsError);
    }

    [Fact]
    public async Task RepeatedRouteFailureStopsAfterOneRetryWithExplicitError()
    {
        var routeStore = new RecordingRoutePreferenceStore(confirmed: true) { ProxyId = "gh_proxy_org" };
        var management = new RecordingManagementService
        {
            InstallResults =
            {
                new CoreManagementOperationResult(false, false, false, null, "线路超时", true),
                new CoreManagementOperationResult(false, false, false, null, "仍然超时", true),
            },
        };
        var dialogs = new RecordingDialogService { RouteSelection = "hk_gh_proxy" };
        var viewModel = CreateViewModel(management, routeStore, dialogs);

        await viewModel.InstallOfficialCommand.ExecuteAsync(null);

        Assert.Equal(2, management.InstallCalls);
        Assert.Single(dialogs.RoutePrompts);
        var failure = Assert.Single(dialogs.Messages);
        Assert.True(failure.IsError);
        Assert.Contains("仍然不可用", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteRequiresConfirmationAndReportsResultDialog()
    {
        var management = new RecordingManagementService
        {
            Installation = Installed(),
        };
        var dialogs = new RecordingDialogService { Confirmation = false };
        var viewModel = CreateViewModel(management, new RecordingRoutePreferenceStore(true), dialogs);

        await viewModel.DeleteCommand.ExecuteAsync(null);

        Assert.Null(management.DeleteVariant);
        Assert.Equal(["删除核心"], dialogs.Confirmations);
        Assert.Empty(dialogs.Messages);

        dialogs.Confirmation = true;
        await viewModel.DeleteCommand.ExecuteAsync(null);

        Assert.Equal(ManagedCoreVariant.Stable, management.DeleteVariant);
        Assert.Equal(2, dialogs.Confirmations.Count);
        var success = Assert.Single(dialogs.Messages);
        Assert.Contains("核心已删除", success.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonRouteFailureIsNotTreatedAsRouteInvalidation()
    {
        var routeStore = new RecordingRoutePreferenceStore(confirmed: true) { ProxyId = "gh_proxy_org" };
        var management = new RecordingManagementService
        {
            InstallResults =
            {
                new CoreManagementOperationResult(false, false, true, null, "核心校验失败", false),
            },
        };
        var dialogs = new RecordingDialogService();
        var viewModel = CreateViewModel(management, routeStore, dialogs);

        await viewModel.InstallOfficialCommand.ExecuteAsync(null);

        Assert.Equal(0, routeStore.InvalidateCalls);
        Assert.Equal(1, management.InstallCalls);
        var failure = Assert.Single(dialogs.Messages);
        Assert.True(failure.IsError);
        Assert.Contains("核心校验失败", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CanceledProgressDialogKeepsCurrentCoreAndExplains()
    {
        var routeStore = new RecordingRoutePreferenceStore(confirmed: true) { ProxyId = "gh_proxy_org" };
        var management = new RecordingManagementService
        {
            InstallResult = new CoreManagementOperationResult(true, true, true, Installed(), "核心操作已完成"),
        };
        var dialogs = new RecordingDialogService { CancelProgressOperation = true };
        var viewModel = CreateViewModel(management, routeStore, dialogs);

        await viewModel.InstallOfficialCommand.ExecuteAsync(null);

        Assert.Equal(0, management.InstallCalls);
        var message = Assert.Single(dialogs.Messages);
        Assert.False(message.IsError);
        Assert.Contains("当前核心未变更", message.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReinstallAsksConfirmationThenProgressThenResult()
    {
        var management = new RecordingManagementService
        {
            Installation = Installed(),
            InstallResult = new CoreManagementOperationResult(true, true, true, Installed(), "核心操作已完成"),
        };
        var dialogs = new RecordingDialogService { Confirmation = false };
        var viewModel = CreateViewModel(management, new RecordingRoutePreferenceStore(true), dialogs);

        await viewModel.ReinstallCommand.ExecuteAsync(null);

        Assert.Equal(["重新安装"], dialogs.Confirmations);
        Assert.Equal(0, management.InstallCalls);

        dialogs.Confirmation = true;
        await viewModel.ReinstallCommand.ExecuteAsync(null);

        Assert.Equal(1, management.InstallCalls);
        Assert.Equal(["重新安装核心"], dialogs.ProgressTitles);
        var success = Assert.Single(dialogs.Messages);
        Assert.False(success.IsError);
    }

    [Fact]
    public async Task RollbackToCommitConfirmsWithRollbackWording()
    {
        var management = new RecordingManagementService
        {
            Installation = Installed(),
            InstallResult = new CoreManagementOperationResult(true, true, true, Installed(), "核心操作已完成"),
        };
        var dialogs = new RecordingDialogService { Confirmation = true };
        var viewModel = CreateViewModel(management, new RecordingRoutePreferenceStore(true), dialogs);
        var commit = new GithubCommit("cccccccccccccccccccccccccccccccccccccccc", "修复弹幕", "修复弹幕", "dev", DateTimeOffset.UtcNow, []);

        await viewModel.RollbackToCommitCommand.ExecuteAsync(commit);

        Assert.Equal(["确认回退"], dialogs.Confirmations);
        Assert.Equal(["回退到 ccccccc"], dialogs.ProgressTitles);
        var success = Assert.Single(dialogs.Messages);
        Assert.False(success.IsError);
    }

    [Fact]
    public async Task PullRequestInstallOnStableVariantExplainsCustomOnly()
    {
        var management = new RecordingManagementService { Installation = Installed() };
        var dialogs = new RecordingDialogService { Confirmation = true };
        var viewModel = CreateViewModel(management, new RecordingRoutePreferenceStore(true), dialogs);
        var pullRequest = PullRequest(7);

        await viewModel.InstallSelectedPullRequestCommand.ExecuteAsync(pullRequest);

        Assert.Equal(0, management.InstallCalls);
        var message = Assert.Single(dialogs.Messages);
        Assert.True(message.IsError);
        Assert.Contains("自定义核心", message.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullRequestInstallSuccessFeedbackNamesThePr()
    {
        var management = new RecordingManagementService
        {
            Installation = InstalledCustom(),
            InstallResult = new CoreManagementOperationResult(true, true, true, InstalledCustom(), "核心操作已完成"),
        };
        var dialogs = new RecordingDialogService { Confirmation = true };
        var routeStore = new RecordingRoutePreferenceStore(confirmed: true) { ProxyId = "gh_proxy_org" };
        var viewModel = CreateViewModel(management, routeStore, dialogs);
        viewModel.SelectedVariant = ManagedCoreVariant.Custom;
        var pullRequest = PullRequest(7);

        await viewModel.InstallSelectedPullRequestCommand.ExecuteAsync(pullRequest);

        Assert.Equal(1, management.InstallCalls);
        Assert.Equal("gh_proxy_org", management.LastInstallProxyId);
        var success = Assert.Single(dialogs.Messages);
        Assert.False(success.IsError);
        Assert.Contains("安装 PR #7", dialogs.ProgressTitles[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task BranchSwitchRequiresConfirmationBeforeInstall()
    {
        var management = new RecordingManagementService
        {
            Installation = Installed(),
            InstallResult = new CoreManagementOperationResult(true, true, true, Installed(), "核心操作已完成"),
        };
        var dialogs = new RecordingDialogService { Confirmation = false };
        var viewModel = CreateViewModel(management, new RecordingRoutePreferenceStore(true), dialogs);
        var branch = new GithubBranch("dev", "dddddddd", false);

        await viewModel.SwitchBranchCommand.ExecuteAsync(branch);

        Assert.Equal(["切换并重装"], dialogs.Confirmations);
        Assert.Equal(0, management.InstallCalls);
    }

    [Fact]
    public async Task LoadBranchesShowsResultDialogWithCount()
    {
        var management = new RecordingManagementService { Installation = Installed() };
        var dialogs = new RecordingDialogService();
        var viewModel = CreateViewModel(management, new RecordingRoutePreferenceStore(true), dialogs);

        await viewModel.LoadBranchesCommand.ExecuteAsync(null);

        Assert.Single(viewModel.Branches);
        var message = Assert.Single(dialogs.Messages);
        Assert.False(message.IsError);
        Assert.Contains("1 个分支", message.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateDetailsFlowOpensCompareDialogAndAppliesOnConfirm()
    {
        var update = new CoreUpdateCheckResult(
            ManagedCoreVariant.Stable,
            CoreUpdateCheckStatus.Checked,
            true,
            Installed().Manifest,
            new GithubCommit("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "新版本", "新版本", "dev", DateTimeOffset.UtcNow, []),
            null,
            DateTimeOffset.UtcNow,
            "发现新提交");
        var management = new RecordingManagementService
        {
            Installation = Installed(),
            InstallResult = new CoreManagementOperationResult(true, true, true, Installed(), "核心操作已完成"),
        };
        var remote = new CompareStubRemote(update.Remote);
        var dialogs = new RecordingDialogService { UpdateDetailsResult = true };
        var viewModel = CreateViewModel(management, new RecordingRoutePreferenceStore(true), dialogs, remote, new StubScheduler(update));

        await viewModel.CheckUpdateCommand.ExecuteAsync(null);
        await viewModel.ShowUpdateDetailsCommand.ExecuteAsync(null);

        Assert.NotNull(dialogs.LastComparison);
        Assert.Equal(["检查核心更新", "读取更新详情", "应用核心更新"], dialogs.ProgressTitles);
        Assert.Contains(dialogs.Messages, message => message.Title == "操作完成");
    }

    [Fact]
    public async Task CheckUpdateWithNoUpdateReportsLatest()
    {
        var management = new RecordingManagementService { Installation = Installed() };
        var dialogs = new RecordingDialogService();
        var viewModel = CreateViewModel(
            management,
            new RecordingRoutePreferenceStore(true),
            dialogs,
            new CompareStubRemote(null),
            new StubScheduler(new CoreUpdateCheckResult(
                ManagedCoreVariant.Stable,
                CoreUpdateCheckStatus.Checked,
                false,
                Installed().Manifest,
                null,
                null,
                DateTimeOffset.UtcNow,
                "当前已是最新提交")));

        await viewModel.CheckUpdateCommand.ExecuteAsync(null);

        var message = Assert.Single(dialogs.Messages);
        Assert.False(message.IsError);
        Assert.Contains("已是最新", message.Message, StringComparison.Ordinal);
    }

    /// <summary>检查到新版本时必须能就地升级，不需要先关掉提示再回总览点「应用更新」。</summary>
    [Fact]
    public async Task CheckUpdateOffersImmediateApplyAndRunsItFromTheSameDialog()
    {
        var update = PendingUpdate();
        var management = new RecordingManagementService
        {
            Installation = Installed(),
            AppliedInstallation = InstalledApplied(update),
            InstallResult = new CoreManagementOperationResult(true, true, true, InstalledApplied(update), "核心操作已完成"),
        };
        var dialogs = new RecordingDialogService { Confirmation = true };
        var viewModel = CreateViewModel(management, new RecordingRoutePreferenceStore(true), dialogs,
            new CompareStubRemote(update.Remote), new StubScheduler(update));

        await viewModel.CheckUpdateCommand.ExecuteAsync(null);

        // 一次确认就把更新跑完：确认文案是「立即更新」，随后直接进入应用更新的进度对话框。
        Assert.Equal(["立即更新"], dialogs.Confirmations);
        Assert.Equal(1, management.InstallCalls);
        Assert.Equal(["检查核心更新", "应用核心更新"], dialogs.ProgressTitles);
    }

    /// <summary>只检查不升级时，页面仍要如实显示"有新版本"。</summary>
    [Fact]
    public async Task DecliningImmediateApplyKeepsThePendingUpdateVisible()
    {
        var update = PendingUpdate();
        var dialogs = new RecordingDialogService { Confirmation = false };
        var viewModel = CreateViewModel(new RecordingManagementService { Installation = Installed() },
            new RecordingRoutePreferenceStore(true), dialogs, new CompareStubRemote(update.Remote), new StubScheduler(update));

        await viewModel.CheckUpdateCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasUpdate);
        Assert.Contains("发现新提交", viewModel.UpdateStatusText, StringComparison.Ordinal);
    }

    /// <summary>升级完成后立即不再显示"有新版本"，不需要用户手动再检查一次。</summary>
    [Fact]
    public async Task AppliedUpdateStopsReportingANewVersionWithoutAnotherCheck()
    {
        var update = PendingUpdate();
        var management = new RecordingManagementService
        {
            Installation = Installed(),
            AppliedInstallation = InstalledApplied(update),
            InstallResult = new CoreManagementOperationResult(true, true, true, InstalledApplied(update), "核心操作已完成"),
        };
        var dialogs = new RecordingDialogService { Confirmation = true };
        var viewModel = CreateViewModel(management, new RecordingRoutePreferenceStore(true), dialogs,
            new CompareStubRemote(update.Remote), new StubScheduler(update));

        await viewModel.CheckUpdateCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasUpdate);
        Assert.False(viewModel.CanApplyUpdate);
        Assert.Equal("当前已是最新提交", viewModel.UpdateStatusText);
        Assert.Contains("已更新到", viewModel.UpdateDetailText, StringComparison.Ordinal);
    }

    /// <summary>装上的提交和缓存里的远端提交对不上（重装/回退/切分支）时，旧的更新结论必须失效。</summary>
    [Fact]
    public async Task UnrelatedMutationDropsTheStaleUpdateConclusion()
    {
        var update = PendingUpdate();
        var management = new RecordingManagementService
        {
            Installation = Installed(),
            // 装完以后的提交既不是原提交 aaaa… 也不是缓存里的远端提交，属于"换了别的版本"。
            AppliedInstallation = Installed() with { Manifest = Installed().Manifest! with { CommitSha = "cccccccccccccccccccccccccccccccccccccccc" } },
            InstallResult = new CoreManagementOperationResult(true, true, true,
                Installed() with { Manifest = Installed().Manifest! with { CommitSha = "cccccccccccccccccccccccccccccccccccccccc" } },
                "核心操作已完成"),
        };
        var dialogs = new RecordingDialogService { Confirmation = false };
        var viewModel = CreateViewModel(management, new RecordingRoutePreferenceStore(true), dialogs,
            new CompareStubRemote(update.Remote), new StubScheduler(update));
        await viewModel.CheckUpdateCommand.ExecuteAsync(null);
        Assert.True(viewModel.HasUpdate);

        dialogs.Confirmation = true;
        await viewModel.ReinstallCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasUpdate);
        Assert.Equal("尚未检查", viewModel.UpdateStatusText);
    }

    /// <summary>
    /// 点开分支下拉时的刷新必须是静默的：不能弹模态进度对话框（用户还没选完就被打断），
    /// 也不能把用户已经选好的目标分支清掉——否则"切换"永远点不了。
    /// </summary>
    [Fact]
    public async Task QuietBranchRefreshNeitherBlocksNorClearsThePendingChoice()
    {
        var remote = new StubRemote
        {
            Branches = [new GithubBranch("main", "aaaaaaa", false), new GithubBranch("develop", "bbbbbbb", false)],
        };
        var dialogs = new RecordingDialogService();
        var viewModel = CreateViewModel(new RecordingManagementService { Installation = Installed() },
            new RecordingRoutePreferenceStore(true), dialogs, remote);

        await viewModel.RefreshBranchesQuietlyAsync();

        // 静默：没有进度对话框，也没有结果弹窗。
        Assert.Empty(dialogs.ProgressTitles);
        Assert.Empty(dialogs.Messages);
        Assert.Equal(2, viewModel.Branches.Count);
        Assert.Equal("", viewModel.BranchStatusText);

        // 用户选了目标分支后再次点开下拉（会再刷一次），选择必须保留。
        viewModel.SelectedBranch = viewModel.Branches.Single(branch => branch.Name == "develop");
        Assert.True(viewModel.CanSwitchSelectedBranch);
        await viewModel.RefreshBranchesQuietlyAsync();

        Assert.Equal("develop", viewModel.SelectedBranch!.Name);
        Assert.True(viewModel.CanSwitchSelectedBranch);
        Assert.Equal(2, remote.BranchCalls);
    }

    [Fact]
    public async Task QuietBranchRefreshFailureIsShownInlineAndRecordedWithoutADialog()
    {
        var remote = new StubRemote { BranchesFailure = new IOException("分支接口不可用") };
        var dialogs = new RecordingDialogService();
        var diagnostics = new StubDiagnostics();
        var viewModel = CreateViewModel(new RecordingManagementService { Installation = Installed() },
            new RecordingRoutePreferenceStore(true), dialogs, remote, diagnostics: diagnostics);

        await viewModel.RefreshBranchesQuietlyAsync();

        Assert.True(viewModel.HasBranchStatus);
        Assert.Contains("刷新失败", viewModel.BranchStatusText, StringComparison.Ordinal);
        Assert.Contains("分支接口不可用", viewModel.BranchStatusText, StringComparison.Ordinal);
        Assert.Contains("刷新分支失败", diagnostics.LastDiagnostic ?? "", StringComparison.Ordinal);
        // 失败也只在行内提示，不打断用户。
        Assert.Empty(dialogs.Messages);
    }

    private static CoreUpdateCheckResult PendingUpdate() => new(
        ManagedCoreVariant.Stable,
        CoreUpdateCheckStatus.Checked,
        true,
        Installed().Manifest,
        new GithubCommit("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "新版本", "新版本", "dev", DateTimeOffset.UtcNow, []),
        null,
        DateTimeOffset.UtcNow,
        "发现新提交");

    /// <summary>模拟"这次更新已经装完"：安装后的 manifest 提交就是更新目标提交。</summary>
    private static CoreInstallationInfo InstalledApplied(CoreUpdateCheckResult update) =>
        Installed() with { Manifest = Installed().Manifest! with { CommitSha = update.Remote!.Sha } };

    [Fact]
    public async Task RenameCoreUsesPromptAndPersistsThroughManagement()
    {
        var management = new RecordingManagementService { Installation = Installed() };
        var dialogs = new RecordingDialogService { PromptText = "我的核心" };
        var viewModel = CreateViewModel(management, new RecordingRoutePreferenceStore(true), dialogs);

        await viewModel.RenameCoreCommand.ExecuteAsync(null);

        Assert.Equal(["核心设置"], dialogs.PromptRequests);
        Assert.Equal("我的核心", management.RenamedTo);
        var success = Assert.Single(dialogs.Messages);
        Assert.False(success.IsError);
    }

    private static CorePageViewModel CreateViewModel(
        RecordingManagementService management,
        RecordingRoutePreferenceStore routeStore,
        RecordingDialogService dialogs,
        IGithubCoreRemote? remote = null,
        ICoreUpdateScheduler? scheduler = null,
        StubDiagnostics? diagnostics = null) =>
        new(
            management,
            remote ?? new StubRemote(),
            routeStore,
            new StubSpeedTester(),
            scheduler ?? new StubScheduler(null),
            dialogs,
            diagnostics ?? new StubDiagnostics(),
            new StubGithubTokenStore());

    private static CoreInstallationInfo Installed()
    {
        var manifest = new CoreInstallationManifest(
            1,
            ManagedCoreVariant.Stable,
            "huangxd-/danmu_api",
            "main",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "1.0.0",
            "官方核心",
            CoreInstallKind.Branch,
            null,
            DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        return new CoreInstallationInfo(ManagedCoreVariant.Stable, "dir", true, true, "1.0.0", manifest, null);
    }

    private static CoreInstallationInfo InstalledCustom()
    {
        var manifest = new CoreInstallationManifest(
            1,
            ManagedCoreVariant.Custom,
            "lilixu3/danmu_api",
            "main",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "1.0.0",
            "自定义核心",
            CoreInstallKind.Branch,
            null,
            DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        return new CoreInstallationInfo(ManagedCoreVariant.Custom, "dir", true, true, "1.0.0", manifest, null);
    }

    private static GithubPullRequest PullRequest(int number) => new(
        number,
        "实验特性",
        "body",
        "open",
        "contributor",
        "main",
        "contributor/danmu_api",
        "feature",
        "cccccccccccccccccccccccccccccccccccccccc",
        false,
        false,
        DateTimeOffset.UtcNow,
        null,
        10,
        2,
        3);

    private sealed class RecordingManagementService : ICoreManagementService
    {
        public CoreInstallationInfo Installation { get; init; } = new(
            ManagedCoreVariant.Stable,
            "dir",
            false,
            false,
            null,
            null,
            "核心尚未安装");
        public List<CoreManagementOperationResult> InstallResults { get; } = [];
        private int _installResultIndex;
        public CoreManagementOperationResult? InstallResult { get; init; }
        /// <summary>安装成功后磁盘上的状态；为空表示安装不改变 Inspect 的返回（保持旧行为）。</summary>
        public CoreInstallationInfo? AppliedInstallation { get; init; }
        public int InstallCalls { get; private set; }
        public string? LastInstallProxyId { get; private set; }
        public ManagedCoreVariant? DeleteVariant { get; private set; }
        public string? RenamedTo { get; private set; }

        // 真实实现里 Inspect 读的是磁盘：安装完成后必须看到新提交，否则"更新后仍显示有新版本"
        // 这类状态在测试里永远复现不出来。
        public CoreInstallationInfo Inspect(ManagedCoreVariant variant) =>
            InstallCalls > 0 && AppliedInstallation is not null ? AppliedInstallation : Installation;
        public IReadOnlyList<CoreVersionRecord> LocalHistory { get; set; } = [];
        public string? RestoredHistoryId { get; private set; }
        public IReadOnlyList<CoreVersionRecord> GetHistory(ManagedCoreVariant variant) => LocalHistory.Where(record => record.Variant == variant).ToArray();

        public Task<GithubRepositoryReference> ResolveRepositoryAsync(GithubRepositoryReference repository, CancellationToken cancellationToken = default) =>
            Task.FromResult(repository.HasExplicitBranch ? repository : repository.WithBranch("main"));

        public Task<CoreManagementOperationResult> InstallBranchAsync(
            ManagedCoreVariant variant,
            GithubRepositoryReference repository,
            string displayName,
            string proxyId,
            IProgress<CoreInstallProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            InstallCalls++;
            LastInstallProxyId = proxyId;
            return Task.FromResult(_installResultIndex < InstallResults.Count
                ? InstallResults[_installResultIndex++]
                : InstallResult ?? throw new InvalidOperationException("test install result missing"));
        }

        public Task<CoreManagementOperationResult> InstallCommitAsync(
            ManagedCoreVariant variant,
            GithubRepositoryReference repository,
            string branch,
            string commitSha,
            string displayName,
            string proxyId,
            IProgress<CoreInstallProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            InstallBranchAsync(variant, repository, displayName, proxyId, progress, cancellationToken);

        public Task<CoreManagementOperationResult> InstallPullRequestAsync(
            GithubRepositoryReference baseRepository,
            int pullRequestNumber,
            string displayName,
            string proxyId,
            IProgress<CoreInstallProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            InstallBranchAsync(ManagedCoreVariant.Custom, baseRepository, displayName, proxyId, progress, cancellationToken);

        public Task<CoreManagementOperationResult> ApplyUpdateAsync(
            CoreUpdateCheckResult update,
            string proxyId,
            IProgress<CoreInstallProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            InstallBranchAsync(update.Variant, GithubRepositoryReference.Official("main"), "官方核心", proxyId, progress, cancellationToken);

        public Task<CoreManagementOperationResult> ReinstallAsync(
            ManagedCoreVariant variant,
            string proxyId,
            IProgress<CoreInstallProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            InstallBranchAsync(variant, GithubRepositoryReference.Official("main"), "官方核心", proxyId, progress, cancellationToken);

        public Task<CoreManagementOperationResult> RollbackAsync(ManagedCoreVariant variant, string historyId, CancellationToken cancellationToken = default)
        {
            RestoredHistoryId = historyId;
            return Task.FromResult(new CoreManagementOperationResult(true, true, true, Installation, "核心已回退"));
        }

        public Task<CoreManagementOperationResult> RenameAsync(ManagedCoreVariant variant, string displayName, CancellationToken cancellationToken = default)
        {
            RenamedTo = displayName;
            return Task.FromResult(new CoreManagementOperationResult(true, false, true, Installation, "核心显示名称已更新。"));
        }

        public Task<CoreManagementOperationResult> DeleteAsync(ManagedCoreVariant variant, CancellationToken cancellationToken = default)
        {
            DeleteVariant = variant;
            return Task.FromResult(new CoreManagementOperationResult(true, true, false, null, "核心已删除"));
        }
    }

    private sealed class RecordingRoutePreferenceStore(bool confirmed) : IGithubRoutePreferenceStore
    {
        public string ProxyId { get; set; } = GithubProxyCatalog.OriginalId;
        public List<string> ConfirmedIds { get; } = [];
        public int InvalidateCalls { get; private set; }
        public GithubRoutePreference Read() => new(ProxyId, confirmed);
        public void Confirm(string proxyId)
        {
            ConfirmedIds.Add(proxyId);
            ProxyId = proxyId;
            confirmed = true;
        }

        public void Invalidate()
        {
            InvalidateCalls++;
            confirmed = false;
        }
    }

    private sealed class StubRemote : IGithubCoreRemote
    {
        public GithubRateLimit? LastRateLimit => null;
        /// <summary>分支列表内容；默认与当前安装分支一致，便于"不该允许切换"的用例。</summary>
        public IReadOnlyList<GithubBranch> Branches { get; set; } = [new GithubBranch("main", "aaaaaaa", false)];
        public Exception? BranchesFailure { get; set; }
        public int BranchCalls { get; private set; }
        public Task<GithubRepositoryMetadata> GetRepositoryAsync(GithubRepositoryReference repository, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GithubRepositoryMetadata(repository.FullName, "main", null, false));
        public Task<IReadOnlyList<GithubBranch>> GetAllBranchesAsync(GithubRepositoryReference repository, CancellationToken cancellationToken = default)
        {
            BranchCalls++;
            return BranchesFailure is null
                ? Task.FromResult(Branches)
                : Task.FromException<IReadOnlyList<GithubBranch>>(BranchesFailure);
        }
        public Task<GithubCommit> GetCommitAsync(GithubRepositoryReference repository, string reference, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GithubCommit("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "title", "title", "dev", DateTimeOffset.UtcNow, []));
        public Task<GithubCommitPage> GetCommitsAsync(GithubRepositoryReference repository, string reference, int page = 1, int pageSize = 30, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GithubCommitPage([], 1, false, false));
        public Task<GithubCommitDetails> GetCommitDetailsAsync(GithubRepositoryReference repository, string sha, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<GithubPullRequestPage> GetPullRequestsAsync(GithubRepositoryReference repository, string baseBranch, string state = "open", int page = 1, int pageSize = 30, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GithubPullRequestPage([], 1, false, false));
        public Task<GithubPullRequest> GetPullRequestAsync(GithubRepositoryReference repository, int number, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<GithubFileChange>> GetAllPullRequestFilesAsync(GithubRepositoryReference repository, int number, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<GithubCompareResult> GetCompareAsync(GithubRepositoryReference repository, string baseSha, string headSha, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<GithubRateLimit> GetRateLimitAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class CompareStubRemote(GithubCommit? remote) : StubRemoteWithCompare(
        remote is null
            ? null
            : new GithubCompareResult(
                "ahead",
                0,
                2,
                2,
                [remote],
                [new GithubFileChange("danmu_api/worker.js", null, "modified", 5, 1, 6, "@@ -1 +1 @@\n-old\n+new", null)],
                5,
                1,
                false,
                false));

    private class StubRemoteWithCompare(GithubCompareResult? comparison) : IGithubCoreRemote
    {
        public GithubRateLimit? LastRateLimit => null;
        public Task<GithubRepositoryMetadata> GetRepositoryAsync(GithubRepositoryReference repository, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GithubRepositoryMetadata(repository.FullName, "main", null, false));
        public Task<IReadOnlyList<GithubBranch>> GetAllBranchesAsync(GithubRepositoryReference repository, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GithubBranch>>([]);
        public Task<GithubCommit> GetCommitAsync(GithubRepositoryReference repository, string reference, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GithubCommit("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "title", "title", "dev", DateTimeOffset.UtcNow, []));
        public Task<GithubCommitPage> GetCommitsAsync(GithubRepositoryReference repository, string reference, int page = 1, int pageSize = 30, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GithubCommitPage([], 1, false, false));
        public Task<GithubCommitDetails> GetCommitDetailsAsync(GithubRepositoryReference repository, string sha, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<GithubPullRequestPage> GetPullRequestsAsync(GithubRepositoryReference repository, string baseBranch, string state = "open", int page = 1, int pageSize = 30, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GithubPullRequestPage([], 1, false, false));
        public Task<GithubPullRequest> GetPullRequestAsync(GithubRepositoryReference repository, int number, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<GithubFileChange>> GetAllPullRequestFilesAsync(GithubRepositoryReference repository, int number, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<GithubCompareResult> GetCompareAsync(GithubRepositoryReference repository, string baseSha, string headSha, CancellationToken cancellationToken = default) =>
            comparison is null ? Task.FromException<GithubCompareResult>(new NotSupportedException()) : Task.FromResult(comparison);
        public Task<GithubRateLimit> GetRateLimitAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubSpeedTester : IGithubProxySpeedTester
    {
        public Task<IReadOnlyList<GithubProxyLatencyResult>> TestAllAsync(
            IProgress<GithubProxyLatencyResult>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GithubProxyLatencyResult>>([]);
    }

#pragma warning disable CS0067
    private sealed class StubScheduler(CoreUpdateCheckResult? manualResult) : ICoreUpdateScheduler
    {
        public Func<CancellationToken, Task<CoreUpdateCheckResult>>? ManualOperation { get; init; }
        public event EventHandler<CoreUpdateCheckResult>? CheckCompleted;
        public event EventHandler<string>? DiagnosticChanged;
        public void Start() { }
        public void SetBackgroundActive(bool active) { }
        public void NotifyPolicyChanged() { }

        public Task<CoreUpdateCheckResult> CheckForegroundAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(manualResult ?? Checked());

        public Task<CoreUpdateCheckResult?> CheckBackgroundAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<CoreUpdateCheckResult?>(null);

        public Task<CoreUpdateCheckResult> CheckManualAsync(ManagedCoreVariant variant, CancellationToken cancellationToken = default) =>
            ManualOperation?.Invoke(cancellationToken) ?? Task.FromResult(manualResult ?? Checked());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static CoreUpdateCheckResult Checked() => new(
            ManagedCoreVariant.Stable,
            CoreUpdateCheckStatus.Checked,
            false,
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            "已是最新");
#pragma warning restore CS0067
    }

    private sealed class StubDiagnostics : IAppDiagnostics
    {
        public string? LastDiagnostic { get; private set; }
        public void Record(string message, Exception? error = null) =>
            LastDiagnostic = error is null ? message : $"{message}: {error.Message}";
    }

    private sealed class StubGithubTokenStore : IGithubTokenStore
    {
        private string? _token;
        public bool IsConfigured => _token is not null;
        public string? GetToken() => _token;
        public void Save(string token) => _token = token;
        public void Clear() => _token = null;
    }
}
