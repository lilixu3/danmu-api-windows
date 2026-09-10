using System.Net;
using System.Net.Sockets;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class NodeSupervisorIntegrationTests
{
    [SkippableFact]
    public async Task StartsAndStopsRealNodeWithStrictHealthIdentity()
    {
        var node = RequireNode();
        using var fixture = new NodeFixture(node);
        await using var supervisor = fixture.CreateSupervisor();

        var running = await supervisor.StartAsync(fixture.CreateConfig());

        Assert.True(
            running.State == DesktopRuntimeState.Running,
            $"启动失败: state={running.State}, pid={running.Pid}, exitCode={running.ExitCode}, identity={running.RuntimeIdentity}, reason={running.FailureReason}");
        Assert.NotNull(running.Pid);
        Assert.StartsWith("desktop-", running.RuntimeIdentity, StringComparison.Ordinal);
        Assert.False(IsPortFree(fixture.Port));

        var stopped = await supervisor.StopAsync();

        Assert.Equal(DesktopRuntimeState.Stopped, stopped.State);
        Assert.True(IsPortFree(fixture.Port));
        Assert.False(IsProcessAlive(running.Pid!.Value));
    }

    [SkippableFact]
    public async Task MissingEntryAndOccupiedPortFailExplicitly()
    {
        var node = RequireNode();
        using var fixture = new NodeFixture(node, createScript: false);
        await using var missingEntry = fixture.CreateSupervisor();

        var missing = await missingEntry.StartAsync(fixture.CreateConfig());
        Assert.Equal(DesktopRuntimeState.Failed, missing.State);
        Assert.Contains("main.js", missing.FailureReason, StringComparison.OrdinalIgnoreCase);

        fixture.WriteScript();
        using var listener = new TcpListener(IPAddress.Any, fixture.Port);
        listener.Start();
        await using var occupied = fixture.CreateSupervisor();
        var blocked = await occupied.StartAsync(fixture.CreateConfig());

        Assert.Equal(DesktopRuntimeState.Failed, blocked.State);
        Assert.Contains("其他实例", blocked.FailureReason, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task TwentyConsecutiveCyclesLeaveNoProcessesOrPorts()
    {
        Skip.IfNot(string.Equals(Environment.GetEnvironmentVariable("DANMU_TEST_LONG_SMOKE"), "1", StringComparison.Ordinal),
            "设置 DANMU_TEST_LONG_SMOKE=1 才执行 20 次真实 Node 长冒烟；跳过不算 W-0003 Gate 通过。");
        var node = RequireNode();
        using var fixture = new NodeFixture(node);
        var pids = new List<long>();

        for (var iteration = 0; iteration < 20; iteration++)
        {
            await using var supervisor = fixture.CreateSupervisor();
            var running = await supervisor.StartAsync(fixture.CreateConfig());
            Assert.Equal(DesktopRuntimeState.Running, running.State);
            Assert.NotNull(running.Pid);
            pids.Add(running.Pid!.Value);
            var stopped = await supervisor.StopAsync($"cycle-{iteration + 1}");
            Assert.Equal(DesktopRuntimeState.Stopped, stopped.State);
            Assert.True(IsPortFree(fixture.Port));
        }

        Assert.All(pids, pid => Assert.False(IsProcessAlive(pid)));
    }

    private static string RequireNode()
    {
        var path = Environment.GetEnvironmentVariable("DANMU_TEST_NODE_EXE");
        Skip.If(string.IsNullOrWhiteSpace(path), "DANMU_TEST_NODE_EXE 未设置；真实 Node 集成测试跳过，不算 W-0003 Gate 通过。");
        Skip.IfNot(File.Exists(path), $"DANMU_TEST_NODE_EXE 指向不存在的文件: {path}");
        return Path.GetFullPath(path!);
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static bool IsProcessAlive(long pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(checked((int)pid));
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed class NodeFixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();

        public NodeFixture(string nodeExe, bool createScript = true)
        {
            NodeExe = nodeExe;
            Port = GetFreePort();
            ScriptDir = Path.Combine(_directory.Path, "含空格 Node Project");
            Directory.CreateDirectory(Path.Combine(ScriptDir, "danmu_api_stable"));
            File.WriteAllText(Path.Combine(ScriptDir, "danmu_api_stable", "worker.js"), "export {};", new System.Text.UTF8Encoding(false));
            if (createScript)
            {
                WriteScript();
            }
        }

        public string NodeExe { get; }
        public int Port { get; }
        public string ScriptDir { get; }

        public NodeSupervisor CreateSupervisor() => new(new RuntimeHealthClient(requestTimeout: TimeSpan.FromSeconds(1)), new WindowsProcessTerminator());

        public StartConfig CreateConfig() => new(
            NodeExe,
            ScriptDir,
            Port,
            IdentityFile: Path.Combine(_directory.Path, "identity", "instance-id"),
            StartupTimeout: TimeSpan.FromSeconds(10));

        public void WriteScript()
        {
            var script = """
                import http from 'node:http';
                import process from 'node:process';
                import path from 'node:path';
                import fs from 'node:fs';

                const envText = fs.readFileSync(path.join(process.cwd(), 'config', '.env'), 'utf8');
                const values = Object.fromEntries(envText.split(/\r?\n/).filter(Boolean).filter(x => !x.startsWith('#')).map(x => {
                  const i = x.indexOf('='); return [x.slice(0, i), x.slice(i + 1).replace(/^\"|\"$/g, '')];
                }));
                const port = Number(values.DANMU_API_PORT);
                const identity = process.env.DANMU_API_RUNTIME_IDENTITY;
                const home = path.resolve(process.cwd());
                const server = http.createServer((req, res) => {
                  if (req.url !== '/__health') { res.writeHead(404); res.end(); return; }
                  res.setHeader('content-type', 'application/json');
                  res.end(JSON.stringify({ ok: true, pid: process.pid, ports: { main: port }, runtimeIdentity: identity, cwd: home, envHome: home, resolvedHome: home }));
                });
                server.listen(port, '0.0.0.0');
                """;
            File.WriteAllText(Path.Combine(ScriptDir, "main.js"), script, new System.Text.UTF8Encoding(false));
            File.WriteAllText(Path.Combine(ScriptDir, "package.json"), "{\"type\":\"module\"}", new System.Text.UTF8Encoding(false));
        }

        public void Dispose() => _directory.Dispose();

        private static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}
