using System.Security.Cryptography;
using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
using DanmuApi.Core;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class RuntimeMaintenanceTests
{
    [Fact]
    public void CheckMissingRootIsReadOnlyAndCountsAllFiles()
    {
        using var fixture = new Fixture();
        var result = RuntimeDependencyInspector.Check(fixture.Bundle, fixture.Paths);
        Assert.Equal(new RuntimeDependencyCheckResult(3, 3, 0), result);
        Assert.False(Directory.Exists(fixture.Paths.RuntimeDirectory));
    }

    [Fact]
    public void FullCheckDetectsSameMetadataDamageAndDoesNotWriteMarkerOrData()
    {
        using var f = new Fixture();
        BundledRuntimePreparer.Prepare(f.Bundle, f.Paths);
        var marker = Path.Combine(f.Paths.RuntimeDirectory, ".bundled-runtime.json");
        var before = File.ReadAllBytes(marker);
        var stamp = File.GetLastWriteTimeUtc(marker);
        var target = f.Target("nodejs-project/node_modules/pkg/index.js");
        var originalStamp = File.GetLastWriteTimeUtc(target);
        File.WriteAllText(target, "bad!");
        File.SetLastWriteTimeUtc(target, originalStamp);
        File.Delete(f.Target("node.exe"));
        var result = RuntimeDependencyInspector.Check(f.Bundle, f.Paths);
        Assert.Equal(new RuntimeDependencyCheckResult(3, 1, 1), result);
        Assert.Equal(before, File.ReadAllBytes(marker));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(marker));
        Assert.Equal("bad!", File.ReadAllText(target));
        Assert.False(File.Exists(f.Target("node.exe")));
    }

    [Fact]
    public async Task RepairUsesBundleAndPreservesCoreConfigurationAndLogs()
    {
        using var f = new Fixture();
        await using var controller = f.Controller();
        var scheduler = new Scheduler();
        var service = new RuntimeMaintenanceService(f.Bundle, f.Paths, controller, scheduler, new Pending());
        string[] preserved = ["nodejs-project/config/.env", "nodejs-project/danmu_api_custom/worker.js", "nodejs-project/logs/node-stdout.log"];
        foreach (var relative in preserved)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(f.Target(relative))!);
            File.WriteAllText(f.Target(relative), "keep-private-content");
        }
        var phases = new List<string>();
        await service.RepairAsync(new CallbackProgress(p => phases.Add(p.Phase)));
        Assert.True((await service.CheckAsync()).IsHealthy);
        foreach (var relative in preserved) Assert.Equal("keep-private-content", File.ReadAllText(f.Target(relative)));
        Assert.Contains("Staging", phases);
        Assert.Contains("Replacing", phases);
        Assert.Equal(1, scheduler.Paused);
        Assert.Equal(1, scheduler.Resumed);
    }

    [Fact]
    public async Task CancellationDuringReplacementRollsBackAndResumesScheduler()
    {
        using var f = new Fixture();
        BundledRuntimePreparer.Prepare(f.Bundle, f.Paths);
        File.WriteAllText(f.Target("node.exe"), "damaged-before-repair");
        await using var controller = f.Controller();
        var scheduler = new Scheduler();
        var service = new RuntimeMaintenanceService(f.Bundle, f.Paths, controller, scheduler, new Pending());
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RepairAsync(new CallbackProgress(p =>
        {
            if (p.Phase == "Replacing" && p.Completed == 1) cancel.Cancel();
        }), cancel.Token));
        Assert.Equal("damaged-before-repair", File.ReadAllText(f.Target("node.exe")));
        Assert.Equal(1, scheduler.Resumed);
        Assert.Equal(1, (await service.CheckAsync()).Damaged);
        await using var lease = await controller.AcquireStoppedMaintenanceLeaseAsync();
    }

    [Theory]
    [InlineData(DesktopRuntimeState.Running)]
    [InlineData(DesktopRuntimeState.Starting)]
    [InlineData(DesktopRuntimeState.Failed)]
    public async Task RepairRejectsNonStoppedRuntimeWithoutStoppingIt(DesktopRuntimeState state)
    {
        using var f = new Fixture();
        var supervisor = new Supervisor(state);
        await using var controller = new RuntimeController(supervisor, () => throw new NotSupportedException());
        var scheduler = new Scheduler();
        var service = new RuntimeMaintenanceService(f.Bundle, f.Paths, controller, scheduler, new Pending());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RepairAsync());
        Assert.Contains("先停止服务", error.Message);
        Assert.Equal(0, supervisor.StopCalls);
        Assert.Equal(0, scheduler.Paused);
        Assert.False(Directory.Exists(f.Paths.RuntimeDirectory));
    }

    [Fact]
    public async Task MaintenanceLeaseBlocksRuntimeStartupUntilDisposed()
    {
        using var f = new Fixture();
        await using var controller = f.Controller();
        var scheduler = new Scheduler();
        var service = new RuntimeMaintenanceService(f.Bundle, f.Paths, controller, scheduler, new Pending());
        var lease = await service.AcquireStoppedLeaseAsync();
        using var cancellation = new CancellationTokenSource();
        var start = controller.StartAsync(cancellation.Token);
        Assert.False(start.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(DesktopRuntimeState.Stopped, controller.Snapshot.State);
        Assert.Equal(0, scheduler.Resumed);
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        Assert.Equal(1, scheduler.Resumed);
        await using var next = await controller.AcquireStoppedMaintenanceLeaseAsync();
    }

    [Fact]
    public async Task PendingUpdateRejectsRepairAndResumesScheduler()
    {
        using var f = new Fixture();
        await using var controller = f.Controller();
        var scheduler = new Scheduler();
        var service = new RuntimeMaintenanceService(f.Bundle, f.Paths, controller, scheduler, new Pending { IsApplying = true });
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RepairAsync());
        Assert.Equal(1, scheduler.Resumed);
        Assert.False(Directory.Exists(f.Paths.RuntimeDirectory));
    }

    [Fact]
    public async Task DamagedBundleFailsRepairAndReleasesMaintenance()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Bundle, "node.exe"), "damaged-source");
        await using var controller = f.Controller();
        var scheduler = new Scheduler();
        var service = new RuntimeMaintenanceService(f.Bundle, f.Paths, controller, scheduler, new Pending());
        await Assert.ThrowsAsync<IOException>(() => service.RepairAsync());
        Assert.False(Directory.Exists(f.Paths.RuntimeDirectory));
        Assert.Equal(1, scheduler.Resumed);
        await using var lease = await controller.AcquireStoppedMaintenanceLeaseAsync();
    }

    [Fact]
    public void CheckCancellationAndMalformedManifestNeverCreateRuntime()
    {
        using var f = new Fixture();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => RuntimeDependencyInspector.Check(f.Bundle, f.Paths, cancellationToken: cancel.Token));
        File.AppendAllText(Path.Combine(f.Bundle, "SHA256SUMS.txt"), new string('a', 64) + "  nodejs-project/config/.env\n");
        Assert.Throws<IOException>(() => RuntimeDependencyInspector.Check(f.Bundle, f.Paths));
        Assert.False(Directory.Exists(f.Paths.RuntimeDirectory));
    }

    [Fact]
    public async Task ViewModelRequiresConfirmationAndCanCancelChecks()
    {
        var service = new VmService();
        var dialogs = new RecordingDialogService();
        var vm = new RuntimeMaintenanceViewModel(service, dialogs, new Diagnostics());
        await vm.RepairCommand.ExecuteAsync(null);
        Assert.Equal(0, service.Repairs);
        dialogs.Confirmation = true;
        await vm.RepairCommand.ExecuteAsync(null);
        Assert.Equal(1, service.Repairs);
        Assert.Contains("缺失 0", vm.CountsText);
        Assert.Contains("完整校验通过", vm.StatusText);
        service.WaitForCancellation = true;
        var checking = vm.CheckCommand.ExecuteAsync(null);
        Assert.True(vm.CancelCommand.CanExecute(null));
        Assert.False(vm.RepairCommand.CanExecute(null));
        vm.CancelCommand.Execute(null);
        await checking;
        Assert.Contains("已取消", vm.StatusText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ErrorMessagesDoNotExposeSensitivePayload()
    {
        var diagnostics = new Diagnostics();
        var vm = new RuntimeMaintenanceViewModel(new VmService { Failure = new IOException("TOKEN=secret-cookie") }, new RecordingDialogService(), diagnostics);
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Contains("失败", vm.StatusText);
        Assert.DoesNotContain("secret-cookie", vm.StatusText + diagnostics.LastDiagnostic);
        Assert.Contains("IOException", diagnostics.LastDiagnostic);
    }

    private sealed class VmService : IRuntimeMaintenanceService
    {
        public int Repairs;
        public bool WaitForCancellation;
        public Exception? Failure;
        public async Task<RuntimeDependencyCheckResult> CheckAsync(IProgress<BundledRuntimeProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            if (Failure is not null) throw Failure;
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
            return new(3, 0, 0);
        }
        public Task RepairAsync(IProgress<BundledRuntimeProgress>? progress = null, CancellationToken cancellationToken = default) { Repairs++; return Task.CompletedTask; }
    }
    private sealed class Diagnostics : IAppDiagnostics
    {
        public string? LastDiagnostic { get; private set; }
        public void Record(string message, Exception? error = null) => LastDiagnostic = message;
    }
    private sealed class CallbackProgress(Action<BundledRuntimeProgress> callback) : IProgress<BundledRuntimeProgress>
    {
        public void Report(BundledRuntimeProgress value) => callback(value);
    }
    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        public string Bundle { get; }
        public AppPaths Paths { get; }
        public Fixture()
        {
            Bundle = Path.Combine(_directory.Path, "bundle");
            Paths = new AppPaths(Path.Combine(_directory.Path, "target"), Path.Combine(_directory.Path, "settings"));
            string[] files = ["node.exe", "nodejs-project/main.js", "nodejs-project/node_modules/pkg/index.js"];
            foreach (var relative in files)
            {
                var path = Path.Combine(Bundle, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "good");
            }
            File.WriteAllLines(Path.Combine(Bundle, "SHA256SUMS.txt"), files.Select(relative =>
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Bundle, relative)))) + "  " + relative));
        }
        public string Target(string relative) => Path.Combine(Paths.RuntimeDirectory, relative);
        public RuntimeController Controller() => new(new Supervisor(DesktopRuntimeState.Stopped), () => throw new NotSupportedException());
        public void Dispose() => _directory.Dispose();
    }
    private sealed class Supervisor(DesktopRuntimeState state) : INodeSupervisor
    {
        public int StopCalls;
        public RuntimeSnapshot Snapshot => new(state);
        public Task<RuntimeSnapshot> StartAsync(StartConfig config, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdoptionResult> AdoptAsync(StartConfig config, RuntimeHealthSnapshot? health = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RuntimeSnapshot> StopAsync(string reason = "user", CancellationToken cancellationToken = default) { StopCalls++; return Task.FromResult(new RuntimeSnapshot(DesktopRuntimeState.Stopped)); }
        public Task<RuntimeSnapshot> ForceStopAsync(string reason = "application-exit", CancellationToken cancellationToken = default) => StopAsync(reason, cancellationToken);
        public string? LivenessFailure() => null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Pending : IPendingCoreUpdateService
    {
        public CoreUpdateCheckResult? PendingUpdate => null;
        public bool IsApplying { get; set; }
        public event EventHandler? StateChanged { add { } remove { } }
        public Task<CoreManagementOperationResult?> ApplyPendingAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Scheduler : ICoreUpdateScheduler
    {
        public int Paused;
        public int Resumed;
        public event EventHandler<CoreUpdateCheckResult>? CheckCompleted { add { } remove { } }
        public event EventHandler<string>? DiagnosticChanged { add { } remove { } }
        public void Start() { }
        public void SetBackgroundActive(bool active) { }
        public void NotifyPolicyChanged() { }
        public Task PauseForApplicationUpdateAsync(CancellationToken cancellationToken = default) { Paused++; return Task.CompletedTask; }
        public void ResumeAfterApplicationUpdate() => Resumed++;
        public Task<CoreUpdateCheckResult> CheckForegroundAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoreUpdateCheckResult?> CheckBackgroundAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoreUpdateCheckResult> CheckManualAsync(ManagedCoreVariant variant, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
