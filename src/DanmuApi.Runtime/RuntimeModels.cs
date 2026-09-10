using System.Globalization;
using System.Text;

namespace DanmuApi.Runtime;

public enum DesktopRuntimeState
{
    Stopped,
    Preparing,
    Starting,
    Running,
    Stopping,
    CoreSetupRequired,
    Failed,
}

public sealed record StartConfig(
    string NodeExe,
    string ScriptDir,
    int Port = 9321,
    string ListenHost = "0.0.0.0",
    string Variant = "stable",
    string? AdminToken = null,
    string? IdentityFile = null,
    TimeSpan? StartupTimeout = null,
    TimeSpan? ShutdownTimeout = null)
{
    public TimeSpan EffectiveStartupTimeout => StartupTimeout ?? TimeSpan.FromSeconds(30);
    public TimeSpan EffectiveShutdownTimeout => ShutdownTimeout ?? TimeSpan.FromSeconds(10);
}

public sealed record RuntimeSnapshot(
    DesktopRuntimeState State,
    int? Port = null,
    long? Pid = null,
    string? RuntimeIdentity = null,
    string? FailureReason = null,
    int? ExitCode = null);

public enum AdoptionFailureKind
{
    NotFound,
    InvalidHealth,
    NotOwned,
    InvalidPid,
    ProcessUnavailable,
    InvalidState,
    InvalidConfiguration,
}

public sealed record AdoptionResult(
    bool Succeeded,
    RuntimeSnapshot Snapshot,
    string Diagnostic,
    AdoptionFailureKind? FailureKind = null)
{
    public static AdoptionResult Success(RuntimeSnapshot snapshot, string diagnostic = "已认领运行中的 Node 实例") =>
        new(true, snapshot, diagnostic);

    public static AdoptionResult Failure(
        AdoptionFailureKind kind,
        RuntimeSnapshot snapshot,
        string diagnostic) =>
        new(false, snapshot, diagnostic, kind);
}

public sealed record AccessControlSummary(
    string? Mode,
    int? WhitelistCount,
    int? BlacklistCount,
    long? BlockedRequests);

public sealed record RuntimeHealthSnapshot(
    long? Pid,
    string? Node,
    long? UptimeSec,
    string? Host,
    int? MainPort,
    int? ProxyPort,
    string? Cwd,
    string? EnvHome,
    string? ResolvedHome,
    string? CacheProbeDir,
    bool? CacheProbeWritable,
    string? Variant,
    string? VariantLabel,
    string? RuntimeIdentity,
    long? RequestCount,
    long? LastRequestAt,
    string? LastRequestPath,
    string? LastClientIp,
    decimal? EnvFileMtimeMs,
    string? LogFile,
    string? LogLevel,
    AccessControlSummary? AccessControl);

public enum HealthFailureKind
{
    InvalidRequest,
    Connection,
    HttpStatus,
    InvalidDocument,
}

public sealed class RuntimeHealthException : IOException
{
    public RuntimeHealthException(HealthFailureKind kind, string message, Exception? inner = null, int? statusCode = null)
        : base(message, inner)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    public HealthFailureKind Kind { get; }
    public int? StatusCode { get; }
}

public static class RuntimeDefaults
{
    public const int Port = 9321;
    public const string ListenHost = "0.0.0.0";
    public const string Variant = "stable";
    public const string FallbackToken = "87654321";
    public const int MaxHealthBodyBytes = 1_048_576;
}

public sealed record RuntimeConfig(int Port, string ListenHost, string Variant);

public sealed record RuntimeFirewallResult(
    bool Succeeded,
    bool RulePresent,
    bool AuthorizationAttempted,
    string Diagnostic)
{
    public static RuntimeFirewallResult Success(string diagnostic, bool authorizationAttempted = false) =>
        new(true, true, authorizationAttempted, diagnostic);

    public static RuntimeFirewallResult Failure(string diagnostic, bool authorizationAttempted = false) =>
        new(false, false, authorizationAttempted, diagnostic);
}

public interface IRuntimeFirewall
{
    Task<RuntimeFirewallResult> EnsureInboundRuleAsync(
        string nodeExe,
        CancellationToken cancellationToken = default);
}

public static class RuntimeValidation
{
    public static void ValidatePort(int port)
    {
        if (port is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "端口必须在 1 到 65535 之间");
        }
    }

    public static void ValidateHost(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (host.Any(char.IsWhiteSpace) || host.Contains("//", StringComparison.Ordinal) || host.Contains('/', StringComparison.Ordinal) || host.Contains('?', StringComparison.Ordinal) || host.Contains('#', StringComparison.Ordinal))
        {
            throw new ArgumentException("监听地址必须是主机名或 IP 地址，不能包含 URI 或空白字符", nameof(host));
        }
    }

    public static void ValidateVariant(string variant)
    {
        if (!string.Equals(variant, "stable", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(variant, "dev", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(variant, "custom", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("核心变体必须是 stable、dev 或 custom", nameof(variant));
        }
    }

    public static string CanonicalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
