using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DanmuApi.App.ViewModels;
using DanmuApi.App.Views;
using DanmuApi.Core.Frp;
using DanmuApi.Runtime;
using DanmuApi.Runtime.Frp;

namespace DanmuApi.Tests;

/// <summary>
/// 概览页「连接地址」卡片（含内网穿透分区）在穿透三态下的结构与渲染。
///
/// 之所以单独一组：旧实现只在"穿透未接入"时被渲染过 —— 有地址、有代理、有失败原因的那几种
/// 状态从来没进过图，而"太乱"恰恰是在那些状态下最明显（同一个公网入口被说了三遍）。
/// 这里把三态都渲染出来并锁住关键结构，避免以后又退回重复播报。
/// </summary>
public sealed partial class MainWindowViewModelBehaviorTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverviewAddressCardRendersTunnelEntryAndStatusChip(bool dark)
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(directory.Path, "card-token");
        var settings = new RecordingSettingsStore();
        settings.Values["ipv6_enabled"] = "true";
        using var harness = FrpTestHarness.Create(9321);
        harness.Runtime.SetState(DesktopRuntimeState.Running);
        await FrpSurfaceFixture.UseInstalledFrpAsync(harness);
        await using var model = CreateViewModel(
            paths,
            settings,
            new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Running, Pid: 1234, Port: 9321)),
            dialogs: new RecordingDialogService(),
            healthClient: new PreviewHealthClient(),
            frp: harness.Service);
        var view = new OverviewView { DataContext = new OverviewPageViewModel(model) };
        var window = new Window
        {
            Width = 1280,
            Height = 800,
            Padding = new Thickness(24),
            Content = view,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light,
        };
        window.Show();
        try
        {
            // 穿透没跑：中性胶囊 + 「未启动」+ 去工具页的入口都在。
            Assert.Equal("未启动", model.FrpStatusText);
            Assert.False(model.FrpChipOk);
            Assert.False(model.FrpChipBad);
            Assert.DoesNotContain(model.EndpointItems, item => item.Title.Contains("外网", StringComparison.Ordinal));
            Assert.Same(model.OpenFrpTunnelCommand, view.FindControl<Button>("ManageFrpButton")!.Command);
            RenderOverview(window, view, dark ? "overview-frp-never-dark" : "overview-frp-never-light");

            // 穿透正常：外网入口进地址列表，胶囊转"正常"，服务在跑所以不给警告。
            harness.Supervisor.Publish(new FrpSnapshot(
                FrpTunnelState.Running,
                Pid: 4242,
                RemoteAddress: FrpSurfaceFixture.PublicEntry,
                Proxies: [FrpSurfaceFixture.RunningProxy]));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal("穿透正常", model.FrpStatusText);
            Assert.True(model.FrpChipOk);
            Assert.False(model.ShowFrpServiceWarning);
            var entry = Assert.Single(model.EndpointItems, item => item.Title.Contains("外网", StringComparison.Ordinal));
            Assert.Equal($"http://{FrpSurfaceFixture.PublicEntry}/card-token", entry.Address);
            RenderOverview(window, view, dark ? "overview-frp-running-dark" : "overview-frp-running-light");

            // 弹幕服务停了：穿透仍"正常"但送不出去，警告胶囊必须出现。
            model.Runtime = new RuntimeSnapshot(DesktopRuntimeState.Stopped);
            Dispatcher.UIThread.RunJobs();
            Assert.True(model.ShowFrpServiceWarning);
            Assert.True(view.FindControl<Border>("FrpServiceWarning")!.IsVisible);
            RenderOverview(window, view, dark ? "overview-frp-running-stopped-dark" : "overview-frp-running-stopped-light");

            // 穿透失败：frp 的原话直接进说明行，胶囊转"失败"，外网入口从地址列表消失。
            harness.Supervisor.Publish(new FrpSnapshot(
                FrpTunnelState.Failed,
                Diagnostic: "frpc.exe 已退出，exitCode=1；listen tcp 127.0.0.1:7400: bind: Only one usage of each socket address",
                ExitCode: 1));
            model.Runtime = new RuntimeSnapshot(DesktopRuntimeState.Running, Pid: 1234, Port: 9321);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(model.FrpChipBad);
            Assert.Contains("Only one usage of each socket address", model.FrpHintText, StringComparison.Ordinal);
            Assert.DoesNotContain(model.EndpointItems, item => item.Title.Contains("外网", StringComparison.Ordinal));
            RenderOverview(window, view, dark ? "overview-frp-failed-dark" : "overview-frp-failed-light");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// README 与文档用的整窗渲染：1280 宽、窗口够高，概览页从上到下（含连接地址卡片与穿透分区）完整可见。
    /// 数据是夹具造的真实结构（趋势来自 36 次采样），不是手绘占位。
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverviewWindowRendersForDocumentation(bool dark)
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(directory.Path, "preview-only-token");
        // 让文档图里的"当前核心"不是"未安装"：夹具写一份可被 CoreVersionReader 认出的核心目录。
        WriteCoreVersion(paths, "stable", "1.21.3");
        var settings = new RecordingSettingsStore();
        settings.Values["ipv6_enabled"] = "true";
        using var harness = FrpTestHarness.Create(9321);
        harness.Runtime.SetState(DesktopRuntimeState.Running);
        await FrpSurfaceFixture.UseInstalledFrpAsync(harness);
        await using var model = CreateViewModel(
            paths,
            settings,
            new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped)),
            dialogs: new RecordingDialogService(),
            healthClient: new PreviewHealthClient(),
            frp: harness.Service);
        // 健康快照要与 runtime 五项一致才会被接受（PID/端口/运行身份都要对得上），
        // 否则概览会显示"未读取 / 未运行"，文档图看起来像坏的。
        model.Runtime = new RuntimeSnapshot(DesktopRuntimeState.Running, Pid: 1234, Port: 9321);
        harness.Supervisor.Publish(new FrpSnapshot(
            FrpTunnelState.Running,
            Pid: 4242,
            RemoteAddress: FrpSurfaceFixture.PublicEntry,
            Proxies: [FrpSurfaceFixture.RunningProxy]));
        Dispatcher.UIThread.RunJobs();
        var fixture = new RuntimeHealthSnapshot(1234, "15", 220, "::", 9321, null, null, null, null,
            null, true, "stable", "稳定核心", "preview-instance", 220, null, null, null, null, null, null, null);
        model.RequestTrend.Reset();
        long count = 220;
        for (var i = 0; i < 36; i++)
        {
            count += new[] { 4, 8, 12, 25, 14, 5, 9, 18, 30, 16, 7, 5 }[i % 12];
            if (i is 19 or 20) model.RequestTrend.Disconnect(i * 5);
            else model.RequestTrend.Sample(fixture with { RequestCount = count }, i * 5);
        }

        var window = new MainWindow
        {
            DataContext = model,
            Width = 1280,
            Height = 1000,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light,
        };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.Position = new PixelPoint(8, 8);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var name = dark ? "overview-window-dark" : "overview-window-light";
            var directoryPath = Environment.GetEnvironmentVariable("DANMU_TEST_RENDER_DIRECTORY");
            if (!string.IsNullOrEmpty(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                bitmap.Render(window);
                bitmap.Save(Path.Combine(directoryPath, name + ".png"));
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static void RenderOverview(Window window, OverviewView view, string name)
    {
        Dispatcher.UIThread.RunJobs();
        // 地址卡片在页面底部，出图前滚到底，否则渲染的是服务控制台那一段。
        var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First();
        scroll.ScrollToEnd();
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
