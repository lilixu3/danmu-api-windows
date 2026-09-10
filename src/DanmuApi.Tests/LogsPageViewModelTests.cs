using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
using DanmuApi.Core;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class LogsPageViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"danmu-logs-{Guid.NewGuid():N}");

    [Fact]
    public async Task SearchNarrowsCopyScopeToFilteredRowsOnly()
    {
        var paths = CreateRuntime();
        await File.WriteAllTextAsync(
            Path.Combine(paths.NodeProjectDirectory, "logs", "node-stdout.log"),
            "[2026-09-01T10:00:00.000+08:00] info: [bilibili] 搜索命中 12 条\n" +
            "[2026-09-01T10:00:01.000+08:00] error: [tencent] 请求失败\n" +
            "[2026-09-01T10:00:02.000+08:00] info: [bilibili] 搜索命中 3 条\n");
        var dialogs = new RecordingDialogService();
        await using var viewModel = CreateFileViewModel(paths, dialogs);

        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(3, viewModel.FilteredEntries.Count);
        Assert.Contains("复制全部", viewModel.CopyButtonText, StringComparison.Ordinal);

        viewModel.SearchText = "bilibili";
        await viewModel.CopyCommand.ExecuteAsync(null);

        Assert.Equal(2, viewModel.FilteredEntries.Count);
        Assert.Contains("复制筛选结果 (2)", viewModel.CopyButtonText, StringComparison.Ordinal);
        Assert.Contains("匹配 2 / 3 行", viewModel.MatchSummaryText, StringComparison.Ordinal);
        var copied = dialogs.CopiedText ?? string.Empty;
        Assert.Equal(2, copied.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain("tencent", copied, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportUsesFilteredRowsAndSuggestedFileName()
    {
        var paths = CreateRuntime();
        await File.WriteAllTextAsync(
            Path.Combine(paths.NodeProjectDirectory, "logs", "node-stdout.log"),
            "[2026-09-01T10:00:00.000+08:00] info: [system] 启动完成\n" +
            "[2026-09-01T10:00:01.000+08:00] error: [system] 端口占用\n");
        var dialogs = new RecordingDialogService();
        await using var viewModel = CreateFileViewModel(paths, dialogs);

        await viewModel.RefreshCommand.ExecuteAsync(null);
        viewModel.SelectedLevel = LogLevel.Error;
        await viewModel.ExportCommand.ExecuteAsync(null);

        Assert.Contains("导出筛选结果 (1)", viewModel.ExportButtonText, StringComparison.Ordinal);
        var saved = dialogs.SavedFiles.Single();
        Assert.Contains("端口占用", saved.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("启动完成", saved.Content, StringComparison.Ordinal);
        Assert.Contains("node-stdout", saved.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LevelAndCategoryFiltersCombine()
    {
        var paths = CreateRuntime();
        await File.WriteAllTextAsync(
            Path.Combine(paths.NodeProjectDirectory, "logs", "node-stdout.log"),
            "[2026-09-01T10:00:00.000+08:00] info: [bilibili] 搜索 a\n" +
            "[2026-09-01T10:00:01.000+08:00] error: [bilibili] 搜索失败\n" +
            "[2026-09-01T10:00:02.000+08:00] info: [tencent] 搜索 b\n");
        await using var viewModel = CreateFileViewModel(paths, new RecordingDialogService());

        await viewModel.RefreshCommand.ExecuteAsync(null);
        viewModel.SelectCategoryCommand.Execute("bilibili");

        Assert.Equal(2, viewModel.FilteredEntries.Count);
        Assert.Equal(1, viewModel.LevelOptions.First(option => option.Value == LogLevel.Error).Count);

        viewModel.SelectLevelCommand.Execute(LogLevel.Info);

        Assert.Single(viewModel.FilteredEntries);
        Assert.True(viewModel.IsFilterActive);
        Assert.Contains("搜索 a", viewModel.FilteredEntries[0].Message, StringComparison.Ordinal);

        viewModel.ClearFiltersCommand.Execute(null);

        Assert.Equal(3, viewModel.FilteredEntries.Count);
        Assert.False(viewModel.IsFilterActive);
    }

    [Fact]
    public async Task MissingLogFileShowsDiagnosticInsteadOfSilentEmptyList()
    {
        var paths = CreateRuntime();
        await using var viewModel = CreateFileViewModel(paths, new RecordingDialogService());

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.FilteredEntries);
        Assert.True(viewModel.HasDiagnostic);
        Assert.Contains("不存在", viewModel.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("还没有可显示", viewModel.EmptyStateText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContinuationLinesInheritPreviousCategoryAndLevel()
    {
        var paths = CreateRuntime();
        await File.WriteAllTextAsync(
            Path.Combine(paths.NodeProjectDirectory, "logs", "node-stdout.log"),
            "[2026-09-01T10:00:00.000+08:00] error: [bilibili] 请求异常\n" +
            "    at fetch (worker.js:42:7)\n");
        await using var viewModel = CreateFileViewModel(paths, new RecordingDialogService());

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, viewModel.FilteredEntries.Count);
        var continuation = viewModel.FilteredEntries[1];
        Assert.True(continuation.IsContinuation);
        Assert.Equal(LogLevel.Error, continuation.Level);
        Assert.Equal("bilibili", continuation.Category);
    }

    [Fact]
    public async Task SwitchingSourceClearsRowsForTheNewFile()
    {
        var paths = CreateRuntime();
        await File.WriteAllTextAsync(
            Path.Combine(paths.NodeProjectDirectory, "logs", "node-stdout.log"),
            "[2026-09-01T10:00:00.000+08:00] info: [system] 核心输出行\n");
        await File.WriteAllTextAsync(
            Path.Combine(paths.NodeProjectDirectory, "logs", "node-stderr.log"),
            "[2026-09-01T10:00:01.000+08:00] error: [system] 核心错误行\n");
        await using var viewModel = CreateFileViewModel(paths, new RecordingDialogService());

        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.Single(viewModel.FilteredEntries);

        viewModel.SelectedSource = viewModel.Sources.First(source => source.Value == LogSourceKind.CoreError);
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(LogSourceKind.CoreError, viewModel.SelectedSource.Value);
        Assert.Single(viewModel.FilteredEntries);
        Assert.Contains("核心错误行", viewModel.FilteredEntries[0].Message, StringComparison.Ordinal);
        Assert.DoesNotContain("核心输出行", viewModel.FilteredEntries[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CoreApiSnapshotAppendsOnlyNewTailLines()
    {
        var paths = CreateRuntime("custom-access-token");
        var client = new RecordingCoreLogClient(
            ["[t1] info: [system] first", "[t2] info: [system] second"],
            ["[t2] info: [system] second", "[t3] warn: [system] third"]);
        await using var viewModel = CreateViewModel(paths, new RecordingDialogService(), client);

        await viewModel.RefreshCommand.ExecuteAsync(null);
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(3, viewModel.FilteredEntries.Count);
        Assert.Equal("first", viewModel.FilteredEntries[0].Message.Split(' ').Last());
        Assert.Equal("third", viewModel.FilteredEntries[^1].Message.Split(' ').Last());
        Assert.All(client.Tokens, token => Assert.Equal("custom-access-token", token));
    }

    [Fact]
    public async Task UserAwayFromLatestGetsPendingCountUntilExplicitJump()
    {
        var paths = CreateRuntime();
        var client = new RecordingCoreLogClient(
            ["[t1] info: first"],
            ["[t1] info: first", "[t2] info: second", "[t3] info: third"]);
        await using var viewModel = CreateViewModel(paths, new RecordingDialogService(), client);
        var scrollRequests = 0;
        viewModel.ScrollToEndRequested += (_, _) => scrollRequests++;

        await viewModel.RefreshCommand.ExecuteAsync(null);
        viewModel.NotifyViewportAtLatest(false);
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, viewModel.PendingNewCount);
        Assert.True(viewModel.HasPendingNewLogs);
        var requestsBeforeJump = scrollRequests;

        viewModel.ViewLatestCommand.Execute(null);

        Assert.Equal(0, viewModel.PendingNewCount);
        Assert.Equal(requestsBeforeJump + 1, scrollRequests);
    }

    [Fact]
    public async Task StoppedServiceDoesNotCallCoreApiOrRecordPollingFailure()
    {
        var paths = CreateRuntime();
        var client = new RecordingCoreLogClient(["unused"]);
        var diagnostics = new StubDiagnostics();
        await using var viewModel = CreateViewModel(
            paths,
            new RecordingDialogService(),
            client,
            new StubRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped)),
            diagnostics);

        await viewModel.RefreshCommand.ExecuteAsync(null);
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(0, client.Calls);
        Assert.Contains("服务未运行", viewModel.Diagnostic, StringComparison.Ordinal);
        Assert.Null(diagnostics.LastDiagnostic);
    }

    [Fact]
    public async Task UnexpectedPollExceptionRecordsStackAndNextPollRecovers()
    {
        var paths = CreateRuntime();
        var client = new FlakyCoreLogClient();
        var diagnostics = new RecordingDiagnostics();
        await using var viewModel = CreateViewModel(paths, new RecordingDialogService(), client, null, diagnostics);

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Contains("日志轮询失败", viewModel.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("injected poll failure", viewModel.Diagnostic, StringComparison.Ordinal);
        Assert.Contains(
            diagnostics.Records,
            record => record.Contains("InvalidOperationException", StringComparison.Ordinal) &&
                      record.Contains("堆栈", StringComparison.Ordinal));

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Single(viewModel.FilteredEntries);
        Assert.Equal(string.Empty, viewModel.Diagnostic);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task RepeatedIdenticalPollFailuresRecordStackTraceOnlyOnce()
    {
        var paths = CreateRuntime();
        var client = new AlwaysFailingCoreLogClient();
        var diagnostics = new RecordingDiagnostics();
        await using var viewModel = CreateViewModel(paths, new RecordingDialogService(), client, null, diagnostics);

        await viewModel.RefreshCommand.ExecuteAsync(null);
        await viewModel.RefreshCommand.ExecuteAsync(null);
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(3, client.Calls);
        Assert.Single(diagnostics.Records, record => record.Contains("堆栈", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AppPaths CreateRuntime(string token = RuntimeDefaults.FallbackToken)
    {
        var paths = new AppPaths(_root, Path.Combine(_root, "appdata"));
        Directory.CreateDirectory(Path.Combine(paths.NodeProjectDirectory, "logs"));
        Directory.CreateDirectory(Path.Combine(paths.NodeProjectDirectory, "config"));
        File.WriteAllText(
            Path.Combine(paths.NodeProjectDirectory, "config", ".env"),
            $"TOKEN={token}{Environment.NewLine}");
        return paths;
    }

    private static LogsPageViewModel CreateFileViewModel(AppPaths paths, RecordingDialogService dialogs)
    {
        var viewModel = CreateViewModel(paths, dialogs);
        viewModel.SelectedSource = viewModel.Sources.First(source => source.Value == LogSourceKind.CoreOutput);
        return viewModel;
    }

    private static LogsPageViewModel CreateViewModel(
        AppPaths paths,
        RecordingDialogService dialogs,
        ICoreLogClient? coreLogClient = null,
        IRuntimeController? runtimeController = null,
        IAppDiagnostics? diagnostics = null) =>
        new(
            paths,
            new LogFileTailReader(),
            coreLogClient ?? new RecordingCoreLogClient([]),
            runtimeController ?? new StubRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 42)),
            dialogs,
            diagnostics ?? new StubDiagnostics());

    private sealed class RecordingCoreLogClient(params IReadOnlyList<string>[] snapshots) : ICoreLogClient
    {
        private int _index;
        public int Calls { get; private set; }
        public List<string?> Tokens { get; } = [];

        public Task<CoreLogReadResult> ReadAsync(
            string host,
            int port,
            string? token,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Tokens.Add(token);
            var snapshot = snapshots.Length == 0
                ? Array.Empty<string>()
                : snapshots[Math.Min(_index++, snapshots.Length - 1)];
            return Task.FromResult(CoreLogReadResult.Success(snapshot));
        }
    }

    private sealed class StubRuntimeController(RuntimeSnapshot snapshot) : IRuntimeController
    {
        public RuntimeSnapshot Snapshot { get; private set; } = snapshot;
        event EventHandler<RuntimeSnapshot>? IRuntimeController.SnapshotChanged
        {
            add { }
            remove { }
        }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AdoptionResult> AdoptAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(AdoptionResult.Failure(AdoptionFailureKind.NotFound, Snapshot, "unused"));
        public string? ReconcileLiveness() => null;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RestartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FlakyCoreLogClient : ICoreLogClient
    {
        private bool _thrown;
        public int Calls { get; private set; }

        public Task<CoreLogReadResult> ReadAsync(
            string host,
            int port,
            string? token,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (!_thrown)
            {
                _thrown = true;
                throw new InvalidOperationException("injected poll failure");
            }

            return Task.FromResult(CoreLogReadResult.Success(["[t1] info: [system] recovered"]));
        }
    }

    private sealed class AlwaysFailingCoreLogClient : ICoreLogClient
    {
        public int Calls { get; private set; }

        public Task<CoreLogReadResult> ReadAsync(
            string host,
            int port,
            string? token,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("persistent poll failure");
        }
    }

    private sealed class RecordingDiagnostics : IAppDiagnostics
    {
        public List<string> Records { get; } = [];
        public string? LastDiagnostic => Records.Count > 0 ? Records[^1] : null;
        public void Record(string message, Exception? error = null) =>
            Records.Add(error is null ? message : $"{message}: {error.Message}");
    }

    private sealed class StubDiagnostics : IAppDiagnostics
    {
        public string? LastDiagnostic { get; private set; }
        public void Record(string message, Exception? error = null) =>
            LastDiagnostic = error is null ? message : $"{message}: {error.Message}";
    }
}
