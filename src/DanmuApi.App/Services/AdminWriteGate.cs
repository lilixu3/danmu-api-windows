namespace DanmuApi.App.Services;

public interface IAdminWriteGate
{
    Action? NavigateToSecurity { get; set; }

    Task<bool> EnsureAsync(string action, CancellationToken cancellationToken = default);
}

public sealed class AdminWriteGate : IAdminWriteGate
{
    private readonly IAdminSessionService _adminSession;
    private readonly IUiDialogService _dialogService;
    private readonly IAppDiagnostics _diagnostics;

    public AdminWriteGate(
        IAdminSessionService adminSession,
        IUiDialogService dialogService,
        IAppDiagnostics diagnostics)
    {
        _adminSession = adminSession ?? throw new ArgumentNullException(nameof(adminSession));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    public Action? NavigateToSecurity { get; set; }

    public async Task<bool> EnsureAsync(string action, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _adminSession.Refresh();
        }
        catch (IOException error)
        {
            var diagnostic = $"读取管理员配置失败：{error.Message}";
            _diagnostics.Record(diagnostic);
            await _dialogService.ShowMessageAsync("管理员模式", diagnostic, isError: true).ConfigureAwait(true);
            return false;
        }
        catch (UnauthorizedAccessException error)
        {
            var diagnostic = $"读取管理员配置权限不足：{error.Message}";
            _diagnostics.Record(diagnostic);
            await _dialogService.ShowMessageAsync("管理员模式", diagnostic, isError: true).ConfigureAwait(true);
            return false;
        }
        catch (FormatException error)
        {
            var diagnostic = $"管理员配置格式错误：{error.Message}";
            _diagnostics.Record(diagnostic);
            await _dialogService.ShowMessageAsync("管理员模式", diagnostic, isError: true).ConfigureAwait(true);
            return false;
        }

        if (_adminSession.State.IsAdminMode)
        {
            return true;
        }

        var goSettings = await _dialogService.ConfirmAdminModeRequiredAsync(
            $"{action}属于管理员写操作。" +
            (_adminSession.State.HasAdminTokenConfigured
                ? "请先输入管理员密码开启管理员模式。"
                : "请先配置管理员密码并开启管理员模式。"))
            .ConfigureAwait(true);
        if (goSettings)
        {
            NavigateToSecurity?.Invoke();
        }

        return false;
    }
}
