using DanmuApi.Core;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.App.Services;

public sealed record RuntimeVariantSwitchResult(
    bool Succeeded,
    ManagedCoreVariant Variant,
    bool ServiceRunning,
    bool RestoredPreviousSelection,
    string Diagnostic);

public interface IRuntimeVariantSwitchService
{
    /// <summary>把运行核心切换到指定变体（写 variant_override；服务在运行则安全重启）。</summary>
    Task<RuntimeVariantSwitchResult> SwitchAsync(
        ManagedCoreVariant variant,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 「安装后切换」/「切换到此核心」的实现：运行变体由 settings.properties 的 variant_override 决定，
/// 核心进程启动时宿主会把它写进核心 config\.env，因此切换 = 落盘 + （服务在跑时）重启。
/// 服务本来就没运行时只落盘、不擅自启动；重启失败则恢复原选择并重启原核心，
/// 绝不留下"设置指向 A、服务跑着 B"或"服务起不来但界面说切换成功"这类不一致。
/// </summary>
public sealed class RuntimeVariantSwitchService(
    IActiveCoreVariantStore variantStore,
    IRuntimeController runtimeController,
    Action<string>? diagnosticSink = null) : IRuntimeVariantSwitchService
{
    private readonly IActiveCoreVariantStore _variantStore = variantStore ?? throw new ArgumentNullException(nameof(variantStore));
    private readonly IRuntimeController _runtimeController = runtimeController ?? throw new ArgumentNullException(nameof(runtimeController));
    private readonly Action<string>? _diagnostics = diagnosticSink;

    public async Task<RuntimeVariantSwitchResult> SwitchAsync(
        ManagedCoreVariant variant,
        CancellationToken cancellationToken = default)
    {
        var previous = _variantStore.Read();
        if (previous == variant)
        {
            return new RuntimeVariantSwitchResult(
                true, variant, IsRunning(), false, $"运行核心已经是{variant.ToLabel()}");
        }

        var wasRunning = IsRunning();
        _variantStore.Write(variant);
        if (!wasRunning)
        {
            return new RuntimeVariantSwitchResult(
                true, variant, false, false,
                $"运行核心已切换到{variant.ToLabel()}；服务当前未运行，启动后生效。");
        }

        await RestartAsync(cancellationToken).ConfigureAwait(false);
        if (IsRunning())
        {
            return new RuntimeVariantSwitchResult(
                true, variant, true, false, $"运行核心已切换到{variant.ToLabel()}，服务已重新启动。");
        }

        var failure = FailureText();
        var revertFailure = await RevertAsync(previous, cancellationToken).ConfigureAwait(false);
        return new RuntimeVariantSwitchResult(
            false,
            variant,
            IsRunning(),
            revertFailure is null,
            revertFailure is null
                ? $"切换到{variant.ToLabel()}后服务未能运行（{failure}）；已恢复原来的核心选择并重启。"
                : $"切换到{variant.ToLabel()}后服务未能运行（{failure}）；恢复原核心选择也失败：{revertFailure}");
    }

    /// <summary>写回原选择并重启；返回 null 表示恢复成功，否则返回失败原因。</summary>
    private async Task<string?> RevertAsync(ManagedCoreVariant? previous, CancellationToken cancellationToken)
    {
        try
        {
            _variantStore.Write(previous ?? ManagedCoreVariant.Stable);
            await RestartAsync(cancellationToken).ConfigureAwait(false);
            return IsRunning() ? null : FailureText();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _diagnostics?.Invoke($"恢复原运行核心选择失败：{error.Message}");
            return error.Message;
        }
    }

    private async Task RestartAsync(CancellationToken cancellationToken)
    {
        if (_runtimeController.Snapshot.State is DesktopRuntimeState.Running or DesktopRuntimeState.Starting)
        {
            await _runtimeController.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        await _runtimeController.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool IsRunning() => _runtimeController.Snapshot.State == DesktopRuntimeState.Running;

    private string FailureText() =>
        _runtimeController.Snapshot.FailureReason ?? _runtimeController.Snapshot.State.ToString();
}
