using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class OutboundSupervisorIntegrationTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task SupervisorStopTerminatesTheRealOutboundHelperAndReleasesItsPort()
    {
        var inputs = RequireInputs();
        using var fixture = new OutboundFixture(inputs.Node, inputs.Helper, inputs.HostSource);
        await using var supervisor = fixture.CreateSupervisor();
        var running = await supervisor.StartAsync(fixture.CreateConfig());
        Assert.True(running.State == DesktopRuntimeState.Running, running.FailureReason);
        var session = fixture.ReadSession();
        Assert.Equal(running.Pid, session.NodePid);
        Assert.Equal(running.RuntimeIdentity, session.Identity);
        Assert.True(IsAlive(session.HelperPid));
        using var originalHelper = PinnedHelper.Capture(session.HelperPid);
        ObserveHelperPort("beforeStop", session, originalHelper);

        var stopped = await supervisor.StopAsync("outbound-integration");

        Assert.Equal(DesktopRuntimeState.Stopped, stopped.State);
        await AssertExitedAsync(session.HelperPid);
        AssertPortReleased(session, originalHelper);
    }

    [SkippableFact]
    public async Task AbruptNodeExitClosesHelperStdinAndCleansItsProcess()
    {
        var inputs = RequireInputs();
        using var fixture = new OutboundFixture(inputs.Node, inputs.Helper, inputs.HostSource);
        await using var supervisor = fixture.CreateSupervisor();
        var running = await supervisor.StartAsync(fixture.CreateConfig());
        Assert.True(running.State == DesktopRuntimeState.Running, running.FailureReason);
        var session = fixture.ReadSession();
        using var originalHelper = PinnedHelper.Capture(session.HelperPid);
        ObserveHelperPort("beforeAbruptNodeExit", session, originalHelper);
        using var node = Process.GetProcessById(checked((int)running.Pid!.Value));
        node.Kill(entireProcessTree: false);
        await node.WaitForExitAsync();

        await AssertExitedAsync(session.HelperPid);
        Assert.NotNull(supervisor.LivenessFailure());
        AssertPortReleased(session, originalHelper);
    }

    private static (string Node, string Helper, string HostSource) RequireInputs()
    {
        var node = Environment.GetEnvironmentVariable("DANMU_TEST_NODE_EXE");
        var helper = Environment.GetEnvironmentVariable("DANMU_TEST_OUTBOUND_EXE");
        var host = Environment.GetEnvironmentVariable("DANMU_TEST_NODE_HOST_SOURCE");
        Skip.If(string.IsNullOrWhiteSpace(node) || string.IsNullOrWhiteSpace(helper) || string.IsNullOrWhiteSpace(host),
            "设置 DANMU_TEST_NODE_EXE、DANMU_TEST_OUTBOUND_EXE、DANMU_TEST_NODE_HOST_SOURCE 才运行真实 helper 监督测试；跳过不算验收通过。");
        Skip.IfNot(File.Exists(node) && File.Exists(helper) && File.Exists(Path.Combine(host!, "app-outbound-runtime.js")),
            "真实 Node/helper/宿主源码输入不存在；跳过不算验收通过。");
        return (Path.GetFullPath(node!), Path.GetFullPath(helper!), Path.GetFullPath(host!));
    }

    private static IPEndPoint[] IPGlobalListeners() => System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();

    private void AssertPortReleased(Session session, PinnedHelper originalHelper)
    {
        // 原验收谓词与预算不变：即使是其它 owner，也不能静默忽略仍在监听的端口。
        var conflicts = IPGlobalListeners().Where(endpoint => endpoint.Port == session.Port).ToArray();
        var evidence = ObserveHelperPort(conflicts.Length == 0 ? "afterStop" : "portReleaseFailure", session, originalHelper);
        Assert.True(originalHelper.Process.HasExited, $"原始 helper 未退出；{evidence}");
        Assert.True(conflicts.Length == 0,
            $"Node PID={session.NodePid}, helper PID={session.HelperPid}, helper alive={IsAlive(session.HelperPid)}, " +
            $"helper port={session.Port}; matching listeners: {string.Join(", ", conflicts.Select(endpoint => endpoint.ToString()))}; {evidence}");
    }

    private string ObserveHelperPort(string phase, Session session, PinnedHelper originalHelper)
    {
        // 两次表读取都是即时只读观察，不重试、不把第二次空表当成第一条冲突已不存在的证明。
        var evidence = $"phase={phase} helperPid={originalHelper.Pid} helperName={originalHelper.Name} "
            + $"helperStartedUtc={originalHelper.StartedUtc:O} originalHelperExited={originalHelper.Process.HasExited} "
            + $"port={session.Port} ownerTable={ReadTcpOwnerEvidence(session.Port)}";
        output.WriteLine(evidence);
        return evidence;
    }

    private static string ReadTcpOwnerEvidence(int port)
    {
        try
        {
            var rows = ReadNativeTcpRows(port, ipv6: false).Concat(ReadNativeTcpRows(port, ipv6: true)).ToArray();
            return rows.Length == 0 ? "noMatchingRows" : string.Join(" | ", rows.Select(row =>
                $"local={row.Local} state={row.State} ownerPid={row.OwnerPid}"));
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            // 诊断查询失败同样显式记录，绝不把无法读取 owner 伪装成空表。
            return $"queryFailed={error.GetType().Name} HResult=0x{error.HResult:X8} detail={error.Message}";
        }
    }

    private sealed record TcpOwnerRow(IPEndPoint Local, int State, int OwnerPid);

    private static IReadOnlyList<TcpOwnerRow> ReadNativeTcpRows(int port, bool ipv6)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("native TCP owner diagnostics require Windows");
        const uint insufficientBuffer = 122;
        const int ownerPidAll = 5; // TCP_TABLE_OWNER_PID_ALL，既记录监听也记录临时连接/TIME_WAIT，便于分类。
        var family = ipv6 ? 23 : 2;
        var length = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref length, false, family, ownerPidAll, 0);
        if (result is not (0 or insufficientBuffer)) throw new IOException($"GetExtendedTcpTable size family={family} code={result}");
        if (length < sizeof(int)) throw new IOException($"GetExtendedTcpTable invalid size family={family} bytes={length}");
        var memory = Marshal.AllocHGlobal(length);
        try
        {
            var capacity = length;
            result = GetExtendedTcpTable(memory, ref length, false, family, ownerPidAll, 0);
            if (result != 0) throw new IOException($"GetExtendedTcpTable rows family={family} code={result} capacity={capacity} requested={length}");
            var count = Marshal.ReadInt32(memory);
            var rowSize = ipv6 ? 56 : 24;
            if (count < 0 || count > (capacity - sizeof(int)) / rowSize)
                throw new IOException($"GetExtendedTcpTable invalid row count family={family} count={count}");
            var rows = new List<TcpOwnerRow>();
            for (var index = 0; index < count; index++)
            {
                var row = IntPtr.Add(memory, sizeof(int) + index * rowSize);
                var portOffset = ipv6 ? 20 : 8;
                var localPort = (Marshal.ReadByte(row, portOffset) << 8) | Marshal.ReadByte(row, portOffset + 1);
                if (localPort != port) continue;
                var bytes = new byte[ipv6 ? 16 : 4];
                Marshal.Copy(IntPtr.Add(row, ipv6 ? 0 : 4), bytes, 0, bytes.Length);
                var address = ipv6 ? new IPAddress(bytes, unchecked((uint)Marshal.ReadInt32(row, 16))) : new IPAddress(bytes);
                rows.Add(new(new IPEndPoint(address, localPort), Marshal.ReadInt32(row, ipv6 ? 48 : 0),
                    Marshal.ReadInt32(row, ipv6 ? 52 : 20)));
            }
            return rows;
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily, int tableClass, uint reserved);

    private sealed record PinnedHelper(Process Process, int Pid, string Name, DateTime StartedUtc) : IDisposable
    {
        public static PinnedHelper Capture(int pid)
        {
            var process = Process.GetProcessById(pid);
            try
            {
                Assert.False(process.SafeHandle.IsInvalid);
                Assert.False(process.HasExited);
                return new(process, pid, process.ProcessName, process.StartTime.ToUniversalTime());
            }
            catch { process.Dispose(); throw; }
        }
        public void Dispose() => Process.Dispose();
    }

    private static bool IsAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task AssertExitedAsync(int pid)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (IsAlive(pid) && !timeout.IsCancellationRequested) await Task.Delay(50);
        Assert.False(IsAlive(pid), $"网络 helper {pid} 在 Node 退出后仍未清理。");
    }

    private sealed class OutboundFixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        private int? _helperPid;

        public OutboundFixture(string node, string helper, string hostSource)
        {
            Paths = new AppPaths(Path.Combine(_directory.Path, "含空格 & 网络环境"), Path.Combine(_directory.Path, "settings"));
            Directory.CreateDirectory(Paths.NodeProjectDirectory);
            Node = Path.Combine(Paths.RuntimeDirectory, "node.exe");
            File.Copy(node, Node);
            foreach (var file in Directory.EnumerateFiles(hostSource, "app-outbound-*.js"))
                File.Copy(file, Path.Combine(Paths.NodeProjectDirectory, Path.GetFileName(file)));
            Directory.CreateDirectory(Path.Combine(Paths.NodeProjectDirectory, "outbound"));
            File.Copy(helper, Path.Combine(Paths.NodeProjectDirectory, "outbound", "danmu-outbound.exe"));
            Directory.CreateDirectory(Path.Combine(Paths.NodeProjectDirectory, "danmu_api_stable"));
            File.WriteAllText(Path.Combine(Paths.NodeProjectDirectory, "danmu_api_stable", "worker.js"), "export {};", new UTF8Encoding(false));
            new OutboundSettingsStore(Paths).Write(OutboundSettings.Default with { Enabled = true });
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            File.WriteAllText(Path.Combine(Paths.NodeProjectDirectory, "main.js"), """
                const fs=require('node:fs');
                const path=require('node:path');
                const http=require('node:http');
                const {createAppOutboundRuntime}=require('./app-outbound-runtime.js');
                const values=Object.fromEntries(fs.readFileSync(path.join(__dirname,'config','.env'),'utf8')
                    .split(/\r?\n/).filter(x=>x&&!x.startsWith('#')).map(x=>{const i=x.indexOf('=');return[x.slice(0,i),x.slice(i+1).replace(/^"|"$/g,'')]}));
                const runtime=createAppOutboundRuntime();
                (async()=>{
                    await runtime.start();
                    const state=runtime.safeSnapshot();
                    if(state.status!=='ready')throw new Error('Outbound helper failed: '+state.reason);
                    const server=http.createServer((req,res)=>{
                        if(req.url!=='/__health'){res.writeHead(404);res.end();return;}
                        res.setHeader('content-type','application/json');
                        res.end(JSON.stringify({ok:true,pid:process.pid,runtimeIdentity:process.env.DANMU_API_RUNTIME_IDENTITY,
                            ports:{main:Number(values.DANMU_API_PORT)},envHome:__dirname,resolvedHome:__dirname,cwd:process.cwd()}));
                    });
                    server.listen(Number(values.DANMU_API_PORT),'0.0.0.0');
                })().catch(error=>{console.error(error.stack);process.exitCode=1;});
                """, new UTF8Encoding(false));
        }

        public string Node { get; }
        public AppPaths Paths { get; }
        public int Port { get; }
        public StartConfig CreateConfig() => new(Node, Paths.NodeProjectDirectory, Port,
            IdentityFile: Paths.IdentityFile, StartupTimeout: TimeSpan.FromSeconds(20));
        public NodeSupervisor CreateSupervisor() => new(new RuntimeHealthClient(requestTimeout: TimeSpan.FromSeconds(2)), new WindowsProcessTerminator());
        public Session ReadSession()
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Paths.NodeProjectDirectory, "config", "outbound", "session.json")));
            var root = document.RootElement;
            _helperPid = root.GetProperty("helperPid").GetInt32();
            return new(root.GetProperty("nodePid").GetInt64(), _helperPid.Value,
                root.GetProperty("runtimeIdentity").GetString()!, new Uri(root.GetProperty("endpoint").GetString()!).Port);
        }

        public void Dispose()
        {
            if (_helperPid is { } pid && IsAlive(pid))
            {
                using var process = Process.GetProcessById(pid);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            _directory.Dispose();
        }
    }

    private sealed record Session(long NodePid, int HelperPid, string Identity, int Port);
}
