using DanmuApi.Core;

namespace DanmuApi.App.Services;

public sealed record GithubTokenConfigurationState(
    bool Configured,
    string Hint,
    GithubRateLimit? RateLimit);

public sealed record GithubTokenConfigurationResult(
    bool Succeeded,
    bool Changed,
    string Diagnostic,
    GithubTokenConfigurationState State);

public interface IGithubTokenConfigurationService
{
    GithubTokenConfigurationState GetState();
    Task<GithubTokenConfigurationResult> ValidateAndSaveAsync(
        string token,
        CancellationToken cancellationToken = default);
    Task<GithubTokenConfigurationResult> ClearAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// GitHub Token 事务边界：验证成功后才写 DPAPI 存储；保存后再读额度，额度失败不回滚已验证 Token。
/// 所有返回和诊断均不得包含 Token 原文。
/// </summary>
public sealed class GithubTokenConfigurationService : IGithubTokenConfigurationService
{
    private readonly IGithubTokenStore _store;
    private readonly IGithubCoreRemote _remote;
    private readonly IAppDiagnostics _diagnostics;
    private GithubRateLimit? _lastRateLimit;
    private bool _rateLimitKnown;

    public GithubTokenConfigurationService(
        IGithubTokenStore store,
        IGithubCoreRemote remote,
        IAppDiagnostics diagnostics)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    public GithubTokenConfigurationState GetState()
    {
        var current = _store.GetToken();
        return new GithubTokenConfigurationState(
            !string.IsNullOrWhiteSpace(current),
            Mask(current),
            _rateLimitKnown ? _lastRateLimit : _remote.LastRateLimit);
    }

    public async Task<GithubTokenConfigurationResult> ValidateAndSaveAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(token);
        if (normalized.Length == 0)
        {
            return Failure("GitHub Token 不能为空");
        }

        string? login;
        try
        {
            login = await _remote.ValidateTokenAsync(normalized, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure("GitHub Token 验证已取消");
        }
        catch (Exception error)
        {
            var validationDiagnostic = $"GitHub Token 验证失败：{Redact(Describe(error), normalized)}";
            _diagnostics.Record(validationDiagnostic);
            return Failure(validationDiagnostic);
        }

        try
        {
            _store.Save(normalized);
        }
        catch (Exception error)
        {
            var storageDiagnostic = $"GitHub Token 安全存储失败：{Redact(Describe(error), normalized)}";
            _diagnostics.Record(storageDiagnostic);
            return Failure(storageDiagnostic);
        }

        GithubRateLimit? rateLimit = null;
        string? rateDiagnostic = null;
        try
        {
            rateLimit = await _remote.GetRateLimitAsync(cancellationToken).ConfigureAwait(false);
            _lastRateLimit = rateLimit;
            _rateLimitKnown = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            rateDiagnostic = "额度刷新已取消";
        }
        catch (Exception error)
        {
            rateDiagnostic = $"额度刷新失败：{Redact(Describe(error), normalized)}";
            _diagnostics.Record(rateDiagnostic);
        }

        var account = string.IsNullOrWhiteSpace(login) ? "未知" : login;
        var diagnostic = rateLimit is null
            ? $"GitHub Token 已验证并保存（账号：{account}）；{rateDiagnostic ?? "额度尚未读取"}。"
            : $"GitHub Token 已验证并保存（账号：{account}）；剩余额度 {rateLimit.Remaining}/{rateLimit.Limit}。";
        return new GithubTokenConfigurationResult(
            true,
            true,
            diagnostic,
            new GithubTokenConfigurationState(true, Mask(normalized), rateLimit));
    }

    public Task<GithubTokenConfigurationResult> ClearAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _store.Clear();
            _lastRateLimit = null;
            _rateLimitKnown = true;
            return Task.FromResult(new GithubTokenConfigurationResult(
                true,
                true,
                "GitHub Token 已清除，后续请求将使用未认证额度。",
                new GithubTokenConfigurationState(false, "未配置", null)));
        }
        catch (Exception error)
        {
            var current = SafeReadStoredToken();
            var diagnostic = $"清除 GitHub Token 失败：{RedactKnown(Describe(error), current)}";
            _diagnostics.Record(diagnostic);
            return Task.FromResult(Failure(diagnostic));
        }
    }

    private GithubTokenConfigurationResult Failure(string diagnostic) =>
        new(false, false, diagnostic, SafeGetState());

    private GithubTokenConfigurationState SafeGetState()
    {
        try
        {
            return GetState();
        }
        catch (Exception error)
        {
            _diagnostics.Record($"读取 GitHub Token 配置状态失败：{Describe(error)}");
            return new GithubTokenConfigurationState(false, "状态读取失败", _remote.LastRateLimit);
        }
    }

    public static string Mask(string? token)
    {
        var value = token?.Trim() ?? string.Empty;
        return value.Length switch
        {
            0 => "未配置",
            <= 4 => new string('•', value.Length),
            _ => $"{value[..2]}••••{value[^2..]}",
        };
    }

    private static string Normalize(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var value = token.Trim();
        return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? value[7..].Trim()
            : value.StartsWith("token ", StringComparison.OrdinalIgnoreCase)
                ? value[6..].Trim()
                : value;
    }

    private string? SafeReadStoredToken()
    {
        try
        {
            return _store.GetToken();
        }
        catch
        {
            return null;
        }
    }

    private static string RedactKnown(string value, string? token) =>
        string.IsNullOrWhiteSpace(token) ? value : Redact(value, token);

    private static string Redact(string value, string token) =>
        value.Replace(token, "***", StringComparison.Ordinal)
            .Replace(Uri.EscapeDataString(token), "***", StringComparison.Ordinal);

    private static string Describe(Exception error) =>
        string.IsNullOrWhiteSpace(error.Message)
            ? error.GetType().Name
            : error.Message.Replace('\r', ' ').Replace('\n', ' ');
}
