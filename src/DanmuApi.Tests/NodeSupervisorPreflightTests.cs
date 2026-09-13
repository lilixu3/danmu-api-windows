using System.Diagnostics;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

/// <summary>
/// 启动前端口预检的行为约束：
/// 1. 端口"不可绑定但没有监听者"（Windows 把动态端口范围内的端口临时借给别的连接）必须等待释放，而不是判成"已有其他实例"；
/// 2. 真的有监听者时才判占用，并且要把健康检查失败的原因写进失败原因；
/// 3. 预检绝不代杀进程；
/// 4. 预检失败后运行时必须能回到 Stopped，否则应用退出会被卡住。
/// </summary>
public sealed class NodeSupervisorPreflightTests
{
    [Fact]
    public async Task TransientPortSquatIsWaitedOutAndStartProceeds()
    {
        using var directory = new TemporaryDirectory();
        var squat = new PortSquat();
        var config = CreateConfig(directory, squat.Port);
        var diagnostics = new List<string>();
        var terminator = new NeverCalledTerminator();
        _ = Task.Run(async () =>
        {
            await Task.Delay(400).ConfigureAwait(false);
            squat.Dispose();
        });

        try
        {
            await using var supervisor = new NodeSupervisor(
                new StubHealthClient(),
                terminator,
                diagnostics.Add,
                transientPortWait: TimeSpan.FromSeconds(10));

            var snapshot = await supervisor.StartAsync(config);

            Assert.Equal(DesktopRuntimeState.Failed, snapshot.State);
            // 预检已放行：失败原因落在 node.exe 本身（测试用的是空文件），不再是端口占用。
            Assert.Contains("node.exe 启动失败", snapshot.FailureReason, StringComparison.Ordinal);
            Assert.DoesNotContain("端口", snapshot.FailureReason, StringComparison.Ordinal);
            Assert.Contains(diagnostics, line => line.Contains("临时占用", StringComparison.Ordinal) && line.Contains("已释放", StringComparison.Ordinal));
            Assert.Equal(0, terminator.Calls);
        }
        finally
        {
            squat.Dispose();
        }
    }

