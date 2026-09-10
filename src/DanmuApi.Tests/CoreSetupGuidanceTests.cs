using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
using DanmuApi.Core;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class CoreSetupGuidanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"danmu-core-setup-{Guid.NewGuid():N}");

    [Fact]
    public async Task StartingWithoutCoreAsksBeforeNavigatingToCorePage()
    {
        await using var viewModel = CreateViewModel(accept: true, out var dialogs);

        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.Equal(DesktopRuntimeState.CoreSetupRequired, viewModel.Runtime.State);
        Assert.Contains("缺核心引导", dialogs.Confirmations, StringComparer.Ordinal);
        Assert.IsType<CorePageViewModel>(viewModel.CurrentPage);
        Assert.Contains("请在核心页完成安装", viewModel.DiagnosticText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecliningCoreSetupKeepsServiceStoppedAndStaysOnOverview()
    {
        await using var viewModel = CreateViewModel(accept: false, out _);

        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.IsNotType<CorePageViewModel>(viewModel.CurrentPage);
        Assert.Contains("服务未启动", viewModel.DiagnosticText, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private MainWindowViewModel CreateViewModel(bool accept, out RecordingDialogService dialogs)
    {
        dialogs = new RecordingDialogService { CoreSetupAccepted = accept };
        var paths = CreateRuntime();
        var settings = new RecordingSettingsStore();
        var corePage = new CorePageViewModel(
            new StubManagementService(),
            new StubRemote(),
            new StubRoutePreferenceStore(),
            new StubSpeedTester(),
            new StubScheduler(),
            new RecordingDialogService(),
            new StubDiagnostics(),
            new StubGithubTokenStore());
        var settingsPage = new SettingsPageViewModel(
            settings,
            new StubAutostartService(),
            new StubNotificationService(),
            paths);
        var controller = new StubRuntimeController(
            new RuntimeSnapshot(DesktopRuntimeState.Stopped, 9321));
        return new MainWindowViewModel(
            controller,
            new StubHealthClient(),
            settings,
            new StubCacheClient(),
            paths,
            dialogs,
            settingsPage,
            new StubAdminSessionService(),
            corePage);
    }

    private AppPaths CreateRuntime()
    {
        var paths = new AppPaths(_root, Path.Combine(_root, "appdata"));
        var configDirectory = Path.Combine(paths.NodeProjectDirectory, "config");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(
            Path.Combine(configDirectory, ".env"),
            $"DANMU_API_PORT=9321{Environment.NewLine}DANMU_API_HOST=0.0.0.0{Environment.NewLine}DANMU_API_VARIANT=stable{Environment.NewLine}TOKEN=token{Environment.NewLine}");
        return paths;
    }

    private sealed class StubRuntimeController(RuntimeSnapshot initial) : IRuntimeController
    {
        public RuntimeSnapshot Snapshot { get; private set; } = initial;
        public event EventHandler<RuntimeSnapshot>? SnapshotChanged;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Snapshot = new RuntimeSnapshot(
                DesktopRuntimeState.CoreSetupRequired,
                FailureReason: "核心尚未安装");
            SnapshotChanged?.Invoke(this, Snapshot);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AdoptionResult> AdoptAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(AdoptionResult.Failure(AdoptionFailureKind.NotFound, Snapshot, "unused"));
        public string? ReconcileLiveness() => null;
        public Task RestartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingSettingsStore : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, string> Read() => Values;
        public void Write(IReadOnlyDictionary<string, string?> changes)
        {
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

    private sealed class StubHealthClient : IRuntimeHealthClient
    {
        public Task<RuntimeHealthSnapshot> ReadAsync(string host, int port, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("health must not be called while stopped");
    }

    private sealed class StubCacheClient : ICoreCacheClient
    {
        public Task<CoreCacheClearResult> ClearAsync(
            string host,
            int port,
            string? token,
            string? adminToken,
            IEnumerable<string> items,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CoreCacheClearResult.Failure("unused"));
    }

    private sealed class StubAutostartService : IAutostartService
    {
        public AutostartStatus GetStatus() => new(false, false, "test");
        public Task<AutostartOperationResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
            Task.FromResult(AutostartOperationResult.Failure(new AutostartStatus(false, false, "test"), "unused"));
        public AutostartOperationResult RefreshIfEnabled() =>
            AutostartOperationResult.Failure(new AutostartStatus(false, false, "test"), "unused");
    }

    private sealed class StubNotificationService : IDesktopNotificationService
    {
        public Task<DesktopNotificationResult> ShowAsync(string title, string message, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DesktopNotificationResult(true, "test"));
    }

    private sealed class StubManagementService : ICoreManagementService
    {
        public CoreInstallationInfo Inspect(ManagedCoreVariant variant) =>
            new(variant, "dir", false, false, null, null, "核心尚未安装");
        public IReadOnlyList<CoreVersionRecord> GetHistory(ManagedCoreVariant variant) => [];
        public Task<GithubRepositoryReference> ResolveRepositoryAsync(GithubRepositoryReference repository, CancellationToken cancellationToken = default) =>
            Task.FromResult(repository);
        public Task<CoreManagementOperationResult> InstallBranchAsync(ManagedCoreVariant variant, GithubRepositoryReference repository, string displayName, string proxyId, IProgress<CoreInstallProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<CoreManagementOperationResult> InstallCommitAsync(ManagedCoreVariant variant, GithubRepositoryReference repository, string branch, string commitSha, string displayName, string proxyId, IProgress<CoreInstallProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<CoreManagementOperationResult> InstallPullRequestAsync(GithubRepositoryReference baseRepository, int pullRequestNumber, string displayName, string proxyId, IProgress<CoreInstallProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<CoreManagementOperationResult> ApplyUpdateAsync(CoreUpdateCheckResult update, string proxyId, IProgress<CoreInstallProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<CoreManagementOperationResult> ReinstallAsync(ManagedCoreVariant variant, string proxyId, IProgress<CoreInstallProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<CoreManagementOperationResult> RollbackAsync(ManagedCoreVariant variant, string historyId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<CoreManagementOperationResult> RenameAsync(ManagedCoreVariant variant, string displayName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<CoreManagementOperationResult> DeleteAsync(ManagedCoreVariant variant, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubRemote : IGithubCoreRemote
    {
        public GithubRateLimit? LastRateLimit => null;
        public Task<GithubRepositoryMetadata> GetRepositoryAsync(GithubRepositoryReference repository, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GithubRepositoryMetadata(repository.FullName, "main", null, false));
        public Task<IReadOnlyList<GithubBranch>> GetAllBranchesAsync(GithubRepositoryReference repository, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GithubBranch>>([]);
        public Task<GithubCommit> GetCommitAsync(GithubRepositoryReference repository, string reference, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GithubCommit("aaaaaaa", "t", "t", null, DateTimeOffset.UtcNow, []));
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
        public Task<GithubRateLimit> GetRateLimitAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<string?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubRoutePreferenceStore : IGithubRoutePreferenceStore
    {
        public GithubRoutePreference Read() => new(GithubProxyCatalog.OriginalId, true);
        public void Confirm(string proxyId) { }
        public void Invalidate() { }
    }

    private sealed class StubSpeedTester : IGithubProxySpeedTester
    {
        public Task<IReadOnlyList<GithubProxyLatencyResult>> TestAllAsync(
            IProgress<GithubProxyLatencyResult>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GithubProxyLatencyResult>>([]);
    }

#pragma warning disable CS0067
    private sealed class StubScheduler : ICoreUpdateScheduler
    {
        public event EventHandler<CoreUpdateCheckResult>? CheckCompleted;
        public event EventHandler<string>? DiagnosticChanged;
        public void Start() { }
        public void SetBackgroundActive(bool active) { }
        public void NotifyPolicyChanged() { }
        public Task<CoreUpdateCheckResult> CheckForegroundAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CoreUpdateCheckResult(ManagedCoreVariant.Stable, CoreUpdateCheckStatus.NotInstalled, false, null, null, null, DateTimeOffset.UtcNow, "核心尚未安装"));
        public Task<CoreUpdateCheckResult?> CheckBackgroundAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<CoreUpdateCheckResult?>(null);
        public Task<CoreUpdateCheckResult> CheckManualAsync(ManagedCoreVariant variant, CancellationToken cancellationToken = default) =>
            CheckForegroundAsync(cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
#pragma warning restore CS0067

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
