using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
using DanmuApi.App.Views;
using DanmuApi.Core;

namespace DanmuApi.Tests;

public sealed partial class CorePageViewModelTests
{
    [AvaloniaFact]
    public void CoreViewHasNoManualDependencyMaintenanceEntryPoints()
    {
        var remote = new StubRemote();
        var diagnostics = new StubDiagnostics();
        var dialogs = new RecordingDialogService();
        var model = new CorePageViewModel(new RecordingManagementService { Installation = Installed() }, remote,
            new RecordingRoutePreferenceStore(true), new StubSpeedTester(), new StubScheduler(null), dialogs, diagnostics,
            new DanmuApi.App.Services.GithubTokenConfigurationService(new StubGithubTokenStore(), remote, diagnostics));
        var view = new CorePageView { DataContext = model };
        var window = CreateCoreWindow(view, false);
        try
        {
            FlushCoreLayout(window);
            // 依赖维护改为「核心操作成功后自动核对 + 提醒」，页面上不再保留手动检查、修复与详情面板。
            Assert.Null(view.FindControl<Expander>("DependencyMaintenanceExpander"));
            Assert.Null(view.FindControl<Border>("RuntimeMaintenancePanel"));
            Assert.Null(view.FindControl<Border>("CoreDependencyPanel"));
            Assert.Null(view.FindControl<Button>("CheckRuntimeDependenciesButton"));
            Assert.Null(view.FindControl<Button>("RepairRuntimeDependenciesButton"));
            Assert.Null(view.FindControl<Button>("CheckCoreDependenciesButton"));
            Assert.Null(view.FindControl<Button>("RepairCoreDependenciesButton"));

            // 运行状态带只读：运行环境 / 核心依赖 / 可选 Redis（按配置显示）/ GitHub 配额，整条无按钮。
            var workbench = view.FindControl<Border>("WorkbenchPanel")!;
            var statusBar = view.FindControl<Border>("StatusBar")!;
            Assert.Contains(statusBar, workbench.GetVisualDescendants());
            Assert.Empty(statusBar.GetVisualDescendants().OfType<Button>());
            var barTexts = statusBar.GetVisualDescendants().OfType<TextBlock>()
                .Select(text => text.Text).ToList();
            Assert.Contains("运行状态", barTexts);
            Assert.Contains("运行环境", barTexts);
            Assert.Contains("核心依赖", barTexts);
            Assert.Contains("GitHub 配额", barTexts);
            Assert.DoesNotContain(workbench.GetVisualDescendants().OfType<Button>(),
                button => Equals(button.Content, "检查全部依赖"));
            SaveCorePreview(window, "core-light-wide");
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task DependencyVerificationRunsAfterSuccessfulMutationAndDrivesTheStatusBand()
    {
        var remote = new StubRemote();
        var diagnostics = new StubDiagnostics();
        var dialogs = new RecordingDialogService { Confirmation = true };
        var verified = new List<ManagedCoreVariant>();
        var model = new CorePageViewModel(new RecordingManagementService
            {
                Installation = Installed(),
                InstallResult = new CoreManagementOperationResult(true, true, true, Installed(), "核心操作已完成"),
            },
            remote, new RecordingRoutePreferenceStore(true), new StubSpeedTester(), new StubScheduler(null), dialogs, diagnostics,
            new DanmuApi.App.Services.GithubTokenConfigurationService(new StubGithubTokenStore(), remote, diagnostics),
            (variant, _) =>
            {
                verified.Add(variant);
                return Task.FromResult(new DanmuApi.App.Services.CoreDependencyHealth(true, false, 2));
            });

        Assert.Equal("尚未核对", model.CoreDependencyStatusText);
        await model.ReinstallCommand.ExecuteAsync(null);

        // 核心变更成功后必须核对一次依赖声明，核对的是刚安装的那个变体。
        Assert.Single(verified);
        Assert.Equal(ManagedCoreVariant.Stable, verified[0]);
        // 核对结论要落到状态带上，而不是永远停在"尚未核对"。
        Assert.Equal("缺失 2 项", model.CoreDependencyStatusText);
        Assert.True(model.IsCoreDependencyFailed);
        Assert.False(model.IsCoreDependencyHealthy);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoreViewReflowsWithRealBindingsAndSeparatesMaintenance(bool dark)
    {
        var installation = Installed();
        installation = installation with
        {
            Manifest = installation.Manifest! with
            {
                Repository = "maintainer-with-a-long-name/danmu-api-with-a-long-repository-name",
                Branch = "feature/非常长的分支名称-用于验证窄窗口换行-and-long-branch-name",
            },
        };
        var model = CreateViewModel(new RecordingManagementService { Installation = installation },
            new RecordingRoutePreferenceStore(true), new RecordingDialogService());
        var view = new CorePageView { DataContext = model };
        var window = CreateCoreWindow(view, dark);
        try
        {
            var identity = view.FindControl<Border>("IdentityPanel")!;
            var source = view.FindControl<Border>("SourcePanel")!;
            var explore = view.FindControl<Grid>("ExploreColumn")!;
            Assert.True(identity.Bounds.Height > 0);
            Assert.True(source.Bounds.Height > 0);
            Assert.True(explore.Bounds.Width > 0);
            // 宽窗口：探索入口在来源右侧，两栏靠列定义分列。
            Assert.Equal(1, Grid.GetColumn(explore));
            Assert.Equal(0, Grid.GetRow(explore));
            // 来源与探索同处一栏时，两者的纵向区间必然重叠。
            Assert.True(explore.Bounds.Y < source.Bounds.Bottom && explore.Bounds.Bottom > source.Bounds.Y);
            SaveCorePreview(window, dark ? "core-dark-wide" : "core-light-wide");

            window.Width = 680;
            FlushCoreLayout(window);
            // 窄窗口：来源与探索由两栏收敛为单栏，探索落到来源下方，且不超出窗口。
            Assert.Equal(0, Grid.GetColumn(explore));
            Assert.Equal(1, Grid.GetRow(explore));
            // 单栏时两栏列定义被替换为整宽。
            Assert.Equal("1*", view.FindControl<Grid>("SourceGrid")!.ColumnDefinitions.ToString());
            foreach (var panel in new Control[] { identity, source, explore })
            {
                Assert.True(panel.Bounds.Right <= view.Bounds.Width);
            }
            SaveCorePreview(window, dark ? "core-dark-narrow" : "core-light-narrow");

            window.Width = 1100;
            FlushCoreLayout(window);
            Assert.Equal(1, Grid.GetColumn(explore));
            Assert.Equal(0, Grid.GetRow(explore));
            Assert.Same(model.CheckUpdateCommand, view.FindControl<Button>("CheckUpdateButton")!.Command);
            Assert.False(view.FindControl<Button>("ApplyUpdateButton")!.IsVisible);
            // 分支切换不再藏在折叠面板里：下拉与「切换」按钮都内联在分支行，且没有单独的「刷新分支」按钮。
            Assert.Null(view.FindControl<Expander>("BranchExpander"));
            var branchSelector = view.FindControl<ComboBox>("BranchSelector")!;
            var switchBranch = view.FindControl<Button>("SwitchBranchButton")!;
            Assert.True(branchSelector.IsEffectivelyVisible);
            Assert.True(switchBranch.IsEffectivelyVisible);
            Assert.Equal("切换分支", branchSelector.PlaceholderText);
            Assert.Same(model.SwitchSelectedBranchCommand, switchBranch.Command);
            Assert.True(switchBranch.Bounds.X > branchSelector.Bounds.X);
            Assert.DoesNotContain(view.FindControl<Border>("SourcePanel")!.GetVisualDescendants().OfType<Button>(),
                button => Equals(button.Content, "刷新分支"));
            // 「切换」只在选中的分支与当前安装分支不同时才可用，避免白跑一次重装。
            Assert.False(switchBranch.IsEffectivelyEnabled);
            model.SelectedBranch = new GithubBranch(model.BranchDisplay, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false);
            Dispatcher.UIThread.RunJobs();
            Assert.False(switchBranch.IsEffectivelyEnabled);
            model.SelectedBranch = new GithubBranch("develop", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", false);
            Dispatcher.UIThread.RunJobs();
            Assert.True(switchBranch.IsEffectivelyEnabled);
            // 两栏底部对齐：版本探索的卡片区必须和左栏同时触底。
            var sourceColumn = view.FindControl<Grid>("SourceColumn")!;
            Assert.Equal(sourceColumn.Bounds.Bottom, explore.Bounds.Bottom, 1d);
            Assert.Equal(sourceColumn.Bounds.Bottom, view.FindControl<Button>("OpenPullRequestsButton")!.Bounds.Bottom, 1d);
            // 身份带两行层次：眉标是唯一的主色文字，分支/提交/安装时间退到次级小字。
            var identityTexts = identity.GetVisualDescendants().OfType<TextBlock>().ToList();
            var eyebrow = identityTexts.Single(text => text.Text == model.IdentityEyebrow);
            var meta = identityTexts.Single(text => text.Text == model.IdentityMetaText);
            Assert.True(eyebrow.FontSize > meta.FontSize);
            Assert.Equal(FontWeight.SemiBold, eyebrow.FontWeight);
            Assert.Contains("分支", meta.Text);
            Assert.DoesNotContain("当前运行", meta.Text);
            // 状态带只读，维护带带左侧标签；删除核心被推到最右且与常规维护按钮分开。
            var statusBar = view.FindControl<Border>("StatusBar")!;
            var delete = view.FindControl<Button>("DeleteCoreButton")!;
            Assert.Contains(statusBar, view.FindControl<Border>("WorkbenchPanel")!.GetVisualDescendants());
            Assert.Same(model.DeleteCommand, delete.Command);
            Assert.True(delete.IsEffectivelyVisible);
            Assert.Equal(3, Grid.GetColumn(delete));
            Assert.True(delete.Bounds.X > statusBar.Bounds.X);
            var reinstall = Assert.Single(view.FindControl<Border>("WorkbenchPanel")!.GetVisualDescendants().OfType<Button>(),
                button => Equals(button.Content, "重新安装"));
            Assert.Same(model.ReinstallCommand, reinstall.Command);
            Assert.True(delete.Bounds.X > reinstall.Bounds.X);
            Assert.Contains("维护", view.FindControl<Border>("WorkbenchPanel")!.GetVisualDescendants().OfType<TextBlock>()
                .Select(text => text.Text));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CoreViewKeepsSecondaryPagesAndTypedRecordCommands()
    {
        var model = CreateViewModel(new RecordingManagementService { Installation = Installed() },
            new RecordingRoutePreferenceStore(true), new RecordingDialogService());
        var view = new CorePageView { DataContext = model };
        var window = CreateCoreWindow(view, false);
        try
        {
            window.Width = 680;
            await model.OpenCommitsPageCommand.ExecuteAsync(null);
            var commit = new GithubCommit(new string('a', 40), new string('长', 100), "body", "dev", DateTimeOffset.UtcNow, []);
            model.Commits = [commit];
            FlushCoreLayout(window);
            Assert.False(view.FindControl<StackPanel>("OverviewStage")!.IsVisible);
            Assert.True(view.FindControl<Border>("CommitsStage")!.IsVisible);
            var commits = view.FindControl<ItemsControl>("CommitList")!;
            var rollback = Assert.Single(commits.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "回退到此版本"));
            Assert.Same(model.RollbackToCommitCommand, rollback.Command);
            Assert.Same(commit, rollback.CommandParameter);
            SaveCorePreview(window, "core-commits-narrow");

            await model.OpenPullRequestsPageCommand.ExecuteAsync(null);
            var pullRequest = PullRequest(42) with { Title = new string('变', 100) };
            model.PullRequests = [pullRequest];
            FlushCoreLayout(window);
            Assert.False(view.FindControl<Border>("CommitsStage")!.IsVisible);
            Assert.True(view.FindControl<Border>("PullRequestsStage")!.IsVisible);
            var requests = view.FindControl<ItemsControl>("PullRequestList")!;
            var install = Assert.Single(requests.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "安装 PR"));
            Assert.Same(model.InstallSelectedPullRequestCommand, install.Command);
            Assert.Same(pullRequest, install.CommandParameter);
            SaveCorePreview(window, "core-pr-narrow");

            model.BackToOverviewCommand.Execute(null);
            FlushCoreLayout(window);
            Assert.True(view.FindControl<StackPanel>("OverviewStage")!.IsVisible);
            Assert.False(view.FindControl<Border>("PullRequestsStage")!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CoreViewShowsOnlySelectedVariantInstallFormWhenMissing()
    {
        var model = CreateViewModel(new RecordingManagementService(), new RecordingRoutePreferenceStore(true), new RecordingDialogService());
        var view = new CorePageView { DataContext = model };
        var window = CreateCoreWindow(view, false);
        try
        {
            Assert.True(view.FindControl<Border>("InstallPanel")!.IsVisible);
            // 未安装时工作台不存在，安装表单独立占页；来源面板始终在工作台内，不单独可见。
            Assert.True(view.FindControl<Border>("WorkbenchPanel")!.IsVisible == false);
            Assert.True(view.FindControl<Border>("SourcePanel")!.IsVisible);
            Assert.True(view.FindControl<Button>("InstallOfficialButton")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<Button>("InstallCustomButton")!.IsEffectivelyVisible);
            model.SelectedVariantOption = model.VariantOptions.Single(option => option.Value == ManagedCoreVariant.Custom);
            FlushCoreLayout(window);
            Assert.False(view.FindControl<Button>("InstallOfficialButton")!.IsEffectivelyVisible);
            Assert.Equal("自定义核心", model.VariantLabel);
            Assert.True(view.FindControl<Button>("InstallCustomButton")!.IsEffectivelyVisible);
            Assert.Same(model.InstallCustomCommand, view.FindControl<Button>("InstallCustomButton")!.Command);
            SaveCorePreview(window, "core-custom-install");
        }
        finally
        {
            window.Close();
        }
    }

    private static Window CreateCoreWindow(CorePageView view, bool dark)
    {
        var window = new Window
        {
            Width = 1100, Height = 1100, Content = view, Padding = new Thickness(24),
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light,
        };
        window.Show();
        FlushCoreLayout(window);
        return window;
    }

    private static void FlushCoreLayout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void SaveCorePreview(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("DANMU_TEST_RENDER_DIRECTORY");
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(directory, name + ".png"));
    }
}
