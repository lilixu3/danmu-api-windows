using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DanmuApi.Runtime;
using Xunit.Abstractions;

namespace DanmuApi.Tests;

public sealed class RuntimeAdoptionTests
{
    private readonly ITestOutputHelper _output;

    public RuntimeAdoptionTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task AdoptsOwnedLiveProcessAndStopsItThroughTerminator()
    {
        using var directory = new TemporaryDirectory();
        using var process = StartLongLivedProcess();
        var config = CreateConfig(directory.Path);
        var identity = File.ReadAllText(config.IdentityFile!).Trim();
        var healthClient = new RecordingHealthClient();
        var terminator = new KillingTerminator();
        await using var supervisor = new NodeSupervisor(healthClient, terminator);

        var result = await supervisor.AdoptAsync(config, CreateHealth(process.Id, config, identity));

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal(DesktopRuntimeState.Running, result.Snapshot.State);
        Assert.Equal(process.Id, result.Snapshot.Pid);
        Assert.Equal(0, healthClient.Calls);
        Assert.Equal(DesktopRuntimeState.Running, supervisor.Snapshot.State);

        var stopped = await supervisor.StopAsync("adoption-test");

        Assert.Equal(DesktopRuntimeState.Stopped, stopped.State);
        Assert.Null(stopped.Pid);
        Assert.Equal(1, terminator.Calls);
        Assert.False(IsAlive(process));
    }

    [Fact]
    public async Task ProbesLoopbackWhenHealthWasNotProvided()
    {
        using var directory = new TemporaryDirectory();
        using var process = StartLongLivedProcess();
        var config = CreateConfig(directory.Path);
        var identity = File.ReadAllText(config.IdentityFile!).Trim();
        var healthClient = new RecordingHealthClient { Response = CreateHealth(process.Id, config, identity) };
        await using var supervisor = new NodeSupervisor(healthClient, new KillingTerminator());

        var result = await supervisor.AdoptAsync(config);

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal(1, healthClient.Calls);
        await supervisor.StopAsync("probe-test");
    }

    [Fact]
    public async Task RejectsNonOwnedHealthWithoutCallingTerminator()
    {
        using var directory = new TemporaryDirectory();
        using var process = StartLongLivedProcess();
        var config = CreateConfig(directory.Path);
        var identity = File.ReadAllText(config.IdentityFile!).Trim();
        var terminator = new KillingTerminator();
        await using var supervisor = new NodeSupervisor(new RecordingHealthClient(), terminator);

        var result = await supervisor.AdoptAsync(
            config,
            CreateHealth(process.Id, config, identity) with { RuntimeIdentity = "another-installation" });

        Assert.False(result.Succeeded);
        Assert.Equal(AdoptionFailureKind.NotOwned, result.FailureKind);
        Assert.Equal(DesktopRuntimeState.Stopped, result.Snapshot.State);
        Assert.Equal(0, terminator.Calls);
        Assert.True(IsAlive(process));
    }

    [Fact]
    public async Task RejectsIllegalPidWithoutCallingTerminator()
    {
        using var directory = new TemporaryDirectory();
        var config = CreateConfig(directory.Path);
        var identity = File.ReadAllText(config.IdentityFile!).Trim();
        var terminator = new KillingTerminator();
        await using var supervisor = new NodeSupervisor(new RecordingHealthClient(), terminator);

        var result = await supervisor.AdoptAsync(
            config,
            CreateHealth(0, config, identity));

        Assert.False(result.Succeeded);
        Assert.Equal(AdoptionFailureKind.InvalidPid, result.FailureKind);
        Assert.Contains("PID", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, terminator.Calls);
    }

