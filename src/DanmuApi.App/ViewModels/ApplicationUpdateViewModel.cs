using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DanmuApi.App.Services;
using DanmuApi.Core.ApplicationUpdates;
using DanmuApi.Platform;

namespace DanmuApi.App.ViewModels;

public sealed partial class ApplicationUpdateViewModel : ViewModelBase, IDisposable
{
    private readonly ApplicationUpdateService _remote = new(AppUpdateTrust.PublicKey());
    private readonly ISettingsStore _settings;
    private readonly IUiDialogService _dialogs;
    private readonly IDesktopNotificationService _notifications;
    private readonly IAppDiagnostics _diagnostics;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private ApplicationUpdate? _update;
    private string? _downloadDirectory;
    private string? _assetName;
    private bool _loading;
    private Task? _timer;
    public Func<Task<bool>>? ExitForUpdate { get; set; }
    public Func<bool>? IsServiceRunningBeforeUpdate { get; set; }
    public Func<string?>? UpdateBlockedReason { get; set; }
    public Action? ShowUpdatePage { get; set; }
    public string CurrentVersion => typeof(ApplicationUpdateViewModel).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : throw new InvalidOperationException("应用版本缺失");

    [ObservableProperty] private bool _automaticChecks = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasUpdate;
    [ObservableProperty] private bool _canInstall;
    [ObservableProperty] private string _status = "自动检查测试版更新；也可手动检查。";
    [ObservableProperty] private string _availableVersion = "";
    [ObservableProperty] private string _releaseNotes = "";
    [ObservableProperty] private string _publishedText = "";
    [ObservableProperty] private string _packageText = "";
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private bool _isDownloading;
    public string UpdateBadge => HasUpdate ? "发现软件更新" : "";
    public string DistributionText => ApplicationUpdateHelper.IsInstalled(Environment.ProcessPath!) ? "安装版 · 安装器更新" : "免安装版 · 原目录更新";

    public ApplicationUpdateViewModel(ISettingsStore settings, IUiDialogService dialogs, IDesktopNotificationService notifications, IAppDiagnostics diagnostics)
    {
        _settings = settings; _dialogs = dialogs; _notifications = notifications; _diagnostics = diagnostics;
        _loading = true;
        AutomaticChecks = !settings.Read().TryGetValue("app_update_auto", out var enabled) || bool.Parse(enabled);
        _loading = false;
    }

    partial void OnAutomaticChecksChanged(bool value)
    {
        if (_loading) return;
        try { _settings.Write(new Dictionary<string,string?> { ["app_update_auto"] = value.ToString().ToLowerInvariant() }); }
        catch (Exception error) { Status = "保存软件更新设置失败：" + error.Message; _diagnostics.Record(Status, error); }
    }
    partial void OnHasUpdateChanged(bool value) => OnPropertyChanged(nameof(UpdateBadge));