    [Fact]
    public async Task PersistentSquatFailsWithAccurateDiagnostic()
    {
        using var directory = new TemporaryDirectory();
        using var squat = new PortSquat();
        var config = CreateConfig(directory, squat.Port);
        var watch = Stopwatch.StartNew();

        await using var supervisor = new NodeSupervisor(
            new StubHealthClient(),
            new NeverCalledTerminator(),
            transientPortWait: TimeSpan.FromMilliseconds(400));

        var snapshot = await supervisor.StartAsync(config);

        Assert.Equal(DesktopRuntimeState.Failed, snapshot.State);
        Assert.Contains("无法绑定", snapshot.FailureReason, StringComparison.Ordinal);
        Assert.Contains("没有任何进程在监听", snapshot.FailureReason, StringComparison.Ordinal);
        Assert.DoesNotContain("其他实例", snapshot.FailureReason, StringComparison.Ordinal);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(400), $"应先等待临时占用释放：{watch.Elapsed}");
    }

    [Fact]
    public async Task OwnInstanceOnPortFailsFastWithExistingWindowHint()
    {
        using var directory = new TemporaryDirectory();
        using var listener = TestPorts.Listen(out var port);
        var config = CreateConfig(directory, port, identity: "desktop-known-identity");
        var health = new StubHealthClient { Responder = (_, _) => Health("desktop-known-identity") };
        var watch = Stopwatch.StartNew();

        await using var supervisor = new NodeSupervisor(
            health,
            new NeverCalledTerminator(),
            transientPortWait: TimeSpan.FromSeconds(30));

        var snapshot = await supervisor.StartAsync(config);

        Assert.Equal(DesktopRuntimeState.Failed, snapshot.State);
        Assert.Contains("已被本应用实例占用", snapshot.FailureReason, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"本应用实例占用应立即判定，不该等待临时占用超时：{watch.Elapsed}");
        Assert.Equal(1, health.Calls);
    }

    [Fact]
    public async Task ForeignListenerReportsOtherInstanceWithHealthDetail()
    {
        using var directory = new TemporaryDirectory();
        using var listener = TestPorts.Listen(out var port);
        var config = CreateConfig(directory, port);

        await using var supervisor = new NodeSupervisor(
            new StubHealthClient(),
            new NeverCalledTerminator(),
            transientPortWait: TimeSpan.FromSeconds(30));

        var snapshot = await supervisor.StartAsync(config);

        Assert.Equal(DesktopRuntimeState.Failed, snapshot.State);
        Assert.Contains("其他实例", snapshot.FailureReason, StringComparison.Ordinal);
        // 曾经只报"其他实例"，事后无法区分"别家进程"和"本应用留下的孤儿进程"，这里必须带上健康检查原因。
        Assert.Contains("健康检查失败", snapshot.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopAfterPreflightFailureEndsStoppedSoTheAppCanExit()
    {
        using var directory = new TemporaryDirectory();
        using var squat = new PortSquat();
        var config = CreateConfig(directory, squat.Port);
        var diagnostics = new List<string>();

        await using var supervisor = new NodeSupervisor(
            new StubHealthClient(),
            new NeverCalledTerminator(),
            diagnostics.Add,
            transientPortWait: TimeSpan.FromMilliseconds(300));

        var failed = await supervisor.StartAsync(config);
        Assert.Equal(DesktopRuntimeState.Failed, failed.State);

        // 端口仍被别的连接占着：本实例从未运行过 Node，不能因此把退出路径钉死在 Failed。
        var stopped = await supervisor.StopAsync("exit-after-preflight-failure");

        Assert.Equal(DesktopRuntimeState.Stopped, stopped.State);
        Assert.Contains(diagnostics, line => line.Contains("从未运行 Node 子进程", StringComparison.Ordinal));
    }

    private static StartConfig CreateConfig(TemporaryDirectory directory, int port, string? identity = null)
    {
        File.WriteAllBytes(Path.Combine(directory.Path, "node.exe"), []);
        File.WriteAllText(Path.Combine(directory.Path, "main.js"), "// test entry\n");
        var identityFile = Path.Combine(directory.Path, "instance-id");
        if (identity is not null)
        {
            File.WriteAllText(identityFile, identity + Environment.NewLine);
        }

        return new StartConfig(
            NodeExe: Path.Combine(directory.Path, "node.exe"),
            ScriptDir: directory.Path,
            Port: port,
            ListenHost: "127.0.0.1",
            Variant: "stable",
            IdentityFile: identityFile,
            StartupTimeout: TimeSpan.FromSeconds(5),
            ShutdownTimeout: TimeSpan.FromSeconds(5));
    }

    private static RuntimeHealthSnapshot Health(string identity) => new(
        Pid: 1,
        Node: "node",
        UptimeSec: 1,
        Host: "127.0.0.1",
        MainPort: 1,
        ProxyPort: null,
        Cwd: null,
        EnvHome: null,
        ResolvedHome: null,
        CacheProbeDir: null,
        CacheProbeWritable: null,
        Variant: "stable",
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

    private sealed class StubHealthClient : IRuntimeHealthClient
    {
        public Func<string, int, RuntimeHealthSnapshot>? Responder { get; init; }
        public int Calls { get; private set; }

        public Task<RuntimeHealthSnapshot> ReadAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Responder is null
                ? Task.FromException<RuntimeHealthSnapshot>(
                    new RuntimeHealthException(HealthFailureKind.Connection, "test health response missing"))
                : Task.FromResult(Responder(host, port));
        }
    }

    private sealed class NeverCalledTerminator : IProcessTerminator
    {
        public int Calls { get; private set; }

        public Task<ProcessTerminationResult> TerminateAsync(
            Process process,
            string expectedNodeExe,
            string expectedMainScript,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("端口预检不得代杀进程");
        }
    }
}
