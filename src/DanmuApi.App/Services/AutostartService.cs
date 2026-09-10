using DanmuApi.Platform;

namespace DanmuApi.App.Services;

public sealed record AutostartStatus(bool IsSupported, bool IsEnabled, string Diagnostic,
    AutostartState? State = null, bool? Registered = null)
{
    public AutostartState EffectiveState => State ?? (!IsSupported ? AutostartState.Unsupported
        : IsEnabled ? AutostartState.Registered : AutostartState.NotRegistered);
    public bool? IsRegistered => Registered ?? EffectiveState switch
    {
        AutostartState.Registered or AutostartState.InvalidPath or AutostartState.SystemDisabled => true,
        AutostartState.NotRegistered => false,
        _ => null,
    };
    public bool? EffectiveEnabled => EffectiveState is AutostartState.Unknown or AutostartState.Unsupported
        ? null : EffectiveState == AutostartState.Registered;
    public bool CanToggle => IsSupported && EffectiveState is not (AutostartState.Unknown or AutostartState.Unsupported or AutostartState.SystemDisabled);
}

public sealed record AutostartOperationResult(bool Succeeded, AutostartStatus Status, string Diagnostic)
{
    public static AutostartOperationResult Failure(AutostartStatus status, string diagnostic) =>
        new(false, status, diagnostic);
}

public interface IAutostartService
{
    AutostartStatus GetStatus();
    Task<AutostartOperationResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
    AutostartOperationResult RefreshIfEnabled();
}

public sealed class PlatformAutostartService : IAutostartService
{
    private readonly AutostartManager _manager;

    public PlatformAutostartService(AutostartManager manager)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    public AutostartStatus GetStatus()
    {
        if (!_manager.IsSupported())
            return new(false, false, "仅打包版应用支持开机自启", AutostartState.Unsupported);
        return Map(_manager.IsEnabled());
    }

    public Task<AutostartOperationResult> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = GetStatus();
            if (!before.IsSupported || before.EffectiveState == AutostartState.Unknown ||
                (enabled && before.EffectiveState == AutostartState.SystemDisabled))
                return AutostartOperationResult.Failure(before, before.Diagnostic);
            var operation = enabled ? _manager.Enable() : _manager.Disable();
            var status = GetStatus();
            if (!operation.Succeeded)
            {
                return AutostartOperationResult.Failure(status, operation.Diagnostic);
            }
            if (status.EffectiveState is AutostartState.Unknown or AutostartState.Unsupported)
                return AutostartOperationResult.Failure(status, status.Diagnostic);

            return new AutostartOperationResult(true, status, operation.Diagnostic);
        }, cancellationToken);
    }

    private static AutostartStatus Map(AutostartResult result) => new(result.Supported,
        result.Enabled == true, result.Diagnostic,
        !result.Supported ? AutostartState.Unsupported : !result.Succeeded ? AutostartState.Unknown
        : result.State == AutostartState.Unknown && result.Enabled.HasValue
            ? result.Enabled.Value ? AutostartState.Registered : AutostartState.NotRegistered : result.State,
        result.IsRegistered);

    public AutostartOperationResult RefreshIfEnabled()
    {
        try
        {
            var result = _manager.RefreshIfEnabled();
            var status = Map(result);
            return result.Succeeded
                ? new AutostartOperationResult(true, status, result.Diagnostic)
                : AutostartOperationResult.Failure(status, result.Diagnostic);
        }
        catch (Exception error)
        {
            var status = new AutostartStatus(true, false,
                $"刷新开机自启失败（{error.GetType().Name}, HRESULT=0x{error.HResult:X8}）", AutostartState.Unknown);
            return AutostartOperationResult.Failure(status, status.Diagnostic);
        }
    }
}
