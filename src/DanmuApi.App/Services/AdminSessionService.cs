using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.App.Services;

public sealed record AdminSessionState(
    bool IsAdminMode,
    bool HasAdminTokenConfigured,
    string TokenHint);

public sealed record AdminSessionOperationResult(bool Succeeded, string Diagnostic)
{
    public static AdminSessionOperationResult Success(string diagnostic) => new(true, diagnostic);
    public static AdminSessionOperationResult Failure(string diagnostic) => new(false, diagnostic);
}

/// <summary>
/// 管理员模式会话，语义对齐移动端 AdminSessionRepositoryImpl：
/// 管理员密码正本存在核心 config/.env 的 ADMIN_TOKEN；会话令牌单独 DPAPI 持久化，
/// 重启后仍有效，但一旦 .env 中 ADMIN_TOKEN 被清空或改成其它值，会话立即失效。
/// </summary>
public interface IAdminSessionService
{
    AdminSessionState State { get; }

    /// <summary>.env 读取失败时的显式原因；正常时为 null。管理员功能按不可用处理，但不影响应用启动。</summary>
    string? LoadDiagnostic { get; }

    /// <summary>重读 .env 与本地会话，收敛会话有效性并刷新状态。读取失败不抛出，写入 <see cref="LoadDiagnostic"/>。</summary>
    void Refresh();

    /// <summary>管理员模式下返回会话令牌（核心管理接口路径参数）；否则 null。</summary>
    string? CurrentAdminTokenOrNull();

    /// <summary>校验输入是否等于已配置的 ADMIN_TOKEN，通过则进入管理员模式。</summary>
    AdminSessionOperationResult Login(string inputToken);

    /// <summary>把输入作为新 ADMIN_TOKEN 原子写入 .env（含回读校验），成功后直接进入管理员模式。</summary>
    AdminSessionOperationResult SetAdminTokenAndLogin(string token);

    AdminSessionOperationResult Logout();
}

public sealed class AdminSessionService : IAdminSessionService
{
    private readonly IProtectedStringStore _sessionStore;
    private readonly Func<string> _envPathProvider;
    private readonly Action<string>? _report;
    private string _sessionToken = string.Empty;
    private string _configuredToken = string.Empty;

    public AdminSessionService(
        IProtectedStringStore sessionStore,
        Func<string> envPathProvider,
        Action<string>? diagnosticSink = null)
    {
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _envPathProvider = envPathProvider ?? throw new ArgumentNullException(nameof(envPathProvider));
        _report = diagnosticSink;
        _sessionToken = LoadSessionToken();
        Refresh();
    }

    public AdminSessionState State { get; private set; } = new(false, false, "未配置");

    public string? LoadDiagnostic { get; private set; }

    public void Refresh()
    {
        string configured;
        try
        {
            configured = DotEnvFile.ReadValue(_envPathProvider(), "ADMIN_TOKEN")?.Trim() ?? string.Empty;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or ArgumentException or NotSupportedException)
        {
            // 本服务在启动路径上被 DI 解析（MainWindowViewModel 的构造依赖它）。这里若把异常抛出去，
            // 整个应用会连窗口都建不出来，而故障其实只影响管理员功能。因此显式降级为"不可用"：
            // 保留原因（LoadDiagnostic + 诊断汇），不静默当作"未配置"，也不清掉已保存的会话。
            LoadDiagnostic = $"读取管理员配置失败（{_envPathProvider()}）：{error.GetType().Name}: {error.Message}";
            _configuredToken = string.Empty;
            _report?.Invoke($"管理员会话不可用，已按未配置处理；{LoadDiagnostic}");
            State = new AdminSessionState(false, false, "读取失败");
            return;
        }

        LoadDiagnostic = null;
        _configuredToken = configured;
        if (_sessionToken.Length > 0 &&
            (_configuredToken.Length == 0 || !string.Equals(_sessionToken, _configuredToken, StringComparison.Ordinal)))
        {
            // .env 密码被清空或更换：旧会话不再对应任何有效授权，显式失效而不是继续放行。
            _sessionToken = string.Empty;
            _sessionStore.Clear();
        }

        State = new AdminSessionState(
            _sessionToken.Length > 0 && string.Equals(_sessionToken, _configuredToken, StringComparison.Ordinal),
            _configuredToken.Length > 0,
            MaskToken(_configuredToken));
    }

