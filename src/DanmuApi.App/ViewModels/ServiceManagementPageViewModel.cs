using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DanmuApi.App.Services;
using DanmuApi.Runtime;

namespace DanmuApi.App.ViewModels;

public sealed partial class ServiceManagementPageViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly RuntimeApiContext _context;
    private readonly IRuntimeManagementClient _client;
    private readonly IAdminWriteGate _gate;
    private readonly IUiDialogService _dialogs;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public ServiceManagementPageViewModel(RuntimeApiContext context, IRuntimeManagementClient client,
        IAdminWriteGate gate, IUiDialogService dialogs)
    {
        _context = context;
        _client = client;
        _gate = gate;
        _dialogs = dialogs;
    }

    public ObservableCollection<AccessDevice> Devices { get; } = [];
    [ObservableProperty] private bool _blacklistEnabled;
    [ObservableProperty] private string _blacklistText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasSnapshot;
    [ObservableProperty] private string _diagnostic = "刷新可读取宿主设备记录。仅记录曾访问此服务的设备，不扫描局域网。";
    [ObservableProperty] private string _statistics = "尚未读取";

    private bool CanRefresh() => !IsBusy && !_disposed;
    private bool CanSave() => CanRefresh() && HasSnapshot;
    partial void OnIsBusyChanged(bool value) => RefreshCommands();
    partial void OnHasSnapshotChanged(bool value) => RefreshCommands();
    private void RefreshCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        ClearDevicesCommand.NotifyCanExecuteChanged();
        ClearLogsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => RunAsync(async (token, _) =>
    {
        HasSnapshot = false;
        Devices.Clear();
        Apply(await _client.ReadAccessAsync(_context.Host, _context.Port!.Value, token, _lifetime.Token));
        Diagnostic = "设备记录已刷新。关闭黑名单时保留规则；本机回环访问始终放行。";
    });

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync() => SaveCoreAsync(false);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task ClearDevicesAsync() => SaveCoreAsync(true);

    private Task SaveCoreAsync(bool clearDevices) => RunAsync(async (token, _) =>
    {
        var ips = BlacklistText.Split(['\r', '\n', ',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(RuntimeManagementClient.ValidateIp).Distinct(StringComparer.Ordinal).ToArray();
        if (!await _gate.EnsureAsync("修改设备访问控制", _lifetime.Token))
        {
            Diagnostic = "未获得管理员授权，未发送修改请求。";
            return;
        }
        var confirmed = await _dialogs.ConfirmAsync(clearDevices ? "清除设备记录" : "保存访问控制",
            $"将保存当前黑名单设置（{ips.Length} 条 IP，{(BlacklistEnabled ? "启用拦截" : "关闭拦截")}）。" +
            (clearDevices ? "同时清除宿主设备访问记录，无法撤销；累计允许/拦截计数不会重置。" : "被拦截的设备将无法访问弹幕服务；空列表会删除全部黑名单规则。"),
            clearDevices ? "保存并清除记录" : "保存设置");
        if (!confirmed) { Diagnostic = "已取消，未发送修改请求。"; return; }
        _lifetime.Token.ThrowIfCancellationRequested();
        _context.EnsureRunning();
        // 对话框期间可能重启或更换凭据，提交前重新取当前状态与凭据。
        if (!await _gate.EnsureAsync("修改设备访问控制", _lifetime.Token)) { Diagnostic = "管理员授权已失效，未发送请求。"; return; }
        HasSnapshot = false;
        Apply(await _client.SaveAccessAsync(_context.Host, _context.Port!.Value, _context.Token,
            BlacklistEnabled ? "blacklist" : "off", ips, clearDevices, _lifetime.Token));
        Diagnostic = clearDevices ? "设备记录已清除，访问控制设置已确认保存。" : "访问控制设置已确认保存。";
    });

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task ClearLogsAsync() => RunAsync(async (token, admin) =>
    {
        if (!await _gate.EnsureAsync("清理核心日志", _lifetime.Token)) { Diagnostic = "未获得管理员授权，未发送清理请求。"; return; }
        if (!await _dialogs.ConfirmAsync("清理核心日志", "清空核心当前内存日志，无法撤销。此操作不删除宿主日志或磁盘日志文件。", "清空内存日志"))
        { Diagnostic = "已取消，未发送清理请求。"; return; }
        _lifetime.Token.ThrowIfCancellationRequested();
        _context.EnsureRunning();
        if (!await _gate.EnsureAsync("清理核心日志", _lifetime.Token)) { Diagnostic = "管理员授权已失效，未发送请求。"; return; }
        admin = _context.AdminToken();
        if (string.IsNullOrWhiteSpace(admin)) throw new InvalidOperationException("管理员会话未提供密码，拒绝清理日志");
        await _client.ClearLogsAsync(_context.Host, _context.Port!.Value, _context.Token, admin, _lifetime.Token);
        Diagnostic = "核心已确认内存日志清空。日志页下次刷新将读取新的快照。";
    });

    private async Task RunAsync(Func<string, string?, Task> operation)
    {
        if (IsBusy || _disposed) return;
        IsBusy = true;
        string? token = null, admin = null;
        try
        {
            _context.EnsureRunning();
            token = _context.Token;
            admin = _context.AdminToken();
            await operation(token, admin);
        }
        catch (OperationCanceledException) { Diagnostic = "操作已取消；若写请求已发送，请刷新核对结果。"; }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException or
            FormatException or ArgumentException or InvalidOperationException or TimeoutException or DanmuApiException)
        {
            Diagnostic = RuntimeManagementClient.Redact($"操作失败：{error.Message}", token, admin);
        }
        finally { IsBusy = false; }
    }

    private void Apply(AccessControlSnapshot snapshot)
    {
        BlacklistEnabled = snapshot.Mode == "blacklist";
        BlacklistText = string.Join(Environment.NewLine, snapshot.Blacklist);
        Devices.Clear();
        foreach (var device in snapshot.Devices) Devices.Add(device);
        Statistics = $"设备 {snapshot.Devices.Count} · 黑名单 {snapshot.Blacklist.Count} · 累计允许 {snapshot.TotalAllowedRequests} · 拦截 {snapshot.TotalBlockedRequests}";
        HasSnapshot = true;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _lifetime.Cancel();
        RefreshCommands();
        return ValueTask.CompletedTask;
    }
}