    public void Start() => _timer ??= AutomaticLoopAsync();
    private async Task AutomaticLoopAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), _lifetime.Token);
            while (!_lifetime.IsCancellationRequested)
            {
                await Dispatcher.UIThread.InvokeAsync(() => AutomaticChecks && !IsBusy ? CheckAsync(manual: false) : Task.CompletedTask);
                await Task.Delay(TimeSpan.FromHours(6), _lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { _diagnostics.Record("软件自动更新检查循环停止", error); Dispatcher.UIThread.Post(() => Status = "自动检查已停止：" + error.Message); }
    }

    [RelayCommand] private Task CheckForUpdatesAsync() => CheckAsync(manual: true);
    private async Task CheckAsync(bool manual)
    {
        if (IsBusy) return;
        IsBusy = true; Status = "正在检查 GitHub 测试发行版…";
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            var update = await _remote.CheckAsync(CurrentVersion, _operation.Token);
            if (update is null)
            {
                HasUpdate = false; Status = "当前已是最新版本。";
                if (manual) await _dialogs.ShowMessageAsync("软件更新", Status);
                return;
            }
            if (_update?.Manifest.Version != update.Manifest.Version) { CanInstall = false; _downloadDirectory = null; }
            _update = update;
            AvailableVersion = update.Manifest.Version; ReleaseNotes = update.ReleaseNotes; PublishedText = update.PublishedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            var kind = ApplicationUpdateHelper.IsInstalled(Environment.ProcessPath!) ? "installer" : "portable";
            var asset = update.Manifest.Assets.Single(item => item.Kind == kind);
            _assetName = asset.Name; PackageText = $"{DistributionText} · {asset.Size / 1048576d:0.0} MB";
            var values = _settings.Read();
            var skipped = values.TryGetValue("app_update_skipped", out var skip) && skip == AvailableVersion;
            HasUpdate = manual || !skipped;
            Status = skipped && !manual ? $"已跳过 {AvailableVersion}，手动检查可重新查看。" : $"发现新测试版 {AvailableVersion}。下载后仍需确认安装。";
            if (manual) await _dialogs.ShowMessageAsync("发现软件更新", $"{CurrentVersion} → {AvailableVersion}\n{PackageText}\n在关于页查看发行说明并下载。");
            else if (!skipped && (!values.TryGetValue("app_update_notified", out var notified) || notified != AvailableVersion))
            {
                var result = await _notifications.ShowAsync(DesktopNotificationKind.UpdateDiscovered, "弹幕 API 软件更新", $"发现测试版 {AvailableVersion}，点击查看详情。", new DesktopNotificationAction("查看更新", "danmuapi://update-app"), _operation.Token);
                if (result.Status == DesktopNotificationStatus.Submitted) _settings.Write(new Dictionary<string,string?> { ["app_update_notified"] = AvailableVersion });
                else if (result.Status == DesktopNotificationStatus.Failed) { Status += " 通知未提交，可在此处继续更新。"; _diagnostics.Record(result.Diagnostic); }
            }
        }
        catch (Exception error)
        {
            Status = Describe(error, "检查");
            _diagnostics.Record(Status);
            if (manual) await _dialogs.ShowMessageAsync("软件更新", Status, true);
            else
            {
                var notification = await _notifications.ShowAsync(DesktopNotificationKind.UpdateFailed, "软件更新检查失败", Status, cancellationToken: _lifetime.Token);
                if (notification.Status == DesktopNotificationStatus.Failed) _diagnostics.Record($"软件更新失败通知未提交：{notification.Diagnostic}");
            }
        }
        finally { _operation.Dispose(); _operation = null; IsBusy = false; }
    }

    [RelayCommand] private void SkipVersion()
    {
        if (_update is null || IsBusy) return;
        try { _settings.Write(new Dictionary<string,string?> { ["app_update_skipped"] = AvailableVersion }); HasUpdate = false; Status = $"已跳过 {AvailableVersion}。"; }
        catch (Exception error) { Status = "保存跳过版本失败：" + error.Message; }
    }
    [RelayCommand] private void Cancel()
    {
        if (_operation is not null) _operation.Cancel();
        else if (IsBusy && _downloadDirectory is not null)
        {
            File.WriteAllText(Path.Combine(_downloadDirectory, "cancel"), "cancel");
            Status = "已请求取消更新准备，正在等待助手退出。";
        }
    }

    [RelayCommand] private async Task DownloadUpdateAsync()
    {
        if (_update is null || _assetName is null || IsBusy) return;
        IsBusy = true; IsDownloading = true; CanInstall = false; ProgressPercent = 0;
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var directory = Path.Combine(ApplicationUpdateHelper.JobRoot, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory,"update-manifest.json"), _update.ManifestBytes, _operation.Token);
            await File.WriteAllBytesAsync(Path.Combine(directory,"update-manifest.json.sig"), _update.SignatureBytes, _operation.Token);
            var progress = new Progress<ApplicationUpdateProgress>(value =>
            {
                if (!IsDownloading) return;
                ProgressPercent = 100d * value.BytesReceived / value.TotalBytes;
                Status = $"正在下载 {value.BytesReceived/1048576d:0.0} / {value.TotalBytes/1048576d:0.0} MB";
            });
            var file = await _remote.DownloadAsync(_update, _assetName, Path.Combine(directory,_assetName), progress, _operation.Token);
            if (_update.Manifest.Assets.Single(item => item.Name == _assetName).Kind == "installer") AppUpdateTrust.VerifyExecutable(file);
            _downloadDirectory = directory; CanInstall = true; Status = "下载和签名清单校验完成，可以更新并重启。";
        }
        catch (Exception error) { Status = Describe(error,"下载"); _diagnostics.Record(Status); await _dialogs.ShowMessageAsync("软件下载失败", Status, true); }
        finally { IsDownloading = false; IsBusy = false; _operation.Dispose(); _operation = null; }
    }

    [RelayCommand] private async Task InstallUpdateAsync()
    {
        if (!CanInstall || IsBusy || _downloadDirectory is null || _assetName is null || _update is null) return;
        if (UpdateBlockedReason?.Invoke() is { } reason) { await _dialogs.ShowMessageAsync("暂不能更新", reason, true); return; }
        if (!await _dialogs.ConfirmAsync("更新并重启", "将停止当前弹幕服务并退出应用，安装完成后重新打开。请先结束正在进行的下载和核心操作。", "更新并重启")) return;
        IsBusy = true;
        var directory = _downloadDirectory;
        try
        {
            using var parent = Process.GetCurrentProcess();
            var executable = Environment.ProcessPath ?? throw new IOException("无法定位应用程序");
            var kind = ApplicationUpdateHelper.IsInstalled(executable) ? "installer" : "portable";
            var job = new ApplicationUpdateJob(parent.Id, parent.StartTime.ToUniversalTime().Ticks, Path.GetDirectoryName(executable)!, _assetName, kind, AvailableVersion, IsServiceRunningBeforeUpdate?.Invoke() == true);
            File.WriteAllText(Path.Combine(directory,"job.json"), JsonSerializer.Serialize(job));
            var helper = Path.Combine(directory,"DanmuApi.Updater.exe");
            File.Copy(executable, helper, overwrite: false);
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = directory };
            start.ArgumentList.Add("--apply-app-update"); start.ArgumentList.Add(directory);
            using var process = Process.Start(start) ?? throw new IOException("更新助手未启动");
            Status = "正在准备更新；安装版请确认 Windows 权限提示…";
            var watch = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(directory,"helper.ready")))
            {
                if (process.HasExited) throw new IOException(File.Exists(Path.Combine(directory,"error.txt")) ? File.ReadAllText(Path.Combine(directory,"error.txt")) : $"更新助手退出：{process.ExitCode}");
                if (watch.Elapsed > TimeSpan.FromMinutes(3)) throw new TimeoutException("更新助手准备超时");
                await Task.Delay(150);
            }
            if (File.Exists(Path.Combine(directory,"cancel"))) throw new OperationCanceledException("更新准备已取消");
            if (UpdateBlockedReason?.Invoke() is { } blocked) throw new IOException(blocked);
            if (ExitForUpdate is null || !await ExitForUpdate()) throw new IOException("停止服务或退出未成功，更新已取消");
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(directory,"cancel"), "cancel");
            CanInstall = false; Status = "更新未完成：" + error.Message;
            _diagnostics.Record(Status); await _dialogs.ShowMessageAsync("软件更新失败", Status, true);
        }
        finally { IsBusy = false; }
    }

    private static string Describe(Exception error, string operation) => error switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } => "更新仓库尚未公开或发行版不存在（HTTP 404）。",
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } => "GitHub 限制了请求，请稍后手动检查。",
        OperationCanceledException => $"{operation}已取消或达到请求期限。",
        CryptographicException => "更新签名或文件校验失败，已阻止安装。",
        _ => $"{operation}失败：{error.Message}",
    };

    public void Dispose() { _lifetime.Cancel(); _operation?.Cancel(); }
}
