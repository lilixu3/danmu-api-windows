using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DanmuApi.Core;

namespace DanmuApi.App.ViewModels;

/// <summary>
/// 侧栏「有更新」卡片点开的快速更新弹窗。
/// 核心与软件两条流程共用同一套按钮/进度语义，动作全部转调既有的
/// <see cref="CorePageViewModel.ApplyUpdateAsync(CoreUpdateCheckResult)"/> 与
/// <see cref="ApplicationUpdateViewModel"/> 命令，这里不重复实现下载与安装。
/// </summary>
public sealed partial class QuickUpdateDialogViewModel : ObservableObject, IDisposable
{
    /// <summary>24×24 的 SVG 路径数据（芯片：核心；向下箭头＋托盘：软件）。</summary>
    public const string CoreIcon =
        "M7 7h10v10H7V7zm2 2v6h6V9H9zM9 3h2v3H9V3zm4 0h2v3h-2V3zM9 18h2v3H9v-3zm4 0h2v3h-2v-3z" +
        "M3 9h3v2H3V9zm0 4h3v2H3v-2zm15-4h3v2h-3V9zm0 4h3v2h-3v-2z";
    public const string ApplicationIcon = "M11 3h2v8h3l-4 4.5L8 11h3V3zM5 18h14v3H5v-3z";

    private readonly ApplicationUpdateViewModel? _app;
    private readonly Func<Task>? _applyCoreAsync;
    private readonly Action? _openCorePage;
    private bool _raisingClose;

    private QuickUpdateDialogViewModel(
        string kind,
        string icon,
        string title,
        string summary,
        string detail,
        ApplicationUpdateViewModel? app,
        Func<Task>? applyCoreAsync,
        Action? openCorePage)
    {
        Kind = kind;
        Icon = icon;
        Title = title;
        Summary = summary;
        Detail = detail;
        _app = app;
        _applyCoreAsync = applyCoreAsync;
        _openCorePage = openCorePage;
        if (_app is not null) _app.PropertyChanged += OnAppPropertyChanged;
        RefreshAppSurface();
    }

    public static QuickUpdateDialogViewModel ForApplication(ApplicationUpdateViewModel app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return new QuickUpdateDialogViewModel(
            "app",
            ApplicationIcon,
            "软件可更新",
            $"{app.CurrentVersion} → {app.AvailableVersion}",
            string.Join(' ', new[] { app.PackageText, app.PublishedText }
                .Where(part => !string.IsNullOrWhiteSpace(part))),
            app,
            applyCoreAsync: null,
            openCorePage: null);
    }

    public static QuickUpdateDialogViewModel ForCore(
        ManagedCoreVariant variant,
        string localSha,
        string remoteSha,
        string remoteTitle,
        Func<Task> applyCoreAsync,
        Action openCorePage)
    {
        ArgumentNullException.ThrowIfNull(applyCoreAsync);
        ArgumentNullException.ThrowIfNull(openCorePage);
        return new QuickUpdateDialogViewModel(
            "core",
            CoreIcon,
            $"核心可更新 · {variant.ToLabel()}",
            $"{CoreShortSha(localSha)} → {CoreShortSha(remoteSha)}",
            string.IsNullOrWhiteSpace(remoteTitle) ? "上游有新提交，更新会替换当前核心目录，配置与日志不受影响。" : remoteTitle,
            app: null,
            applyCoreAsync,
            openCorePage);
    }

    private static string CoreShortSha(string sha) => sha.Length > 7 ? sha[..7] : sha;

    public string Kind { get; }
    public string Icon { get; }
    public string Title { get; }
    public string Summary { get; }
    public string Detail { get; }
    public bool IsAppFlow => Kind == "app";
    public bool IsCoreFlow => Kind == "core";
    public string ReleaseNotes => _app?.ReleaseNotes ?? "";
    public bool HasReleaseNotes => !string.IsNullOrWhiteSpace(ReleaseNotes);

    /// <summary>弹窗关闭由这里发起：更新完成、跳过版本或用户点「稍后」。</summary>
    public event EventHandler? CloseRequested;

    [ObservableProperty] private string _primaryText = "";
    [ObservableProperty] private bool _canPrimary = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private string _status = "";

    public string SecondaryText => IsAppFlow ? "跳过这个版本" : "核心页的更多操作…";
    public string TertiaryText => "稍后再说";
    public bool CanSkip => IsAppFlow && !IsBusy;

    /// <summary>只有真的在跑检查/下载时才给「取消」，否则弹窗上会挂一个点了没反应的按钮。</summary>
    public bool CanCancel => IsAppFlow && IsBusy;

    [RelayCommand]
    private async Task PrimaryAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (IsCoreFlow)
        {
            // 先关窗再开始：线路确认与下载进度都是主窗口的模态框，这个弹窗留着就会和它们叠成两层，
            // 用户会看到两层遮罩、还可能点到被压在下面的那一个。失败时卡片不会消失，再点一次即可。
            var apply = _applyCoreAsync!;
            RaiseClose();
            await apply();
            return;
        }

        if (_app is not { } app) return;
        if (app.CanInstall) await app.InstallUpdateCommand.ExecuteAsync(null);
        else await app.DownloadUpdateCommand.ExecuteAsync(null);
        // 安装会退出进程；下载失败或取消时留在弹窗里，让用户看到具体原因。
    }

    [RelayCommand]
    private void Secondary()
    {
        if (IsAppFlow && _app is { } app)
        {
            app.SkipVersionCommand.Execute(null);
            RaiseClose();
            return;
        }

        _openCorePage?.Invoke();
        RaiseClose();
    }

    [RelayCommand]
    private void Tertiary() => RaiseClose();

    [RelayCommand]
    private void Cancel() => _app?.CancelCommand.Execute(null);

    private void RaiseClose()
    {
        if (_raisingClose) return;
        _raisingClose = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnAppPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ApplicationUpdateViewModel.HasUpdate)
            or nameof(ApplicationUpdateViewModel.AvailableVersion)
            or nameof(ApplicationUpdateViewModel.CanInstall)
            or nameof(ApplicationUpdateViewModel.IsBusy)
            or nameof(ApplicationUpdateViewModel.IsDownloading)
            or nameof(ApplicationUpdateViewModel.ProgressPercent)
            or nameof(ApplicationUpdateViewModel.Status)
            or nameof(ApplicationUpdateViewModel.ReleaseNotes)
            or nameof(ApplicationUpdateViewModel.PackageText))
        {
            RefreshAppSurface();
        }

        // 跳过或另一处把更新状态清掉以后，弹窗没有存在的意义了。
        if (args.PropertyName == nameof(ApplicationUpdateViewModel.HasUpdate) && _app?.HasUpdate == false)
        {
            RaiseClose();
        }
    }

    private void RefreshAppSurface()
    {
        OnPropertyChanged(nameof(ReleaseNotes));
        OnPropertyChanged(nameof(HasReleaseNotes));
        if (_app is not { } app)
        {
            PrimaryText = "立即更新核心";
            CanPrimary = true;
            return;
        }

        PrimaryText = app.CanInstall ? "更新并重启" : "下载更新包";
        CanPrimary = !app.IsBusy;
        IsBusy = app.IsBusy;
        ProgressPercent = app.ProgressPercent;
        ShowProgress = app.IsDownloading;
        Status = app.Status;
        OnPropertyChanged(nameof(CanSkip));
        OnPropertyChanged(nameof(CanCancel));
    }

    public void Dispose()
    {
        if (_app is not null) _app.PropertyChanged -= OnAppPropertyChanged;
    }
}
