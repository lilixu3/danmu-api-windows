using DanmuApi.App.Services;
using DanmuApi.Core;
using DanmuApi.Core.Frp;
using DanmuApi.Platform;
using DanmuApi.Platform.Frp;
using DanmuApi.Runtime;
using DanmuApi.Runtime.Frp;

namespace DanmuApi.Tests;

/// <summary>
/// 编排层测试：配置落盘 → 生成 frp 配置 → 交给监督器 → 把结果如实回报。
/// 监督器与安装器都用替身，重点验证"什么时候该拒绝启动、拒绝时说了什么、生成的文件对不对"。
/// </summary>
public sealed class FrpTunnelServiceTests
{
    private static FrpTestHarness Create(int localPort = 9321) => FrpTestHarness.Create(localPort);

    /// <summary>等一个条件成立（生命周期联动是异步的）；超时即失败，让断言报出真实状态。</summary>
    private static async Task WaitForAsync(Func<bool> condition, int milliseconds = 5000)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(milliseconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }
    }

    private static FrpSettings ValidClientSettings() => FrpSettings.Default(9321) with
    {
        Client = FrpSettings.Default(9321).Client with { ServerAddress = "frp.example.com" },
    };

    [Fact]
    public async Task StartWithoutInstalledBinaryRefusesBeforeTouchingTheProcess()
    {
        using var fixture = Create();
        fixture.Store.Save(ValidClientSettings());

        var result = await fixture.Service.StartTunnelAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("尚未安装 frp", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Supervisor.StartCalls);
        Assert.Contains(fixture.Diagnostics.Messages, message => message.Contains("穿透未启动", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartWithoutServerAddressReportsEveryValidationProblem()
    {
        using var fixture = Create();
        fixture.Installer.Version = "0.71.0";
        fixture.Store.Save(FrpSettings.Default(9321));

        var result = await fixture.Service.StartTunnelAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("服务器地址不能为空", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Supervisor.StartCalls);
    }

    [Fact]
    public async Task StartWritesTheGeneratedConfigAndReportsTheFrpAddress()
    {
        using var fixture = Create();
        fixture.Installer.Version = "0.71.0";
        fixture.Store.Save(ValidClientSettings() with { InstalledVersion = "0.71.0" });
        fixture.Runtime.SetState(DesktopRuntimeState.Running);
        fixture.Store.SaveToken("server-token");

        var result = await fixture.Service.StartTunnelAsync();

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("203.0.113.10:19321", result.Message, StringComparison.Ordinal);
        var plan = Assert.IsType<FrpRunPlan>(fixture.Supervisor.LastPlan);
        Assert.Equal(FrpRole.Client, plan.Role);
        Assert.Equal(Path.Combine(fixture.Paths.FrpLogsDirectory), plan.LogDirectory);
        Assert.Equal(FrpClientSettings.DefaultAdminPort, plan.AdminPort);
        Assert.Equal("danmu-api", plan.ProxyName);
        Assert.True(File.Exists(plan.ConfigPath), $"配置文件未生成：{plan.ConfigPath}");

        var config = File.ReadAllText(plan.ConfigPath);
        Assert.Contains("serverAddr = \"frp.example.com\"", config, StringComparison.Ordinal);
        Assert.Contains("auth.token = \"server-token\"", config, StringComparison.Ordinal);
        Assert.Contains("remotePort = 9321", config, StringComparison.Ordinal);
        // 管理密码由本应用生成，必须写进配置，否则状态接口读不到（那是状态判定的唯一依据）。
        Assert.Contains($"webServer.port = {FrpClientSettings.DefaultAdminPort}", config, StringComparison.Ordinal);
        Assert.Contains("webServer.user = \"admin\"", config, StringComparison.Ordinal);
        Assert.Matches("webServer\\.password = \"[0-9A-F]{48}\"", config);
    }

    [Fact]
    public async Task ServerRoleWritesTheServerConfigAndDescribesTheListeningPort()
    {
        using var fixture = Create();
        fixture.Installer.Version = "0.71.0";
        fixture.Supervisor.StartResult = new FrpSnapshot(
            FrpTunnelState.Running,
            Pid: 99,
            Server: new FrpServerInfo("0.71.0", 7200, 8080, 0, ClientCounts: 2));
        fixture.Store.Save(ValidClientSettings() with
        {
            Role = FrpRole.Server,
            InstalledVersion = "0.71.0",
            Server = new FrpServerSettings(7200, 8080, "example.com", 7501),
        });
        fixture.Runtime.SetState(DesktopRuntimeState.Running);

        var result = await fixture.Service.StartTunnelAsync();

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("7200", result.Message, StringComparison.Ordinal);
        Assert.Contains("在线客户端 2", result.Message, StringComparison.Ordinal);
        var plan = Assert.IsType<FrpRunPlan>(fixture.Supervisor.LastPlan);
        Assert.Equal(FrpRole.Server, plan.Role);
        Assert.Equal(7501, plan.AdminPort);
        Assert.EndsWith(FrpConfigWriter.ServerFileName, plan.ConfigPath, StringComparison.Ordinal);
        Assert.Contains("bindPort = 7200", File.ReadAllText(plan.ConfigPath), StringComparison.Ordinal);
        Assert.Contains("vhostHTTPPort = 8080", File.ReadAllText(plan.ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartFailureCarriesTheSupervisorDiagnosticVerbatim()
    {
        using var fixture = Create();
        fixture.Installer.Version = "0.71.0";
        fixture.Supervisor.StartResult = new FrpSnapshot(
            FrpTunnelState.Failed,
            Diagnostic: "代理 danmu-api 启动失败：port already used");
        fixture.Store.Save(ValidClientSettings() with { InstalledVersion = "0.71.0" });
        fixture.Runtime.SetState(DesktopRuntimeState.Running);

        var result = await fixture.Service.StartTunnelAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("port already used", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopFailureIsReportedWithTheReason()
    {
        using var fixture = Create();
        fixture.Supervisor.StopResult = new FrpSnapshot(FrpTunnelState.Failed, Diagnostic: "拒绝终止：PID=1 可执行文件不是预期 frpc.exe");
        fixture.Store.Save(ValidClientSettings() with { InstalledVersion = "0.71.0" });
        fixture.Runtime.SetState(DesktopRuntimeState.Running);

        var result = await fixture.Service.StopTunnelAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("不是预期 frpc.exe", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallPublishesTheDeducedVersionSoTheNextStartCanUseIt()
    {
        using var fixture = Create();
        fixture.Installer.InstallVersion = "0.71.0";

        var result = await fixture.Service.InstallAsync("0.71.0");

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, fixture.Installer.InstallCalls);
        Assert.Equal("0.71.0", fixture.Service.InstalledVersion);
        var reloaded = fixture.Store.Read(9321);
        Assert.Equal("0.71.0", reloaded.Settings.InstalledVersion);
    }

    [Fact]
    public async Task InstallIsRefusedWhileTheTunnelIsRunning()
    {
        using var fixture = Create();
        fixture.Installer.Version = "0.70.0";
        fixture.Store.Save(ValidClientSettings() with { InstalledVersion = "0.70.0" });
        fixture.Runtime.SetState(DesktopRuntimeState.Running);
        Assert.True((await fixture.Service.StartTunnelAsync()).Succeeded);

        var result = await fixture.Service.InstallAsync("0.71.0");

        Assert.False(result.Succeeded);
        Assert.Contains("请先停止穿透", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Installer.InstallCalls);
    }

    [Fact]
    public async Task ReloadAlignsTheStoredVersionWithWhatIsActuallyOnDisk()
    {
        using var fixture = Create();
        // 设置里写着 0.71.0，但磁盘上什么都没有：必须按磁盘对齐，否则界面会声称已安装。
        fixture.Store.Save(ValidClientSettings() with { InstalledVersion = "0.71.0" });
        fixture.Runtime.SetState(DesktopRuntimeState.Running);
        fixture.Installer.Version = null;

        var result = await fixture.Service.ReloadSettingsAsync();

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(string.Empty, fixture.Service.Settings.InstalledVersion);
        Assert.Contains(fixture.Diagnostics.Messages, message => message.Contains("已按磁盘内容对齐", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FollowServiceLaunchesTheTunnelWhenTheServiceStarts()
    {
        using var fixture = Create();
        fixture.Installer.Version = "0.71.0";
        fixture.Store.Save(ValidClientSettings() with { FollowService = true, InstalledVersion = "0.71.0" });
        fixture.Service.Start();

        // 服务起来之前不该动手。
        await Task.Delay(200);
        Assert.Equal(0, fixture.Supervisor.StartCalls);

        fixture.Runtime.SetState(DesktopRuntimeState.Running);
        await WaitForAsync(() => fixture.Supervisor.StartCalls >= 1);

        Assert.Equal(FrpTunnelState.Running, fixture.Service.Snapshot.State);
        await fixture.Service.DisposeAsync();
    }

    [Fact]
    public async Task FollowServiceStaysIdleWhenTheUserDidNotOptIn()
    {
        using var fixture = Create();
        fixture.Installer.Version = "0.71.0";
        fixture.Store.Save(ValidClientSettings() with { InstalledVersion = "0.71.0" });
        fixture.Runtime.SetState(DesktopRuntimeState.Running);
        fixture.Service.Start();

        fixture.Runtime.SetState(DesktopRuntimeState.Running);
        await Task.Delay(300);

        Assert.Equal(0, fixture.Supervisor.StartCalls);
        Assert.Equal(FrpTunnelState.Stopped, fixture.Service.Snapshot.State);
        await fixture.Service.DisposeAsync();
    }

    [Fact]
    public async Task TunnelStopsWheneverTheServiceStops()
    {
        using var fixture = Create();
        fixture.Installer.Version = "0.71.0";
        fixture.Store.Save(ValidClientSettings() with { InstalledVersion = "0.71.0" });
        fixture.Runtime.SetState(DesktopRuntimeState.Running);
        fixture.Runtime.SetState(DesktopRuntimeState.Running);
        fixture.Service.Start();

        Assert.True((await fixture.Service.StartTunnelAsync()).Succeeded);
        Assert.Equal(FrpTunnelState.Running, fixture.Service.Snapshot.State);

        // 停止方向不看「随服务启动」开关：服务一停穿透必停，否则界面上的地址是假的。
        fixture.Runtime.SetState(DesktopRuntimeState.Stopped, port: null);
        await WaitForAsync(() => fixture.Service.Snapshot.State == FrpTunnelState.Stopped);

        Assert.Equal(FrpTunnelState.Stopped, fixture.Supervisor.Snapshot.State);
        Assert.Contains(fixture.Diagnostics.Messages, message => message.Contains("穿透已随之停止", StringComparison.Ordinal));
        await fixture.Service.DisposeAsync();
    }

    [Fact]
    public async Task ManualStartIsRefusedWhileTheServiceIsNotRunning()
    {
        using var fixture = Create();
        fixture.Installer.Version = "0.71.0";
        fixture.Store.Save(ValidClientSettings() with { InstalledVersion = "0.71.0" });

        var result = await fixture.Service.StartTunnelAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("弹幕服务未运行", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Supervisor.StartCalls);
    }

    [Fact]
    public async Task EnablingFollowServiceWhileTheServiceRunsStartsTheTunnelImmediately()
    {
        using var fixture = Create();
        fixture.Installer.Version = "0.71.0";
        fixture.Store.Save(ValidClientSettings() with { InstalledVersion = "0.71.0" });
        fixture.Runtime.SetState(DesktopRuntimeState.Running);
        fixture.Runtime.SetState(DesktopRuntimeState.Running);

        var result = await fixture.Service.SetFollowServiceAsync(true);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, fixture.Supervisor.StartCalls);
        Assert.Equal(FrpTunnelState.Running, fixture.Service.Snapshot.State);
        // 开关落盘：重启应用后仍然记得。
        Assert.True(fixture.Store.Read(9321).Settings.FollowService);
    }

    [Fact]
    public async Task DisablingFollowServiceOnlyStopsFutureAutoStarts()
    {
        using var fixture = Create();
        fixture.Installer.Version = "0.71.0";
        fixture.Store.Save(ValidClientSettings() with { FollowService = true, InstalledVersion = "0.71.0" });
        fixture.Runtime.SetState(DesktopRuntimeState.Running);
        fixture.Service.Start();
        await WaitForAsync(() => fixture.Service.Snapshot.State == FrpTunnelState.Running);

        var result = await fixture.Service.SetFollowServiceAsync(false);

        Assert.True(result.Succeeded, result.Message);
        // 关掉开关不该顺手把正在跑的穿透停掉——那是用户没要求的动作。
        Assert.Equal(FrpTunnelState.Running, fixture.Service.Snapshot.State);
        Assert.False(fixture.Store.Read(9321).Settings.FollowService);
        await fixture.Service.DisposeAsync();
    }

    [Fact]
    public async Task EnablingFollowServiceWhileTheTunnelAlreadyRunsIsNotAnError()
    {
        // 真机事故：用户先手动起了穿透，再来打开这个开关——旧实现会去"再启动一次"，
        // 监督器以"当前状态 Running 不允许启动"抛异常，异常经 AsyncRelayCommand 在 UI 线程重抛，
        // 应用直接闪退（Windows 事件日志 APPCRASH / 0xe0434352）。
        using var fixture = Create();
        fixture.Installer.Version = "0.71.0";
        fixture.Store.Save(ValidClientSettings() with { InstalledVersion = "0.71.0" });
        fixture.Runtime.SetState(DesktopRuntimeState.Running);
        Assert.True((await fixture.Service.StartTunnelAsync()).Succeeded);
        Assert.Equal(FrpTunnelState.Running, fixture.Service.Snapshot.State);
        var startsBefore = fixture.Supervisor.StartCalls;

        var result = await fixture.Service.SetFollowServiceAsync(true);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("已开启", result.Message, StringComparison.Ordinal);
        Assert.Equal(startsBefore, fixture.Supervisor.StartCalls);
        Assert.True(fixture.Store.Read(9321).Settings.FollowService);
    }

    [Fact]
    public async Task SupervisorStartFailureBecomesAFailedResultInsteadOfAnException()
    {
        using var fixture = Create();
        fixture.Installer.Version = "0.71.0";
        fixture.Store.Save(ValidClientSettings() with { InstalledVersion = "0.71.0" });
        fixture.Runtime.SetState(DesktopRuntimeState.Running);
        fixture.Supervisor.StartException = new InvalidOperationException("当前状态 Running 不允许启动，请先停止穿透");

        var start = await fixture.Service.StartTunnelAsync();
        var toggle = await fixture.Service.SetFollowServiceAsync(true);

        Assert.False(start.Succeeded);
        Assert.Contains("当前状态 Running 不允许启动", start.Message, StringComparison.Ordinal);
        Assert.False(toggle.Succeeded);
        Assert.Contains("立即启动穿透失败", toggle.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SupervisorStopFailureBecomesAFailedResultInsteadOfAnException()
    {
        using var fixture = Create();
        fixture.Installer.Version = "0.71.0";
        fixture.Store.Save(ValidClientSettings() with { InstalledVersion = "0.71.0" });
        fixture.Runtime.SetState(DesktopRuntimeState.Running);
        Assert.True((await fixture.Service.StartTunnelAsync()).Succeeded);
        fixture.Supervisor.StopException = new IOException("终止器不可用");

        var result = await fixture.Service.StopTunnelAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("终止器不可用", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavingConfigurationKeepsTheFollowServiceSwitchUntouched()
    {
        using var fixture = Create();
        fixture.Installer.Version = "0.71.0";
        fixture.Store.Save(ValidClientSettings() with { FollowService = true, InstalledVersion = "0.71.0" });

        var saved = await fixture.Service.SaveAsync(ValidClientSettings() with { InstalledVersion = "0.71.0" }, null, clearToken: false);

        Assert.True(saved.Succeeded, saved.Message);
        Assert.True(fixture.Service.Settings.FollowService);
        Assert.True(fixture.Store.Read(9321).Settings.FollowService);
    }

    [Fact]
    public async Task SnapshotFailuresAreVisibleThroughSettingsProblems()
    {
        using var fixture = Create();
        File.WriteAllText(fixture.Paths.SettingsFile, "frp_server_port = not-a-port\n");

        var result = await fixture.Service.ReloadSettingsAsync();

        Assert.False(result.Succeeded);
        Assert.Contains(fixture.Service.SettingsProblems, problem => problem.Contains("frp_server_port", StringComparison.Ordinal));
    }

    [Fact]
    public void PlatformDiagnosticIsEmptyOn64BitHosts()
    {
        using var fixture = Create();

        if (Environment.Is64BitProcess)
        {
            Assert.True(fixture.Service.IsSupportedPlatform);
        }

        Assert.Equal(string.Empty, fixture.Service.PlatformDiagnostic);
    }
}
