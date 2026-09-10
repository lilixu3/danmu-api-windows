using DanmuApi.App.ViewModels;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed partial class MainWindowViewModelBehaviorTests
{
    [Fact]
    public async Task TodayStatsUseCoreDailyCountAndResetOnStop()
    {
        using var directory = new TemporaryDirectory();
        var client = new StatsClient((_, _) => Task.FromResult(CoreRequestRecordsReadResult.Success([], 137)));
        await using var model = CreateStatsModel(directory.Path, client);
        Assert.Equal("今日请求未读取", model.TodayRequestsText);
        Assert.Equal(0, client.Calls);
        model.Runtime = new RuntimeSnapshot(DesktopRuntimeState.Running, Port: 19321, Pid: 42);
        Assert.Equal("今日请求 137 次", model.TodayRequestsText);
        Assert.Equal("10", model.RequestCountText);
        Assert.Equal(("127.0.0.1", 19321, "stats/secret"), client.Endpoint);
        await Task.Delay(100);
        Assert.Equal(1, client.Calls);
        model.Runtime = new RuntimeSnapshot(DesktopRuntimeState.Stopped);
        Assert.Equal("今日请求未读取", model.TodayRequestsText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TodayStatsFailuresAreExplicitRedactedAndLeaveHealthIntact(bool throws)
    {
        using var directory = new TemporaryDirectory();
        var diagnostic = "HTTP 401 stats/secret stats%2Fsecret";
        var client = new StatsClient((_, _) => throws
            ? Task.FromException<CoreRequestRecordsReadResult>(new IOException(diagnostic))
            : Task.FromResult(CoreRequestRecordsReadResult.Failure(diagnostic)));
        await using var model = CreateStatsModel(directory.Path, client);
        model.Runtime = new RuntimeSnapshot(DesktopRuntimeState.Running, Port: 19321, Pid: 42);
        Assert.Contains("今日请求读取失败", model.TodayRequestsText);
        Assert.Contains("401", model.TodayRequestsText);
        Assert.DoesNotContain("secret", model.TodayRequestsText);
        Assert.Equal("10", model.RequestCountText);
        Assert.Equal(DesktopRuntimeState.Running, model.Runtime.State);
        Assert.DoesNotContain("401", model.DiagnosticText);
    }

    [Fact]
    public async Task TodayStatsOldResponseCannotOverwriteReplacementInstance()
    {
        using var directory = new TemporaryDirectory();
        var oldResponse = new TaskCompletionSource<CoreRequestRecordsReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new StatsClient((call, _) => call == 1 ? oldResponse.Task
            : Task.FromResult(CoreRequestRecordsReadResult.Success([], 22)));
        var model = CreateStatsModel(directory.Path, client);
        model.Runtime = new RuntimeSnapshot(DesktopRuntimeState.Running, Port: 19321, Pid: 42);
        Assert.Equal("10", model.RequestCountText); // Pending statistics do not hold up health.
        model.Runtime = new RuntimeSnapshot(DesktopRuntimeState.Running, Port: 19322, Pid: 43);
        Assert.Equal("今日请求 22 次", model.TodayRequestsText);
        oldResponse.SetResult(CoreRequestRecordsReadResult.Success([], 999));
        await model.DisposeAsync(); // Also drains the cancelled old session.
        Assert.Equal("今日请求 22 次", model.TodayRequestsText);
    }

    [Fact]
    public async Task TodayStatsDisposeCancelsAndAwaitsPendingRead()
    {
        using var directory = new TemporaryDirectory();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<CoreRequestRecordsReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new StatsClient(async (_, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            return await finish.Task;
        });
        var model = CreateStatsModel(directory.Path, client);
        model.Runtime = new RuntimeSnapshot(DesktopRuntimeState.Running, Port: 19321, Pid: 42);
        var disposal = model.DisposeAsync().AsTask();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(disposal.IsCompleted);
        finish.SetResult(CoreRequestRecordsReadResult.Success([], 999));
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("今日请求未读取", model.TodayRequestsText);
    }

    [Fact]
    public async Task TodayStatsPollAgainAfterThirtySecondsAndRecoverFromFailure()
    {
        using var directory = new TemporaryDirectory();
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var client = new StatsClient((call, _) => Task.FromResult(call == 1
            ? CoreRequestRecordsReadResult.Failure("HTTP 503")
            : CoreRequestRecordsReadResult.Success([], 0)));
        await using var model = CreateStatsModel(directory.Path, client);
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(model.TodayRequestsText) && model.TodayRequestsText == "今日请求 0 次")
                refreshed.TrySetResult();
        };
        model.Runtime = new RuntimeSnapshot(DesktopRuntimeState.Running, Port: 19321, Pid: 42);
        Assert.Contains("HTTP 503", model.TodayRequestsText);
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(40));
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(30));
        Assert.Equal(2, client.Calls);
        Assert.Equal("今日请求 0 次", model.TodayRequestsText);
        Assert.Equal("10", model.RequestCountText);
    }

    private static MainWindowViewModel CreateStatsModel(string root, ICoreRequestRecordsClient client)
    {
        var paths = CreateRuntime(root, "stats/secret");
        var settings = new RecordingSettingsStore();
        return new MainWindowViewModel(
            new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped)),
            new OverviewSequenceHealthClient(), settings, new RecordingCoreCacheClient(), paths,
            new RecordingDialogService(),
            new SettingsPageViewModel(settings, new RecordingAutostartService(), new RecordingNotificationService(), paths),
            new StubAdminSessionService(), requestRecordsClient: client);
    }

    private sealed class StatsClient(Func<int, CancellationToken, Task<CoreRequestRecordsReadResult>> read) : ICoreRequestRecordsClient
    {
        public int Calls { get; private set; }
        public (string Host, int Port, string? Token) Endpoint { get; private set; }
        public Task<CoreRequestRecordsReadResult> ReadAsync(string host, int port, string? token, CancellationToken cancellationToken = default)
        {
            Endpoint = (host, port, token);
            return read(++Calls, cancellationToken);
        }
    }
}
