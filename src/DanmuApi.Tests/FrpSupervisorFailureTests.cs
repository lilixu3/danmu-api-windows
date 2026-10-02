using System.Diagnostics;
using System.Reflection;
using DanmuApi.Core.Frp;
using DanmuApi.Platform;
using DanmuApi.Runtime;
using DanmuApi.Runtime.Frp;

namespace DanmuApi.Tests;

/// <summary>离线负向回归：仅操作每例自己启动的临时子进程，不扫描/终止任何用户 frp 或 Node。</summary>
public sealed class FrpSupervisorFailureTests
{
    [SkippableFact]
    public async Task FailedStartupCleanupRetainsHandlePlanPidAndRetriesStop()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "仅 Windows 临时进程测试");
        using var directory = new TemporaryDirectory();
        using var child = StartOwnedChild();
        var terminator = new OwnedChildTerminator(child) { Refuse = true };
        var supervisor = InjectSupervisor(child, directory.Path, terminator, out var plan);
        try
        {
            var fail = (Task<FrpSnapshot>)typeof(FrpSupervisor).GetMethod("FailAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(supervisor, ["injected startup failure", ReadField<Process>(supervisor, "_process")])!;
            var failed = await fail;
            Assert.Equal(FrpTunnelState.Failed, failed.State);
            Assert.Equal(child.Id, failed.Pid);
            Assert.True(failed.HasOwnedProcess);
            Assert.True(failed.RequiresStop);
            Assert.True(supervisor.HasOwnedProcess);
            Assert.False(child.HasExited);
            Assert.Same(plan, ReadField<FrpRunPlan>(supervisor, "_plan"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.StartAsync(plan));
            Assert.Equal(FrpTunnelState.Failed, (await supervisor.StopAsync()).State);
            Assert.Equal(2, terminator.Calls);
            terminator.Refuse = false;
            Assert.Equal(FrpTunnelState.Stopped, (await supervisor.StopAsync()).State);
            Assert.True(child.HasExited);
            Assert.False(supervisor.HasOwnedProcess);
            await supervisor.DisposeAsync();
        }
        finally { await CleanOwnedChildAsync(child); }
    }

    [SkippableFact]
    public async Task DisposePropagatesSafetyRefusalWithoutDestroyingRetryableStop()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "仅 Windows 临时进程测试");
        using var directory = new TemporaryDirectory();
        using var child = StartOwnedChild();
        var terminator = new OwnedChildTerminator(child) { Refuse = true };
        var supervisor = InjectSupervisor(child, directory.Path, terminator, out _);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.DisposeAsync().AsTask());
            Assert.Contains("safety refusal", error.Message, StringComparison.Ordinal);
            Assert.False(child.HasExited);
            Assert.True(supervisor.HasOwnedProcess);
            terminator.Refuse = false;
            Assert.Equal(FrpTunnelState.Stopped, (await supervisor.StopAsync()).State);
            await supervisor.DisposeAsync();
        }
        finally { await CleanOwnedChildAsync(child); }
    }

    [SkippableTheory]
    [InlineData("missing")]
    [InlineData("closed")]
    [InlineData("waiting")]
    [InlineData("unrecognized")]
    public async Task NonRunningObservationClearsOldAddressAndPublishesTruthfulReconnecting(string observed)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "仅 Windows 临时进程测试");
        using var directory = new TemporaryDirectory();
        using var child = StartOwnedChild();
        var admin = new ControlledAdminClient { Status = observed == "missing" ? [] : [Proxy(observed)] };
        var supervisor = InjectSupervisor(child, directory.Path, new OwnedChildTerminator(child), out _, admin);
        try
        {
            var refreshed = await supervisor.RefreshAsync();
            Assert.Equal(FrpTunnelState.Reconnecting, refreshed.State);
            Assert.Null(refreshed.RemoteAddress);
            Assert.NotEmpty(refreshed.Diagnostic!);
            Assert.Equal(admin.Status, refreshed.ProxyList);
            Assert.Equal(refreshed, supervisor.Snapshot);
            Assert.False(child.HasExited); // 重连保留自己的进程。
            await supervisor.DisposeAsync();
        }
        finally { await CleanOwnedChildAsync(child); }
    }

    [SkippableFact]
    public async Task RefreshMustRecheckLivenessBeforePublishingRunning()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "仅 Windows 临时进程测试");
        using var directory = new TemporaryDirectory();
        using var child = StartOwnedChild();
        var admin = new ControlledAdminClient { Status = [Proxy("running")] };
        admin.BeforeResponse = () => CleanOwnedChildAsync(child);
        var supervisor = InjectSupervisor(child, directory.Path, new OwnedChildTerminator(child), out _, admin);
        SetField(supervisor, "_snapshot", new FrpSnapshot(FrpTunnelState.Reconnecting, Pid: child.Id, HasOwnedProcess: true));
        var states = new List<FrpTunnelState>();
        supervisor.SnapshotChanged += (_, snapshot) => states.Add(snapshot.State);
        try
        {
            Assert.Equal(FrpTunnelState.Failed, (await supervisor.RefreshAsync()).State);
            Assert.DoesNotContain(FrpTunnelState.Running, states);
            await supervisor.DisposeAsync();
        }
        finally { await CleanOwnedChildAsync(child); }
    }

    [SkippableFact]
    public async Task ActualMetadataRefusalNeverLeaksTokenMarkerOrRawCommandLine()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "仅 Windows 元数据测试");
        using var child = StartOwnedChild("AUDIT_NOT_A_SECRET");
        try
        {
            var result = await new WindowsProcessTerminator().TerminateVerifiedAsync(child, child.StartInfo.FileName,
                @"C:\owned\frpc.toml", "frpc.exe", TimeSpan.FromSeconds(10));
            Assert.False(result.Succeeded);
            Assert.Contains("配置参数", result.Diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("AUDIT_NOT_A_SECRET", result.Diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("Start-Sleep", result.Diagnostic, StringComparison.Ordinal);
            Assert.False(child.HasExited);
        }
        finally { await CleanOwnedChildAsync(child); }
    }

    [SkippableFact]
    public async Task StartupMustNotPublishRunningBeforeItsFinalLivenessCheck()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "仅 Windows Node fixture 测试");
        var node = Environment.GetEnvironmentVariable("DANMU_TEST_NODE_EXE");
        Skip.If(string.IsNullOrWhiteSpace(node) || !File.Exists(node), "需要真实 DANMU_TEST_NODE_EXE（跳过不算通过）");
        using var directory = new TemporaryDirectory();
        // 唯一进程名保证启动预检不扫描任何用户进程；Node -c 仅校验脚本然后退出，不访问网络。
        var executable = Path.Combine(directory.Path, $"frp-liveness-{Guid.NewGuid():N}.exe");
        File.Copy(node!, executable);
        var script = Path.Combine(directory.Path, "config.js");
        File.WriteAllText(script, "// exit after syntax check");
        var admin = new ControlledAdminClient { Status = [Proxy("running")] };
        var supervisor = new FrpSupervisor(admin, new OwnedChildTerminator(), pollInterval: TimeSpan.FromMilliseconds(20));
        admin.BeforeResponse = async () =>
        {
            var process = ReadField<Process>(supervisor, "_process");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        };
        var states = new List<FrpTunnelState>();
        supervisor.SnapshotChanged += (_, snapshot) => states.Add(snapshot.State);
        var plan = new FrpRunPlan(FrpRole.Client, executable, script, directory.Path, directory.Path, TestPorts.FreePort(),
            "admin", "local-test-password", "danmu-api", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(FrpTunnelState.Failed, (await supervisor.StartAsync(plan)).State);
            Assert.DoesNotContain(FrpTunnelState.Running, states);
            await supervisor.DisposeAsync();
        }
        finally
        {
            if (supervisor.HasOwnedProcess)
            {
                var process = ReadField<Process>(supervisor, "_process");
                await CleanOwnedChildAsync(process);
                await supervisor.StopAsync();
            }
        }
    }

    private static FrpSupervisor InjectSupervisor(Process process, string root, OwnedChildTerminator terminator,
        out FrpRunPlan plan, ControlledAdminClient? admin = null)
    {
        plan = new(FrpRole.Client, process.StartInfo.FileName, Path.Combine(root, "frpc.toml"), root, root, 7400,
            "admin", "local-test-password", "danmu-api", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        var supervisor = new FrpSupervisor(admin ?? new ControlledAdminClient(), terminator);
        var tracked = Process.GetProcessById(process.Id);
        // GetProcessById 只有懒加载 PID；必须在仍存活时固定 native handle，退出后才能读取 ExitCode。
        // 正式 WindowsProcessTerminator 在归属查询前同样固定 SafeHandle；不能让测试替身跳过这一步。
        Assert.False(tracked.SafeHandle.IsInvalid);
        SetField(supervisor, "_process", tracked);
        SetField(supervisor, "_plan", plan);
        SetField(supervisor, "_snapshot", new FrpSnapshot(FrpTunnelState.Running, process.Id, "old.invalid:19321", HasOwnedProcess: true));
        return supervisor;
    }

    private static FrpProxyStatus Proxy(string status) => new("danmu-api", "tcp", status, "", "127.0.0.1:9321", "old.invalid:19321");
    private static void SetField(object instance, string name, object value) => instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);
    private static T ReadField<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static Process StartOwnedChild(string marker = "ready")
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", $"[Console]::WriteLine('{marker}'); Start-Sleep -Seconds 60" }) start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start owned test child");
        return process;
    }

    private static async Task CleanOwnedChildAsync(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        Assert.True(process.HasExited);
    }

    private sealed class OwnedChildTerminator(Process? ownedProcess = null) : IVerifiedProcessTerminator
    {
        public bool Refuse { get; set; }
        public int Calls { get; private set; }
        public async Task<ProcessTerminationResult> TerminateVerifiedAsync(Process process, string expectedExecutablePath,
            string expectedArgumentFragment, string expectedExecutableLabel, TimeSpan timeout, CancellationToken cancellationToken = default,
            string? expectedArgumentLabel = null)
        {
            Calls++;
            if (ownedProcess is not null) Assert.Equal(ownedProcess.Id, process.Id);
            Assert.NotEqual(Environment.ProcessId, process.Id);
            if (Refuse) return new(false, "injected safety refusal");
            if (!process.HasExited)
            {
                Assert.NotNull(ownedProcess); // 未持有本例原始启动句柄时不许杀任何进程。
                ownedProcess.Kill(entireProcessTree: true);
                await ownedProcess.WaitForExitAsync(cancellationToken);
            }
            Assert.True(process.HasExited);
            return new(true, "owned test child exited", (ownedProcess ?? process).ExitCode);
        }
    }

    private sealed class ControlledAdminClient : IFrpAdminClient
    {
        public IReadOnlyList<FrpProxyStatus> Status { get; set; } = [];
        public Func<Task>? BeforeResponse { get; set; }
        public async Task<IReadOnlyList<FrpProxyStatus>> ReadClientStatusAsync(int port, string user, string password, CancellationToken cancellationToken = default)
        {
            if (BeforeResponse is not null) await BeforeResponse();
            return Status;
        }
        public Task<FrpServerInfo> ReadServerInfoAsync(int port, string user, string password, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FrpServerInfo("0.71.0", 7000, 0, 0, 0));
    }
}