    [Theory]
    [InlineData(DesktopRuntimeState.Preparing)]
    [InlineData(DesktopRuntimeState.Starting)]
    [InlineData(DesktopRuntimeState.Stopped)]
    public async Task LivenessFailureIgnoresNonRunningStates(DesktopRuntimeState state)
    {
        using var directory = new TemporaryDirectory();
        await using var supervisor = new NodeSupervisor(new RecordingHealthClient(), new KillingTerminator());
        var snapshotField = typeof(NodeSupervisor).GetField("_snapshot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        snapshotField!.SetValue(supervisor, new RuntimeSnapshot(state));

        Assert.Null(supervisor.LivenessFailure());
        Assert.Equal(state, supervisor.Snapshot.State);
    }

    [Fact]
    public async Task LivenessFailureOnlyChangesAnActiveSupervisorToFailed()
    {
        using var directory = new TemporaryDirectory();
        using var process = StartLongLivedProcess();
        var config = CreateConfig(directory.Path);
        var identity = File.ReadAllText(config.IdentityFile!).Trim();
        await using var supervisor = new NodeSupervisor(new RecordingHealthClient(), new KillingTerminator());

        var adopted = await supervisor.AdoptAsync(config, CreateHealth(process.Id, config, identity));
        Assert.True(adopted.Succeeded, adopted.Diagnostic);
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();

        var failure = supervisor.LivenessFailure();

        Assert.Contains("exitCode", failure, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DesktopRuntimeState.Failed, supervisor.Snapshot.State);
        Assert.Null(supervisor.LivenessFailure());
    }

    [Fact]
    public async Task LivenessFailureIsIgnoredAfterSuccessfulStop()
    {
        using var directory = new TemporaryDirectory();
        using var process = StartLongLivedProcess();
        var config = CreateConfig(directory.Path);
        var identity = File.ReadAllText(config.IdentityFile!).Trim();
        await using var supervisor = new NodeSupervisor(new RecordingHealthClient(), new KillingTerminator());

        var adopted = await supervisor.AdoptAsync(config, CreateHealth(process.Id, config, identity));
        Assert.True(adopted.Succeeded, adopted.Diagnostic);
        var stopped = await supervisor.StopAsync("liveness-test");

        Assert.Equal(DesktopRuntimeState.Stopped, stopped.State);
        Assert.Null(supervisor.LivenessFailure());
    }

    [SkippableFact]
    public async Task CancellingStartupCleansCreatedNodeAndWaitsForPumps()
    {
        var node = Environment.GetEnvironmentVariable("DANMU_TEST_NODE_EXE");
        Skip.If(string.IsNullOrWhiteSpace(node), "DANMU_TEST_NODE_EXE 未设置，跳过真实 Node 取消启动清理测试。");
        Skip.IfNot(File.Exists(node), $"DANMU_TEST_NODE_EXE 指向不存在的文件: {node}");

        using var directory = new TemporaryDirectory();
        var config = CreateConfig(directory.Path) with
        {
            NodeExe = Path.GetFullPath(node!),
            StartupTimeout = TimeSpan.FromSeconds(10),
        };
        File.WriteAllText(Path.Combine(config.ScriptDir, "main.js"), "setTimeout(() => {}, 60000);\n");
        var terminator = new KillingTerminator();
        var healthClient = new BlockingStartupHealthClient();
        await using var supervisor = new NodeSupervisor(healthClient, terminator);
        using var cancellation = new CancellationTokenSource();
        var startup = supervisor.StartAsync(config, cancellation.Token);
        int createdPid;
        Task stdoutPump;
        Task stderrPump;
        OperationCanceledException cancelled;
        try
        {
            // Startup health is queried only after StartProcess has created both pumps
            // and the supervisor has assigned _process. Starting alone is not that signal.
            await Task.WhenAny(healthClient.Entered, startup).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(healthClient.Entered.IsCompletedSuccessfully,
                $"Startup ended before the post-spawn health checkpoint: {supervisor.Snapshot.FailureReason}");
            Assert.True(supervisor.HasOwnedProcess);
            var process = Assert.IsType<Process>(ReadSupervisorField(supervisor, "_process"));
            Assert.False(process.HasExited);
            createdPid = process.Id;
            stdoutPump = Assert.IsAssignableFrom<Task>(ReadSupervisorField(supervisor, "_stdoutPump"));
            stderrPump = Assert.IsAssignableFrom<Task>(ReadSupervisorField(supervisor, "_stderrPump"));
            Assert.False(stdoutPump.IsCompleted);
            Assert.False(stderrPump.IsCompleted);
            _output.WriteLine($"Post-spawn health checkpoint: state={supervisor.Snapshot.State}, " +
                $"owned={supervisor.HasOwnedProcess}, pid={createdPid}, " +
                $"stdoutPumpCompleted={stdoutPump.IsCompleted}, stderrPumpCompleted={stderrPump.IsCompleted}");
        }
        finally
        {
            // Also unblock startup on assertion/observation failure so disposal can
            // clean our child; the timeout only fails a missing observation/cleanup.
            cancellation.Cancel();
            cancelled = await Assert.ThrowsAsync<OperationCanceledException>(() => startup.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        _output.WriteLine($"Cancellation cleanup: exception={cancelled.GetType().Name}, calls={terminator.Calls}, " +
            $"lastPid={terminator.LastPid}, alive={IsProcessAlive(createdPid)}, " +
            $"stdoutPump={stdoutPump.Status}, stderrPump={stderrPump.Status}, " +
            $"owned={supervisor.HasOwnedProcess}, state={supervisor.Snapshot.State}, " +
            $"reason={supervisor.Snapshot.FailureReason}");
        Assert.Equal(1, terminator.Calls);
        Assert.NotNull(terminator.LastPid);
        Assert.Equal(createdPid, terminator.LastPid);
        Assert.False(IsProcessAlive(terminator.LastPid!.Value));
        Assert.True(stdoutPump.IsCompletedSuccessfully);
        Assert.True(stderrPump.IsCompletedSuccessfully);
        Assert.Null(ReadSupervisorField(supervisor, "_stdoutPump"));
        Assert.Null(ReadSupervisorField(supervisor, "_stderrPump"));
        Assert.False(supervisor.HasOwnedProcess);
        Assert.Equal(DesktopRuntimeState.Failed, supervisor.Snapshot.State);
        Assert.Equal("启动已取消", supervisor.Snapshot.FailureReason);
    }

    [Fact]
    public async Task CancellingBeforeNodeCreationDoesNotCallTerminator()
    {
        using var directory = new TemporaryDirectory();
        using var squat = new PortSquat();
        var config = CreateConfig(directory.Path) with
        {
            Port = squat.Port,
            NodeExe = Path.Combine(directory.Path, "node.exe"),
        };
        // A placeholder satisfies preparation validation but must never be executed.
        File.WriteAllBytes(config.NodeExe, []);
        var terminator = new KillingTerminator();
        var healthClient = new RecordingHealthClient();
        using var cancellation = new CancellationTokenSource();
        var released = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var supervisor = new NodeSupervisor(
            healthClient,
            terminator,
            diagnosticSink: diagnostic =>
            {
                // This real preflight callback runs before the explicit pre-spawn
                // ThrowIfCancellationRequested, not inside the cancellable port wait.
                if (diagnostic.StartsWith($"端口 {config.Port} 启动前被临时占用（无监听进程）", StringComparison.Ordinal))
                {
                    cancellation.Cancel();
                    Assert.True(released.TrySetResult(diagnostic), "Unexpected second preflight-release diagnostic");
                }
            },
            transientPortWait: TimeSpan.FromSeconds(10));
        Assert.Equal(PortAvailabilityState.Unbindable, PortAvailability.Probe(config.Port));
        var startup = supervisor.StartAsync(config, cancellation.Token);
        OperationCanceledException cancelled;
        try
        {
            // With the uncontended gate and held port, StartAsync has synchronously
            // reached the pending preflight wait. Release it only after observing that.
            Assert.False(startup.IsCompleted);
            Assert.Equal(DesktopRuntimeState.Preparing, supervisor.Snapshot.State);
            Assert.False(supervisor.HasOwnedProcess);
            Assert.False(cancellation.IsCancellationRequested);
            squat.Dispose();
            await Task.WhenAny(released.Task, startup).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(released.Task.IsCompletedSuccessfully,
                $"Startup ended before the pre-spawn release checkpoint: {supervisor.Snapshot.FailureReason}");
        }
        finally
        {
            cancellation.Cancel();
            cancelled = await Assert.ThrowsAsync<OperationCanceledException>(() => startup.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        _output.WriteLine($"Pre-spawn checkpoint: diagnostic={await released.Task}; " +
            $"exception={cancelled.GetType().Name}, calls={terminator.Calls}, lastPid={terminator.LastPid}, " +
            $"owned={supervisor.HasOwnedProcess}, state={supervisor.Snapshot.State}, pid={supervisor.Snapshot.Pid}, " +
            $"reason={supervisor.Snapshot.FailureReason}");
        Assert.Contains("启动前被临时占用（无监听进程）", await released.Task, StringComparison.Ordinal);
        Assert.Contains("已释放，继续启动", await released.Task, StringComparison.Ordinal);
        Assert.Equal(cancellation.Token, cancelled.CancellationToken);
        Assert.Equal(0, terminator.Calls);
        Assert.Null(terminator.LastPid);
        Assert.Equal(0, healthClient.Calls);
        Assert.False(supervisor.HasOwnedProcess);
        Assert.Null(ReadSupervisorField(supervisor, "_stdoutPump"));
        Assert.Null(ReadSupervisorField(supervisor, "_stderrPump"));
        Assert.Equal(DesktopRuntimeState.Failed, supervisor.Snapshot.State);
        Assert.Null(supervisor.Snapshot.Pid);
        Assert.Contains("启动已取消（创建 Node 进程前）", supervisor.Snapshot.FailureReason, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(config.ScriptDir, "logs", "node-stdout.log")));
        Assert.False(File.Exists(Path.Combine(config.ScriptDir, "logs", "node-stderr.log")));
    }

    private static object? ReadSupervisorField(NodeSupervisor supervisor, string name)
    {
        var field = typeof(NodeSupervisor).GetField(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field.GetValue(supervisor);
    }

    private static StartConfig CreateConfig(string scriptDir)
    {
        Directory.CreateDirectory(Path.Combine(scriptDir, "logs"));
        File.WriteAllText(Path.Combine(scriptDir, "main.js"), "// adoption test");
        var identityFile = Path.Combine(scriptDir, "identity", "instance-id");
        Directory.CreateDirectory(Path.GetDirectoryName(identityFile)!);
        File.WriteAllText(identityFile, "desktop-adoption-test\n");
        return new StartConfig(
            Environment.ProcessPath ?? throw new InvalidOperationException("测试进程路径不可用"),
            scriptDir,
            Port: GetFreePort(),
            IdentityFile: identityFile);
    }

    private static RuntimeHealthSnapshot CreateHealth(int pid, StartConfig config, string identity) =>
        new(
            Pid: pid,
            Node: "test-node",
            UptimeSec: 1,
            Host: "127.0.0.1",
            MainPort: config.Port,
            ProxyPort: null,
            Cwd: config.ScriptDir,
            EnvHome: config.ScriptDir,
            ResolvedHome: config.ScriptDir,
            CacheProbeDir: null,
            CacheProbeWritable: null,
            Variant: config.Variant,
            VariantLabel: null,
            RuntimeIdentity: identity,
            RequestCount: 0,
            LastRequestAt: null,
            LastRequestPath: null,
            LastClientIp: null,
            EnvFileMtimeMs: null,
            LogFile: null,
            LogLevel: null,
            AccessControl: null);

    private static Process StartLongLivedProcess()
    {
        var shell = Environment.GetEnvironmentVariable("COMSPEC") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = shell,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("ping 127.0.0.1 -n 30 > nul");
        return Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动测试子进程");
    }

    private static bool IsAlive(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed class BlockingStartupHealthClient : IRuntimeHealthClient
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<RuntimeHealthSnapshot> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public Task<RuntimeHealthSnapshot> ReadAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            // Occupancy probes use no cancellation token and are not post-spawn.
            // Fail that unexpected path explicitly instead of parking preflight forever.
            Assert.True(cancellationToken.CanBeCanceled, "Expected a cancellable post-spawn health check");
            Assert.True(_entered.TrySetResult(), "Unexpected second startup health check");
            return _response.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class RecordingHealthClient : IRuntimeHealthClient
    {
        public int Calls { get; private set; }
        public RuntimeHealthSnapshot? Response { get; init; }

        public Task<RuntimeHealthSnapshot> ReadAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Response is null
                ? Task.FromException<RuntimeHealthSnapshot>(new RuntimeHealthException(HealthFailureKind.Connection, "test health response missing"))
                : Task.FromResult(Response);
        }
    }

    private sealed class KillingTerminator : IProcessTerminator
    {
        public int Calls { get; private set; }
        public int? LastPid { get; private set; }

        public async Task<ProcessTerminationResult> TerminateAsync(
            Process process,
            string expectedNodeExe,
            string expectedMainScript,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastPid = process.Id;
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }

            return new(true, "test terminator");
        }
    }
}
