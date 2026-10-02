using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DanmuApi.App.ViewModels;
using DanmuApi.App.Views;
using DanmuApi.Core;
using DanmuApi.Core.Frp;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

/// <summary>
/// 「内网穿透」分区（监控 / 配置 / 日志 三个页签）的行为测试：
/// 页签切换不丢状态、配置校验不落盘、JSON 导入导出走通、监控页在服务没跑时拒绝启动。
/// </summary>
public sealed class FrpTunnelViewTests
{
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
        FrpTunnelConfigViewModel Config,
        FrpTunnelLogViewModel Logs,
        FrpTestHarness Harness,
        RecordingDialogService Dialogs) : IDisposable
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
        var page = new FrpTunnelPageViewModel(
            () => monitor ??= new FrpTunnelMonitorViewModel(
                harness.Service,
                harness.Runtime,
                dialogs,
                harness.Diagnostics,
                harness.Paths,
                new FrpReleaseDiscovery(new HttpClient()),
                new UnconfirmedRoute(),
                new StubSpeedTester(),
                () => localPort),
            () => config ??= new FrpTunnelConfigViewModel(
                harness.Service,
                dialogs,
                harness.Diagnostics,
                () => localPort),
            () => logs ??= new FrpTunnelLogViewModel(
                harness.Service,
                dialogs,
                harness.Diagnostics,
                harness.Paths));

        // 页签是惰性创建的，这里先把三块都建出来供断言使用。
        page.SelectTab(FrpTunnelTab.Configuration);
        page.SelectTab(FrpTunnelTab.Logs);
        page.SelectTab(FrpTunnelTab.Monitor);

        return new Model(page, monitor!, config!, logs!, harness, dialogs);
    }

    [AvaloniaFact]
    public void MonitorTabIsTheDefaultAndExplainsWhyStartIsBlocked()
    {
        using var model = CreateModel();
        var view = new FrpTunnelView { DataContext = model.Page };
        var window = new Window { Width = 1100, Height = 900, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(FrpTunnelTab.Monitor, model.Page.SelectedTabOption.Value);
            Assert.Same(model.Monitor, model.Page.CurrentTab);
            Assert.Equal("未启动", model.Monitor.StatusText);
            Assert.Contains("未安装", model.Monitor.BinaryText, StringComparison.Ordinal);
            // 服务没跑、frp 也没装：两个理由都要说清楚，而不是让按钮默默点不动。
            Assert.True(model.Monitor.HasStartBlockedReason);
            Assert.Contains("弹幕服务未运行", model.Monitor.StartBlockedReason, StringComparison.Ordinal);
            Assert.False(model.Monitor.CanStart);
            RenderPreview(window, "frp-monitor-light");

            // 配置页与日志页也各出一张，便于人工复核版式。
            model.Page.SelectTab(FrpTunnelTab.Configuration);
            RenderPreview(window, "frp-config-light");
            model.Page.SelectTab(FrpTunnelTab.Logs);
            RenderPreview(window, "frp-logs-light");
            model.Page.SelectTab(FrpTunnelTab.Monitor);

            window.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            RenderPreview(window, "frp-monitor-dark");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TabsKeepTheirOwnStateWhenSwitching()
    {
        using var model = CreateModel();
        model.Config.ServerAddress = "frp.example.com";

        model.Page.SelectTab(FrpTunnelTab.Configuration);
        Assert.Same(model.Config, model.Page.CurrentTab);
        model.Page.SelectTab(FrpTunnelTab.Logs);
        Assert.Same(model.Logs, model.Page.CurrentTab);
        model.Page.SelectTab(FrpTunnelTab.Monitor);
        Assert.Same(model.Monitor, model.Page.CurrentTab);

        // 切回配置页仍是同一个实例：表单里没保存的输入不会因为切页签被丢掉。
        model.Page.SelectTab(FrpTunnelTab.Configuration);
        Assert.Same(model.Config, model.Page.CurrentTab);
        Assert.Equal("frp.example.com", model.Config.ServerAddress);
    }

    [AvaloniaFact]
    public void SwitchingRoleAndProxyKindTogglesTheFieldGroups()
    {
        using var model = CreateModel();
        var config = model.Config;

        Assert.True(config.IsClientRole);
        Assert.True(config.ShowTcpFields);
        Assert.False(config.ShowDomainFields);

        config.SelectedProxyKindOption = config.ProxyKindOptions.Single(option => option.Value == FrpProxyKind.Http);
        Assert.False(config.ShowTcpFields);
        Assert.True(config.ShowDomainFields);

        config.SelectedRoleOption = config.RoleOptions.Single(option => option.Value == FrpRole.Server);
        Assert.True(config.IsServerRole);
        Assert.False(config.ShowTcpFields);
        Assert.False(config.ShowDomainFields);
    }

    [AvaloniaFact]
    public async Task InvalidFieldsAreRefusedWithPerFieldReasonsAndNothingIsPersisted()
    {
        using var model = CreateModel();
        model.Config.ServerAddress = string.Empty;
        model.Config.RemotePortText = "abc";

        await model.Config.SaveCommand.ExecuteAsync(null);

        Assert.True(model.Config.HasProblems);
        Assert.Contains("服务器地址不能为空", model.Config.ValidationText, StringComparison.Ordinal);
        Assert.Contains("公网端口必须是数字", model.Config.ValidationText, StringComparison.Ordinal);

        // 校验不过就不该写盘：半份配置落盘会让下次启动拿到一个"看起来配过、其实不能用"的状态。
        var onDisk = model.Harness.Store.Read(9321);
        Assert.Equal(string.Empty, onDisk.Settings.Client.ServerAddress);
        Assert.Equal(string.Empty, onDisk.Settings.InstalledVersion);
    }

    [AvaloniaFact]
    public async Task SavingAValidConfigurationPersistsIt()
    {
        using var model = CreateModel();
        model.Config.ServerAddress = "frp.example.com";
        model.Config.RemotePortText = "19321";

        await model.Config.SaveCommand.ExecuteAsync(null);

        Assert.False(model.Config.HasProblems);
        Assert.Contains("设置已保存", model.Config.OperationMessage, StringComparison.Ordinal);
        var onDisk = model.Harness.Store.Read(9321);
        Assert.Equal("frp.example.com", onDisk.Settings.Client.ServerAddress);
        Assert.Equal(19321, onDisk.Settings.Client.RemotePort);
    }

    [AvaloniaFact]
    public async Task ExportingJsonCopiesItAndShowsTheSameDocument()
    {
        using var model = CreateModel();
        model.Config.ServerAddress = "frp.example.com";
        model.Config.SelectedProxyKindOption = model.Config.ProxyKindOptions.Single(option => option.Value == FrpProxyKind.Tcp);

        await model.Config.ExportJsonCommand.ExecuteAsync(null);

        var copied = Assert.IsType<string>(model.Dialogs.CopiedText);
        Assert.Contains("\"serverAddr\": \"frp.example.com\"", copied, StringComparison.Ordinal);
        // 弹窗里显示的必须是同一份文档（用户看到的就是他复制到的）。
        var prompt = Assert.Single(model.Dialogs.MultilineTextPrompts);
        Assert.True(prompt.ReadOnly);
        Assert.Equal(copied, prompt.Initial);
    }

    [AvaloniaFact]
    public async Task ImportingJsonFillsTheFormAndListsUnsupportedFields()
    {
        using var model = CreateModel();
        model.Dialogs.MultilineTextResult = """
        {
          "serverAddr": "1.2.3.4",
          "serverPort": 7000,
          "auth": { "method": "token", "token": "pasted-token" },
          "dnsServer": "1.1.1.1",
          "proxies": [
            { "name": "home", "type": "http", "localIP": "127.0.0.1", "localPort": 9321, "customDomains": ["danmu.example.com"], "healthCheck": { "type": "tcp" } }
          ]
        }
        """;

        await model.Config.ImportJsonCommand.ExecuteAsync(null);

        Assert.Equal("1.2.3.4", model.Config.ServerAddress);
        Assert.Equal(FrpProxyKind.Http, model.Config.SelectedProxyKindOption.Value);
        Assert.Equal("danmu.example.com", model.Config.CustomDomainsText);
        Assert.Equal("pasted-token", model.Config.TokenInput);
        // 认得出但不管理的字段必须逐条列出来，不能悄悄丢掉。
        Assert.Contains("dnsServer", model.Config.ImportWarningText, StringComparison.Ordinal);
        Assert.Contains("proxies[0].healthCheck", model.Config.ImportWarningText, StringComparison.Ordinal);
        // 导入只填表单：磁盘上还没变。
        Assert.Equal(string.Empty, model.Harness.Store.Read(9321).Settings.Client.ServerAddress);
    }

    [AvaloniaFact]
    public async Task ImportingBrokenJsonReportsWhyInsteadOfGuessing()
    {
        using var model = CreateModel();
        model.Dialogs.MultilineTextResult = """{ "serverAddr": "1.2.3.4" }""";

        await model.Config.ImportJsonCommand.ExecuteAsync(null);

        Assert.True(model.Config.HasProblems);
        Assert.Contains("proxies", model.Config.ValidationText + model.Config.ImportWarningText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void ValidationUpdatesWithoutSavingAndNeverDefaultsBlankManagedFields()
    {
        using var model = CreateModel();
        var config = model.Config;
        config.ServerAddress = "frp.example.com";
        Assert.False(config.HasProblems);
        config.ServerPortText = "bad-int";
        Assert.Contains("服务器端口必须是数字", config.ValidationText, StringComparison.Ordinal);
        config.ServerPortText = "7000";
        Assert.False(config.HasProblems);
        config.ProxyName = "";
        config.LocalAddress = "";
        Assert.Contains("代理名称不能为空", config.ValidationText, StringComparison.Ordinal);
        Assert.Contains("本地服务地址不能为空", config.ValidationText, StringComparison.Ordinal);
        config.ProxyName = "danmu-api";
        config.LocalAddress = "127.0.0.1";
        config.SelectedProxyKindOption = config.ProxyKindOptions.Single(option => option.Value == FrpProxyKind.Http);
        Assert.Contains("至少需要一个域名", config.ValidationText, StringComparison.Ordinal);
        config.CustomDomainsText = "danmu.example.com";
        Assert.False(config.HasProblems);
        config.SelectedRoleOption = config.RoleOptions.Single(option => option.Value == FrpRole.Server);
        config.BindPortText = "0";
        Assert.Contains("穿透端口必须在", config.ValidationText, StringComparison.Ordinal);
        Assert.False(File.Exists(model.Harness.Paths.SettingsFile));
    }

    [AvaloniaFact]
    public async Task EveryPreviouslyOmittedDraftFieldSurvivesSavedConfigurationChanges()
    {
        using var model = CreateModel();
        Action<FrpTunnelConfigViewModel>[] edits =
        [
            config => config.SelectedProxyKindOption = config.ProxyKindOptions.Single(option => option.Value == FrpProxyKind.Http),
            config => config.TransportTls = false,
            config => config.UseEncryption = true,
            config => config.UseCompression = true,
            config => config.TokenInput = "unsaved-token",
            config => config.TokenIntent = FrpTokenEditIntent.Clear,
        ];
        foreach (var edit in edits)
        {
            await model.Config.ReloadCommand.ExecuteAsync(null);
            edit(model.Config);
            var kind = model.Config.SelectedProxyKindOption.Value;
            var tls = model.Config.TransportTls;
            var encryption = model.Config.UseEncryption;
            var compression = model.Config.UseCompression;
            var token = model.Config.TokenInput;
            var intent = model.Config.TokenIntent;
            model.Harness.Store.Save(model.Harness.Store.Read(9321).Settings with
            {
                Server = new FrpServerSettings(7000, 8080, Guid.NewGuid().ToString("N") + ".example.com", 7500),
            });
            await model.Harness.Service.ReloadSettingsAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(kind, model.Config.SelectedProxyKindOption.Value);
            Assert.Equal(tls, model.Config.TransportTls);
            Assert.Equal(encryption, model.Config.UseEncryption);
            Assert.Equal(compression, model.Config.UseCompression);
            Assert.Equal(token, model.Config.TokenInput);
            Assert.Equal(intent, model.Config.TokenIntent);
            Assert.Contains("未自动覆盖", model.Config.OperationMessage, StringComparison.Ordinal);
        }
    }

    [AvaloniaFact]
    public async Task ChangesToSavedProxyOptionsUpdateAnUntouchedForm()
    {
        using var model = CreateModel();
        var saved = model.Harness.Store.Read(9321).Settings;
        model.Harness.Store.Save(saved with
        {
            Client = saved.Client with { ProxyKind = FrpProxyKind.Http, UseEncryption = true, UseCompression = true, TransportTls = false },
        });
        await model.Harness.Service.ReloadSettingsAsync();
        Assert.Equal(FrpProxyKind.Http, model.Config.SelectedProxyKindOption.Value);
        Assert.True(model.Config.UseEncryption);
        Assert.True(model.Config.UseCompression);
        Assert.False(model.Config.TransportTls);
    }

    [AvaloniaFact]
    public async Task ExportUsesKeepSetAndClearTokenDraftAndClearDoesNotWriteBeforeSave()
    {
        using var model = CreateModel();
        model.Harness.Store.SaveToken("saved-token");
        await model.Config.ReloadCommand.ExecuteAsync(null);
        model.Config.ServerAddress = "frp.example.com";
        await model.Config.ExportJsonCommand.ExecuteAsync(null);
        Assert.Contains("saved-token", model.Dialogs.CopiedText!, StringComparison.Ordinal);
        model.Config.TokenInput = "new-draft-token";
        Assert.Equal(FrpTokenEditIntent.Set, model.Config.TokenIntent);
        await model.Config.ExportJsonCommand.ExecuteAsync(null);
        Assert.Contains("new-draft-token", model.Dialogs.CopiedText!, StringComparison.Ordinal);
        Assert.DoesNotContain("saved-token", model.Dialogs.CopiedText!, StringComparison.Ordinal);
        Assert.Equal("saved-token", model.Harness.Store.ReadToken());
        model.Dialogs.Confirmation = true;
        await model.Config.ClearTokenCommand.ExecuteAsync(null);
        Assert.Equal(FrpTokenEditIntent.Clear, model.Config.TokenIntent);
        Assert.Equal("saved-token", model.Harness.Store.ReadToken());
        Assert.Equal("", model.Harness.Store.Read(9321).Settings.Client.ServerAddress);
        await model.Config.ExportJsonCommand.ExecuteAsync(null);
        Assert.DoesNotContain("\"auth\"", model.Dialogs.CopiedText!, StringComparison.Ordinal);
        await model.Config.SaveCommand.ExecuteAsync(null);
        Assert.False(model.Harness.Store.HasToken());
        Assert.Equal(FrpTokenEditIntent.Keep, model.Config.TokenIntent);
        Assert.Null(model.Config.TokenInput);
    }

    [AvaloniaFact]
    public async Task TokenDraftCanBeRevertedToKeepWithoutDiscardingOtherFields()
    {
        using var model = CreateModel();
        model.Harness.Store.SaveToken("saved-token");
        await model.Config.ReloadCommand.ExecuteAsync(null);
        model.Config.ServerAddress = "unsaved.example.com";
        model.Dialogs.Confirmation = true;
        await model.Config.ClearTokenCommand.ExecuteAsync(null);
        Assert.True(model.Config.HasTokenEdit);
        model.Config.KeepTokenCommand.Execute(null);
        Assert.Equal(FrpTokenEditIntent.Keep, model.Config.TokenIntent);
        Assert.Null(model.Config.TokenInput);
        Assert.False(model.Config.HasTokenEdit);
        Assert.Equal("unsaved.example.com", model.Config.ServerAddress);
        await model.Config.ExportJsonCommand.ExecuteAsync(null);
        Assert.Contains("saved-token", model.Dialogs.CopiedText!, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task WholeImportWithoutAuthStagesClearAndPreservesOppositeRoleDraft()
    {
        using var model = CreateModel();
        model.Harness.Store.SaveToken("saved-token");
        await model.Config.ReloadCommand.ExecuteAsync(null);
        model.Config.ServerAddress = "unsaved-opposite.example.com";
        model.Config.TransportTls = false;
        model.Config.TokenInput = "old-draft-token";
        model.Dialogs.MultilineTextResult = "{\"bindPort\":7001}";
        await model.Config.ImportJsonCommand.ExecuteAsync(null);
        Assert.True(model.Config.IsServerRole);
        Assert.Equal("unsaved-opposite.example.com", model.Config.ServerAddress);
        Assert.False(model.Config.TransportTls);
        Assert.Equal("0", model.Config.VhostHttpPortText);
        Assert.Equal("", model.Config.SubdomainHost);
        Assert.Equal(FrpTokenEditIntent.Clear, model.Config.TokenIntent);
        Assert.Null(model.Config.TokenInput);
        Assert.Equal("saved-token", model.Harness.Store.ReadToken());
        await model.Config.ExportJsonCommand.ExecuteAsync(null);
        Assert.DoesNotContain("\"auth\"", model.Dialogs.CopiedText!, StringComparison.Ordinal);
        // Import is unsaved even if the only difference is Token intent.
        model.Harness.Store.Save(model.Harness.Store.Read(9321).Settings with { Server = new FrpServerSettings(7002, 8080, "external.example.com", 7500) });
        await model.Harness.Service.ReloadSettingsAsync();
        Assert.Equal("7001", model.Config.BindPortText);
        Assert.Equal(FrpTokenEditIntent.Clear, model.Config.TokenIntent);
        await model.Config.SaveCommand.ExecuteAsync(null);
        Assert.False(model.Harness.Store.HasToken());
        Assert.Equal(7001, model.Harness.Store.Read(9321).Settings.Server.BindPort);
    }

    [AvaloniaFact]
    public async Task ServerRoleHasUsableTokenInputAndClearControls()
    {
        using var model = CreateModel();
        model.Config.SelectedRoleOption = model.Config.RoleOptions.Single(option => option.Value == FrpRole.Server);
        var view = new FrpTunnelConfigView { DataContext = model.Config };
        var window = new Window { Width = 1100, Height = 900, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var tokenBox = Assert.Single(view.GetVisualDescendants().OfType<TextBox>(), box => box.PasswordChar != default(char));
            Assert.True(tokenBox.IsEffectivelyVisible);
            tokenBox.Text = "server-token-draft";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("server-token-draft", model.Config.TokenInput);
            var clear = Assert.Single(view.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "清除"));
            Assert.True(clear.IsEffectivelyVisible);
            Assert.True(clear.IsEnabled);
            await model.Config.SaveCommand.ExecuteAsync(null);
            Assert.Equal("server-token-draft", model.Harness.Store.ReadToken());
            model.Dialogs.Confirmation = true;
            await model.Config.ClearTokenCommand.ExecuteAsync(null);
            Assert.Equal(FrpTokenEditIntent.Clear, model.Config.TokenIntent);
            Assert.Equal("server-token-draft", model.Harness.Store.ReadToken());
            await model.Config.SaveCommand.ExecuteAsync(null);
            Assert.False(model.Harness.Store.HasToken());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void StartingWithoutTheServiceRunningIsRefusedWithTheReason()
    {
        using var model = CreateModel();
        model.Harness.Installer.Version = "0.71.0";
        model.Harness.Store.Save(FrpSettings.Default(9321) with
        {
            InstalledVersion = "0.71.0",
            Client = FrpSettings.Default(9321).Client with { ServerAddress = "frp.example.com" },
        });
        model.Page.SelectTab(FrpTunnelTab.Monitor);

        model.Monitor.StartTunnelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("弹幕服务未运行", model.Monitor.OperationMessage, StringComparison.Ordinal);
        Assert.Equal(0, model.Harness.Supervisor.StartCalls);
    }

    [AvaloniaFact]
    public async Task FollowServiceSwitchPersistsImmediately()
    {
        using var model = CreateModel();

        Assert.False(model.Monitor.IsFollowServiceEnabled);
        await model.Monitor.ToggleFollowServiceCommand.ExecuteAsync(null);

        Assert.True(model.Monitor.IsFollowServiceEnabled);
        Assert.True(model.Harness.Store.Read(9321).Settings.FollowService);
        Assert.Contains("随弹幕服务启动", model.Monitor.OperationMessage, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void UseCurrentServicePortFillsTheLocalPort()
    {
        using var model = CreateModel();
        model.Config.LocalPortText = string.Empty;

        model.Config.UseCurrentServicePortCommand.Execute(null);

        Assert.Equal("9321", model.Config.LocalPortText);
        Assert.Equal(FrpClientSettings.DefaultLocalAddress, model.Config.LocalAddress);
    }

    [AvaloniaFact]
    public void ToolsPageOffersTheIntranetPenetrationSectionAndCanSelectIt()
    {
        using var model = CreateModel();
        // 初始分区由构造函数立即构造；本用例只关心内网穿透分区，故初始分区留空（不会用到）。
        var tools = new ToolsPageViewModel(
            () => null!,
            () => null!,
            frpFactory: () => model.Page);

        Assert.Contains(tools.SectionOptions, option => option.Value == ToolsSection.IntranetPenetration);
        Assert.True(tools.SelectSection(ToolsSection.IntranetPenetration));
        Assert.Same(model.Page, tools.CurrentSection);
    }

    /// <summary>按仓库既有约定出图：设了 DANMU_TEST_RENDER_DIRECTORY 才写 PNG。</summary>
    private static void RenderPreview(Window window, string name)
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
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
            new Avalonia.PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(directory, name + ".png"));
    }
}
