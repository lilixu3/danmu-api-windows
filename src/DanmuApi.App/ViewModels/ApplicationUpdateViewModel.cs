using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DanmuApi.App.Services;
using DanmuApi.Core;
using DanmuApi.Core.ApplicationUpdates;
using DanmuApi.Platform;

namespace DanmuApi.App.ViewModels;

public sealed partial class ApplicationUpdateViewModel : ViewModelBase, IDisposable, IForegroundUpdateCheck
{
    /// <summary>自动检查（进前台、启动后定时器）的冷却：命中就只改状态文案，绝不发网络请求。</summary>
    public static readonly TimeSpan AutomaticCheckCooldown = TimeSpan.FromMinutes(30);

    /// <summary>「已发现新版本」这条结果跨重启复用的有效期；过期后交给下一次自动检查刷新。</summary>
    public static readonly TimeSpan DiscoveryTtl = TimeSpan.FromDays(1);

    private const string LastCheckKey = "app_update_last_check_ms";
    private const string FoundVersionKey = "app_update_found_version";
    private const string FoundAtKey = "app_update_found_at_ms";

    private readonly ApplicationUpdateService _remote;
    private readonly ISettingsStore _settings;
    private readonly IUiDialogService _dialogs;
    private readonly IDesktopNotificationService _notifications;
    private readonly IAppDiagnostics _diagnostics;
    private readonly TimeProvider _clock;
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

    public ApplicationUpdateViewModel(
        ISettingsStore settings,
        IUiDialogService dialogs,
        IDesktopNotificationService notifications,
        IAppDiagnostics diagnostics,
        ApplicationUpdateService? remote = null,
        TimeProvider? clock = null,
        IGithubTokenProvider? tokenProvider = null)
    {
        _settings = settings; _dialogs = dialogs; _notifications = notifications; _diagnostics = diagnostics;
        // 软件更新走的是 api.github.com，会消耗每小时配额；已配置 Token 时必须用上，
        // 否则就是"存了 Token、核心检查额度刷新了，软件更新仍报超限"。
        // Token 只会挂到 api.github.com（GithubTokenPolicy），资产 CDN 与重定向都不带。
        _remote = remote ?? new ApplicationUpdateService(AppUpdateTrust.PublicKey(), tokenProvider: tokenProvider);
        _clock = clock ?? TimeProvider.System;
        _loading = true;
        var values = settings.Read();
        AutomaticChecks = !values.TryGetValue("app_update_auto", out var enabled) || bool.Parse(enabled);
        RestoreDiscovery(values);
        _loading = false;
    }

    /// <summary>
    /// 重启后把「上次发现的版本」接回来：侧栏卡片与关于页因此不必重新联网，
    /// 也不会出现「一重启就又正在检查」。存量值读不动时按「没有发现」降级并记一条诊断：
    /// 丢的只是一个可选缓存（代价是下一次自动检查照常进行），不该让应用启动失败。
    /// </summary>
    private void RestoreDiscovery(IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue(FoundVersionKey, out var stored) || string.IsNullOrWhiteSpace(stored))
        {
            return;
        }

        if (!long.TryParse(values.TryGetValue(FoundAtKey, out var storedAt) ? storedAt : null,
                NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) || milliseconds <= 0)
        {
            // 缓存被写坏时不能默默当成"没有发现"：记一条诊断，代价只是下一次自动检查照常进行。
            _diagnostics.Record($"软件更新缓存时间无效，按未发现更新处理：{storedAt}");
            return;
        }

        var version = stored.Trim();
        TimeSpan age;
        try
        {
            age = _clock.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            _diagnostics.Record($"软件更新缓存时间无效，按未发现更新处理：{storedAt}");
            return;
        }

        if (age < TimeSpan.Zero || age > DiscoveryTtl) return;
        if (string.Equals(version, CurrentVersion, StringComparison.OrdinalIgnoreCase)) return;
        if (values.TryGetValue("app_update_skipped", out var skipped) && skipped == version) return;

