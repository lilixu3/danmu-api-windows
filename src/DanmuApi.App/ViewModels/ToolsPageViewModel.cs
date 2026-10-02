using CommunityToolkit.Mvvm.ComponentModel;
using DanmuApi.App.Services;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.App.ViewModels;

public enum ToolsSection
{
    DanmuTest,
    LocalDanmu,
    ApiDebug,
    RequestRecords,
    ServiceManagement,
    IntranetPenetration,
}

public sealed record ToolsSectionOption(ToolsSection Value, string Title, string Description)
{
    public override string ToString() => Title;
}

public sealed class RuntimeApiContext
{
    private readonly AppPaths _paths;
    private readonly IRuntimeController _controller;
    private readonly IAdminSessionService _adminSession;

    /// <summary>运行状态快照变化（启动/停止/重启）；长寿命页面据此刷新可执行状态。</summary>
    public event EventHandler<RuntimeSnapshot>? RuntimeStateChanged;

    public RuntimeApiContext(AppPaths paths, IRuntimeController controller, IAdminSessionService adminSession)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _adminSession = adminSession ?? throw new ArgumentNullException(nameof(adminSession));
        _controller.SnapshotChanged += (_, snapshot) => RuntimeStateChanged?.Invoke(this, snapshot);
    }

    public RuntimeSnapshot Snapshot => _controller.Snapshot;
    public string Host => "127.0.0.1";
    public int? Port => Snapshot.State == DesktopRuntimeState.Running ? Snapshot.Port : null;
    public string Token => RuntimeTokenResolver.Resolve(Path.Combine(_paths.NodeProjectDirectory, "config", ".env"));

    public string? AdminToken()
    {
        _adminSession.Refresh();
        return _adminSession.CurrentAdminTokenOrNull();
    }

    public void EnsureRunning()
    {
        if (Snapshot.State != DesktopRuntimeState.Running || Snapshot.Port is null)
        {
            throw new DanmuApiException(DanmuApiFailureKind.ServiceNotRunning, "服务未运行，无法调用弹幕 API");
        }
    }
}

public sealed partial class ToolsPageViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly Func<DanmuTestPageViewModel> _danmuFactory;
    private readonly Func<LocalDanmuPageViewModel>? _localDanmuFactory;
    private readonly Func<ApiDebugPageViewModel> _apiFactory;
    private readonly Func<ServiceManagementPageViewModel>? _managementFactory;
    private readonly Func<RequestRecordsPageViewModel>? _requestsFactory;
    private readonly Func<FrpTunnelPageViewModel>? _frpFactory;
    private readonly List<Task> _pendingDisposals = [];

    [ObservableProperty]
    private ToolsSectionOption _selectedSectionOption = null!;

    [ObservableProperty]
    private object _currentSection = null!;

    public ToolsPageViewModel(
        Func<DanmuTestPageViewModel> danmuFactory,
        Func<ApiDebugPageViewModel> apiFactory,
        Func<ServiceManagementPageViewModel>? managementFactory = null,
        Func<RequestRecordsPageViewModel>? requestsFactory = null,
        Func<LocalDanmuPageViewModel>? localDanmuFactory = null,
        Func<FrpTunnelPageViewModel>? frpFactory = null)
    {
        _danmuFactory = danmuFactory ?? throw new ArgumentNullException(nameof(danmuFactory));
        _apiFactory = apiFactory ?? throw new ArgumentNullException(nameof(apiFactory));
        _managementFactory = managementFactory;
        _requestsFactory = requestsFactory;
        _localDanmuFactory = localDanmuFactory;
        _frpFactory = frpFactory;
        var sections = new List<ToolsSectionOption>
        {
            new(ToolsSection.DanmuTest, "弹幕测试", "自动匹配、手动匹配和收藏"),
        };
        if (localDanmuFactory is not null)
        {
            sections.Add(new(ToolsSection.LocalDanmu, "本地弹幕", "导入、维护与预览本地弹幕文件"));
        }

        sections.Add(new(ToolsSection.ApiDebug, "接口调试", "标准与兼容核心 API"));
        if (requestsFactory is not null) sections.Add(new(ToolsSection.RequestRecords, "请求记录", "请求筛选、统计与脱敏详情"));
        if (managementFactory is not null) sections.Add(new(ToolsSection.ServiceManagement, "设备与日志管理", "设备访问控制、核心日志清理"));
        if (frpFactory is not null) sections.Add(new(ToolsSection.IntranetPenetration, "内网穿透", "用 frp 把本机服务映射到公网，供外网访问"));
        SectionOptions = sections;
        _selectedSectionOption = SectionOptions[0];
        _currentSection = _danmuFactory();
    }

    public IReadOnlyList<ToolsSectionOption> SectionOptions { get; }

    /// <summary>
    /// 由外部（概览页的「去配置穿透」入口）指定要打开的分区。找不到对应分区时返回 false，
    /// 由调用方决定怎么提示，而不是悄悄停在一个不相干的页面上。
    /// </summary>
    public bool SelectSection(ToolsSection section)
    {
        var option = SectionOptions.FirstOrDefault(candidate => candidate.Value == section);
        if (option is null)
        {
            return false;
        }

        SelectedSectionOption = option;
        return true;
    }

    partial void OnSelectedSectionOptionChanged(ToolsSectionOption value)
    {
        if (CurrentSection is IAsyncDisposable disposable)
        {
            _pendingDisposals.Add(disposable.DisposeAsync().AsTask());
        }

        CurrentSection = value.Value switch
        {
            ToolsSection.DanmuTest => _danmuFactory(),
            ToolsSection.LocalDanmu when _localDanmuFactory is not null => _localDanmuFactory(),
            ToolsSection.ApiDebug => _apiFactory(),
            ToolsSection.RequestRecords when _requestsFactory is not null => _requestsFactory(),
            ToolsSection.ServiceManagement when _managementFactory is not null => _managementFactory(),
            ToolsSection.IntranetPenetration when _frpFactory is not null => _frpFactory(),
            _ => throw new InvalidOperationException("工具页面未注册"),
        };
        if (CurrentSection is RequestRecordsPageViewModel requests) requests.Start();
        if (CurrentSection is LocalDanmuPageViewModel localDanmu) _ = localDanmu.LoadAsync(force: false);
    }

    public async ValueTask DisposeAsync()
    {
        if (CurrentSection is IAsyncDisposable disposable)
        {
            _pendingDisposals.Add(disposable.DisposeAsync().AsTask());
        }
        await Task.WhenAll(_pendingDisposals).ConfigureAwait(false);
    }
}
