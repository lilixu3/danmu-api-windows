using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using DanmuApi.App.ViewModels;
using DanmuApi.App.Views;
using DanmuApi.Core;
using DanmuApi.Core.Frp;
using DanmuApi.Runtime;
using DanmuApi.Runtime.Frp;

namespace DanmuApi.Tests;

/// <summary>
/// 内网穿透「监控」页：链路三节、代理去重、按钮随状态出现，以及四态渲染。
///
/// 这一组同时是"页面为什么这样排版"的可执行说明：公网入口只在链路节点出现一次，
/// 代理行不重复报同一个地址（旧实现把 203.0.113.10:19321 在地址、详细状态、代理状态里说了三遍）。
/// </summary>
public sealed class FrpMonitorRedesignTests
{
    private const string PublicEntry = FrpSurfaceFixture.PublicEntry;

    private sealed class UnconfirmedRoute : IGithubRoutePreferenceStore
    {
        public GithubRoutePreference Read() => new("original", false);

        public void Confirm(string proxyId)
        {
        }

        public void Invalidate()
        {
        }
    }

    private sealed class StubSpeedTester : IGithubProxySpeedTester
    {
        public Task<IReadOnlyList<GithubProxyLatencyResult>> TestAllAsync(
            IProgress<GithubProxyLatencyResult>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GithubProxyLatencyResult>>([]);
    }

    private sealed record Model(
        FrpTunnelPageViewModel Page,
        FrpTunnelMonitorViewModel Monitor,
        FrpTestHarness Harness) : IDisposable
    {
        public void Dispose() => Harness.Dispose();
    }

