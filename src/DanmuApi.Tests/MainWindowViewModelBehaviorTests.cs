using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
using DanmuApi.App.Views;
using DanmuApi.Core;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed partial class MainWindowViewModelBehaviorTests
{
    [Fact]
    public async Task SamePortDoesNotWriteSettingsOrRestart()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(pathsRoot: directory.Path, token: "same-token");
        var settings = new RecordingSettingsStore();
        var controller = new RecordingRuntimeController(new RuntimeSnapshot(
            DesktopRuntimeState.Stopped,
            Port: 9321));
        await using var viewModel = CreateViewModel(paths, settings, controller);

        await viewModel.ApplyPortAsync(9321);

        Assert.Equal(0, settings.WriteCalls);
        Assert.Equal(0, controller.RestartCalls);
        Assert.Contains("端口未修改", viewModel.DiagnosticText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MergedPullRequestStackIsVisibleOnTheShellAndUpdatesAfterAMerge()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(pathsRoot: directory.Path, token: "token");
        WriteCoreVersion(paths, "stable", "1.20.10");
        var management = new StubCoreManagementService();
        await using var viewModel = CreateViewModel(
            paths,
            new RecordingSettingsStore(),
            new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped, Port: 9321)),
            coreManagement: management);

        // 普通分支安装：不显示「已合并 PR」。
        Assert.False(viewModel.HasMergedPullRequests);
        Assert.Equal("无", viewModel.MergedPullRequestText);
        Assert.Equal(string.Empty, viewModel.MergedPullRequestSummaryText);

        // 核心页把 PR 组合装上并落盘 manifest（不经重启界面）：概览与侧栏必须立刻显示已合并 PR。
        WriteCoreVersion(paths, "stable", "1.21.0");
        WriteMergedStack(paths, "stable", [12, 3]);
        management.Announce(ManagedCoreVariant.Stable);

        Assert.True(viewModel.HasMergedPullRequests);
        Assert.Equal("#12 #3", viewModel.MergedPullRequestText);
        Assert.Equal("已合并 PR #12 #3", viewModel.MergedPullRequestSummaryText);
    }

    [Fact]
    public async Task UnreadableCoreManifestIsReportedAsUnknownNotAsNoStack()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(pathsRoot: directory.Path, token: "token");
        WriteCoreVersion(paths, "stable", "1.20.10");
        var manifestPath = Path.Combine(paths.NodeProjectDirectory, "danmu_api_stable", ".danmuapi-core-source.json");
        File.WriteAllText(manifestPath, "{ this is not json");
        var diagnostics = new ConfigurationDiagnostics();
        await using var viewModel = CreateViewModel(
            paths,
            new RecordingSettingsStore(),
            new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped, Port: 9321)),
            diagnostics: diagnostics);

        // 读不出来必须报「未知」，绝不能让用户以为装的是普通分支版本。
        Assert.Equal("未知", viewModel.MergedPullRequestText);
        Assert.False(viewModel.HasMergedPullRequests);
        Assert.Equal(string.Empty, viewModel.MergedPullRequestSummaryText);
        Assert.Contains("读取核心 PR 组合来源失败", diagnostics.LastDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplacedCoreUpdatesTheShellVersionWithoutRefreshOrRestart()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(pathsRoot: directory.Path, token: "token");
        WriteCoreVersion(paths, "stable", "1.20.10");
        var controller = new RecordingRuntimeController(new RuntimeSnapshot(
            DesktopRuntimeState.Stopped,
            Port: 9321));
        var management = new StubCoreManagementService();
        await using var viewModel = CreateViewModel(
            paths,
            new RecordingSettingsStore(),
            controller,
            coreManagement: management);
        Assert.Equal("1.20.10", viewModel.CoreVersionText);

        // 托盘「立即更新核心」或后台自动更新把磁盘换成新提交：不重启界面，也不该再显示旧版本号。
        WriteCoreVersion(paths, "stable", "1.21.0");
        management.Announce(ManagedCoreVariant.Stable);

        Assert.Equal("1.21.0", viewModel.CoreVersionText);
        Assert.Equal("1.21.0", viewModel.CoreVersionShortText);
    }

    [Fact]
    public async Task SameTokenDoesNotWriteEnvOrRestart()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(directory.Path, "same-token");
        var settings = new RecordingSettingsStore();
        var controller = new RecordingRuntimeController(new RuntimeSnapshot(
            DesktopRuntimeState.Stopped,
            Port: 9321));
        await using var viewModel = CreateViewModel(paths, settings, controller);
        var envPath = Path.Combine(paths.NodeProjectDirectory, "config", ".env");
        var before = File.ReadAllText(envPath);

        await viewModel.ApplyTokenAsync("same-token");

        Assert.Equal(0, settings.WriteCalls);
        Assert.Equal(0, controller.RestartCalls);
        Assert.Equal(before, File.ReadAllText(envPath));
        Assert.Contains("Token 未修改", viewModel.DiagnosticText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyTokenFromMaskedEditorPreservesExistingToken()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(directory.Path, "existing-token");
        var settings = new RecordingSettingsStore();
        var controller = new RecordingRuntimeController(new RuntimeSnapshot(
            DesktopRuntimeState.Stopped,
            Port: 9321));
        await using var viewModel = CreateViewModel(paths, settings, controller);
        var envPath = Path.Combine(paths.NodeProjectDirectory, "config", ".env");
        var before = File.ReadAllText(envPath);

        await viewModel.ApplyTokenAsync(string.Empty);

        Assert.Equal(0, settings.WriteCalls);
        Assert.Equal(0, controller.RestartCalls);
        Assert.Equal(before, File.ReadAllText(envPath));
        Assert.Equal("ex••••••••••", viewModel.TokenMasked);
    }

    [Fact]
    public async Task ChangedStoppedPortWritesOverrideWithoutRestart()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(directory.Path, "same-token");
        var settings = new RecordingSettingsStore();
        var controller = new RecordingRuntimeController(new RuntimeSnapshot(
            DesktopRuntimeState.Stopped,
            Port: 9321));
        await using var viewModel = CreateViewModel(paths, settings, controller);

        await viewModel.ApplyPortAsync(9432);

        Assert.Equal(1, settings.WriteCalls);
        Assert.Equal("9432", settings.Values["port_override"]);
        Assert.Equal(0, controller.RestartCalls);
        Assert.Contains("下次启动", viewModel.DiagnosticText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ipv6SettingWhileRunningReloadsConfigAndRestartsService()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(directory.Path, "same-token");
        var settings = new RecordingSettingsStore();
        var controller = new RecordingRuntimeController(new RuntimeSnapshot(
            DesktopRuntimeState.Running,
            Port: 9321));
        await using var viewModel = CreateViewModel(paths, settings, controller);

        viewModel.SettingsPage.Ipv6Enabled = true;

        Assert.Equal("true", settings.Values["ipv6_enabled"]);
        Assert.Equal(1, controller.RestartCalls);
        Assert.Equal("::", viewModel.ListenHostText);
    }

    [Fact]
    public async Task DualStackKeepsIpv4EntryAndAddsIpv6Entry()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(directory.Path, "same-token");
        var settings = new RecordingSettingsStore();
        var controller = new RecordingRuntimeController(new RuntimeSnapshot(
            DesktopRuntimeState.Running,
            Port: 9321));
        await using var viewModel = CreateViewModel(paths, settings, controller);

        viewModel.SettingsPage.Ipv6Enabled = true;

        var titles = viewModel.EndpointItems.Select(item => item.Title).ToList();
        Assert.Contains("本机 API", titles);
        Assert.Equal("局域网 IPv4 API", titles[1]);
        var ipv4 = viewModel.EndpointItems[1];
        var resolvedIpv4 = RuntimeNetworkAddressResolver.ResolvePrimaryIpv4Address();
        if (resolvedIpv4 is not null)
        {
            Assert.Equal(RuntimeEndpointBuilder.BuildApiAddress(resolvedIpv4, 9321, "same-token"), ipv4.Address);
        }
        else
        {
            Assert.Equal("未找到可分享的局域网 IPv4 地址", ipv4.Hint);
            Assert.False(ipv4.HasAddress);
            Assert.False(ipv4.CopyCommand.CanExecute(null));
        }

        var ipv6 = Assert.Single(titles, title => title == "局域网 IPv6 API");
        Assert.Equal(ipv6, titles[^1]);
    }

    [Fact]
    public async Task Ipv4OnlyModeNeverShowsIpv6Entry()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(directory.Path, "same-token");
        var settings = new RecordingSettingsStore();
        var controller = new RecordingRuntimeController(new RuntimeSnapshot(
            DesktopRuntimeState.Running,
            Port: 9321));
        await using var viewModel = CreateViewModel(paths, settings, controller);

        Assert.DoesNotContain(
            viewModel.EndpointItems,
            item => item.Title == "局域网 IPv6 API");
    }

    [Fact]
    public async Task ConfigurationTokenChangeRefreshesEndpointsWithoutRestart()
    {
        using var fixture = new ConfigurationFixture("old-token");
        var controller = new RecordingRuntimeController(new RuntimeSnapshot(
            DesktopRuntimeState.Running,
            Port: 9321));
        await using var viewModel = fixture.CreateMainViewModel(controller);
        var before = viewModel.EndpointItems.Single(item => item.Title == "本机 API").Address;

        fixture.Dialogs.CoreEnvEdit = CoreEnvEditResult.Set("new-token");
        await fixture.Configuration.EditVariableCommand.ExecuteAsync(fixture.Find("TOKEN"));

        var after = viewModel.EndpointItems.Single(item => item.Title == "本机 API").Address;
        Assert.NotEqual(before, after);
        Assert.Contains("new-token", after, StringComparison.Ordinal);
        Assert.Equal(0, controller.RestartCalls);
    }

    [Fact]
    public void RuntimeTokenResolverUsesProcessThenDotEnvThenFallback()
    {
        using var directory = new TemporaryDirectory();
        var envPath = Path.Combine(directory.Path, ".env");
        File.WriteAllText(envPath, "TOKEN=dotenv-token\n");

        Assert.Equal("process-token", RuntimeTokenResolver.Resolve(envPath, " process-token "));
        Assert.Equal("dotenv-token", RuntimeTokenResolver.Resolve(envPath, null));
        File.Delete(envPath);
        Assert.Equal(RuntimeDefaults.FallbackToken, RuntimeTokenResolver.Resolve(envPath, null));
    }

    [Fact]
    public async Task TokenWriteBlockedWithoutAdminModeLeavesEnvUntouched()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(directory.Path, "same-token");
        var controller = new RecordingRuntimeController(new RuntimeSnapshot(
            DesktopRuntimeState.Stopped,
            Port: 9321));
        var dialogs = new RecordingDialogService();
        await using var viewModel = CreateViewModel(
            paths,
            new RecordingSettingsStore(),
            controller,
            new StubAdminSessionService(configured: true),
            dialogs);
        var envPath = Path.Combine(paths.NodeProjectDirectory, "config", ".env");
        var before = File.ReadAllText(envPath);

        await viewModel.ApplyTokenAsync("brand-new-token");

        Assert.Equal(before, File.ReadAllText(envPath));
        var prompt = Assert.Single(dialogs.AdminPrompts);
        Assert.Contains("开启管理员模式", prompt, StringComparison.Ordinal);
        Assert.Equal("overview", viewModel.SelectedNavigationItem.Key);
    }

    [Fact]
    public async Task AdminPromptConfirmationNavigatesToSecuritySettings()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(directory.Path, "same-token");
        var controller = new RecordingRuntimeController(new RuntimeSnapshot(
            DesktopRuntimeState.Stopped,
            Port: 9321));
        var dialogs = new RecordingDialogService { AdminPromptResult = true };
        await using var viewModel = CreateViewModel(
            paths,
            new RecordingSettingsStore(),
            controller,
            new StubAdminSessionService(configured: true),
            dialogs);

        await viewModel.ApplyTokenAsync("brand-new-token");

        Assert.Equal("settings", viewModel.SelectedNavigationItem.Key);
        Assert.Equal("security", viewModel.SettingsPage.SelectedCategory.Key);
    }

    [Fact]
    public async Task TokenWriteWithAdminModeUpdatesEnv()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(directory.Path, "same-token");
        var controller = new RecordingRuntimeController(new RuntimeSnapshot(
            DesktopRuntimeState.Stopped,
            Port: 9321));
        await using var viewModel = CreateViewModel(
            paths,
            new RecordingSettingsStore(),
            controller,
            new StubAdminSessionService(adminMode: true, configured: true, sessionToken: "adm-sess"));
        var envPath = Path.Combine(paths.NodeProjectDirectory, "config", ".env");

        await viewModel.ApplyTokenAsync("brand-new-token");

        Assert.Equal("brand-new-token", DotEnvFile.ReadValue(envPath, "TOKEN"));
    }

    [Fact]
    public async Task CacheClearBlockedWithoutAdminModeNeverCallsCoreClient()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(directory.Path, "same-token");
        var controller = new RecordingRuntimeController(new RuntimeSnapshot(
            DesktopRuntimeState.Running,
            9321,
            43));
        var client = new RecordingCoreCacheClient();
        await using var viewModel = CreateViewModel(
            paths,
            new RecordingSettingsStore(),
            controller,
            new StubAdminSessionService(configured: true),
            cacheClient: client);

        var result = await viewModel.ClearCoreCachesAsync(["searchCache"]);

        Assert.False(result.Succeeded);
        Assert.Contains("管理员模式", result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task CacheClearInAdminModeUsesSessionTokenForAdminPath()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(directory.Path, "same-token");
        var controller = new RecordingRuntimeController(new RuntimeSnapshot(
            DesktopRuntimeState.Running,
            9321,
            43));
        var client = new RecordingCoreCacheClient();
        await using var viewModel = CreateViewModel(
            paths,
            new RecordingSettingsStore(),
            controller,
            new StubAdminSessionService(adminMode: true, configured: true, sessionToken: "adm-sess"),
            cacheClient: client);

        var result = await viewModel.ClearCoreCachesAsync(["searchCache"]);

        Assert.True(result.Succeeded);
        Assert.Equal(1, client.Calls);
        Assert.Equal("adm-sess", client.LastAdminToken);
    }

    private sealed class ConfigurationFixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();

        public ConfigurationFixture(string token)
        {
            Root = Path.Combine(_directory.Path, "runtime-root");
            var project = Path.Combine(Root, "runtime", "nodejs-project");
            var catalogDirectory = Path.Combine(project, "danmu_api_stable", "configs");
            Directory.CreateDirectory(catalogDirectory);
            File.WriteAllText(Path.Combine(catalogDirectory, "envs.js"), """
                const envVarConfig = {
                  'TOKEN': { category: 'api', type: 'text', description: '访问令牌' },
                };
                this.get('TOKEN', '87654321', 'string');
                """);
            var configDirectory = Path.Combine(project, "config");
            Directory.CreateDirectory(configDirectory);
            EnvPath = Path.Combine(configDirectory, ".env");
            File.WriteAllText(EnvPath, $"DANMU_API_PORT=9321{Environment.NewLine}DANMU_API_HOST=127.0.0.1{Environment.NewLine}DANMU_API_VARIANT=stable{Environment.NewLine}TOKEN={token}{Environment.NewLine}");
        }

        public string Root { get; }
        public string EnvPath { get; }
        public global::DanmuApi.Tests.RecordingDialogService Dialogs { get; } = new();
        public StubConfigurationWriteGate Gate { get; } = new();
        public StubConfigurationEnvClient EnvClient { get; } = new();
        public ConfigurationPageViewModel Configuration { get; private set; } = null!;

        public MainWindowViewModel CreateMainViewModel(RecordingRuntimeController controller)
        {
            var paths = new AppPaths(Root, Path.Combine(_directory.Path, "appdata"));
            var settings = new ConfigurationSettingsStore();
            Configuration = new ConfigurationPageViewModel(
                paths,
                settings,
                Dialogs,
                Gate,
                EnvClient,
                new StubAdminSessionService(adminMode: true, configured: true, sessionToken: "admin-session"),
                new ConfigurationDiagnostics());
            var settingsPage = new SettingsPageViewModel(
                settings,
                new RecordingAutostartService(),
                new RecordingNotificationService(),
                paths);
            return new MainWindowViewModel(
                controller,
                new RecordingHealthClient(),
                settings,
                new RecordingCoreCacheClient(),
                paths,
                new RecordingDialogService(),
                settingsPage,
                new StubAdminSessionService(adminMode: true, configured: true, sessionToken: "admin-session"),
                configurationPage: Configuration,
                writeGate: Gate);
        }

        public ConfigurationVariableRow Find(string key)
        {
            foreach (var category in Configuration.Categories)
            {
                Configuration.SelectedCategory = category;
                var row = Configuration.FilteredVariables.SingleOrDefault(item => item.Key == key);
                if (row is not null)
                {
                    return row;
                }
            }

            throw new Xunit.Sdk.XunitException($"未找到配置变量：{key}");
        }

        public void Dispose() => _directory.Dispose();
    }

    private sealed class StubConfigurationWriteGate : IAdminWriteGate
    {
        public Action? NavigateToSecurity { get; set; }
        public Task<bool> EnsureAsync(string action, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class StubConfigurationEnvClient : ICoreEnvClient
    {
        public Task<CoreEnvDeleteResult> DeleteAsync(string host, int port, string? token, string? adminToken, string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(CoreEnvDeleteResult.Failure("not used"));

        public Task<CoreEnvSetResult> SetAsync(string host, int port, string? token, string? adminToken, string key, string value, CancellationToken cancellationToken = default) =>
            Task.FromResult(CoreEnvSetResult.Success());

        public Task<CoreEnvValueResult> ReadConfigValueAsync(string host, int port, string? token, string? adminToken, string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(CoreEnvValueResult.Failure("not used"));
    }

    private sealed class ConfigurationSettingsStore : ISettingsStore
    {
        public IReadOnlyDictionary<string, string> Read() => new Dictionary<string, string>(StringComparer.Ordinal);
        public void Write(IReadOnlyDictionary<string, string?> changes) => throw new NotSupportedException("not used");
    }

    private sealed class ConfigurationDiagnostics : IAppDiagnostics
    {
        public string? LastDiagnostic { get; private set; }
        public void Record(string message, Exception? error = null) => LastDiagnostic = message;
    }

    /// <summary>
    /// 回归：协调器在壳层构造之前就把落盘结论接回来时，卡片要立刻出现；
    /// 反之壳层先建、结论后到时也要亮起来 —— 这两条路径曾经只有一条是对的。
    /// </summary>
    [Fact]
    public async Task SidebarUpdateCardAppearsForARestoredDiscoveryInEitherOrder()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(pathsRoot: directory.Path, token: "token");

        // 顺序 A：协调器手里已有结论（App 启动时先 RestorePersistedDiscovery）。
        var before = new StubCoreUpdateCoordinator(CoreUpdate("abcdef1234567890abcdef1234567890abcdef12"));
        await using (var viewModel = CreateViewModel(paths, new RecordingSettingsStore(),
            new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped)), coreUpdate: before))
        {
            var hint = Assert.Single(viewModel.UpdateHints);
            Assert.Equal("核心可更新", hint.Title);
            Assert.True(viewModel.HasUpdateHints);
        }

        // 顺序 B：结论晚一步接回来（壳层构造时还没恢复），广播后卡片必须补上。
        var after = new StubCoreUpdateCoordinator(lastResult: null);
        after.SetPersisted(CoreUpdate("abcdef1234567890abcdef1234567890abcdef12"));
        await using (var viewModel = CreateViewModel(paths, new RecordingSettingsStore(),
            new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped)), coreUpdate: after))
        {
            Assert.Empty(viewModel.UpdateHints);
            Assert.False(viewModel.HasUpdateHints);

            after.ReconcileDiscovery(ManagedCoreVariant.Stable);

            Assert.Equal("核心可更新", Assert.Single(viewModel.UpdateHints).Title);
            Assert.True(viewModel.HasUpdateHints);
        }
    }

    /// <summary>
    /// 回归：冷却命中不再广播结果，卡片必须原地保留。
    /// 真协调器只广播"有结论"的结果，所以这里的替身也照这个语义走。
    /// </summary>
    [Fact]
    public async Task SidebarUpdateCardSurvivesACooldownSkipBroadcast()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(pathsRoot: directory.Path, token: "token");
        var coordinator = new StubCoreUpdateCoordinator(CoreUpdate("abcdef1234567890abcdef1234567890abcdef12"));
        await using var viewModel = CreateViewModel(paths, new RecordingSettingsStore(),
            new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped)), coreUpdate: coordinator);
        Assert.Single(viewModel.UpdateHints);

        // 冷却命中的结果只回给调用方，不经过 ResultChanged：卡片与文案都不变。
        var skipped = CoreUpdate("abcdef1234567890abcdef1234567890abcdef12") with
        {
            Status = CoreUpdateCheckStatus.SkippedCooldown,
            UpdateAvailable = false,
            Diagnostic = "距离上次检查未满 5 分钟",
        };
        await coordinator.ReportSkipAsync(skipped);

        Assert.Equal("核心可更新", Assert.Single(viewModel.UpdateHints).Title);
        Assert.Equal("稳定核心 · abcdef1", viewModel.UpdateHints[0].Detail);
    }

    /// <summary>
    /// 遗留问题回归（壳层）：核心在核心页或托盘被更新/回退/删除后，协调器广播 null 作废结论，
    /// 侧栏卡片必须立刻收起。以前缺这条路径，卡片会一直挂到下一次联网检查。
    /// </summary>
    [Fact]
    public async Task SidebarUpdateCardDisappearsWhenTheConclusionIsVoided()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(pathsRoot: directory.Path, token: "token");
        var coordinator = new StubCoreUpdateCoordinator(CoreUpdate("abcdef1234567890abcdef1234567890abcdef12"));
        await using var viewModel = CreateViewModel(paths, new RecordingSettingsStore(),
            new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped)), coreUpdate: coordinator);
        Assert.Single(viewModel.UpdateHints);
        Assert.True(viewModel.HasUpdateHints);

        coordinator.Void();

        Assert.Empty(viewModel.UpdateHints);
        Assert.False(viewModel.HasUpdateHints);
    }

    [Fact]
    public async Task SidebarUpdateCardCombinesCoreAndSoftwareUpdates()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(pathsRoot: directory.Path, token: "token");
        var coordinator = new StubCoreUpdateCoordinator(CoreUpdate("abcdef1234567890abcdef1234567890abcdef12"));
        await using var viewModel = CreateViewModel(paths, new RecordingSettingsStore(),
            new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped)), coreUpdate: coordinator);

        // 只有核心有更新：一条，标题点明变体，详情只留短提交（侧栏文本宽度有限）。
        var only = Assert.Single(viewModel.UpdateHints);
        Assert.Equal("核心可更新", only.Title);
        Assert.Equal("稳定核心 · abcdef1", only.Detail);

        // 两个都有：并列两条，核心在前。
        var applicationUpdates = new ApplicationUpdateViewModel(
            new SettingsStore(Path.Combine(directory.Path, "updates.properties")),
            new RecordingDialogService(),
            new SilentNotifications(),
            new ConfigurationDiagnostics());
        viewModel.ApplicationUpdates = applicationUpdates;
        applicationUpdates.AvailableVersion = "0.6.0-preview.1";
        applicationUpdates.HasUpdate = true;
        Assert.Equal(
            ["核心可更新", "软件可更新"],
            viewModel.UpdateHints.Select(hint => hint.Title).ToArray());
        Assert.Equal("→ 0.6.0-preview.1", viewModel.UpdateHints[1].Detail);

        // 后台或前台检查完一广播，卡片立刻跟上 —— 用户不必先进过核心页。
        await coordinator.CheckAsync(ManagedCoreVariant.Stable, force: true);
        Assert.Equal(2, viewModel.UpdateHints.Count);
        Assert.Contains("1234567", viewModel.UpdateHints[0].Detail, StringComparison.Ordinal);

        // 核心已到最新 → 只剩软件那条；软件也没有 → 整块收起，不留空洞。
        coordinator.Raise(NoUpdate("abcdef1234567890abcdef1234567890abcdef12"));
        Assert.Equal("软件可更新", Assert.Single(viewModel.UpdateHints).Title);
        applicationUpdates.HasUpdate = false;
        Assert.Empty(viewModel.UpdateHints);
        Assert.False(viewModel.HasUpdateHints);
    }

    /// <summary>
    /// 点卡片是「就地弹窗完成更新」，不是把用户扔到核心页/关于页自己找按钮。
    /// </summary>
    [Fact]
    public async Task SidebarUpdateCardClickOpensTheQuickUpdateDialogWithoutNavigating()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(pathsRoot: directory.Path, token: "token");
        var dialogs = new RecordingDialogService();
        var coordinator = new StubCoreUpdateCoordinator(CoreUpdate("abcdef1234567890abcdef1234567890abcdef12"));
        await using var viewModel = CreateViewModel(paths, new RecordingSettingsStore(),
            new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped)),
            dialogs: dialogs, coreUpdate: coordinator);
        var applicationUpdates = new ApplicationUpdateViewModel(
            new SettingsStore(Path.Combine(directory.Path, "updates.properties")),
            dialogs, new SilentNotifications(), new ConfigurationDiagnostics());
        viewModel.ApplicationUpdates = applicationUpdates;
        applicationUpdates.AvailableVersion = "0.6.0-preview.1";
        applicationUpdates.HasUpdate = true;

        Assert.Equal("overview", viewModel.SelectedNavigationItem.Key);
        var appHint = viewModel.UpdateHints.Single(hint => hint.Kind == "app");
        viewModel.OpenUpdateHintCommand.Execute(appHint);

        Assert.Single(dialogs.QuickUpdates);
        Assert.True(dialogs.QuickUpdates[0].IsAppFlow);
        Assert.Equal("overview", viewModel.SelectedNavigationItem.Key);

        // 核心那条没有核心页可依托时不能静默：给一条明确的提示，仍然不跳页。
        var coreHint = viewModel.UpdateHints.Single(hint => hint.Kind == "core");
        viewModel.OpenUpdateHintCommand.Execute(coreHint);

        Assert.Single(dialogs.QuickUpdates);
        Assert.Contains(dialogs.Messages, message => message.IsError && message.Title == "核心更新");
        Assert.Equal("overview", viewModel.SelectedNavigationItem.Key);
    }

    /// <summary>卡片图标是 24×24 的 SVG 路径数据（PathIcon），不再是侧栏字体字形。</summary>
    [Fact]
    public async Task SidebarUpdateCardsUseSvgGeometryIcons()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(pathsRoot: directory.Path, token: "token");
        var coordinator = new StubCoreUpdateCoordinator(CoreUpdate("abcdef1234567890abcdef1234567890abcdef12"));
        await using var viewModel = CreateViewModel(paths, new RecordingSettingsStore(),
            new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped)), coreUpdate: coordinator);

        var hint = Assert.Single(viewModel.UpdateHints);
        Assert.StartsWith("M", hint.Icon, StringComparison.Ordinal);
        Assert.True(hint.Icon.Length > 40, "SVG 路径数据不该只有一个字符那么长");
        Assert.Equal("核心可更新｜稳定核心 · abcdef1", hint.ToolTip);
    }

    [AvaloniaFact]
    public async Task SidebarUpdateCardRendersExpandedAndCollapsed()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(pathsRoot: directory.Path, token: "token");
        var coordinator = new StubCoreUpdateCoordinator(CoreUpdate("abcdef1234567890abcdef1234567890abcdef12"));
        await using var model = CreateViewModel(paths, new RecordingSettingsStore(),
            new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped)), coreUpdate: coordinator);
        var applicationUpdates = new ApplicationUpdateViewModel(
            new SettingsStore(Path.Combine(directory.Path, "updates.properties")),
            new RecordingDialogService(),
            new SilentNotifications(),
            new ConfigurationDiagnostics());
        model.ApplicationUpdates = applicationUpdates;
        applicationUpdates.AvailableVersion = "0.6.0-preview.1";
        applicationUpdates.HasUpdate = true;

        var window = new MainWindow { DataContext = model, Width = 1280, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Equal(2, VisibleUpdateCards(window));
        // 展开态：徽章 + 标题 + 详情 + 箭头都在，且卡片是真的按钮（可点）。
        var expanded = VisibleUpdateCardButtons(window);
        Assert.All(expanded, button => Assert.NotNull(button.Command));
        Assert.Equal(2, window.GetVisualDescendants().OfType<PathIcon>()
            .Count(icon => icon.Data is not null && icon.Width is 17 or 19));
        SaveThemeRender(window, "sidebar-two-update-hints-expanded.png");
        window.Close();

        // 深色主题在**新建窗口**上渲染（主题变体必须在 Show 之前给定，
        // 换肤发生在已显示的窗口上时 DynamicResource 不会重算，会渲出深浅混搭的假象）。
        // 卡片的渐变底与徽章都是按主题分开定的，只测浅色会漏掉"深色下徽章糊在卡片底上"。
        var darkWindow = new MainWindow { DataContext = model, Width = 1280, Height = 800 };
        darkWindow.RequestedThemeVariant = ThemeVariant.Dark;
        darkWindow.Show();
        Dispatcher.UIThread.RunJobs();
        darkWindow.UpdateLayout();
        Assert.Equal(2, VisibleUpdateCards(darkWindow));
        // 断言真的是深色那套笔刷：整窗渲染在这种 headless 场景下侧栏配色不跟随窗口主题
        // （基线 shell-dark-* 截图也是浅色侧栏），所以这里直接对着资源值验，别让深色悄悄漏测。
        var darkCard = VisibleUpdateCardButtons(darkWindow).First();
        var darkGradient = Assert.IsType<LinearGradientBrush>(darkCard.Background);
        Assert.Equal(Color.Parse("#1B2436"), darkGradient.GradientStops[0].Color);
        var darkBadge = darkCard.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Classes.Contains("update-badge"));
        Assert.Equal(
            Color.Parse("#4F7DFF"),
            Assert.IsType<LinearGradientBrush>(darkBadge.Background).GradientStops[0].Color);
        SaveThemeRender(darkWindow, "sidebar-two-update-hints-expanded-dark.png");
        darkWindow.Close();

        var collapsedWindow = new MainWindow { DataContext = model, Width = 1280, Height = 800 };
        collapsedWindow.Show();
        Dispatcher.UIThread.RunJobs();
        model.ToggleSidebarCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        collapsedWindow.UpdateLayout();
        Assert.False(model.IsSidebarExpanded);
        // 折叠态同样是两条，但只剩徽章那一个图标，标题文字不再参与可见布局。
        // 注意用 IsEffectivelyVisible：展开态那张卡片仍在可视树里，只是父按钮被隐藏了。
        Assert.Equal(2, VisibleUpdateCards(collapsedWindow));
        Assert.DoesNotContain(
            collapsedWindow.GetVisualDescendants().OfType<TextBlock>(),
            text => text.IsEffectivelyVisible && text.Text == "核心可更新");
        SaveThemeRender(collapsedWindow, "sidebar-two-update-hints-collapsed.png");
        collapsedWindow.Close();
    }

    /// <summary>快速更新弹窗两种流程各渲染一次，确认按钮、进度与说明文字都落在版面上。</summary>
    [AvaloniaFact]
    public void QuickUpdateWindowRendersBothFlows()
    {
        using var directory = new TemporaryDirectory();
        var app = new ApplicationUpdateViewModel(
            new SettingsStore(Path.Combine(directory.Path, "updates.properties")),
            new RecordingDialogService(), new SilentNotifications(), new ConfigurationDiagnostics())
        {
            AvailableVersion = "0.6.0-preview.1",
            HasUpdate = true,
            PackageText = "免安装版 · 原目录更新 · 99.6 MB",
            PublishedText = "发布于 2026-09-21 15:49",
            ReleaseNotes = "修复本地弹幕上传；新增回到前台静默检查更新。",
            IsDownloading = true,
            ProgressPercent = 42,
            Status = "正在下载 41.8 / 99.6 MB",
        };
        using var appDialog = QuickUpdateDialogViewModel.ForApplication(app);
        RenderDialog(appDialog, "quick-update-dialog-application.png");

        using var coreDialog = QuickUpdateDialogViewModel.ForCore(
            ManagedCoreVariant.Stable,
            "abcdef1234567890abcdef1234567890abcdef12",
            "9876543210fedcba9876543210fedcba98765432",
            "fix: 修复合并规则在空标题下的崩溃",
            applyCoreAsync: () => Task.CompletedTask,
            openCorePage: () => { });
        RenderDialog(coreDialog, "quick-update-dialog-core.png");
    }

    private static void RenderDialog(QuickUpdateDialogViewModel model, string filename)
    {
        var window = new QuickUpdateWindow { DataContext = model };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        SaveThemeRender(window, filename);
        window.Close();
    }

    private static int VisibleUpdateCards(MainWindow window) =>
        VisibleUpdateCardButtons(window).Count();

    private static List<Button> VisibleUpdateCardButtons(MainWindow window) => window
        .GetVisualDescendants()
        .OfType<Button>()
        .Where(button => button.Classes.Contains("update-card") && button.IsVisible)
        .ToList();

    private static CoreUpdateCheckResult CoreUpdate(string sha) => new(
        ManagedCoreVariant.Stable,
        CoreUpdateCheckStatus.Checked,
        true,
        null,
        new GithubCommit(sha, "测试提交", "测试提交", "author", DateTimeOffset.UnixEpoch, []),
        null,
        DateTimeOffset.UnixEpoch,
        "测试");

    private static CoreUpdateCheckResult NoUpdate(string sha) => CoreUpdate(sha) with { UpdateAvailable = false };

    private sealed class StubCoreUpdateCoordinator(CoreUpdateCheckResult? lastResult) : ICoreUpdateCoordinator
    {
        private int _round;

        public TimeSpan AutomaticInterval => TimeSpan.FromMinutes(5);
        public CoreUpdateCheckResult? LastResult { get; private set; } = lastResult;
        public event EventHandler<CoreUpdateCheckResult?>? ResultChanged;

        public void Raise(CoreUpdateCheckResult result)
        {
            LastResult = result;
            ResultChanged?.Invoke(this, result);
        }

        /// <summary>模拟"结论作废"：与真实协调器一样广播 null，卡片必须立刻收起。</summary>
        public void Void()
        {
            LastResult = null;
            ResultChanged?.Invoke(this, null);
        }

        /// <summary>模拟启动时接回落盘结论：与真实协调器一样，已有结果时不覆盖。</summary>
        public void ReconcileDiscovery(ManagedCoreVariant variant)
        {
            if (Restored is null || LastResult is not null) return;
            Raise(Restored);
        }

        public CoreUpdateCheckResult? Restored { get; private set; }

        public void SetPersisted(CoreUpdateCheckResult result) => Restored = result;

        /// <summary>冷却/失败这类"没有结论"的结果：真协调器只回给调用方，不广播。</summary>
        public Task<CoreUpdateCheckResult> ReportSkipAsync(CoreUpdateCheckResult skipped) =>
            Task.FromResult(skipped);

        public Task<CoreUpdateCheckResult> CheckAsync(
            ManagedCoreVariant variant, bool force, CancellationToken cancellationToken = default) =>
            CheckAsync(variant, force, AutomaticInterval, cancellationToken);

        public Task<CoreUpdateCheckResult> CheckAsync(
            ManagedCoreVariant variant, bool force, TimeSpan automaticInterval, CancellationToken cancellationToken = default)
        {
            // 每次检查换一个短提交，方便断言卡片确实跟着刷新。
            _round++;
            Raise(CoreUpdate($"{_round}234567890abcdef1234567890abcdef123456"));
            return Task.FromResult(LastResult!);
        }
    }

    private sealed class SilentNotifications : IDesktopNotificationService
    {
        public Task<DesktopNotificationResult> ShowAsync(string title, string message, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DesktopNotificationResult(true, "test"));
    }

    private static MainWindowViewModel CreateViewModel(
        AppPaths paths,
        RecordingSettingsStore settings,
        RecordingRuntimeController controller,
        StubAdminSessionService? adminSession = null,
        RecordingDialogService? dialogs = null,
        RecordingCoreCacheClient? cacheClient = null,
        IRuntimeHealthClient? healthClient = null,
        RuntimePreparationService? preparation = null,
        StubCoreManagementService? coreManagement = null,
        CorePageViewModel? corePage = null,
        ICoreUpdateCoordinator? coreUpdate = null,
        IAppDiagnostics? diagnostics = null)
    {
        var settingsPage = new SettingsPageViewModel(
            settings,
            new RecordingAutostartService(),
            new RecordingNotificationService(),
            paths);
        return new MainWindowViewModel(
            controller,
            healthClient ?? new RecordingHealthClient(),
            settings,
            cacheClient ?? new RecordingCoreCacheClient(),
            paths,
            dialogs ?? new RecordingDialogService(),
            settingsPage,
            adminSession ?? new StubAdminSessionService(), preparation: preparation,
            coreManagement: coreManagement, corePage: corePage, coreUpdateCoordinator: coreUpdate,
            diagnostics: diagnostics);
    }

    /// <summary>写出一个"已安装本地 PR 组合"的核心目录：worker.js 让它可运行，
    /// 来源 manifest 决定已合并的 PR 列表。</summary>
    private static void WriteMergedStack(AppPaths paths, string variant, IReadOnlyList<int> numbers)
    {
        var directory = Path.Combine(paths.NodeProjectDirectory, $"danmu_api_{variant}");
        var sources = numbers
            .Select(number => new CorePullRequestSource(
                number, "contributor/danmu_api", "feature",
                "cccccccccccccccccccccccccccccccccccccccc", null))
            .ToArray();
        CoreManifestStore.Write(directory, new CoreInstallationManifest(
            CoreInstallationManifest.CurrentSchemaVersion,
            ManagedCoreVariantExtensions.ParseManagedVariant(variant),
            "huangxd-/danmu_api",
            "main",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "1.21.0",
            "官方核心",
            CoreInstallKind.LocalPullRequestStack,
            null,
            DateTimeOffset.UtcNow)
        {
            BaseCommitSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            LocalMergeSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            PullRequests = sources,
        });
    }

    /// <summary>写出一个可被 CoreVersionReader 识别的核心目录：worker.js 决定"已安装"，
    /// configs/globals.js 的 VERSION 决定显示的版本号。</summary>
    private static void WriteCoreVersion(AppPaths paths, string variant, string version)
    {
        var directory = Path.Combine(paths.NodeProjectDirectory, $"danmu_api_{variant}");
        Directory.CreateDirectory(Path.Combine(directory, "configs"));
        File.WriteAllText(Path.Combine(directory, "worker.js"), "// entry" + Environment.NewLine);
        File.WriteAllText(Path.Combine(directory, "configs", "globals.js"), $"export const VERSION = '{version}';" + Environment.NewLine);
    }

    private sealed class StubCoreManagementService : ICoreManagementService
    {
        public event EventHandler<CoreInstallationChangedEventArgs>? InstallationChanged;

        public void Announce(ManagedCoreVariant variant) =>
            InstallationChanged?.Invoke(this, new CoreInstallationChangedEventArgs(variant));

        public CoreInstallationInfo Inspect(ManagedCoreVariant variant) => throw new NotSupportedException();
        public IReadOnlyList<CoreVersionRecord> GetHistory(ManagedCoreVariant variant) => throw new NotSupportedException();
        public Task<GithubRepositoryReference> ResolveRepositoryAsync(GithubRepositoryReference repository, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoreManagementOperationResult> InstallBranchAsync(ManagedCoreVariant variant, GithubRepositoryReference repository, string displayName, string proxyId, IProgress<CoreInstallProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoreManagementOperationResult> InstallCommitAsync(ManagedCoreVariant variant, GithubRepositoryReference repository, string branch, string commitSha, string displayName, string proxyId, IProgress<CoreInstallProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoreManagementOperationResult> ApplyUpdateAsync(CoreUpdateCheckResult update, string proxyId, IProgress<CoreInstallProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoreManagementOperationResult> ReinstallAsync(ManagedCoreVariant variant, string proxyId, IProgress<CoreInstallProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoreManagementOperationResult> RollbackAsync(ManagedCoreVariant variant, string historyId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoreManagementOperationResult> RenameAsync(ManagedCoreVariant variant, string displayName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoreManagementOperationResult> DeleteAsync(ManagedCoreVariant variant, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static AppPaths CreateRuntime(string pathsRoot, string token)
    {
        var paths = new AppPaths(pathsRoot, Path.Combine(pathsRoot, "appdata"));
        var configDirectory = Path.Combine(paths.NodeProjectDirectory, "config");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(
            Path.Combine(configDirectory, ".env"),
            $"DANMU_API_PORT=9321{Environment.NewLine}DANMU_API_HOST=127.0.0.1{Environment.NewLine}DANMU_API_VARIANT=stable{Environment.NewLine}TOKEN={token}{Environment.NewLine}");
        return paths;
    }

    private sealed class RecordingSettingsStore : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public int WriteCalls { get; private set; }
        public IReadOnlyDictionary<string, string> Read() => Values;

        public void Write(IReadOnlyDictionary<string, string?> changes)
        {
            WriteCalls++;
            foreach (var (key, value) in changes)
            {
                if (value is null)
                {
                    Values.Remove(key);
                }
                else
                {
                    Values[key] = value;
                }
            }
        }
    }

    private sealed class RecordingRuntimeController(RuntimeSnapshot snapshot) : IRuntimeController
    {
        public Task RefreshCoreSetupRequiredAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        event EventHandler<RuntimeSnapshot>? IRuntimeController.SnapshotChanged
        {
            add { }
            remove { }
        }

        public RuntimeSnapshot Snapshot { get; private set; } = snapshot;
        public int RestartCalls { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AdoptionResult> AdoptAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(AdoptionResult.Failure(
                AdoptionFailureKind.NotFound,
                Snapshot,
                "not used"));
        public string? ReconcileLiveness() => null;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RestartAsync(CancellationToken cancellationToken = default)
        {
            RestartCalls++;
            return Task.CompletedTask;
        }
        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingHealthClient : IRuntimeHealthClient
    {
        public Task<RuntimeHealthSnapshot> ReadAsync(string host, int port, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("health should not be called while stopped");
    }

    private sealed class RecordingCoreCacheClient : ICoreCacheClient
    {
        public int Calls { get; private set; }
        public string? LastAdminToken { get; private set; }

        public Task<CoreCacheClearResult> ClearAsync(
            string host,
            int port,
            string? token,
            string? adminToken,
            IEnumerable<string> items,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastAdminToken = adminToken;
            return Task.FromResult(CoreCacheClearResult.Success("cleared"));
        }
    }

    private sealed class RecordingDialogService : IUiDialogService
    {
        public bool AdminPromptResult { get; set; }
        public List<string> AdminPrompts { get; } = [];
        public List<(string Title, string Message, bool IsError)> Messages { get; } = [];
        public List<QuickUpdateDialogViewModel> QuickUpdates { get; } = [];

        public Task ShowQuickUpdateAsync(QuickUpdateDialogViewModel model)
        {
            QuickUpdates.Add(model);
            return Task.CompletedTask;
        }
        public Task EditPortAsync(MainWindowViewModel viewModel) => Task.CompletedTask;
        public Task EditTokenAsync(MainWindowViewModel viewModel) => Task.CompletedTask;
        public Task ShowCacheAsync(MainWindowViewModel viewModel) => Task.CompletedTask;
        public Task<CloseActionDecision?> AskCloseActionAsync() => Task.FromResult<CloseActionDecision?>(null);
        public Task<string?> ChooseGithubRouteAsync(string reason, string selectedProxyId, IGithubProxySpeedTester speedTester, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<string?> ChooseRuntimeRootAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task OpenDirectoryAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public string? CopiedText { get; private set; }
        public Task CopyTextAsync(string text)
        {
            CopiedText = text;
            return Task.CompletedTask;
        }
        public Task<bool> ConfirmAdminModeRequiredAsync(string message)
        {
            AdminPrompts.Add(message);
            return Task.FromResult(AdminPromptResult);
        }

        public Task ShowMessageAsync(string title, string message, bool isError = false)
        {
            Messages.Add((title, message, isError));
            return Task.CompletedTask;
        }

        public Task<GithubTokenDialogResult> PromptGithubTokenAsync(bool configured, string hint) =>
            Task.FromResult(GithubTokenDialogResult.Cancel());
    }

    private sealed class RecordingAutostartService : IAutostartService
    {
        public AutostartStatus GetStatus() => new(false, false, "test");
        public Task<AutostartOperationResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
            Task.FromResult(AutostartOperationResult.Failure(
                new AutostartStatus(false, false, "test"),
                "not used"));
        public AutostartOperationResult RefreshIfEnabled() =>
            AutostartOperationResult.Failure(new AutostartStatus(false, false, "test"), "not used");
    }

    private sealed class RecordingNotificationService : IDesktopNotificationService
    {
        public Task<DesktopNotificationResult> ShowAsync(
            string title,
            string message,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DesktopNotificationResult(true, "test"));
    }
}
