using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DanmuApi.App.Services;
using DanmuApi.Core.Frp;
using DanmuApi.Platform;
using DanmuApi.Platform.Frp;
using DanmuApi.Runtime;
using DanmuApi.Runtime.Frp;

namespace DanmuApi.Tests;

/// <summary>
/// 用真实的 frpc.exe / frps.exe 跑通穿透的集成测试。
///
/// 需要的环境变量：<c>DANMU_TEST_FRP_DIR</c> 指向同时含 frpc.exe 与 frps.exe 的目录
/// （与 <c>DANMU_TEST_NODE_EXE</c> 同一约定：没有就跳过，跳过不算通过）。
///
/// 这一组用例存在的理由：状态判定、公网地址、"到底通没通"这三件事全部依赖 frp 自己的管理接口，
/// 只靠假对象测不出协议细节（实测过的坑：未登录时 <c>/api/status</c> 返回 <c>{}</c> 而不是报错，
/// 代理失败时 <c>status</c> 是 "start error" 且 <c>err</c> 才有原因）。因此这里真起 frps、真连隧道、
/// 真收字节。
/// </summary>
public sealed class FrpSupervisorIntegrationTests
{
    private static string? FrpDirectory => Environment.GetEnvironmentVariable("DANMU_TEST_FRP_DIR");

