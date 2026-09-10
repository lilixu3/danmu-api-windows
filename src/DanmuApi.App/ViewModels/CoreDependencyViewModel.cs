using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DanmuApi.App.Services;
using DanmuApi.Core;

namespace DanmuApi.App.ViewModels;

public sealed partial class CoreDependencyViewModel(ICoreDependencyService service, IUiDialogService dialogs,
    IAppDiagnostics diagnostics) : ViewModelBase
{
    private CancellationTokenSource? _cancellation;
    private int _operation;
    private DependencyStatus _status = DependencyStatus.Unknown;
    public Exception? LastFailure { get; private set; }
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    [NotifyCanExecuteChangedFor(nameof(RepairCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RepairCommand))]
    [NotifyPropertyChangedFor(nameof(ScopeText))]
    private ManagedCoreVariant _variant = ManagedCoreVariant.Stable;
    [ObservableProperty] private string _statusText = "尚未检查核心依赖";
    [ObservableProperty] private string _countsText = "按选中核心的依赖声明检查必需依赖及其入口。";
    [ObservableProperty] private IReadOnlyList<string> _issueDetails = [];
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isIndeterminate;

    public string ScopeText => Variant == ManagedCoreVariant.Custom
        ? "自定义核心可检查依赖，禁止在线自动修复。"
        : "在线修复从固定可信来源下载签名依赖包，仅补齐选中核心本地依赖，保留公共依赖、核心配置和日志。请先停止服务。";

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

    private bool CanCheck() => !IsBusy;
    private bool CanRepair() => !IsBusy && Variant != ManagedCoreVariant.Custom;
    private bool CanCancel() => IsBusy && _cancellation is not null;
    partial void OnVariantChanged(ManagedCoreVariant value)
    {
        SetStatus(DependencyStatus.Unknown, "尚未检查核心依赖");
        CountsText = "按选中核心的依赖声明检查必需依赖及其入口。";
        IssueDetails = [];
    }
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckAsync() => RunAsync(false);
    [RelayCommand(CanExecute = nameof(CanRepair))]
    private Task RepairAsync() => RunAsync(true);
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cancellation?.Cancel();

    private async Task RunAsync(bool repair)
    {
        if (IsBusy || repair && Variant == ManagedCoreVariant.Custom) return;
        IsBusy = true;
        var variant = Variant;
        var operation = ++_operation;
        try
        {
            if (repair && !await dialogs.ConfirmAsync("在线修复核心依赖",
                "请先停止服务。将从固定可信来源下载并验证签名依赖包，仅修改选中核心的 node_modules，保留公共运行依赖、配置和日志。\n缺少可用可信包或版本不兼容时会明确失败。\n" + CountsText,
                "确认下载并修复")) return;
            using var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            CancelCommand.NotifyCanExecuteChanged();
            SetStatus(DependencyStatus.Busy, repair ? "正在在线修复核心依赖" : "正在检查核心依赖");
            CountsText = "检查结果待更新";
            IssueDetails = [];
            ProgressValue = 0;
            IsIndeterminate = true;
            var progress = new Progress<CoreDependencyRepairProgress>(p =>
            {
                if (!IsBusy || operation != _operation) return;
                ProgressText = PhaseText(p.Stage);
                IsIndeterminate = p.TotalBytes is not > 0 || p.CompletedBytes is null;
                ProgressValue = !IsIndeterminate ? 100d * p.CompletedBytes!.Value / p.TotalBytes!.Value : 0;
                if (!IsIndeterminate) ProgressText += $" · {p.CompletedBytes:N0}/{p.TotalBytes:N0} 字节";
            });
            var result = repair ? await service.RepairAsync(variant, progress, cancellation.Token)
                : await service.CheckAsync(variant, cancellation.Token);
            if (Variant != variant) return;
            CountsText = $"检查依赖 {result.Total} · 异常 {result.Issues.Count}";
            IssueDetails = result.Issues.Select(FormatIssue).ToArray();
            SetStatus(result.IsHealthy ? DependencyStatus.Healthy : DependencyStatus.Warning,
                result.IsHealthy ? (repair ? "核心依赖修复完成，校验通过" : "核心必需依赖校验通过") : "核心依赖存在缺失或不兼容，请查看下方详情");
        }
        catch (OperationCanceledException) { SetStatus(DependencyStatus.Canceled, "操作已取消，请重新检查核心依赖状态"); }
        catch (Exception error)
        {
            LastFailure = error;
            var code = $"{error.GetType().Name} / 0x{error.HResult:X8}";
            var reason = DependencyMaintenanceDiagnostics.Describe(error);
            diagnostics.Record($"核心依赖{(repair ? "修复" : "检查")}失败：{reason}（{code}）");
            SetStatus(DependencyStatus.Failed,
                error.Message.StartsWith("核心依赖已修复并生效，但", StringComparison.Ordinal)
                    ? $"{reason}（{code}）。"
                    : $"核心依赖{(repair ? "修复" : "检查")}失败：{reason}（{code}）。");
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

    public static string FormatIssue(CoreDependencyIssue issue)
    {
        // Allow only validated package identifiers/ranges; diagnostics are classified, never echoed with paths or payloads.
        var name = Regex.IsMatch(issue.Name, @"\A(?:@[a-z0-9][a-z0-9._-]*/)?[a-z0-9][a-z0-9._-]*\z") ? issue.Name : "依赖名称无效";
        var range = Regex.IsMatch(issue.Range, @"\A[0-9vVxX*+.^~<>=| -]{1,160}\z") ? issue.Range : "版本声明不可展示";
        var diagnostic = issue.Diagnostic switch
        {
            "Missing required package" => "缺少必需依赖包",
            "Invalid package name/version" => "依赖包名称或版本信息无效",
            "Required package entry is absent or outside its package" => "依赖入口缺失或越界",
            "Dependency links are forbidden" => "依赖路径包含不允许的链接",
            "Dependency escaped core/public directories" => "依赖路径越过允许目录",
            var text when text.StartsWith("Installed version ", StringComparison.Ordinal) => "已安装版本不符合声明",
            var text when text.Contains("ERR_MODULE_NOT_FOUND", StringComparison.Ordinal) || text.StartsWith("Cannot find", StringComparison.Ordinal) => "Node 无法解析依赖入口",
            _ => "依赖元数据或入口检查失败（原始路径与内容已隐藏）"
        };
        return $"{name} · 要求 {range} · {diagnostic}";
    }

    public static string PhaseText(string stage) => stage switch
    {
        "Checking" => "检查核心必需依赖",
        "Manifest" => "验证可信签名清单",
        "Downloading" => "下载签名依赖包",
        "Extracting" => "校验并解压依赖包",
        "Merging" => "补齐核心本地依赖",
        "Replacing" => "替换核心本地依赖",
        "Completed" => "验证最终核心依赖状态",
        _ => "处理核心依赖"
    };
}
