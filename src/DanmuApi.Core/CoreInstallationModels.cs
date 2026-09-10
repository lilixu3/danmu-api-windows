namespace DanmuApi.Core;

public enum ManagedCoreVariant
{
    Stable,
    Custom,
}

public enum CoreInstallKind
{
    Branch,
    Commit,
    PullRequest,
    Reinstall,
    Rollback,
}

public enum CoreInstallStage
{
    Downloading,
    Extracting,
    Validating,
    Replacing,
    Completed,
}

public sealed record CoreInstallationManifest(
    int SchemaVersion,
    ManagedCoreVariant Variant,
    string Repository,
    string Branch,
    string CommitSha,
    string? Version,
    string DisplayName,
    CoreInstallKind InstallKind,
    int? PullRequestNumber,
    DateTimeOffset InstalledAt)
{
    public const int CurrentSchemaVersion = 1;
    public string ShortSha => CommitSha.Length > 7 ? CommitSha[..7] : CommitSha;
}

public sealed record CoreInstallationInfo(
    ManagedCoreVariant Variant,
    string Directory,
    bool IsInstalled,
    bool IsValid,
    string? Version,
    CoreInstallationManifest? Manifest,
    string? Diagnostic);

public sealed record CoreInstallRequest(
    ManagedCoreVariant Variant,
    GithubRepositoryReference Repository,
    string Branch,
    string CommitSha,
    string DisplayName,
    CoreInstallKind Kind,
    string ProxyId,
    int? PullRequestNumber = null);

public sealed record CoreInstallProgress(
    CoreInstallStage Stage,
    string Detail,
    long? DownloadedBytes = null,
    long? TotalBytes = null,
    string? RouteLabel = null);

public sealed record CoreVersionRecord(
    string Id,
    ManagedCoreVariant Variant,
    string Directory,
    CoreInstallationManifest Manifest);

public interface ICoreInstaller
{
    CoreInstallationInfo Inspect(ManagedCoreVariant variant);
    IReadOnlyList<CoreVersionRecord> GetHistory(ManagedCoreVariant variant);
    Task<CoreInstallationInfo> InstallAsync(
        CoreInstallRequest request,
        IProgress<CoreInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<CoreInstallationInfo> RestoreHistoryAsync(
        ManagedCoreVariant variant,
        string historyId,
        CancellationToken cancellationToken = default);
    void Delete(ManagedCoreVariant variant);
    void UpdateDisplayName(ManagedCoreVariant variant, string displayName);
}

public static class ManagedCoreVariantExtensions
{
    public static string ToStorageKey(this ManagedCoreVariant variant) => variant switch
    {
        ManagedCoreVariant.Stable => "stable",
        ManagedCoreVariant.Custom => "custom",
        _ => throw new ArgumentOutOfRangeException(nameof(variant)),
    };

    public static string ToDirectoryName(this ManagedCoreVariant variant) =>
        $"danmu_api_{variant.ToStorageKey()}";

    public static ManagedCoreVariant ParseManagedVariant(string value) => value switch
    {
        "stable" => ManagedCoreVariant.Stable,
        "custom" => ManagedCoreVariant.Custom,
        _ => throw new FormatException($"不受支持的核心变体：{value}"),
    };
}