    private string LoadSessionToken()
    {
        try
        {
            return _sessionStore.Load() ?? string.Empty;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            // 会话文件损坏/被占用同样不能让应用起不来；会话丢失只意味着需要重新输入管理员密码。
            _report?.Invoke($"读取已保存的管理员会话失败，本次按未登录处理：{error.GetType().Name}: {error.Message}");
            return string.Empty;
        }
    }

    public string? CurrentAdminTokenOrNull() => State.IsAdminMode ? _sessionToken : null;

    public AdminSessionOperationResult Login(string inputToken)
    {
        Refresh();
        var candidate = inputToken.Trim();
        if (candidate.Length == 0)
        {
            return AdminSessionOperationResult.Failure("请输入管理员密码");
        }

        if (_configuredToken.Length == 0)
        {
            return AdminSessionOperationResult.Failure("当前未配置管理员密码，请先输入新密码完成配置");
        }

        if (!string.Equals(candidate, _configuredToken, StringComparison.Ordinal))
        {
            return AdminSessionOperationResult.Failure("管理员密码不正确");
        }

        return EnterMode(candidate, "已进入管理员模式");
    }

    public AdminSessionOperationResult SetAdminTokenAndLogin(string token)
    {
        var candidate = token.Trim();
        if (candidate.Length == 0)
        {
            return AdminSessionOperationResult.Failure("管理员密码不能为空");
        }

        var envPath = _envPathProvider();
        var previous = DotEnvFile.ReadValue(envPath, "ADMIN_TOKEN");
        try
        {
            DotEnvFile.UpdateValues(envPath, new Dictionary<string, string?>
            {
                ["ADMIN_TOKEN"] = candidate,
            });
            var readBack = DotEnvFile.ReadValue(envPath, "ADMIN_TOKEN")?.Trim();
            if (!string.Equals(readBack, candidate, StringComparison.Ordinal))
            {
                throw new IOException("管理员密码保存失败：.env 回读校验不一致，请检查文件写入权限");
            }

            return EnterMode(candidate, "管理员密码已保存，并开启管理员模式");
        }
        catch (Exception sessionError)
        {
            try
            {
                DotEnvFile.UpdateValues(envPath, new Dictionary<string, string?>
                {
                    ["ADMIN_TOKEN"] = previous,
                });
                _sessionToken = string.Empty;
                _configuredToken = previous?.Trim() ?? string.Empty;
                State = new AdminSessionState(false, _configuredToken.Length > 0, MaskToken(_configuredToken));
            }
            catch (Exception rollbackError)
            {
                throw new IOException(
                    $"管理员会话保存失败，且恢复原 ADMIN_TOKEN 失败：{rollbackError.Message}",
                    new AggregateException(sessionError, rollbackError));
            }

            throw new IOException("管理员会话保存失败，已恢复原 ADMIN_TOKEN", sessionError);
        }
    }

    public AdminSessionOperationResult Logout()
    {
        _sessionToken = string.Empty;
        _sessionStore.Clear();
        Refresh();
        return AdminSessionOperationResult.Success("已退出管理员模式");
    }

    private AdminSessionOperationResult EnterMode(string token, string diagnostic)
    {
        _sessionToken = token;
        _sessionStore.Save(token);
        Refresh();
        return AdminSessionOperationResult.Success(diagnostic);
    }

    public static string MaskToken(string token)
    {
        var value = token.Trim();
        if (value.Length == 0)
        {
            return "未配置";
        }

        return value.Length <= 4
            ? value[..1] + "***"
            : value[..2] + "***" + value[^2..];
    }
}
