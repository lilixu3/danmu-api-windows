using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DanmuApi.App.Services;
using DanmuApi.Platform;

namespace DanmuApi.App.ViewModels;

public sealed partial class RuntimeMaintenanceViewModel(IRuntimeMaintenanceService service, IUiDialogService dialogs,
    IAppDiagnostics diagnostics) : ViewModelBase
{
    private CancellationTokenSource? _cancellation;
    private int _operation;
    private DependencyStatus _status = DependencyStatus.Unknown;
    // Retain the complete exception chain for diagnostics without binding raw messages to UI/logs.
    public Exception? LastFailure { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    [NotifyCanExecuteChangedFor(nameof(RepairCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;
    [ObservableProperty] private string _statusText = "尚未检查运行依赖";
    [ObservableProperty] private string _countsText = "检查将只读校验随包清单中全部受管文件。";
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isIndeterminate;

    /// <summary>Short label for the compact footer; the full sentence stays in <see cref="StatusText"/>.</summary>
    public string StatusShortText => _status switch
    {
        DependencyStatus.Busy => "检查中",
        DependencyStatus.Healthy => "正常",
        DependencyStatus.Warning => "需修复",
        DependencyStatus.Failed => "失败",
        DependencyStatus.Canceled => "已取消",
        _ => "未检查",
    };
    public bool IsStatusHealthy => _status == DependencyStatus.Healthy;
    public bool IsStatusFailed => _status is DependencyStatus.Failed or DependencyStatus.Warning;

    private void SetStatus(DependencyStatus status, string text)
    {
        _status = status;
        StatusText = text;
        OnPropertyChanged(nameof(StatusShortText));
        OnPropertyChanged(nameof(IsStatusHealthy));
        OnPropertyChanged(nameof(IsStatusFailed));
    }

    private bool CanOperate() => !IsBusy;
    private bool CanCancel() => IsBusy && _cancellation is not null;

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task CheckAsync() => RunAsync(false);

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task RepairAsync() => RunAsync(true);

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cancellation?.Cancel();

    private async Task RunAsync(bool repair)
    {
        if (IsBusy) return;
        IsBusy = true;
        var operation = ++_operation;
        try
        {
            if (repair && !await dialogs.ConfirmAsync("修复运行依赖",
                "请先停止服务。确认后将使用本机随包文件修复 Node、宿主脚本及生产依赖，无需网络。核心、配置和日志将保留。\n" + CountsText,
                "确认修复")) return;
            using var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            CancelCommand.NotifyCanExecuteChanged();
            SetStatus(DependencyStatus.Busy, repair ? "正在修复运行依赖" : "正在完整校验运行依赖");
            ProgressValue = 0;
            IsIndeterminate = true;
            CountsText = "检查结果待更新";
            var progress = new Progress<BundledRuntimeProgress>(p =>
            {
                if (!IsBusy || operation != _operation) return;
                ProgressText = PhaseText(p.Phase) + (p.Total > 0 ? $" · {p.Completed}/{p.Total}" : string.Empty);
                IsIndeterminate = p.Total <= 0;
                ProgressValue = p.Total > 0 ? 100d * p.Completed / p.Total : 0;
            });
            if (repair) await service.RepairAsync(progress, cancellation.Token);
            var result = await service.CheckAsync(progress, cancellation.Token);
            CountsText = $"受管文件 {result.Total} · 缺失 {result.Missing} · 损坏 {result.Damaged}";
            SetStatus(result.IsHealthy ? DependencyStatus.Healthy : DependencyStatus.Warning,
                result.IsHealthy ? (repair ? "修复完成，完整校验通过" : "运行依赖完整，校验通过") : "运行依赖不完整，请停止服务后确认修复");
        }
        catch (OperationCanceledException)
        {
            SetStatus(DependencyStatus.Canceled, "操作已取消，请重新检查确认当前依赖状态");
        }
        catch (Exception error)
        {
            LastFailure = error;
            // Do not expose exception messages: filesystem names and external diagnostics may contain secrets.
            var code = $"{error.GetType().Name} / 0x{error.HResult:X8}";
            var reason = DependencyMaintenanceDiagnostics.Describe(error);
            diagnostics.Record($"运行依赖{(repair ? "修复" : "检查")}失败：{reason}（{code}）");
            SetStatus(DependencyStatus.Failed, $"运行依赖{(repair ? "修复" : "检查")}失败：{reason}（{code}）。");
        }
        finally
        {
            _operation++;
            _cancellation = null;
            IsBusy = false;
            IsIndeterminate = false;
            ProgressText = string.Empty;
        }
    }

    public static string PhaseText(string phase) => phase switch
    {
        "ReadingManifest" => "读取依赖清单",
        "ReadingState" => "读取现有依赖状态",
        "InspectingCleanup" => "检查待清理的修复文件",
        "CheckingTargetMetadata" => "检查目标文件信息",
        "VerifyingTarget" => "校验现有依赖",
        "CheckingConflicts" => "检查文件冲突",
        "BackingUp" => "备份受管文件",
        "WritingJournal" => "写入修复事务记录",
        "Snapshotting" => "记录依赖状态",
        "Committing" => "提交修复事务",
        "CleaningUp" => "清理修复临时文件",
        "Completed" => "依赖准备完成，等待完整校验",
        "Recovering" => "恢复中断的修复事务",
        "VerifyingSource" => "校验随包依赖",
        "Staging" => "暂存修复文件",
        "Replacing" => "替换受管文件",
        _ => "处理运行依赖"
    };
}
