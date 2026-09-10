using DanmuApi.App.ViewModels;
using DanmuApi.Core;

namespace DanmuApi.App.Services;

/// <summary>核心依赖核对结论，供核心页状态带展示（不再有"永远未检查"的占位状态）。</summary>
public sealed record CoreDependencyHealth(bool Checked, bool Healthy, int MissingCount)
{
    public static CoreDependencyHealth Unknown { get; } = new(false, false, 0);
}

/// <summary>
/// 核心依赖提醒：随包依赖只覆盖宿主自己用到的那批包，核心升级后如果声明了新依赖，
/// 这些包不会跟着随包出现。移动端也没有在线修复通道，做法是核对后提醒用户。
/// 这里同样只做只读探测与一次提醒：不提供手动检查或修复入口，不写核心目录，
/// 探测失败按诊断记录，不改变核心操作结果。
/// </summary>
public sealed class CoreDependencyVerifier(
    ICoreDependencyService service,
    IUiDialogService dialogs,
    IDesktopNotificationService notifications,
    IAppDiagnostics diagnostics)
{
    private readonly object _sync = new();
    private readonly HashSet<string> _notified = new(StringComparer.Ordinal);

    public async Task<CoreDependencyHealth> VerifyAsync(ManagedCoreVariant variant, CancellationToken cancellationToken = default)
    {
        var result = await service.CheckAsync(variant, cancellationToken).ConfigureAwait(false);
        var health = new CoreDependencyHealth(true, result.IsHealthy, result.Issues.Count);
        if (result.IsHealthy)
        {
            return health;
        }

        var missing = string.Join("、", result.Issues.Select(issue => issue.Name).Distinct(StringComparer.Ordinal));
        diagnostics.Record($"核心依赖核对发现 {result.Issues.Count} 项缺失或不兼容：{missing}");
        // 同一变体与同一批缺失只提醒一次，避免每次操作都重复弹窗。
        lock (_sync)
        {
            if (!_notified.Add($"{variant}:{missing}"))
            {
                return health;
            }
        }

        var detail = result.Issues.Count <= 8
            ? string.Join("\n", result.Issues.Select(CoreDependencyViewModel.FormatIssue))
            : string.Join("\n", result.Issues.Take(8).Select(CoreDependencyViewModel.FormatIssue)) + $"\n……另有 {result.Issues.Count - 8} 项";
        var message = $"{VariantLabel(variant)}的依赖声明里有 {result.Issues.Count} 项在随包运行依赖中找不到：\n\n{detail}\n\n"
            + "Windows 端不提供在线依赖修复，请等核心发布方补充依赖或改用不依赖这些包的版本。";

        await notifications.ShowAsync(
            DesktopNotificationKind.CoreDependencyMissing,
            "核心依赖缺失",
            $"{VariantLabel(variant)}缺少 {result.Issues.Count} 项依赖，服务可能启动失败。",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await dialogs.ShowMessageAsync("核心依赖缺失", message, isError: true).ConfigureAwait(false);
        return health;
    }

    private static string VariantLabel(ManagedCoreVariant variant) => variant switch
    {
        ManagedCoreVariant.Stable => "稳定核心",
        ManagedCoreVariant.Custom => "自定义核心",
        _ => variant.ToString(),
    };
}
