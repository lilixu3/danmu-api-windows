using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
using DanmuApi.Core;
using DanmuApi.Platform;

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
    public async Task InstallingDevVariantUsesTheDevUpstream()
    {
        // specs/02 §1 的三种变体里 dev 曾只在文档与运行层存在（.env 能写 dev），宿主侧装不了也管不了，
        // 从 Kotlin 版迁移过来的用户会直接卡住。这里锁住「dev 可安装、且只从 lilixu3/danmu_api 装」。
        var routeStore = new RecordingRoutePreferenceStore(confirmed: true) { ProxyId = "gh_proxy_org" };
        var management = new RecordingManagementService
        {
            InstallResult = new CoreManagementOperationResult(true, true, true, Installed(), "核心操作已完成"),
        };
        var dialogs = new RecordingDialogService();
        var viewModel = CreateViewModel(management, routeStore, dialogs);

        viewModel.SelectedVariant = ManagedCoreVariant.Dev;
        // 显示名称输入框是跨变体复用的（切变体不会改写用户已填的内容），留空才走各变体默认名。
        viewModel.DisplayName = string.Empty;
        Assert.True(viewModel.CanInstallDev);
        Assert.False(viewModel.CanInstallOfficial);
        Assert.False(viewModel.CanInstallCustom);
        Assert.Equal("开发核心", viewModel.VariantLabel);
        Assert.Equal("lilixu3/danmu_api", viewModel.RepositoryDisplay);

        await viewModel.InstallDevCommand.ExecuteAsync(null);

        Assert.Equal(ManagedCoreVariant.Dev, management.LastInstallVariant);
        Assert.Equal("lilixu3/danmu_api", management.LastInstallRepository?.FullName);
        Assert.Equal(CoreRepositorySource.Dev, management.LastInstallRepository?.Source);
        Assert.Equal("开发核心", management.LastInstallDisplayName);
        Assert.Equal(["安装开发核心"], dialogs.ProgressTitles);
    }

    [Fact]
    public void VariantsMapToOneStorageKeyDirectoryAndUpstream()
    {
        Assert.Equal("dev", ManagedCoreVariant.Dev.ToStorageKey());
        Assert.Equal("danmu_api_dev", ManagedCoreVariant.Dev.ToDirectoryName());
        Assert.Equal("lilixu3/danmu_api", ManagedCoreVariant.Dev.DefaultRepositoryFullName());
        Assert.Null(ManagedCoreVariant.Custom.DefaultRepositoryFullName());
        Assert.Equal(ManagedCoreVariant.Dev, ManagedCoreVariantExtensions.ParseManagedVariant("dev"));
        // 核心自己的别名（android-server.js 的 _getVariant）也要认，否则迁移过来的 .env 会直接判错。
        Assert.Equal(ManagedCoreVariant.Dev, ManagedCoreVariantExtensions.ParseManagedVariant("development"));
        var error = Assert.Throws<FormatException>(() =>
            ManagedCoreVariantExtensions.ParseManagedVariant("beta"));
        Assert.Contains("stable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositorySourceMustMatchTheVariantBeingInstalled()
    {
        Assert.True(GithubRepositoryReference.Official().Source.Matches(ManagedCoreVariant.Stable));
        Assert.True(GithubRepositoryReference.Dev().Source.Matches(ManagedCoreVariant.Dev));
        Assert.False(GithubRepositoryReference.Dev().Source.Matches(ManagedCoreVariant.Stable));
        Assert.False(GithubRepositoryReference.Official().Source.Matches(ManagedCoreVariant.Dev));
        // 自选仓库仍然只能装成自定义核心；把 lilixu3/danmu_api 当自定义仓库装也照旧允许。
        var parsed = GithubRepositoryReference.Parse("lilixu3/danmu_api");
        Assert.Equal(CoreRepositorySource.Custom, parsed.Source);
        Assert.True(parsed.Source.Matches(ManagedCoreVariant.Custom));
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
        Assert.Equal(["停止服务并删除核心"], dialogs.Confirmations);
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
    public void QueueMoveCommandsReorderAndClampAtTheEdges()
    {
        var management = new RecordingManagementService { Installation = Installed() };
        var model = CreateViewModel(management, new RecordingRoutePreferenceStore(true), new RecordingDialogService(),
            merge: new RecordingPullRequestMergeService());
        var first = PullRequest(12);
        var second = PullRequest(3);
        var third = PullRequest(7);
        model.TogglePullRequestSelectionCommand.Execute(first);
        model.TogglePullRequestSelectionCommand.Execute(second);
        model.TogglePullRequestSelectionCommand.Execute(third);
        Assert.Equal([12, 3, 7], model.SelectedPullRequests.Select(item => item.Number));

        model.MoveSelectedPullRequestUpCommand.Execute(third);
        Assert.Equal([12, 7, 3], model.SelectedPullRequests.Select(item => item.Number));
        model.MoveSelectedPullRequestDownCommand.Execute(first);
        Assert.Equal([7, 12, 3], model.SelectedPullRequests.Select(item => item.Number));

        // 越界不动：队列第一项再上移、最后一项再下移都必须保持原样。
        model.MoveSelectedPullRequestUpCommand.Execute(model.SelectedPullRequests[0]);
        model.MoveSelectedPullRequestDownCommand.Execute(model.SelectedPullRequests[2]);
        Assert.Equal([7, 12, 3], model.SelectedPullRequests.Select(item => item.Number));
        Assert.Contains("1. #7", model.PullRequestQueueSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void PullRequestRowsExposeQueuePositionAndActionLabel()
    {
        var management = new RecordingManagementService { Installation = Installed() };
        var model = CreateViewModel(management, new RecordingRoutePreferenceStore(true), new RecordingDialogService(),
            merge: new RecordingPullRequestMergeService());
        var queued = PullRequest(12);
        model.PullRequests = [queued, PullRequest(3)];
        model.TogglePullRequestSelectionCommand.Execute(queued);

        var rows = model.PullRequestRows;
        Assert.Equal(1, rows.Single(row => row.Number == 12).QueuePosition);
        Assert.Equal("队列第 1 位", rows.Single(row => row.Number == 12).QueueBadgeText);
        Assert.Equal("移出队列", rows.Single(row => row.Number == 12).QueueActionText);
        Assert.Equal(0, rows.Single(row => row.Number == 3).QueuePosition);
        Assert.Equal("加入队列", rows.Single(row => row.Number == 3).QueueActionText);
    }

    [Fact]
    public void MergedStackIsVisibleOnTheCorePage()
    {
        var management = new RecordingManagementService
        {
            Installation = InstalledStack([12, 3]),
        };
        var model = CreateViewModel(management, new RecordingRoutePreferenceStore(true), new RecordingDialogService());

        Assert.True(model.HasMergedPullRequests);
        Assert.Equal([12, 3], model.MergedPullRequestNumbers);
        Assert.Equal("#12 #3", model.MergedPullRequestText);
        // 身份带第二行必须点明这是本地组合，并按基线上报提交，不能让用户以为装的是分支最新提交。
        Assert.Contains("本地 PR 组合 #12 #3", model.IdentityMetaText, StringComparison.Ordinal);
        Assert.Contains(model.Manifest!.ShortSha, model.IdentityMetaText, StringComparison.Ordinal);
    }

    [Fact]
    public void PullRequestsAlreadyMergedIntoTheCoreCannotBeQueuedAgain()
    {
        var management = new RecordingManagementService { Installation = InstalledStack([12]) };
        var model = CreateViewModel(management, new RecordingRoutePreferenceStore(true), new RecordingDialogService(),
            merge: new RecordingPullRequestMergeService());
        var merged = PullRequest(12);
        var fresh = PullRequest(3);
        model.PullRequests = [merged, fresh];

        model.TogglePullRequestSelectionCommand.Execute(merged);

        Assert.Empty(model.SelectedPullRequests);
        var rows = model.PullRequestRows;
        Assert.True(rows.Single(row => row.Number == 12).IsIncludedInCore);
        Assert.False(rows.Single(row => row.Number == 12).CanQueue);
        Assert.Equal("已并入当前核心", rows.Single(row => row.Number == 12).InclusionBadgeText);
        Assert.True(rows.Single(row => row.Number == 3).CanQueue);

        model.TogglePullRequestSelectionCommand.Execute(fresh);
        Assert.Equal([3], model.SelectedPullRequests.Select(item => item.Number));
    }

    [Fact]
    public void PlainBranchInstallReportsNoMergedStack()
    {
        var management = new RecordingManagementService { Installation = Installed() };
        var model = CreateViewModel(management, new RecordingRoutePreferenceStore(true), new RecordingDialogService());

        Assert.False(model.HasMergedPullRequests);
        Assert.Empty(model.MergedPullRequestNumbers);
        Assert.Contains("分支 main", model.IdentityMetaText, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivateActionIsOfferedOnlyForANonRunningVariant()
    {
        var management = new RecordingManagementService { Installation = Installed() };
        var onOther = CreateViewModel(management, new RecordingRoutePreferenceStore(true), new RecordingDialogService(),
            activeVariant: new RecordingActiveCoreVariantStore(ManagedCoreVariant.Custom),
            variantSwitch: (variant, _) => Task.FromResult(new RuntimeVariantSwitchResult(true, variant, true, false, "ok")));
        Assert.True(onOther.CanActivateSelectedVariant);

        var onSame = CreateViewModel(management, new RecordingRoutePreferenceStore(true), new RecordingDialogService(),
            activeVariant: new RecordingActiveCoreVariantStore(ManagedCoreVariant.Stable),
            variantSwitch: (variant, _) => Task.FromResult(new RuntimeVariantSwitchResult(true, variant, true, false, "ok")));
        Assert.False(onSame.CanActivateSelectedVariant);

        // 没有装配切换服务时不许显示一个点了没反应的按钮。
        var unwired = CreateViewModel(management, new RecordingRoutePreferenceStore(true), new RecordingDialogService());
        Assert.False(unwired.CanActivateSelectedVariant);
    }

    [Fact]
    public async Task ActivateSelectedVariantConfirmsThenSwitches()
    {
        var management = new RecordingManagementService { Installation = Installed() };
        var dialogs = new MergeDialog { ApplyConfirmed = true };
        var switches = new List<ManagedCoreVariant>();
        var model = CreateViewModel(management, new RecordingRoutePreferenceStore(true), dialogs,
            activeVariant: new RecordingActiveCoreVariantStore(ManagedCoreVariant.Custom),
            variantSwitch: (variant, _) =>
            {
                switches.Add(variant);
                return Task.FromResult(new RuntimeVariantSwitchResult(true, variant, true, false, "已切换"));
            });

        await model.ActivateSelectedVariantCommand.ExecuteAsync(null);

        Assert.Equal([ManagedCoreVariant.Stable], switches);
        var confirmation = Assert.Single(dialogs.Confirmations);
        Assert.Contains("安全停止", confirmation, StringComparison.Ordinal);
        Assert.Contains(dialogs.Messages, item => item.Title == "已切换运行核心" && !item.IsError);
    }

    [Fact]
    public async Task DecliningTheActivateConfirmationLeavesTheRunningCoreAlone()
    {
        var management = new RecordingManagementService { Installation = Installed() };
        var dialogs = new MergeDialog { AskConfirmed = false };
        var switches = new List<ManagedCoreVariant>();
        var model = CreateViewModel(management, new RecordingRoutePreferenceStore(true), dialogs,
            activeVariant: new RecordingActiveCoreVariantStore(ManagedCoreVariant.Custom),
            variantSwitch: (variant, _) =>
            {
                switches.Add(variant);
                return Task.FromResult(new RuntimeVariantSwitchResult(true, variant, true, false, "已切换"));
            });

        await model.ActivateSelectedVariantCommand.ExecuteAsync(null);

        Assert.Empty(switches);
        Assert.Empty(dialogs.Messages);
    }

    [Fact]
    public async Task PullRequestDetailsAreReadOnlyAndNeverInstallAnything()
    {
        var management = new RecordingManagementService { Installation = Installed() };
        var dialogs = new RecordingDialogService();
        var pullRequest = PullRequest(42);
        var remote = new StubRemote
        {
            PullRequestDetailsOperation = _ => Task.FromResult(pullRequest),
            PullRequestFilesOperation = () => Task.FromResult<IReadOnlyList<GithubFileChange>>(
                [new GithubFileChange("worker.js", null, "modified", 3, 1, 4, "@@", null)]),
        };
        var model = CreateViewModel(management, new RecordingRoutePreferenceStore(true), dialogs, remote,
            merge: new RecordingPullRequestMergeService());

        await model.ShowPullRequestDetailsCommand.ExecuteAsync(pullRequest);

        // 详情走只读展示：弹窗拿到的是远端最新元数据 + 文件清单。
        Assert.Equal(42, dialogs.LastPullRequest!.Number);
        Assert.Equal("worker.js", Assert.Single(dialogs.LastPullRequestFiles!).Path);
        // 唯一的 PR 动作是"构建本地 PR 组合"：看详情不能顺手装任何东西。
        Assert.Equal(0, management.InstallCalls);
        Assert.Empty(model.SelectedPullRequests);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task DeclinedBuildConfirmationDoesNotPrepareAnything()
    {
        var management = new RecordingManagementService { Installation = Installed() };
        var merge = new RecordingPullRequestMergeService();
        var dialogs = new RecordingDialogService { BuildConfirmed = false };
        var model = CreateViewModel(management, new RecordingRoutePreferenceStore(true), dialogs, merge: merge);
        model.TogglePullRequestSelectionCommand.Execute(PullRequest(7));

        await model.BuildPullRequestStackCommand.ExecuteAsync(null);

        Assert.Empty(merge.PreparedNumbers);
        Assert.Equal(0, merge.DiscardCalls);
        Assert.Equal([7], model.SelectedPullRequests.Select(item => item.Number));
        Assert.Empty(dialogs.Messages);
        // 确认框里必须带目标变体、基线与有序队列，用户才知道要点什么。
        var prompt = Assert.IsType<PullRequestBuildPrompt>(dialogs.LastBuildPrompt);
        Assert.Equal("稳定核心", prompt.VariantLabel);
        Assert.Equal(Installed().Manifest!.CommitSha, prompt.BaseCommitSha);
        Assert.Equal([7], prompt.PullRequests.Select(item => item.Number));
        Assert.Empty(prompt.InheritedPullRequestNumbers);
    }

    [Fact]
    public async Task BuildConfirmationListsInheritedNumbersSeparatelyFromNewOnes()
    {
        var management = new RecordingManagementService { Installation = InstalledStack([12]) };
        var merge = new RecordingPullRequestMergeService();
        var dialogs = new RecordingDialogService { BuildConfirmed = false };
        var model = CreateViewModel(management, new RecordingRoutePreferenceStore(true), dialogs, merge: merge);
        model.TogglePullRequestSelectionCommand.Execute(PullRequest(7));

        await model.BuildPullRequestStackCommand.ExecuteAsync(null);

        var prompt = Assert.IsType<PullRequestBuildPrompt>(dialogs.LastBuildPrompt);
        Assert.Equal([7], prompt.PullRequests.Select(item => item.Number));
        Assert.Equal([12], prompt.InheritedPullRequestNumbers);
        Assert.Equal(InstalledStack([12]).Manifest!.BaseCommitSha, prompt.BaseCommitSha);
    }

    private static CoreInstallationInfo InstalledStack(IReadOnlyList<int> numbers)
    {
        var manifest = Installed().Manifest! with
        {
            SchemaVersion = 2,
            InstallKind = CoreInstallKind.LocalPullRequestStack,
            BaseCommitSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            LocalMergeSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            PullRequests = numbers
                .Select(number => new CorePullRequestSource(
                    number, "contributor/danmu_api", "feature",
                    "cccccccccccccccccccccccccccccccccccccccc", null))
                .ToArray(),
        };
        return Installed() with { Manifest = manifest };
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

    /// <summary>
    /// 本地 PR 组合发现远端新提交时，必须先核对"远端到底包不包含已并入的 PR"，
    /// 再把核对结果（每个 PR 的结论 + 证据）交给用户选：更新并重新并入 / 仅更新 / 取消。
    /// </summary>
    [Fact]
    public async Task StackUpdateAsksForConfirmationWithPerPullRequestEvidence()
    {
        var update = StackPendingUpdate([12]);
        var management = new RecordingManagementService { Installation = InstalledStack([12]) };
        var merge = new RecordingPullRequestMergeService
        {
            PresenceResult = new CorePullRequestPresenceReport(
                "huangxd-/danmu_api", "main", update.Remote!.Sha,
                [
                    new CorePullRequestPresenceEntry(12, new string('c', 40), null, "open", false, true, true,
                        CorePullRequestPresence.Missing, "PR #12 仍是 open：改动还没进远端 main"),
                ],
                []),
        };
        var dialogs = new RecordingDialogService
        {
            Confirmation = true,
            StackUpdateChoice = PullRequestStackUpdateChoice.UpdateAndReMerge,
        };
        var viewModel = CreateViewModel(management, new RecordingRoutePreferenceStore(true), dialogs,
            new CompareStubRemote(update.Remote), new StubScheduler(update), merge: merge);

        await viewModel.ApplyUpdateAsync(update);

        var prompt = Assert.IsType<PullRequestStackUpdatePrompt>(dialogs.LastStackUpdatePrompt);
        Assert.Equal(update.Remote.Sha, prompt.RemoteSha);
        Assert.Equal(InstalledStack([12]).Manifest!.BaseCommitSha, prompt.BaseCommitSha);
        Assert.Equal([12], prompt.ReMergeableNumbers);
        Assert.Contains(prompt.Entries, entry => entry.Evidence.Contains("仍是 open", StringComparison.Ordinal));
        // 用户选了"更新并重新并入" → 走的是组合更新通道，编号原样带过去。
        Assert.Equal([12], merge.StackUpdateNumbers);
        Assert.Equal(1, merge.PresenceCalls);
        Assert.Equal(update.Remote.Sha, merge.PresenceRemoteSha);
        Assert.Equal(0, management.InstallCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuditQuickPrUpdateUsesPendingVariantForInspectionEvidenceAndPrompt(bool selectedIsStack)
    {
        var target = InstalledStack([15]) with
        {
            Variant = ManagedCoreVariant.Dev,
            Manifest = InstalledStack([15]).Manifest! with
            {
                Variant = ManagedCoreVariant.Dev,
                Repository = "lilixu3/danmu_api",
                Branch = "develop",
                DisplayName = "dev target stack",
                CommitSha = new string('d', 40),
                BaseCommitSha = new string('d', 40),
            },
        };
        var update = StackPendingUpdate([15]) with { Variant = ManagedCoreVariant.Dev, Local = target.Manifest };
        var management = new RecordingManagementService { Installation = selectedIsStack ? InstalledStack([12]) : Installed() };
        management.VariantInstallations[ManagedCoreVariant.Dev] = target;
        var merge = new RecordingPullRequestMergeService
        {
            PresenceResult = new CorePullRequestPresenceReport("lilixu3/danmu_api", "develop", update.Remote!.Sha,
                [new CorePullRequestPresenceEntry(15, new string('c', 40), null, "open", false, true, true,
                    CorePullRequestPresence.Missing, "dev PR #15 evidence")], []),
        };
        var dialogs = new RecordingDialogService { StackUpdateChoice = PullRequestStackUpdateChoice.UpdateOnly };
        var model = CreateViewModel(management, new RecordingRoutePreferenceStore(true), dialogs,
            new CompareStubRemote(update.Remote), new StubScheduler(update), merge: merge);
        Assert.Equal(ManagedCoreVariant.Stable, model.SelectedVariant);

        await model.ApplyUpdateAsync(update);

        Assert.Equal(ManagedCoreVariant.Dev, merge.PresenceVariant);
        Assert.Same(update, merge.AppliedStackUpdate);
        var prompt = Assert.IsType<PullRequestStackUpdatePrompt>(dialogs.LastStackUpdatePrompt);
        Assert.Equal(ManagedCoreVariant.Dev.ToLabel(), prompt.VariantLabel);
        Assert.Equal(target.Manifest!.DisplayName, prompt.DisplayName);
        Assert.Equal(target.Manifest.Repository, prompt.Repository);
        Assert.Equal(target.Manifest.Branch, prompt.Branch);
        Assert.Equal(target.Manifest.BaseCommitSha, prompt.BaseCommitSha);
        Assert.Equal(15, Assert.Single(prompt.Entries).Number);
        Assert.Equal(ManagedCoreVariant.Stable, model.SelectedVariant); // Updating does not change page selection.
        Assert.Equal(0, management.InstallCalls);
    }

    private static CoreInstallationInfo DevStack() => InstalledStack([15]) with
    {
        Variant = ManagedCoreVariant.Dev,
        Manifest = InstalledStack([15]).Manifest! with
        {
            Variant = ManagedCoreVariant.Dev,
            Repository = "lilixu3/danmu_api",
            DisplayName = "dev stack",
        },
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AuditQuickPrUpdateVerifiesActualDevTargetEvenWhenSelectedStableIsNotInstalled(bool stableInstalled)
    {
        var target = DevStack();
        var update = StackPendingUpdate([15]) with { Variant = ManagedCoreVariant.Dev, Local = target.Manifest };
        var management = new RecordingManagementService { Installation = stableInstalled ? Installed() :
            new CoreInstallationInfo(ManagedCoreVariant.Stable, "missing", false, false, null, null, "missing") };
        management.VariantInstallations[ManagedCoreVariant.Dev] = target;
        var merge = new RecordingPullRequestMergeService
        {
            ApplyResult = new CoreManagementOperationResult(true, true, true, target, "dev applied"),
        };
        var checkedVariants = new List<ManagedCoreVariant>();
        var dialogs = new RecordingDialogService { StackUpdateChoice = PullRequestStackUpdateChoice.UpdateOnly };
        var model = CreateViewModel(management, new RecordingRoutePreferenceStore(true), dialogs,
            merge: merge, verifyDependencies: (variant, _) =>
            {
                checkedVariants.Add(variant);
                return Task.FromResult(new CoreDependencyHealth(true, false, 7));
            });

        var result = await model.ApplyUpdateAsync(update);

        Assert.True(result!.Succeeded);
        Assert.Equal([ManagedCoreVariant.Dev], checkedVariants);
        Assert.Equal(ManagedCoreVariant.Stable, model.SelectedVariant);
        Assert.Equal(stableInstalled, model.IsInstalled);
        Assert.Equal("尚未核对", model.CoreDependencyStatusText);
        Assert.False(model.IsCoreDependencyFailed); // Dev's missing dependencies must not become Stable's health.
    }

    [Theory]
    [InlineData("mutation", false)]
    [InlineData("verification", false)]
    [InlineData("verification", true)]
    [InlineData("awayAndBack", false)]
    public async Task AuditQuickPrUpdateKeepsTargetAndDoesNotPublishHealthAcrossAsyncPageChanges(
        string changePhase, bool verificationFails)
    {
        var target = DevStack();
        var update = StackPendingUpdate([15]) with { Variant = ManagedCoreVariant.Dev, Local = target.Manifest };
        var management = new RecordingManagementService { Installation = Installed() };
        management.VariantInstallations[ManagedCoreVariant.Dev] = target;
        management.VariantInstallations[ManagedCoreVariant.Custom] = InstalledCustom();
        var mutationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMutation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verifyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseVerify = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var merge = new RecordingPullRequestMergeService
        {
            StackUpdateOperation = async () =>
            {
                mutationEntered.SetResult();
                await releaseMutation.Task;
                return new CoreManagementOperationResult(true, true, true, target, "dev applied");
            },
        };
        var checkedVariants = new List<ManagedCoreVariant>();
        var diagnostics = new StubDiagnostics();
        var original = new IOException("AUDIT_EXTERNAL_PAYLOAD_DO_NOT_LOG");
        var model = CreateViewModel(management, new RecordingRoutePreferenceStore(true),
            new RecordingDialogService { StackUpdateChoice = PullRequestStackUpdateChoice.UpdateOnly },
            diagnostics: diagnostics, merge: merge, verifyDependencies: async (variant, _) =>
            {
                checkedVariants.Add(variant);
                verifyEntered.SetResult();
                await releaseVerify.Task;
                if (verificationFails) throw original;
                return new CoreDependencyHealth(true, true, 0);
            });
        var applying = model.ApplyUpdateAsync(update);
        try
        {
            await mutationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            model.SelectedVariant = changePhase == "mutation" ? ManagedCoreVariant.Custom : ManagedCoreVariant.Dev;
            releaseMutation.SetResult();
            await verifyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal([ManagedCoreVariant.Dev], checkedVariants);
            if (changePhase != "mutation")
            {
                model.SelectedVariant = ManagedCoreVariant.Stable;
                if (changePhase == "awayAndBack") model.SelectedVariant = ManagedCoreVariant.Dev;
            }
        }
        finally
        {
            releaseMutation.TrySetResult();
            releaseVerify.TrySetResult();
        }
        var result = await applying;
        Assert.True(result!.Succeeded); // Dependency diagnostics do not turn a committed mutation into failure.
        Assert.Same(update, merge.AppliedStackUpdate);
        Assert.Equal("尚未核对", model.CoreDependencyStatusText);
        Assert.False(model.IsCoreDependencyHealthy);
        Assert.False(model.IsCoreDependencyFailed);
        Assert.Equal(ManagedCoreVariant.Dev, model.DependencyVerificationVariant);
        if (verificationFails)
        {
            Assert.Equal($"开发核心依赖核对失败：IOException / {DependencyMaintenanceDiagnostics.Describe(original)}", diagnostics.LastDiagnostic);
            Assert.DoesNotContain(original.Message, diagnostics.LastDiagnostic!);
            Assert.Same(original, model.DependencyVerificationError);
        }
        else Assert.Null(model.DependencyVerificationError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuditPagePendingRefreshesCompleteSameBaseIdentityWithoutAnotherCheck(bool becomesStack)
    {
        var update = PendingUpdate();
        var management = new RecordingManagementService
        {
            Installation = Installed(),
            InstallResult = new CoreManagementOperationResult(true, true, true, InstalledApplied(update), "applied"),
        };
        var merge = new RecordingPullRequestMergeService();
        var scheduler = new StubScheduler(update);
        var dialogs = new RecordingDialogService
        {
            Confirmation = false, // Keep the first discovery pending instead of applying it immediately.
            StackUpdateChoice = PullRequestStackUpdateChoice.UpdateOnly,
        };
        var remote = System.Reflection.DispatchProxy.Create<IGithubCoreRemote, NoNetworkRemote>();
        var model = CreateViewModel(management, new RecordingRoutePreferenceStore(true), dialogs,
            remote: remote, scheduler: scheduler, merge: merge);
        await model.CheckUpdateCommand.ExecuteAsync(null);
        Assert.True(model.HasUpdate);
        var discoveryPrompts = dialogs.Confirmations.Count;
        var changed = becomesStack ? InstalledStack([15]) : Installed() with
        {
            Manifest = Installed().Manifest! with { DisplayName = "renamed pending source" },
        };

        management.ReplaceInstallationOutOfBand(changed, ManagedCoreVariant.Stable);
        Assert.True(model.HasUpdate);
        Assert.True(model.CanApplyUpdate);
        await model.ApplyUpdateCommand.ExecuteAsync(null);

        var appliedUpdate = becomesStack ? merge.AppliedStackUpdate : management.LastAppliedUpdate;
        Assert.NotNull(appliedUpdate);
        Assert.True(CoreInstallationManifest.SourcesEqual(changed.Manifest, appliedUpdate.Local));
        Assert.Equal(1, scheduler.ManualCalls);
        Assert.Equal(0, ((NoNetworkRemote)remote).UnexpectedReads);
        Assert.Equal(discoveryPrompts, dialogs.Confirmations.Count);
        if (becomesStack)
        {
            Assert.Equal(0, management.InstallCalls);
            Assert.NotNull(dialogs.LastStackUpdatePrompt);
            Assert.Equal(1, merge.PresenceCalls);
        }
        else
        {
            Assert.Equal(1, management.InstallCalls);
            Assert.Null(dialogs.LastStackUpdatePrompt);
            Assert.Equal(0, merge.PresenceCalls);
        }
    }

    /// <summary>用户选「仅更新」：远端已包含全部 PR，或明确放弃那些改动 → 不重新并入。</summary>
    [Fact]
    public async Task StackUpdateWithUpdateOnlyCarriesNoReMergeNumbers()
    {
        var update = StackPendingUpdate([12]);
        var management = new RecordingManagementService { Installation = InstalledStack([12]) };
        var merge = new RecordingPullRequestMergeService();
        var dialogs = new RecordingDialogService { StackUpdateChoice = PullRequestStackUpdateChoice.UpdateOnly };
        var viewModel = CreateViewModel(management, new RecordingRoutePreferenceStore(true), dialogs,
            new CompareStubRemote(update.Remote), new StubScheduler(update), merge: merge);

        await viewModel.ApplyUpdateAsync(update);

        Assert.NotNull(dialogs.LastStackUpdatePrompt);
        Assert.Empty(merge.StackUpdateNumbers);
        Assert.Equal(1, merge.PresenceCalls);
    }

    /// <summary>取消核对框：什么都不做 —— 绝不能默认把本地改动丢掉。</summary>
    [Fact]
    public async Task CanceledStackUpdateChangesNothing()
    {
        var update = StackPendingUpdate([12]);
        var management = new RecordingManagementService { Installation = InstalledStack([12]) };
        var merge = new RecordingPullRequestMergeService();
        var dialogs = new RecordingDialogService { StackUpdateChoice = PullRequestStackUpdateChoice.Cancel };
        var viewModel = CreateViewModel(management, new RecordingRoutePreferenceStore(true), dialogs,
            new CompareStubRemote(update.Remote), new StubScheduler(update), merge: merge);

        var result = await viewModel.ApplyUpdateAsync(update);

        Assert.Null(result);
        Assert.Equal(1, merge.PresenceCalls);
        Assert.Empty(merge.StackUpdateNumbers);
        Assert.Equal(0, management.InstallCalls);
    }

    /// <summary>普通（非组合）核心不受影响：不弹核对框，直接走原来的分支更新。</summary>
    [Fact]
    public async Task PlainBranchUpdateNeverAsksForPullRequestConfirmation()
    {
        var update = PendingUpdate();
        var management = new RecordingManagementService
        {
            Installation = Installed(),
            AppliedInstallation = InstalledApplied(update),
            InstallResult = new CoreManagementOperationResult(true, true, true, InstalledApplied(update), "核心操作已完成"),
        };
        var merge = new RecordingPullRequestMergeService();
        var dialogs = new RecordingDialogService { Confirmation = true };
        var viewModel = CreateViewModel(management, new RecordingRoutePreferenceStore(true), dialogs,
            new CompareStubRemote(update.Remote), new StubScheduler(update), merge: merge);

        await viewModel.ApplyUpdateAsync(update);

        Assert.Null(dialogs.LastStackUpdatePrompt);
        Assert.Equal(0, merge.PresenceCalls);
        Assert.Equal(1, management.InstallCalls);
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

    /// <summary>本地 PR 组合上的更新结论：Local 必须是那份组合 manifest，界面据此走核对流程。</summary>
    private static CoreUpdateCheckResult StackPendingUpdate(IReadOnlyList<int> numbers) => PendingUpdate() with
    {
        Local = InstalledStack(numbers).Manifest,
    };

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

    [Fact]
    public void CoreReplacedElsewhereRefreshesVersionCommitAndInstallTimeWithoutAPageOperation()
    {
        var management = new RecordingManagementService { Installation = Installed() };
        var viewModel = CreateViewModel(management, new RecordingRoutePreferenceStore(true), new RecordingDialogService());
        Assert.Equal("1.0.0", viewModel.VersionDisplay);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", viewModel.CommitDisplay);

        var replaced = Installed() with
        {
            Manifest = Installed().Manifest! with
            {
                CommitSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                Version = "1.21.0",
                InstalledAt = DateTimeOffset.Parse("2026-09-12T13:08:23Z"),
            },
        };
        management.ReplaceInstallationOutOfBand(replaced, ManagedCoreVariant.Stable);

        Assert.Equal("1.21.0", viewModel.VersionDisplay);
        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", viewModel.CommitDisplay);
        Assert.Equal(DateTimeOffset.Parse("2026-09-12T13:08:23Z").ToLocalTime().ToString("yyyy-MM-dd HH:mm"), viewModel.InstalledAtDisplay);
    }

    [Fact]
    public void CoreReplacedForAnotherVariantLeavesThisPageUntouched()
    {
        var management = new RecordingManagementService { Installation = Installed() };
        var viewModel = CreateViewModel(management, new RecordingRoutePreferenceStore(true), new RecordingDialogService());

        management.ReplaceInstallationOutOfBand(InstalledCustom(), ManagedCoreVariant.Custom);

        Assert.Equal("1.0.0", viewModel.VersionDisplay);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", viewModel.CommitDisplay);
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("confirmation-exception")]
    [InlineData("canceled")]
    [InlineData("precondition-failure")]
    [InlineData("apply-failure")]
    [InlineData("success")]
    public async Task PullRequestStackBuildAlwaysDiscardsStaging(string scenario)
    {
        var management = new RecordingManagementService { Installation = Installed() };
        var merge = new RecordingPullRequestMergeService();
        var dialogs = new MergeDialog
        {
            ApplyConfirmed = scenario != "denied",
            ThrowOnApplyConfirmation = scenario == "confirmation-exception",
            ApplyOutcome = scenario == "canceled" ? ProgressOperationOutcome.Canceled :
                scenario == "precondition-failure" ? ProgressOperationOutcome.Failed : null,
        };
        if (scenario == "apply-failure")
        {
            merge.ApplyFailure = new InvalidOperationException("应用故障");
        }
        var model = CreateViewModel(management, new RecordingRoutePreferenceStore(true), dialogs, merge: merge);
        model.TogglePullRequestSelectionCommand.Execute(PullRequest(12));
        model.TogglePullRequestSelectionCommand.Execute(PullRequest(3));

        await model.BuildPullRequestStackCommand.ExecuteAsync(null);

        Assert.False(model.IsBusy);
        if (scenario is "denied" or "confirmation-exception")
        {
            // 新语义：先确认再准备。用户在确认框取消（或确认框自己出错）时，
            // 根本不应该创建隔离工作区，也就没有暂存目录要清理。
            Assert.Empty(merge.PreparedNumbers);
            Assert.Equal(0, merge.ApplyCalls);
            Assert.Equal(0, merge.DiscardCalls);
            Assert.Equal(2, model.SelectedPullRequests.Count);
            if (scenario == "confirmation-exception")
            {
                Assert.Contains(dialogs.Messages, item => item.IsError && item.Title == "PR 组合构建失败");
            }
            else
            {
                Assert.Empty(dialogs.Messages);
            }
            return;
        }

        Assert.Equal([12, 3], merge.PreparedNumbers);
        Assert.Equal(1, merge.DiscardCalls);
        // 进度弹窗自己判定取消/前置失败时根本不会进入应用，所以只有真正跑到应用的两个场景计 1 次。
        Assert.Equal(scenario is "canceled" or "precondition-failure" ? 0 : 1, merge.ApplyCalls);
        if (scenario == "success")
        {
            Assert.Empty(model.SelectedPullRequests);
            Assert.Contains(dialogs.Messages, item => item.Title == "PR 组合已安装");
            // 确认框里显式列出基线与有序队列（与移动端构建对话框同义）。
            Assert.Equal(Installed().Manifest!.CommitSha, dialogs.LastBuildPrompt!.BaseCommitSha);
            Assert.Equal([12, 3], dialogs.LastBuildPrompt.PullRequests.Select(item => item.Number));
        }
        else
        {
            Assert.Equal(2, model.SelectedPullRequests.Count);
            Assert.Contains(dialogs.Messages, item => item.IsError || item.Title == "操作已取消");
        }
    }

    [Fact]
    public async Task BuildingPullRequestStackActivatesTheVariantWhenRequested()
    {
        var management = new RecordingManagementService { Installation = Installed() };
        var merge = new RecordingPullRequestMergeService();
        var dialogs = new MergeDialog { ActivateAfterInstall = true };
        var switches = new List<ManagedCoreVariant>();
        var model = CreateViewModel(management, new RecordingRoutePreferenceStore(true), dialogs, merge: merge,
            activeVariant: new RecordingActiveCoreVariantStore(ManagedCoreVariant.Custom),
            variantSwitch: (variant, _) =>
            {
                switches.Add(variant);
                return Task.FromResult(new RuntimeVariantSwitchResult(true, variant, true, false, "已切换"));
            });
        model.TogglePullRequestSelectionCommand.Execute(PullRequest(12));

        await model.BuildPullRequestStackCommand.ExecuteAsync(null);

        Assert.Equal([ManagedCoreVariant.Stable], switches);
        Assert.Contains(dialogs.Messages, item => item.Title == "已切换运行核心");
    }

    [Fact]
    public async Task BuildingPullRequestStackDoesNotSwitchWhenTheToggleIsOff()
    {
        var management = new RecordingManagementService { Installation = Installed() };
        var merge = new RecordingPullRequestMergeService();
        var dialogs = new MergeDialog { ActivateAfterInstall = false };
        var switches = new List<ManagedCoreVariant>();
        var model = CreateViewModel(management, new RecordingRoutePreferenceStore(true), dialogs, merge: merge,
            activeVariant: new RecordingActiveCoreVariantStore(ManagedCoreVariant.Custom),
            variantSwitch: (variant, _) =>
            {
                switches.Add(variant);
                return Task.FromResult(new RuntimeVariantSwitchResult(true, variant, true, false, "已切换"));
            });
        model.TogglePullRequestSelectionCommand.Execute(PullRequest(12));

        await model.BuildPullRequestStackCommand.ExecuteAsync(null);

        Assert.Empty(switches);
        Assert.False(model.ActivateAfterBuild);
    }

    [Fact]
    public async Task FailedRuntimeSwitchIsReportedAsError()
    {
        var management = new RecordingManagementService { Installation = Installed() };
        var merge = new RecordingPullRequestMergeService();
        var dialogs = new MergeDialog { ActivateAfterInstall = true };
        var model = CreateViewModel(management, new RecordingRoutePreferenceStore(true), dialogs, merge: merge,
            activeVariant: new RecordingActiveCoreVariantStore(ManagedCoreVariant.Custom),
            variantSwitch: (variant, _) => Task.FromResult(new RuntimeVariantSwitchResult(
                false, variant, false, true, "切换到稳定核心后服务未能运行；已恢复原来的核心选择并重启。")));
        model.TogglePullRequestSelectionCommand.Execute(PullRequest(12));

        await model.BuildPullRequestStackCommand.ExecuteAsync(null);

        Assert.Contains(dialogs.Messages, item => item.IsError && item.Title == "切换运行核心失败");
    }

    [Fact]
    public async Task FailedStagingCleanupReportsDiagnosticAndError()
    {
        var management = new RecordingManagementService { Installation = Installed() };
        var merge = new RecordingPullRequestMergeService { DiscardFailure = new IOException("暂存文件被占用") };
        var dialogs = new MergeDialog { ApplyConfirmed = true };
        var diagnostics = new StubDiagnostics();
        var model = CreateViewModel(management, new RecordingRoutePreferenceStore(true), dialogs, diagnostics: diagnostics, merge: merge);
        model.TogglePullRequestSelectionCommand.Execute(PullRequest(12));

        await model.BuildPullRequestStackCommand.ExecuteAsync(null);

        Assert.Equal(1, merge.DiscardCalls);
        Assert.Contains("暂存文件被占用", diagnostics.LastDiagnostic, StringComparison.Ordinal);
        Assert.Contains(dialogs.Messages, item => item.IsError && item.Title == "暂存目录清理失败");
    }

    [Fact]
    public void ChangingInstalledSourceClearsStalePullRequestsAndSelection()
    {
        var management = new RecordingManagementService { Installation = Installed() };
        var merge = new RecordingPullRequestMergeService();
        var model = CreateViewModel(management, new RecordingRoutePreferenceStore(true), new RecordingDialogService(), merge: merge);
        var request = PullRequest(42);
        model.PullRequests = [request];
        model.TogglePullRequestSelectionCommand.Execute(request);
        Assert.True(model.CanBuildPullRequestStack);

        management.ReplaceInstallationOutOfBand(Installed() with
        {
            Manifest = Installed().Manifest! with { Branch = "next" },
        }, ManagedCoreVariant.Stable);

        Assert.Empty(model.PullRequests);
        Assert.Empty(model.SelectedPullRequests);
        Assert.False(model.CanBuildPullRequestStack);
        Assert.False(model.BuildPullRequestStackCommand.CanExecute(null));
        Assert.Equal(1, model.PullRequestPage);
        Assert.False(model.HasPullRequestNextPage);
    }

    [Fact]
    public async Task InFlightPullRequestResponseCannotRestoreOldSourceList()
    {
        var management = new RecordingManagementService { Installation = Installed() };
        var response = new TaskCompletionSource<GithubPullRequestPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remote = new StubRemote { PullRequestPageOperation = () => response.Task };
        var model = CreateViewModel(management, new RecordingRoutePreferenceStore(true), new RecordingDialogService(), remote);
        var load = model.OpenPullRequestsPageCommand.ExecuteAsync(null);
        Assert.False(load.IsCompleted);

        management.ReplaceInstallationOutOfBand(Installed() with
        {
            Manifest = Installed().Manifest! with { Repository = "example/another", Branch = "develop" },
        }, ManagedCoreVariant.Stable);
        response.SetResult(new GithubPullRequestPage([PullRequest(42)], 1, false, false));
        await load;

        Assert.Empty(model.PullRequests);
        Assert.Empty(model.SelectedPullRequests);
    }

    [Fact]
    public void SelectionRetainsClickOrderAndTogglesOff()
    {
        var management = new RecordingManagementService { Installation = Installed() };
        var model = CreateViewModel(management, new RecordingRoutePreferenceStore(true), new RecordingDialogService(), merge: new RecordingPullRequestMergeService());
        model.TogglePullRequestSelectionCommand.Execute(PullRequest(12));
        model.TogglePullRequestSelectionCommand.Execute(PullRequest(3));
        Assert.Equal([12, 3], model.SelectedPullRequests.Select(item => item.Number));
        Assert.Contains("#12、#3", model.SelectedPullRequestSummary, StringComparison.Ordinal);
        model.TogglePullRequestSelectionCommand.Execute(PullRequest(12));
        Assert.Equal([3], model.SelectedPullRequests.Select(item => item.Number));
        Assert.Contains("已安装", model.IdentityEyebrow, StringComparison.Ordinal);
        Assert.DoesNotContain("当前运行", model.IdentityEyebrow, StringComparison.Ordinal);
    }

    private static CorePageViewModel CreateViewModel(
        RecordingManagementService management,
        RecordingRoutePreferenceStore routeStore,
        IUiDialogService dialogs,
        IGithubCoreRemote? remote = null,
        ICoreUpdateScheduler? scheduler = null,
        StubDiagnostics? diagnostics = null,
        ICorePullRequestManagementService? merge = null,
        IActiveCoreVariantStore? activeVariant = null,
        Func<ManagedCoreVariant, CancellationToken, Task<RuntimeVariantSwitchResult>>? variantSwitch = null,
        Func<ManagedCoreVariant, CancellationToken, Task<CoreDependencyHealth>>? verifyDependencies = null) =>
        new(
            management,
            remote ?? new StubRemote(),
            routeStore,
            new StubSpeedTester(),
            scheduler ?? new StubScheduler(null),
            dialogs,
            diagnostics ?? new StubDiagnostics(),
            new GithubTokenConfigurationService(new StubGithubTokenStore(), remote ?? new StubRemote(), diagnostics ?? new StubDiagnostics()),
            verifyDependencies: verifyDependencies,
            pullRequestManagement: merge,
            activeVariant: activeVariant,
            runtimeVariantSwitch: variantSwitch);

    private sealed class RecordingActiveCoreVariantStore(ManagedCoreVariant? initial) : IActiveCoreVariantStore
    {
        public List<ManagedCoreVariant> Writes { get; } = [];

        public ManagedCoreVariant? Read() => Writes.Count > 0 ? Writes[^1] : initial;

        public void Write(ManagedCoreVariant variant) => Writes.Add(variant);
    }

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

    private sealed class RecordingPullRequestMergeService : ICorePullRequestManagementService
    {
        public int ApplyCalls { get; private set; }
        public int DiscardCalls { get; private set; }
        public int[] PreparedNumbers { get; private set; } = [];
        public int PresenceCalls { get; private set; }
        public string? PresenceRemoteSha { get; private set; }
        public ManagedCoreVariant? PresenceVariant { get; private set; }
        public CoreUpdateCheckResult? AppliedStackUpdate { get; private set; }
        public int[] StackUpdateNumbers { get; private set; } = [];
        public CorePullRequestPresenceReport? PresenceResult { get; set; }
        public CoreManagementOperationResult? ApplyResult { get; set; }
        public Exception? ApplyFailure { get; set; }
        public Exception? DiscardFailure { get; set; }
        public Func<Task<CoreManagementOperationResult>>? StackUpdateOperation { get; set; }

        public Task<CorePreparedInstallRequest> PreparePullRequestMergeAsync(
            ManagedCoreVariant variant, IReadOnlyList<GithubPullRequest> pullRequests, string proxyId,
            IProgress<CoreInstallProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            PreparedNumbers = pullRequests.Select(pr => pr.Number).ToArray();
            var installed = Installed();
            return Task.FromResult(new CorePreparedInstallRequest(variant, "staging", installed.Manifest!,
                installed.Manifest!.CommitSha, "dddddddddddddddddddddddddddddddddddddddd", []));
        }

        public Task<CoreManagementOperationResult> ApplyPreparedPullRequestMergeAsync(
            CorePreparedInstallRequest request, IProgress<CoreInstallProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ApplyCalls++;
            return ApplyFailure is not null
                ? Task.FromException<CoreManagementOperationResult>(ApplyFailure)
                : Task.FromResult(ApplyResult ?? new CoreManagementOperationResult(true, true, true, Installed(), "已完成"));
        }

        public void DiscardPreparedPullRequestMerge(CorePreparedInstallRequest request)
        {
            DiscardCalls++;
            if (DiscardFailure is not null) throw DiscardFailure;
        }

        public Task<CorePullRequestPresenceReport> AnalyzePullRequestPresenceAsync(
            ManagedCoreVariant variant, string remoteSha, CancellationToken cancellationToken = default)
        {
            PresenceCalls++;
            PresenceVariant = variant;
            PresenceRemoteSha = remoteSha;
            return Task.FromResult(PresenceResult ?? new CorePullRequestPresenceReport(
                "huangxd-/danmu_api", "main", remoteSha, [], []));
        }

        public Task<CoreManagementOperationResult> ApplyPullRequestStackUpdateAsync(
            CoreUpdateCheckResult update, IReadOnlyList<int> remergeNumbers, string proxyId,
            IProgress<CoreInstallProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            AppliedStackUpdate = update;
            StackUpdateNumbers = remergeNumbers.ToArray();
            if (StackUpdateOperation is not null) return StackUpdateOperation();
            return ApplyFailure is not null
                ? Task.FromException<CoreManagementOperationResult>(ApplyFailure)
                : Task.FromResult(ApplyResult ?? new CoreManagementOperationResult(true, true, true, Installed(), "已更新"));
        }
    }

    private sealed class MergeDialog : IUiDialogService
    {
        public List<(string Title, string Message, bool IsError)> Messages { get; } = [];
        public List<string> Confirmations { get; } = [];
        public bool ApplyConfirmed { get; set; } = true;
        public bool ThrowOnApplyConfirmation { get; set; }
        public ProgressOperationOutcome? ApplyOutcome { get; set; }
        public bool ActivateAfterInstall { get; set; } = true;
        public PullRequestBuildPrompt? LastBuildPrompt { get; private set; }
        public Task EditPortAsync(MainWindowViewModel viewModel) => Task.CompletedTask;
        public Task EditTokenAsync(MainWindowViewModel viewModel) => Task.CompletedTask;
        public Task ShowCacheAsync(MainWindowViewModel viewModel) => Task.CompletedTask;
        public Task<CloseActionDecision?> AskCloseActionAsync() => Task.FromResult<CloseActionDecision?>(null);
        public Task<string?> ChooseGithubRouteAsync(string reason, string selectedProxyId,
            IGithubProxySpeedTester speedTester, CancellationToken cancellationToken = default) => Task.FromResult<string?>("original");
        public Task<string?> ChooseRuntimeRootAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task OpenDirectoryAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CopyTextAsync(string text) => Task.CompletedTask;
        public bool AskConfirmed { get; set; } = true;

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
        {
            Confirmations.Add(message);
            if (ThrowOnApplyConfirmation && Confirmations.Count == 2)
            {
                return Task.FromException<bool>(new InvalidOperationException("确认窗口故障"));
            }
            return Task.FromResult(Confirmations.Count == 1 ? AskConfirmed : ApplyConfirmed);
        }
        public Task<PullRequestBuildConfirmation> ConfirmPullRequestBuildAsync(PullRequestBuildPrompt prompt)
        {
            LastBuildPrompt = prompt;
            Confirmations.Add(prompt.BaseCommitSha);
            if (ThrowOnApplyConfirmation)
            {
                return Task.FromException<PullRequestBuildConfirmation>(new InvalidOperationException("确认窗口故障"));
            }
            return Task.FromResult(ApplyConfirmed
                ? new PullRequestBuildConfirmation(true, ActivateAfterInstall)
                : PullRequestBuildConfirmation.Canceled);
        }
        public Task ShowMessageAsync(string title, string message, bool isError = false)
        {
            Messages.Add((title, message, isError));
            return Task.CompletedTask;
        }
        public async Task<ProgressOperationResult> RunWithProgressDialogAsync(
            string title, Func<IProgress<CoreInstallProgress>, CancellationToken, Task> operation)
        {
            if (title == "应用 PR 组合" && ApplyOutcome is { } outcome)
            {
                return new ProgressOperationResult(outcome, outcome == ProgressOperationOutcome.Failed ? "应用前置条件失败" : null);
            }
            try
            {
                await operation(new Progress<CoreInstallProgress>(), CancellationToken.None);
                return new ProgressOperationResult(ProgressOperationOutcome.Completed, null);
            }
            catch (Exception error)
            {
                return new ProgressOperationResult(ProgressOperationOutcome.Failed, error.Message);
            }
        }
    }

    private sealed class RecordingManagementService : ICoreManagementService
    {
        public event EventHandler<CoreInstallationChangedEventArgs>? InstallationChanged;

        /// <summary>模拟托盘/后台自动更新把磁盘上的核心换掉：先改 Inspect 的返回值，再发出变更通知。</summary>
        public void ReplaceInstallationOutOfBand(CoreInstallationInfo installation, ManagedCoreVariant variant)
        {
            Installation = installation;
            InstallationChanged?.Invoke(this, new CoreInstallationChangedEventArgs(variant));
        }

        public CoreInstallationInfo Installation { get; set; } = new(
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
        public ManagedCoreVariant? LastInstallVariant { get; private set; }
        public CoreUpdateCheckResult? LastAppliedUpdate { get; private set; }
        public GithubRepositoryReference? LastInstallRepository { get; private set; }
        public string? LastInstallDisplayName { get; private set; }
        public string? LastInstallProxyId { get; private set; }
        public ManagedCoreVariant? DeleteVariant { get; private set; }
        public string? RenamedTo { get; private set; }

        // 真实实现里 Inspect 读的是磁盘：安装完成后必须看到新提交，否则"更新后仍显示有新版本"
        // 这类状态在测试里永远复现不出来。
        public Dictionary<ManagedCoreVariant, CoreInstallationInfo> VariantInstallations { get; } = [];
        public CoreInstallationInfo Inspect(ManagedCoreVariant variant) =>
            VariantInstallations.TryGetValue(variant, out var installed) ? installed :
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
            LastInstallVariant = variant;
            LastInstallRepository = repository;
            LastInstallDisplayName = displayName;
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

        public Task<CoreManagementOperationResult> ApplyUpdateAsync(
            CoreUpdateCheckResult update,
            string proxyId,
            IProgress<CoreInstallProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            LastAppliedUpdate = update;
            return InstallBranchAsync(update.Variant, GithubRepositoryReference.Official("main"), "官方核心", proxyId, progress, cancellationToken);
        }

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
        public Func<Task<GithubPullRequestPage>>? PullRequestPageOperation { get; set; }
        public Func<int, Task<GithubPullRequest>>? PullRequestDetailsOperation { get; set; }
        public Func<Task<IReadOnlyList<GithubFileChange>>>? PullRequestFilesOperation { get; set; }
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
            PullRequestPageOperation?.Invoke() ?? Task.FromResult(new GithubPullRequestPage([], 1, false, false));
        public Task<GithubPullRequest> GetPullRequestAsync(GithubRepositoryReference repository, int number, CancellationToken cancellationToken = default) =>
            PullRequestDetailsOperation is null
                ? throw new NotSupportedException()
                : PullRequestDetailsOperation(number);
        public Task<IReadOnlyList<GithubFileChange>> GetAllPullRequestFilesAsync(GithubRepositoryReference repository, int number, CancellationToken cancellationToken = default) =>
            PullRequestFilesOperation is null
                ? throw new NotSupportedException()
                : PullRequestFilesOperation();
        public Task<GithubCompareResult> GetCompareAsync(GithubRepositoryReference repository, string baseSha, string headSha, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<GithubRateLimit> GetRateLimitAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    public class NoNetworkRemote : System.Reflection.DispatchProxy
    {
        public int UnexpectedReads { get; private set; }
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "get_LastRateLimit") return null;
            UnexpectedReads++;
            throw new InvalidOperationException($"Reconciliation must not read GitHub: {method?.Name}");
        }
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
        public int ManualCalls { get; private set; }
        public event EventHandler<CoreUpdateCheckResult>? CheckCompleted;
        public event EventHandler<string>? DiagnosticChanged;
        public void Start() { }
        public void SetBackgroundActive(bool active) { }
        public void NotifyPolicyChanged() { }

        public Task<CoreUpdateCheckResult> CheckForegroundAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(manualResult ?? Checked());

        public Task<CoreUpdateCheckResult?> CheckBackgroundAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<CoreUpdateCheckResult?>(null);

        public Task<CoreUpdateCheckResult> CheckManualAsync(ManagedCoreVariant variant, CancellationToken cancellationToken = default)
        {
            ManualCalls++;
            return ManualOperation?.Invoke(cancellationToken) ?? Task.FromResult(manualResult ?? Checked());
        }

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