        AvailableVersion = version;
        HasUpdate = true;
        Status = $"发现新测试版 {version}（{(int)age.TotalMinutes} 分钟前检查过）。点「下载更新」会先联网确认一次再下载。";
    }

    private DateTimeOffset? ReadLastCheck()
    {
        if (!_settings.Read().TryGetValue(LastCheckKey, out var raw) || string.IsNullOrWhiteSpace(raw)) return null;
        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) || milliseconds <= 0)
        {
            _diagnostics.Record($"软件更新检查时间无效，按未检查过处理：{raw}");
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }

    private void WriteLastCheck()
    {
        try
        {
            _settings.Write(new Dictionary<string, string?>
            {
                [LastCheckKey] = _clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            });
        }
        catch (Exception error)
        {
            _diagnostics.Record("记录软件更新检查时间失败（不影响本次结果）", error);
        }
    }

    /// <summary>把「发现了哪个版本」落盘；写不动只记诊断，不改变本次检查结论。</summary>
    private void PersistDiscovery(string? version)
    {
        try
        {
            _settings.Write(new Dictionary<string, string?>
            {
                [FoundVersionKey] = version,
                [FoundAtKey] = version is null
                    ? null
                    : _clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            });
        }
        catch (Exception error)
        {
            _diagnostics.Record("记录软件更新发现结果失败（不影响本次结果）", error);
        }
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
                await Dispatcher.UIThread.InvokeAsync(CheckOnForegroundAsync);
                await Task.Delay(TimeSpan.FromHours(6), _lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { _diagnostics.Record("软件自动更新检查循环停止", error); Dispatcher.UIThread.Post(() => Status = "自动检查已停止：" + error.Message); }
    }

    [RelayCommand] private Task CheckForUpdatesAsync() => CheckAsync(manual: true);

    /// <summary>
    /// 进前台时的静默检查入口：受冷却约束、且尊重「自动检查」开关，正在忙时直接让路。
    /// 与启动后的定时器共用同一条冷却，所以两套触发不会互相重复检查。
    /// </summary>
    public Task CheckOnForegroundAsync()
    {
        if (!AutomaticChecks || IsBusy || !UpdateCheckCadence.IsDue(_clock, ReadLastCheck(), AutomaticCheckCooldown))
        {
            return Task.CompletedTask;
        }

        return CheckAsync(manual: false);
    }

    private async Task CheckAsync(bool manual)
    {
        if (IsBusy) return;
        if (!manual && !UpdateCheckCadence.IsDue(_clock, ReadLastCheck(), AutomaticCheckCooldown))
        {
            // 冷却命中：不发请求，也不覆盖已有发现结果的文案。
            if (!HasUpdate) Status = $"{(int)AutomaticCheckCooldown.TotalMinutes} 分钟内已检查过，未重复检查；点「检查更新」可立即重查。";
            return;
        }

        IsBusy = true; Status = "正在检查 GitHub 测试发行版…";
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation.CancelAfter(TimeSpan.FromSeconds(45));
        var attempted = false;
        try
        {
            attempted = true;
            var update = await _remote.CheckAsync(CurrentVersion, _operation.Token);
            if (update is null)
            {
                HasUpdate = false; Status = "当前已是最新版本。";
                PersistDiscovery(null);
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
            // 「发现结果」按最终是否要展示给用户来落盘；已跳过的版本不重启后又冒出来。
            PersistDiscovery(HasUpdate ? AvailableVersion : null);
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
        finally
        {
            // 只有真发起过一次检查才记时间：冷却命中时不写，否则窗口会被一次次往后推。
            if (attempted) WriteLastCheck();
            _operation.Dispose(); _operation = null; IsBusy = false;
        }
    }

    [RelayCommand] private void SkipVersion()
    {
        // 重启后从设置里恢复的发现结果没有内存清单（_update 为 null），但版本号是真实的：
        // 跳过只看 AvailableVersion，否则侧栏卡片的「跳过这个版本」会点了没反应、下次又回来。
        if (IsBusy || string.IsNullOrWhiteSpace(AvailableVersion)) return;
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
        if (IsBusy) return;
        if (_update is null || _assetName is null)
        {
            // 重启后的卡片是从缓存恢复的，手里没有签名清单和下载地址。
            // 用户点「下载更新」就是明确要动网络，所以这里做一次**手动**检查（不受冷却约束）。
            await CheckAsync(manual: true).ConfigureAwait(true);
            if (_update is null || _assetName is null)
            {
                Status = "没能取到可用的更新清单，请稍后再试或再点一次「检查更新」。";
                _diagnostics.Record(Status);
                return;
            }
        }

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
        // 这条以前只写"稍后手动检查"，用户看不出为什么"存了 Token 也照样超限"：
        // GitHub 的匿名额度与 Token 额度是两个独立配额桶，而更新检查以前从不带 Token。
        // 现在已配置 Token 时会带上，这条提示改为明确说明两种情形与出路。
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } =>
            "GitHub 限制了请求（每小时 60 次的匿名额度已用尽，与 Token 额度是两套独立配额）。"
            + "可稍后重试，或在「设置 → 网络与 GitHub」配置 Token 后立即重试；配置后本检查也会使用该 Token。",
        OperationCanceledException => $"{operation}已取消或达到请求期限。",
        CryptographicException => "更新签名或文件校验失败，已阻止安装。",
        _ => $"{operation}失败：{error.Message}",
    };

    public void Dispose() { _lifetime.Cancel(); _operation?.Cancel(); }
}
