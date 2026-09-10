using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
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

    private static MainWindowViewModel CreateViewModel(
        AppPaths paths,
        RecordingSettingsStore settings,
        RecordingRuntimeController controller,
        StubAdminSessionService? adminSession = null,
        RecordingDialogService? dialogs = null,
        RecordingCoreCacheClient? cacheClient = null,
        IRuntimeHealthClient? healthClient = null,
        RuntimePreparationService? preparation = null)
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
            adminSession ?? new StubAdminSessionService(), preparation: preparation);
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