    private static Model CreateModel(int localPort = 9321)
    {
        var harness = FrpTestHarness.Create(localPort);
        var dialogs = new RecordingDialogService();
        FrpTunnelMonitorViewModel? monitor = null;
        FrpTunnelConfigViewModel? config = null;
        FrpTunnelLogViewModel? logs = null;
        FrpTunnelPageViewModel? page = null;
        page = new FrpTunnelPageViewModel(
            () => monitor ??= new FrpTunnelMonitorViewModel(
                harness.Service,
                harness.Runtime,
                dialogs,
                harness.Diagnostics,
                harness.Paths,
                new FrpReleaseDiscovery(new HttpClient()),
                new UnconfirmedRoute(),
                new StubSpeedTester(),
                () => localPort,
                openConfiguration: () => page?.SelectTab(FrpTunnelTab.Configuration)),
            () => config ??= new FrpTunnelConfigViewModel(harness.Service, dialogs, harness.Diagnostics, () => localPort),
            () => logs ??= new FrpTunnelLogViewModel(harness.Service, dialogs, harness.Diagnostics, harness.Paths));
        return new Model(page, monitor!, harness);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MonitorRendersLinkProxiesAndStateDrivenActions(bool dark)
    {
        using var model = CreateModel();
        await FrpSurfaceFixture.UseInstalledFrpAsync(model.Harness);
        model.Harness.Runtime.SetState(DesktopRuntimeState.Running);
        var view = new FrpTunnelView { DataContext = model.Page };
        var window = new Window
        {
            Width = 1100,
            Height = 900,
            Content = view,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light,
        };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var monitor = model.Monitor;
            var prefix = dark ? "frp-monitor-redesign-dark" : "frp-monitor-redesign-light";

            // 未启动：只给「启动」，维护段给「更新 frp」（已安装）与目录入口。
            Assert.Equal("未启动", monitor.StatusText);
            Assert.True(monitor.ShowStart);
            Assert.False(monitor.ShowStop);
            Assert.False(monitor.ShowRestart);
            Assert.False(monitor.ShowInstall);
            Assert.True(monitor.ShowUpdate);
            Assert.Equal("127.0.0.1:9321", monitor.ServiceAddressText);
            Assert.Equal("frp.example.com:7000", monitor.ServerText);
            Assert.Equal("未分配", monitor.PublicEntryText);
            Render(window, prefix + "-stopped");

            // 穿透正常：地址、链路第三节、代理都到位；代理行**不**重复公网入口。
            model.Harness.Supervisor.Publish(new FrpSnapshot(
                FrpTunnelState.Running,
                Pid: 4242,
                RemoteAddress: PublicEntry,
                Proxies: [FrpSurfaceFixture.RunningProxy]));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal("穿透正常", monitor.StatusText);
            Assert.True(monitor.HasPublicAddress);
            Assert.Contains(PublicEntry, monitor.PublicEndpoint.Address, StringComparison.Ordinal);
            Assert.Equal(PublicEntry, monitor.PublicEntryText);
            Assert.Equal("公网入口", monitor.PublicEntryLabel);
            Assert.False(monitor.ShowServiceWarning);
            var proxy = Assert.Single(monitor.ProxyItems);
            Assert.Equal("danmu-api · TCP", proxy.Caption);
            Assert.Equal("已连接", proxy.StateText);
            Assert.True(proxy.IsRunning);
            Assert.False(proxy.HasDetail);
            Assert.True(monitor.ShowStop);
            Assert.True(monitor.ShowRestart);
            Assert.False(monitor.ShowStart);
            Assert.False(monitor.HasDiagnostic);
            Render(window, prefix + "-running");

            // 窄窗口：链路三节与操作行都必须收得住（工具页内容列在 960 窗口下只有 ~680px）。
            window.Width = 700;
            window.Height = 900;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(monitor.HasPublicAddress);
            Render(window, prefix + "-running-narrow");
            window.Width = 1100;
            window.Height = 900;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            // 另一条代理落在别的公网端口：只有这种情况才需要单独补一句，
            // 免得用户以为整条隧道只有一个入口。
            model.Harness.Supervisor.Publish(new FrpSnapshot(
                FrpTunnelState.Running,
                Pid: 4242,
                RemoteAddress: PublicEntry,
                Proxies:
                [
                    FrpSurfaceFixture.RunningProxy,
                    new FrpProxyStatus("danmu-api-alt", "tcp", "running", string.Empty, "127.0.0.1:9322", "203.0.113.10:19322"),
                ]));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, monitor.ProxyItems.Count);
            Assert.Equal("203.0.113.10:19322", monitor.ProxyItems[1].Detail);
            Assert.True(monitor.ProxyItems[1].HasDetail);
            Render(window, prefix + "-two-proxies");

            // 失败：frp 的原话进诊断块，按钮回到「启动」，弹幕服务也没跑 → 状态行给警告。
            model.Harness.Supervisor.Publish(new FrpSnapshot(
                FrpTunnelState.Failed,
                Diagnostic: "frpc.exe 已退出，exitCode=1；listen tcp 127.0.0.1:7400: bind: Only one usage of each socket address",
                ExitCode: 1,
                Proxies: [FrpSurfaceFixture.RunningProxy with { Status = "start error", Error = "connect to server error" }]));
            model.Harness.Runtime.SetState(DesktopRuntimeState.Stopped, port: null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal("穿透失败", monitor.StatusText);
            Assert.True(monitor.HasDiagnostic);
            Assert.Contains("Only one usage of each socket address", monitor.DiagnosticText, StringComparison.Ordinal);
            Assert.True(monitor.ShowServiceWarning);
            Assert.True(monitor.HasStartBlockedReason);
            Assert.Contains("弹幕服务未运行", monitor.StartBlockedReason, StringComparison.Ordinal);
            Assert.True(monitor.ShowStart);
            Assert.False(monitor.ShowStop);
            Assert.True(monitor.ShowRestart);
            var failedProxy = Assert.Single(monitor.ProxyItems);
            Assert.True(failedProxy.IsFailed);
            Assert.Equal("启动失败", failedProxy.StateText);
            Assert.Equal("connect to server error", failedProxy.Detail);
            Render(window, prefix + "-failed");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// 「去配置」必须真的切到配置页签 —— 它是本次新增的唯一跨页签动作，
    /// 点不动或切错页都会让"Token 在配置页"这句话重新变成死胡同。
    /// </summary>
    [AvaloniaFact]
    public async Task OpenConfigurationSwitchesToTheConfigurationTab()
    {
        using var model = CreateModel();
        await FrpSurfaceFixture.UseInstalledFrpAsync(model.Harness);
        var monitor = model.Monitor;

        Assert.True(monitor.CanNavigateToConfiguration);
        Assert.True(monitor.OpenConfigurationCommand.CanExecute(null));
        monitor.OpenConfigurationCommand.Execute(null);

        Assert.Equal(FrpTunnelTab.Configuration, model.Page.SelectedTabOption.Value);
        Assert.IsType<FrpTunnelConfigViewModel>(model.Page.CurrentTab);
    }

    /// <summary>服务在跑、frp 已装，但 frps 地址是空的：按钮直接点不动并把原因写清楚。</summary>
    [AvaloniaFact]
    public async Task MissingServerAddressBlocksStartInsteadOfFailingSilently()
    {
        using var model = CreateModel();
        model.Harness.Installer.Version = "0.71.0";
        var defaults = FrpSettings.Default(9321);
        model.Harness.Store.Save(defaults with { InstalledVersion = "0.71.0" });
        var reload = await model.Harness.Service.ReloadSettingsAsync();
        Assert.True(reload.Succeeded, reload.Message);
        model.Harness.Runtime.SetState(DesktopRuntimeState.Running);
        Dispatcher.UIThread.RunJobs();

        Assert.False(model.Monitor.CanStart);
        Assert.Contains("frps 服务器地址", model.Monitor.StartBlockedReason, StringComparison.Ordinal);
    }

    /// <summary>没接回调时（例如测试或未来复用）「去配置」只是不可用，不是崩。</summary>
    [AvaloniaFact]
    public void OpenConfigurationIsDisabledWithoutACallback()
    {
        using var harness = FrpTestHarness.Create();
        var monitor = new FrpTunnelMonitorViewModel(
            harness.Service,
            harness.Runtime,
            new RecordingDialogService(),
            harness.Diagnostics,
            harness.Paths,
            new FrpReleaseDiscovery(new HttpClient()),
            new UnconfirmedRoute(),
            new StubSpeedTester(),
            () => 9321);
        Assert.False(monitor.CanNavigateToConfiguration);
        Assert.False(monitor.OpenConfigurationCommand.CanExecute(null));
    }

    private static void Render(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var directory = Environment.GetEnvironmentVariable("DANMU_TEST_RENDER_DIRECTORY");
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(directory, name + ".png"));
    }
}
