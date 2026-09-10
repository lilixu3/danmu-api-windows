using DanmuApi.App.ViewModels;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed partial class MainWindowViewModelBehaviorTests
{
    [Fact]
    public async Task RunningDiagnosticsDescribePendingHealthInsteadOfStoppedPlaceholder()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(directory.Path, "test-token");
        await using var model = CreateViewModel(paths, new RecordingSettingsStore(),
            new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Running, Port: 9321)));

        Assert.Contains("等待首次健康检查", model.DiagnosticText, StringComparison.Ordinal);
        Assert.DoesNotContain("启动服务后", model.DiagnosticText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthFailureBreaksTrendAndStopClearsItsHistory()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(directory.Path, "test-token");
        var client = new OverviewSequenceHealthClient();
        await using var model = CreateViewModel(paths, new RecordingSettingsStore(),
            new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped)), healthClient: client);
        model.Runtime = new RuntimeSnapshot(DesktopRuntimeState.Running, Port: 9321, Pid: 42);
        Assert.Single(model.RequestTrend.Points);
        Assert.Null(model.RequestTrend.CurrentRate);
        Assert.Equal("10", model.RequestCountText);
        client.Fail = true;
        model.Runtime = new RuntimeSnapshot(DesktopRuntimeState.Running, Port: 9321, Pid: 43);
        Assert.Contains("健康检查失败", model.DiagnosticText);
        Assert.Null(model.RequestTrend.Points[^1].Rate);
        Assert.Equal("未读取", model.RequestCountText);
        client.Fail = false;
        model.Runtime = new RuntimeSnapshot(DesktopRuntimeState.Running, Port: 9321, Pid: 44);
        Assert.Null(model.RequestTrend.CurrentRate);
        Assert.Contains("建立基线", model.RequestTrend.Status);
        model.Runtime = new RuntimeSnapshot(DesktopRuntimeState.Stopped);
        Assert.Empty(model.RequestTrend.Points);
        Assert.Null(model.RequestTrend.CurrentRate);
    }

    private sealed class OverviewSequenceHealthClient : IRuntimeHealthClient
    {
        public bool Fail { get; set; }
        public Task<RuntimeHealthSnapshot> ReadAsync(string host, int port, CancellationToken cancellationToken = default) =>
            Fail ? Task.FromException<RuntimeHealthSnapshot>(new RuntimeHealthException(HealthFailureKind.Connection, "fixture unavailable"))
                : Task.FromResult(new RuntimeHealthSnapshot(42, "24", 1, host, port, null, null, null, null,
                    null, true, "stable", null, "fixture", 10, null, null, null, null, null, null, null));
    }

    [Fact]
    public async Task RuntimeFailureIsNotHiddenByAnUnrelatedActionMessage()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(directory.Path, "test-token");
        await using var model = CreateViewModel(paths, new RecordingSettingsStore(),
            new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Failed, Port: 9321, FailureReason: "进程异常退出：exit 1")));

        await model.ApplyPortAsync(9321);

        Assert.Contains("进程异常退出：exit 1", model.DiagnosticText, StringComparison.Ordinal);
        Assert.Contains("端口未修改", model.DiagnosticText, StringComparison.Ordinal);
    }
}