    private static string RequireFrp(string fileName)
    {
        var directory = FrpDirectory;
        Skip.If(string.IsNullOrWhiteSpace(directory), "DANMU_TEST_FRP_DIR 未设置；跳过真实 frp 集成测试");
        var path = Path.Combine(directory!, fileName);
        Skip.IfNot(File.Exists(path), $"DANMU_TEST_FRP_DIR 下缺少 {fileName}: {path}");
        return path;
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RawSaveStartMultiProxyUserStatusAndStopUseNativeSourceWithoutVisualImport(bool toml)
    {
        var frpc = RequireFrp("frpc.exe");
        var frps = RequireFrp("frps.exe");
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(Path.Combine(directory.Path, "runtime"), Path.Combine(directory.Path, "settings"));
        Directory.CreateDirectory(Path.Combine(paths.FrpBinaryDirectory, "0.71.0"));
        File.Copy(frpc, Path.Combine(paths.FrpBinaryDirectory, "0.71.0", "frpc.exe"));
        File.Copy(frps, Path.Combine(paths.FrpBinaryDirectory, "0.71.0", "frps.exe"));
        await using var server = await FrpTestServer.StartAsync(frps, directory.Path, "integration-token");
        await using var backend = EchoBackend.Start();
        var remotePort = TestPorts.FreePort();
        var otherPort = TestPorts.FreePort();
        var adminPort = TestPorts.FreePort();
        var raw = toml ? $$$"""
            serverAddr = "127.0.0.1"
            serverPort = {{{server.BindPort}}}
            user = "native-user"
            auth.method = "token"
            auth.token = "integration-token"
            webServer.port = {{{adminPort}}}
            transport.tls.enable = true
            [[proxies]]
            name = "api"
            type = "tcp"
            localIP = "127.0.0.1"
            localPort = {{{backend.Port}}}
            remotePort = {{{remotePort}}}
            transport.useCompression = true
            [[proxies]]
            name = "other"
            type = "udp"
            localIP = "127.0.0.1"
            localPort = {{{backend.Port}}}
            remotePort = {{{otherPort}}}
            """ : $$$"""
            {"serverAddr":"127.0.0.1","serverPort":{{{server.BindPort}}},"user":"native-user",
            "auth":{"method":"token","token":"integration-token"},"webServer":{"port":{{{adminPort}}}},
            "transport":{"tls":{"enable":true}},"proxies":[
            {"name":"api","type":"tcp","localIP":"127.0.0.1","localPort":{{{backend.Port}}},"remotePort":{{{remotePort}}},"transport":{"useCompression":true}},
            {"name":"other","type":"udp","localIP":"127.0.0.1","localPort":{{{backend.Port}}},"remotePort":{{{otherPort}}}}]}
            """;
        var store = new FrpSettingsStore(new SettingsStore(paths.SettingsFile),
            new WindowsProtectedStringStore(paths.FrpTokenFile, "DanmuApi.Windows.FrpToken.v1"),
            new WindowsProtectedStringStore(paths.FrpAdminPasswordFile, "DanmuApi.Windows.FrpAdminPassword.v1"),
            new WindowsProtectedDocumentStore(paths.FrpConfigTextFile));
        var visual = FrpSettings.Default(backend.Port) with { Role = FrpRole.Server };
        store.Save(visual);
        store.SaveToken("dormant-visual-token");
        var installer = new FakeFrpInstaller { BinaryDirectory = paths.FrpBinaryDirectory, Version = "0.71.0" };
        var runtime = new FakeRuntimeController();
        runtime.SetState(DesktopRuntimeState.Running, backend.Port);
        var admin = new RecordingAdminClient(new FrpAdminClient(NewHttpClient()));
        await using var supervisor = new FrpSupervisor(admin, new WindowsProcessTerminator());
        await using var service = new FrpTunnelService(store, installer, supervisor, paths, new FrpTestDiagnostics(), runtime,
            () => backend.Port, new FrpNativeVerifier());
        var saved = await service.SaveTextAsync(raw);
        Assert.True(saved.Succeeded, saved.Message);
        Assert.Equal(FrpRole.Server, service.Settings.Role);
        Assert.Equal(FrpRole.Client, service.EffectiveSettings.Role);
        Assert.Equal("dormant-visual-token", store.ReadToken());
        Assert.Equal(raw, store.Read(backend.Port).Settings.RawConfig);
        Assert.Empty(Directory.GetFiles(paths.FrpConfigDirectory, "verify-*.json"));
        var started = await service.StartTunnelAsync();
        Assert.True(started.Succeeded, started.Message + "; observed admin names/types: " + string.Join(",", admin.LastStatus.Select(proxy => proxy.Name + "/" + proxy.Type)));
        Assert.Equal(FrpTunnelState.Running, service.Snapshot.State);
        Assert.Equal(["api", "other"], service.Snapshot.ProxyList.Select(proxy => proxy.Name));
        Assert.Equal(["api", "other"], service.Snapshot.ExpectedProxies.Select(proxy => proxy.AdminName));
        Assert.Equal(["***.api", "***.other"], service.Snapshot.ExpectedProxies.Select(proxy => proxy.Name));
        Assert.Empty(service.Snapshot.ActiveSettings!.Client.User);
        Assert.All(service.Snapshot.ProxyList, proxy => Assert.True(proxy.IsRunning));
        Assert.Equal($"127.0.0.1:{remotePort}", service.Snapshot.RemoteAddress);
        Assert.Equal("DANMU-OK:PING", await TunnelEchoAsync(remotePort));
        Assert.Empty(service.Snapshot.ActiveSettings!.RawConfig);
        Assert.DoesNotContain("integration-token", service.ReadLogTail(), StringComparison.Ordinal);
        Assert.DoesNotContain(store.EnsureAdminPassword(), service.ReadLogTail(), StringComparison.Ordinal);
        Assert.True((await service.SaveAsync(visual, null, false)).Succeeded);
        Assert.Equal(FrpConfigMode.Visual, service.Settings.ConfigMode);
        Assert.Equal(FrpRole.Client, service.EffectiveSettings.Role);
        Assert.Equal(FrpConfigMode.Text, service.Snapshot.ActiveSettings.ConfigMode);
        Assert.Equal(FrpTunnelState.Running, (await supervisor.RefreshAsync()).State);
        Assert.True((await service.StopTunnelAsync()).Succeeded);
        Assert.Equal(FrpRole.Server, service.EffectiveSettings.Role);
        AssertProcessGone(installer.ExecutablePath("0.71.0", false));
        Assert.True(PortAvailability.IsBindable(adminPort));
    }

    [SkippableFact]
    public async Task SwitchingFromLegacyTomlToRawJsonCleansOnlyTheExactOwnedOrphan()
    {
        var frpc = RequireFrp("frpc.exe");
        var frps = RequireFrp("frps.exe");
        using var directory = new TemporaryDirectory();
        await using var server = await FrpTestServer.StartAsync(frps, directory.Path, "integration-token");
        await using var backend = EchoBackend.Start();
        var adminPort = TestPorts.FreePort();
        var remotePort = TestPorts.FreePort();
        var oldPlan = WriteClientPlan(directory.Path, frpc, server.BindPort, "integration-token", backend.Port, remotePort, adminPort);
        var legacyPath = Path.Combine(directory.Path, "frpc.toml");
        File.Move(oldPlan.ConfigPath, legacyPath);
        oldPlan = oldPlan with { ConfigPath = legacyPath };
        using var orphan = Process.Start(new ProcessStartInfo(frpc)
        {
            Arguments = $"-c \"{legacyPath}\"", WorkingDirectory = directory.Path, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Cannot create owned legacy orphan");
        var stdout = orphan.StandardOutput.ReadToEndAsync();
        var stderr = orphan.StandardError.ReadToEndAsync();
        try
        {
            await WaitForAdminAsync(new FrpAdminClient(NewHttpClient()), adminPort, "danmu-api");
            var jsonPath = Path.Combine(directory.Path, "frpc.json");
            var native = FrpNativeConfig.Parse($$$"""
                {"serverAddr":"127.0.0.1","serverPort":{{{server.BindPort}}},"auth":{"method":"token","token":"integration-token"},
                "webServer":{"port":{{{adminPort}}}},"proxies":[{"name":"danmu-api","type":"tcp","localIP":"127.0.0.1","localPort":{{{backend.Port}}},"remotePort":{{{remotePort}}}}]}
                """);
            File.WriteAllText(jsonPath, native.CreateRuntimeConfig("admin", "local-admin-pass"));
            var plan = oldPlan with { ConfigPath = jsonPath, OwnedConfigPaths = [legacyPath, jsonPath],
                ExpectedProxies = native.Proxies, Secrets = native.Secrets, CoreServicePort = backend.Port };
            Assert.True((await new FrpNativeVerifier().VerifyAsync(plan)).Succeeded);
            await using var supervisor = new FrpSupervisor(new FrpAdminClient(NewHttpClient()), new WindowsProcessTerminator());
            Assert.Equal(FrpTunnelState.Running, (await supervisor.StartAsync(plan)).State);
            Assert.True(orphan.HasExited);
            Assert.Equal("DANMU-OK:PING", await TunnelEchoAsync(remotePort));
            Assert.Equal(FrpTunnelState.Stopped, (await supervisor.StopAsync()).State);
        }
        finally
        {
            if (!orphan.HasExited) orphan.Kill(entireProcessTree: true);
            await orphan.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
        }
    }

    [SkippableFact]
    public async Task NativeVerifierRejectsUnknownFieldsWithoutLeakingAnyConfigurationSnippet()
    {
        var frpc = RequireFrp("frpc.exe");
        using var directory = new TemporaryDirectory();
        var config = Path.Combine(directory.Path, "frpc.json");
        File.WriteAllText(config, """
            {"serverAddr":"127.0.0.1","proxies":[{"name":"api","type":"tcp","localPort":9321,"remotePort":19321}],
             "invalidNativeField":"private-unknown-marker","auth":{"method":"token","token":"test-token"}}
            """);
        var plan = new FrpRunPlan(FrpRole.Client, frpc, config, directory.Path, directory.Path, 7400, "admin", "admin-secret", "api",
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)) { Secrets = ["test-token"] };
        var result = await new FrpNativeVerifier().VerifyAsync(plan);
        Assert.False(result.Succeeded);
        Assert.Contains("exitCode=1", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("不支持的字段", result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("invalidNativeField", result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private-unknown-marker", result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("test-token", result.Diagnostic, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ClientTunnelForwardsTrafficAndReportsThePublicAddress()
    {
        var frpc = RequireFrp("frpc.exe");
        var frps = RequireFrp("frps.exe");
        using var directory = new TemporaryDirectory();
        await using var server = await FrpTestServer.StartAsync(frps, directory.Path, token: "integration-token");
        await using var backend = EchoBackend.Start();

        var remotePort = TestPorts.FreePort();
        var adminPort = TestPorts.FreePort();
        var plan = WriteClientPlan(
            directory.Path,
            frpc,
            server.BindPort,
            "integration-token",
            backend.Port,
            remotePort,
            adminPort);

        await using var supervisor = new FrpSupervisor(new FrpAdminClient(NewHttpClient()), new WindowsProcessTerminator());
        var snapshot = await supervisor.StartAsync(plan);

        Assert.Equal(FrpTunnelState.Running, snapshot.State);
        Assert.Equal(FrpTunnelState.Running, supervisor.Snapshot.State);
        // 公网地址必须来自 frp 返回的 remote_addr，不是本应用拼出来的。
        Assert.Equal($"127.0.0.1:{remotePort}", snapshot.RemoteAddress);
        var proxy = Assert.Single(snapshot.ProxyList);
        Assert.Equal("danmu-api", proxy.Name);
        Assert.True(proxy.IsRunning);

        // 真连一次隧道，确认字节确实被转到了本地后端。
        Assert.Equal("DANMU-OK:PING", await TunnelEchoAsync(remotePort));

        var stopped = await supervisor.StopAsync();
        Assert.Equal(FrpTunnelState.Stopped, stopped.State);
        Assert.Null(stopped.Diagnostic);
        AssertProcessGone(frpc);
        Assert.True(PortAvailability.IsBindable(adminPort), $"停止后本地状态端口 {adminPort} 仍被占用");
    }

    [SkippableFact]
    public async Task ProxyStartErrorSurfacesFrpsOwnReason()
    {
        var frpc = RequireFrp("frpc.exe");
        var frps = RequireFrp("frps.exe");
        using var directory = new TemporaryDirectory();
        await using var server = await FrpTestServer.StartAsync(frps, directory.Path, token: "integration-token");
        await using var backend = EchoBackend.Start();

        // 冲突必须由"另一个客户端已经拿到这个公网端口"来制造。
        // 实测：用本机 bind 占住端口是**造不出**这个冲突的 —— frps 的代理监听带 SO_REUSEADDR，
        // 即使本机已有独占 bind，它照样监听成功并把代理报成 running（Windows 的 SO_REUSEADDR 语义）。
        var contestedPort = TestPorts.FreePort();
        await using var owner = new FrpSupervisor(new FrpAdminClient(NewHttpClient()), new WindowsProcessTerminator());
        var ownerSnapshot = await owner.StartAsync(WriteClientPlan(
            directory.Path, frpc, server.BindPort, "integration-token", backend.Port, contestedPort, TestPorts.FreePort(),
            proxyName: "port-owner"));
        Assert.Equal(FrpTunnelState.Running, ownerSnapshot.State);
        var ownerProcessCount = WaitForProcessCount(frpc, expected: 1);

        await using var second = new FrpSupervisor(new FrpAdminClient(NewHttpClient()), new WindowsProcessTerminator());
        var snapshot = await second.StartAsync(WriteClientPlan(
            directory.Path, frpc, server.BindPort, "integration-token", backend.Port, contestedPort, TestPorts.FreePort()));

        Assert.Equal(FrpTunnelState.Failed, snapshot.State);
        Assert.Contains("port already used", snapshot.Diagnostic!, StringComparison.Ordinal);
        Assert.Contains("danmu-api", snapshot.Diagnostic!, StringComparison.Ordinal);
        // 失败后必须已经把这个 frpc 收掉。此时先到者的 frpc 仍在跑（同一个可执行文件），
        // 因此按"进程数回到启动第二个之前的数量"来判定，而不是要求一个 frpc 都不剩。
        Assert.Equal(ownerProcessCount, WaitForProcessCount(frpc, ownerProcessCount));
        // 后来者的失败不能把先到者的链路带下水。
        Assert.Equal(FrpTunnelState.Running, (await owner.RefreshAsync()).State);
        Assert.Equal(FrpTunnelState.Stopped, (await owner.StopAsync()).State);
    }

    [SkippableFact]
    public async Task UnableToReachServerFailsWithTheConnectErrorAndLogTail()
    {
        var frpc = RequireFrp("frpc.exe");
        using var directory = new TemporaryDirectory();
        // 指向一个没有任何监听的端口：frpc 会不断重试，启动超时后必须给出明确原因。
        var plan = WriteClientPlan(
            directory.Path,
            frpc,
            serverPort: TestPorts.FreePort(),
            token: "integration-token",
            backendPort: TestPorts.FreePort(),
            remotePort: TestPorts.FreePort(),
            adminPort: TestPorts.FreePort()) with
        {
            StartupTimeout = TimeSpan.FromSeconds(6),
        };

        await using var supervisor = new FrpSupervisor(new FrpAdminClient(NewHttpClient()), new WindowsProcessTerminator());
        var snapshot = await supervisor.StartAsync(plan);

        Assert.Equal(FrpTunnelState.Failed, snapshot.State);
        Assert.Contains("没有确认穿透成功", snapshot.Diagnostic!, StringComparison.Ordinal);
        Assert.Contains("尚未与服务器建立连接", snapshot.Diagnostic!, StringComparison.Ordinal);
        Assert.Contains("connect to server error", snapshot.Diagnostic!, StringComparison.Ordinal);
        AssertProcessGone(frpc);
    }

    [SkippableFact]
    public async Task ServerRoleReportsListeningStateFromFrpsAdminApi()
    {
        var frps = RequireFrp("frps.exe");
        using var directory = new TemporaryDirectory();
        var bindPort = TestPorts.FreePort();
        var adminPort = TestPorts.FreePort();
        var configPath = WriteServerConfig(directory.Path, bindPort, adminPort, "integration-token");

        var plan = new FrpRunPlan(
            FrpRole.Server,
            frps,
            configPath,
            directory.Path,
            directory.Path,
            adminPort,
            "admin",
            "local-admin-pass",
            "server",
            StartupTimeout: TimeSpan.FromSeconds(20),
            ShutdownTimeout: TimeSpan.FromSeconds(10));

        await using var supervisor = new FrpSupervisor(new FrpAdminClient(NewHttpClient()), new WindowsProcessTerminator());
        var snapshot = await supervisor.StartAsync(plan);

        Assert.Equal(FrpTunnelState.Running, snapshot.State);
        Assert.NotNull(snapshot.Server);
        Assert.Equal(bindPort, snapshot.Server!.BindPort);
        Assert.Equal(0, snapshot.Server.ClientCounts);

        var stopped = await supervisor.StopAsync();
        Assert.Equal(FrpTunnelState.Stopped, stopped.State);
        AssertProcessGone(frps);
        Assert.True(PortAvailability.IsBindable(bindPort), $"停止后穿透端口 {bindPort} 仍被占用");
    }

    [SkippableFact]
    public async Task RepeatedStartStopLeavesNoOrphanFrpcProcess()
    {
        var frpc = RequireFrp("frpc.exe");
        var frps = RequireFrp("frps.exe");
        using var directory = new TemporaryDirectory();
        await using var server = await FrpTestServer.StartAsync(frps, directory.Path, token: "integration-token");
        await using var backend = EchoBackend.Start();

        for (var cycle = 0; cycle < 3; cycle++)
        {
            var plan = WriteClientPlan(
                directory.Path,
                frpc,
                server.BindPort,
                "integration-token",
                backend.Port,
                TestPorts.FreePort(),
                TestPorts.FreePort());

            await using var supervisor = new FrpSupervisor(new FrpAdminClient(NewHttpClient()), new WindowsProcessTerminator());
            var started = await supervisor.StartAsync(plan);
            Assert.Equal(FrpTunnelState.Running, started.State);
            var stopped = await supervisor.StopAsync();
            Assert.Equal(FrpTunnelState.Stopped, stopped.State);
            AssertProcessGone(frpc);
        }
    }

    [SkippableFact]
    public void ExportedJsonConfigIsAcceptedByTheOfficialFrpc()
    {
        // 导出的 JSON 是要给用户复制走、甚至改名 frpc.json 直接交给官方 frpc 用的，
        // 因此这里用真 frp 二进制的 verify 子命令把关（实测 0.71.0：exit 0 + "syntax is ok"）。
        // 客户端文档交给 frpc、服务端文档交给 frps：两者校验各自的 schema，用错二进制会被拒。
        var frpc = RequireFrp("frpc.exe");
        var frps = RequireFrp("frps.exe");
        using var directory = new TemporaryDirectory();

        var settings = FrpSettings.Default(9321) with
        {
            Client = FrpSettings.Default(9321).Client with
            {
                ServerAddress = "frp.example.com",
                ServerPort = 7000,
                // 服务商面板普遍要求 user：写错这个键名 frpc 会直接以 unknown field 拒绝，所以拿真二进制把关。
                User = "panel-user",
                LocalPort = 9321,
                RemotePort = 19321,
                UseCompression = true,
            },
        };

        var clientConfig = Path.Combine(directory.Path, "frpc.json");
        File.WriteAllText(clientConfig, FrpConfigJson.Export(settings, "server-token"), new UTF8Encoding(false));
        var clientVerify = RunVerify(frpc, clientConfig);
        Assert.Equal(0, clientVerify.ExitCode);
        Assert.Contains("syntax is ok", clientVerify.Output, StringComparison.Ordinal);

        var clientToml = Path.Combine(directory.Path, "frpc.toml");
        File.WriteAllText(
            clientToml,
            FrpConfigWriter.WriteClient(settings.Client, "server-token", "admin", "local-admin-pass"),
            new UTF8Encoding(false));
        Assert.Contains("user = \"panel-user\"", File.ReadAllText(clientToml), StringComparison.Ordinal);
        var clientTomlVerify = RunVerify(frpc, clientToml);
        Assert.Equal(0, clientTomlVerify.ExitCode);
        Assert.Contains("syntax is ok", clientTomlVerify.Output, StringComparison.Ordinal);

        var serverSettings = FrpSettings.Default(9321) with
        {
            Role = FrpRole.Server,
            Server = new FrpServerSettings(BindPort: 7200, VhostHttpPort: 8080, SubdomainHost: "example.com", AdminPort: 7501),
        };
        var serverConfig = Path.Combine(directory.Path, "frps.json");
        File.WriteAllText(serverConfig, FrpConfigJson.Export(serverSettings, "server-token"), new UTF8Encoding(false));
        var serverVerify = RunVerify(frps, serverConfig);
        Assert.Equal(0, serverVerify.ExitCode);
        Assert.Contains("syntax is ok", serverVerify.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// 端到端：用户从服务商面板复制回来的 frpc.toml → 粘贴导入 → 我们导出的配置 → 官方 frpc 接受。
    /// 这条链路上任何一环把字段吃掉（例如 user 或代理级 transport）都会在这里被真二进制拦下。
    /// </summary>
    [SkippableFact]
    public void ProviderPastedTomlRoundTripsIntoAConfigTheOfficialFrpcAccepts()
    {
        var frpc = RequireFrp("frpc.exe");
        using var directory = new TemporaryDirectory();
        var imported = FrpConfigText.Import(FrpProviderSample.ClientToml, FrpSettings.Default(9321));
        Assert.True(imported.Succeeded, string.Join("；", imported.Problems));

        var config = Path.Combine(directory.Path, "frpc.json");
        File.WriteAllText(
            config,
            FrpConfigJson.Export(imported.Settings!, imported.Token ?? string.Empty),
            new UTF8Encoding(false));

        var result = RunVerify(frpc, config);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("syntax is ok", result.Output, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData(FrpProxyKind.Http)]
    [InlineData(FrpProxyKind.Https)]
    public void HttpAndHttpsTransportOptionsAreAcceptedByOfficialFrpcInJsonAndToml(FrpProxyKind kind)
    {
        var frpc = RequireFrp("frpc.exe");
        using var directory = new TemporaryDirectory();
        var settings = FrpSettings.Default(9321) with
        {
            Client = FrpSettings.Default(9321).Client with
            {
                ServerAddress = "frp.example.invalid",
                ProxyKind = kind,
                CustomDomains = ["danmu.example.invalid"],
                UseCompression = true,
                UseEncryption = true,
            },
        };
        var json = Path.Combine(directory.Path, "frpc.json");
        var toml = Path.Combine(directory.Path, "frpc.toml");
        File.WriteAllText(json, FrpConfigJson.Export(settings, "test-token"), new UTF8Encoding(false));
        File.WriteAllText(toml, FrpConfigWriter.WriteClient(settings.Client, "test-token", "admin", "test-admin-password"), new UTF8Encoding(false));
        Assert.Contains("useCompression", File.ReadAllText(json), StringComparison.Ordinal);
        Assert.Contains("useEncryption", File.ReadAllText(json), StringComparison.Ordinal);
        Assert.Contains("transport.useCompression = true", File.ReadAllText(toml), StringComparison.Ordinal);
        Assert.Contains("transport.useEncryption = true", File.ReadAllText(toml), StringComparison.Ordinal);
        foreach (var path in new[] { json, toml })
        {
            var result = RunVerify(frpc, path);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("syntax is ok", result.Output, StringComparison.Ordinal);
        }
    }

    [SkippableFact]
    public async Task StartCleansUpAnOrphanFrpcLeftByAPreviousRun()
    {
        // 真机事故：应用崩溃会留下还在跑的 frpc（它占着本地状态端口），下次启动时新进程绑不上该端口
        // 只能退出，而状态查询读到的却是那个孤儿的管理接口 —— 界面会先报"成功"再翻脸。
        // 这里手工起一个"孤儿"（与正式启动同样的配置与命令行），再让监督器启动，期望它把孤儿收掉并真正跑起来。
        var frpc = RequireFrp("frpc.exe");
        var frps = RequireFrp("frps.exe");
        using var directory = new TemporaryDirectory();
        await using var server = await FrpTestServer.StartAsync(frps, directory.Path, token: "integration-token");
        await using var backend = EchoBackend.Start();

        var remotePort = TestPorts.FreePort();
        var adminPort = TestPorts.FreePort();
        var plan = WriteClientPlan(
            directory.Path, frpc, server.BindPort, "integration-token", backend.Port, remotePort, adminPort);

        // 孤儿：直接按 plan 的配置起一个 frpc，等它把状态端口占住。
        using var orphan = Process.Start(new ProcessStartInfo
        {
            FileName = plan.ExecutablePath,
            Arguments = $"-c \"{plan.ConfigPath}\"",
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        }) ?? throw new InvalidOperationException("无法启动孤儿 frpc");
        _ = orphan.StandardOutput.ReadToEndAsync();
        _ = orphan.StandardError.ReadToEndAsync();
        var adminClient = new FrpAdminClient(NewHttpClient());
        await WaitForAdminAsync(adminClient, adminPort, plan.ProxyName);
        Assert.Equal(PortAvailabilityState.Listening, PortAvailability.Probe(adminPort));

        await using var supervisor = new FrpSupervisor(new FrpAdminClient(NewHttpClient()), new WindowsProcessTerminator());
        var snapshot = await supervisor.StartAsync(plan);

        Assert.Equal(FrpTunnelState.Running, snapshot.State);
        Assert.Equal($"127.0.0.1:{remotePort}", snapshot.RemoteAddress);
        // 孤儿必须已经被收掉：现在的 frpc 是监督器自己起的那个。
        Assert.NotEqual(orphan.Id, snapshot.Pid);
        Assert.True(orphan.HasExited, "遗留的 frpc 没有被清理");
        Assert.Equal("DANMU-OK:PING", await TunnelEchoAsync(remotePort));

        Assert.Equal(FrpTunnelState.Stopped, (await supervisor.StopAsync()).State);
        AssertProcessGone(frpc);
    }

    private static async Task WaitForAdminAsync(FrpAdminClient client, int adminPort, string proxyName)
    {
        // 孤儿也需要一点时间登录并让管理接口可用；就绪判据是"接口能读出代理列表"。
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                var proxies = await client.ReadClientStatusAsync(adminPort, "admin", "local-admin-pass");
                if (proxies.Any(proxy => string.Equals(proxy.Name, proxyName, StringComparison.Ordinal)))
                {
                    return;
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay(200);
        }

        Assert.Fail("孤儿 frpc 的管理接口始终没有就绪");
    }

    private static (int ExitCode, string Output) RunVerify(string executable, string configPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = $"verify -c \"{configPath}\"",
            WorkingDirectory = Path.GetDirectoryName(configPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        }) ?? throw new InvalidOperationException($"无法启动 {Path.GetFileName(executable)} verify");

        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "frp verify 超时");
        return (process.ExitCode, output);
    }

    private static FrpRunPlan WriteClientPlan(
        string directory,
        string frpc,
        int serverPort,
        string token,
        int backendPort,
        int remotePort,
        int adminPort,
        string proxyName = "danmu-api")
    {
        var client = new FrpClientSettings(
            "127.0.0.1",
            serverPort,
            User: string.Empty,
            proxyName,
            FrpProxyKind.Tcp,
            "127.0.0.1",
            backendPort,
            remotePort,
            [],
            UseEncryption: false,
            UseCompression: false,
            TransportTls: true,
            adminPort);
        Assert.Empty(FrpSettingsValidation.ValidateClient(client));

        var configPath = Path.Combine(directory, $"frpc-{Guid.NewGuid():N}.toml");
        File.WriteAllText(
            configPath,
            FrpConfigWriter.WriteClient(client, token, "admin", "local-admin-pass"),
            new UTF8Encoding(false));

        return new FrpRunPlan(
            FrpRole.Client,
            frpc,
            configPath,
            directory,
            directory,
            adminPort,
            "admin",
            "local-admin-pass",
            proxyName,
            StartupTimeout: TimeSpan.FromSeconds(20),
            ShutdownTimeout: TimeSpan.FromSeconds(10));
    }

    private static string WriteServerConfig(string directory, int bindPort, int adminPort, string token)
    {
        var configPath = Path.Combine(directory, $"frps-{Guid.NewGuid():N}.toml");
        File.WriteAllText(
            configPath,
            FrpConfigWriter.WriteServer(new FrpServerSettings(bindPort, 0, string.Empty, adminPort), token, "admin", "local-admin-pass"),
            new UTF8Encoding(false));
        return configPath;
    }

    private sealed class RecordingAdminClient(IFrpAdminClient inner) : IFrpAdminClient
    {
        public IReadOnlyList<FrpProxyStatus> LastStatus { get; private set; } = [];
        public async Task<IReadOnlyList<FrpProxyStatus>> ReadClientStatusAsync(int port, string user, string password, CancellationToken cancellationToken = default)
        {
            LastStatus = await inner.ReadClientStatusAsync(port, user, password, cancellationToken);
            return LastStatus;
        }
        public Task<FrpServerInfo> ReadServerInfoAsync(int port, string user, string password, CancellationToken cancellationToken = default) =>
            inner.ReadServerInfoAsync(port, user, password, cancellationToken);
    }

    private static HttpClient NewHttpClient() => new() { Timeout = TimeSpan.FromSeconds(5) };

    private static async Task<string> TunnelEchoAsync(int port)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("PING"));
        await stream.FlushAsync();
        var buffer = new byte[64];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var read = await stream.ReadAsync(buffer, timeout.Token);
        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    /// <summary>断言某个可执行文件路径下不再有活着的进程（按 PID 无法判断孤儿，直接按路径查）。</summary>
    private static void AssertProcessGone(string executablePath)
    {
        var expected = Path.GetFullPath(executablePath);
        var remaining = WaitForProcessCount(expected, 0);
        if (remaining != 0)
        {
            Assert.Fail($"仍有 {remaining} 个 {Path.GetFileName(expected)} 进程在运行（未清理干净）");
        }
    }

    /// <summary>轮询等待指定可执行文件的进程数落到期望值，返回最终数量（等待超时则返回实际值）。</summary>
    private static int WaitForProcessCount(string executablePath, int expected)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var count = CountProcesses(executablePath);
            if (count == expected)
            {
                return count;
            }

            Thread.Sleep(100);
        }

        return CountProcesses(executablePath);
    }

    private static int CountProcesses(string executablePath)
    {
        var expected = Path.GetFullPath(executablePath);
        var count = 0;
        foreach (var process in Process.GetProcesses())
        {
            if (MatchesPath(process, expected))
            {
                count++;
            }
        }

        return count;
    }

    private static bool MatchesPath(Process process, string expected)
    {
        try
        {
            var path = process.MainModule?.FileName;
            return path is not null && string.Equals(Path.GetFullPath(path), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>测试用 frps：用真实二进制起一份，等管理接口可用后才算就绪。</summary>
    private sealed class FrpTestServer : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly string _directory;

        private FrpTestServer(Process process, string directory, int bindPort, int adminPort)
        {
            _process = process;
            _directory = directory;
            BindPort = bindPort;
            AdminPort = adminPort;
        }

        public int BindPort { get; }

        public int AdminPort { get; }

        public static async Task<FrpTestServer> StartAsync(string frpsPath, string directory, string token)
        {
            var bindPort = TestPorts.FreePort();
            var adminPort = TestPorts.FreePort();
            var configPath = WriteServerConfig(directory, bindPort, adminPort, token);
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = frpsPath,
                Arguments = $"-c \"{configPath}\"",
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            }) ?? throw new InvalidOperationException("无法启动测试用 frps.exe");

            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();

            var server = new FrpTestServer(process, directory, bindPort, adminPort);
            var client = new FrpAdminClient(new HttpClient { Timeout = TimeSpan.FromSeconds(2) });
            for (var attempt = 0; attempt < 60; attempt++)
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException($"测试用 frps 提前退出，exitCode={process.ExitCode}");
                }

                try
                {
                    await client.ReadServerInfoAsync(adminPort, "admin", "local-admin-pass");
                    return server;
                }
                catch (IOException)
                {
                    await Task.Delay(200);
                }
            }

            await server.DisposeAsync();
            throw new InvalidOperationException($"测试用 frps 在 {directory} 下启动超时，管理接口 {adminPort} 始终不可用");
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }

            _process.Dispose();
        }
    }

    /// <summary>被穿透的"本机服务"：回显 DANMU-OK:&lt;payload&gt;，用来证明隧道真的转发到了本地端口。</summary>
    private sealed class EchoBackend : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _loop;

        private EchoBackend(TcpListener listener, int port)
        {
            _listener = listener;
            Port = port;
            _loop = AcceptLoopAsync();
        }

        public int Port { get; }

        public static EchoBackend Start()
        {
            var listener = TestPorts.Listen(out var port);
            return new EchoBackend(listener, port);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_lifetime.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                }
                catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                _ = HandleAsync(client);
            }
        }

        private static async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                await using var stream = client.GetStream();
                var buffer = new byte[256];
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    return;
                }

                var reply = Encoding.ASCII.GetBytes($"DANMU-OK:{Encoding.ASCII.GetString(buffer, 0, read)}");
                await stream.WriteAsync(reply);
                await stream.FlushAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            _listener.Stop();
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }

            _lifetime.Dispose();
        }
    }
}
