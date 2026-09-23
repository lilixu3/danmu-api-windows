using DanmuApi.App.Services;
using DanmuApi.Core;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class RuntimeVariantSwitchServiceTests
{
    [Fact]
    public async Task SwitchWhileRunningStopsRestartsAndWritesTheSelection()
    {
        var store = new RecordingVariantStore(ManagedCoreVariant.Stable);
        var runtime = new RecordingRuntime(new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 42));
        var service = new RuntimeVariantSwitchService(store, runtime);

        var result = await service.SwitchAsync(ManagedCoreVariant.Custom);

        Assert.True(result.Succeeded);
        Assert.True(result.ServiceRunning);
        Assert.Equal([ManagedCoreVariant.Custom], store.Writes);
        Assert.Equal(["stop", "start"], runtime.Calls);
    }

    [Fact]
    public async Task SwitchWhileStoppedOnlyWritesTheSelection()
    {
        var store = new RecordingVariantStore(ManagedCoreVariant.Stable);
        var runtime = new RecordingRuntime(new RuntimeSnapshot(DesktopRuntimeState.Stopped));
        var service = new RuntimeVariantSwitchService(store, runtime);

        var result = await service.SwitchAsync(ManagedCoreVariant.Custom);

        Assert.True(result.Succeeded);
        Assert.False(result.ServiceRunning);
        Assert.Equal([ManagedCoreVariant.Custom], store.Writes);
        // 服务本来没运行就不该擅自启动：切换只落盘，启动是用户的动作。
        Assert.Empty(runtime.Calls);
        Assert.Contains("服务当前未运行", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AlreadyOnTargetVariantDoesNothing()
    {
        var store = new RecordingVariantStore(ManagedCoreVariant.Custom);
        var runtime = new RecordingRuntime(new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 42));
        var service = new RuntimeVariantSwitchService(store, runtime);

        var result = await service.SwitchAsync(ManagedCoreVariant.Custom);

        Assert.True(result.Succeeded);
        Assert.Empty(store.Writes);
        Assert.Empty(runtime.Calls);
    }

    [Fact]
    public async Task FailedStartRevertsTheSelectionAndRestartsTheOldCore()
    {
        var store = new RecordingVariantStore(ManagedCoreVariant.Stable);
        var runtime = new RecordingRuntime(new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 42))
        {
            // 第一次 start（切到 Custom）失败，第二次 start（恢复 Stable）成功。
            StartOutcomes =
            [
                new RuntimeSnapshot(DesktopRuntimeState.Failed, FailureReason: "核心入口缺失"),
                new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 77),
            ],
        };
        var service = new RuntimeVariantSwitchService(store, runtime);

        var result = await service.SwitchAsync(ManagedCoreVariant.Custom);

        Assert.False(result.Succeeded);
        Assert.True(result.RestoredPreviousSelection);
        Assert.Equal([ManagedCoreVariant.Custom, ManagedCoreVariant.Stable], store.Writes);
        Assert.Equal(["stop", "start", "start"], runtime.Calls);
        Assert.Contains("核心入口缺失", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("已恢复原来的核心选择", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedRevertReportsBothFailures()
    {
        var store = new RecordingVariantStore(ManagedCoreVariant.Stable);
        var runtime = new RecordingRuntime(new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 42))
        {
            StartOutcomes =
            [
                new RuntimeSnapshot(DesktopRuntimeState.Failed, FailureReason: "候选核心起不来"),
                new RuntimeSnapshot(DesktopRuntimeState.Failed, FailureReason: "旧核心也起不来"),
            ],
        };
        var service = new RuntimeVariantSwitchService(store, runtime);

        var result = await service.SwitchAsync(ManagedCoreVariant.Custom);

        Assert.False(result.Succeeded);
        Assert.False(result.RestoredPreviousSelection);
        Assert.False(result.ServiceRunning);
        Assert.Contains("候选核心起不来", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("恢复原核心选择也失败", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("旧核心也起不来", result.Diagnostic, StringComparison.Ordinal);
    }

    private sealed class RecordingVariantStore(ManagedCoreVariant? initial) : IActiveCoreVariantStore
    {
        public List<ManagedCoreVariant> Writes { get; } = [];

        public ManagedCoreVariant? Read() => Writes.Count > 0 ? Writes[^1] : initial;

        public void Write(ManagedCoreVariant variant) => Writes.Add(variant);
    }

    private sealed class RecordingRuntime(RuntimeSnapshot initial) : IRuntimeController
    {
        public List<string> Calls { get; } = [];
        public RuntimeSnapshot Snapshot { get; private set; } = initial;
        public IReadOnlyList<RuntimeSnapshot> StartOutcomes { get; init; } = [];
        private int _starts;

        public event EventHandler<RuntimeSnapshot>? SnapshotChanged;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("start");
            var next = StartOutcomes.Count == 0
                ? new RuntimeSnapshot(DesktopRuntimeState.Running, 9321, 55)
                : StartOutcomes[Math.Min(_starts, StartOutcomes.Count - 1)];
            _starts++;
            Snapshot = next;
            SnapshotChanged?.Invoke(this, Snapshot);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("stop");
            Snapshot = new RuntimeSnapshot(DesktopRuntimeState.Stopped);
            SnapshotChanged?.Invoke(this, Snapshot);
            return Task.CompletedTask;
        }

        public Task RestartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ShutdownAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RefreshCoreSetupRequiredAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AdoptionResult> AdoptAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(AdoptionResult.Failure(AdoptionFailureKind.NotFound, Snapshot, "unused"));
        public string? ReconcileLiveness() => null;
    }
}

public sealed class SettingsActiveCoreVariantStoreTests
{
    [Fact]
    public void MissingOverrideMeansTheCoreEnvDecides()
    {
        var store = new SettingsActiveCoreVariantStore(new MemorySettingsStore());

        Assert.Null(store.Read());
    }

    [Fact]
    public void WrittenVariantRoundTrips()
    {
        var settings = new MemorySettingsStore();
        var store = new SettingsActiveCoreVariantStore(settings);

        store.Write(ManagedCoreVariant.Dev);

        Assert.Equal(ManagedCoreVariant.Dev, store.Read());
        Assert.Equal("dev", settings.Read()["variant_override"]);
    }

    [Fact]
    public void UnreadableOverrideIsReportedInsteadOfSilentlyIgnored()
    {
        var settings = new MemorySettingsStore();
        settings.Write(new Dictionary<string, string?> { ["variant_override"] = "beta" });
        var store = new SettingsActiveCoreVariantStore(settings);

        var error = Assert.Throws<IOException>(() => store.Read());

        Assert.Contains("variant_override", error.Message, StringComparison.Ordinal);
    }

    private sealed class MemorySettingsStore : ISettingsStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, string> Read() => new Dictionary<string, string>(_values, StringComparer.Ordinal);

        public void Write(IReadOnlyDictionary<string, string?> changes)
        {
            foreach (var (key, value) in changes)
            {
                if (value is null) _values.Remove(key);
                else _values[key] = value;
            }
        }
    }
}
