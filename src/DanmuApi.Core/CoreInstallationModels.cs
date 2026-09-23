namespace DanmuApi.Core;

public enum ManagedCoreVariant
{
    Stable,
    Dev,
    Custom,
}

public enum CoreInstallKind
{
    Branch,
    Commit,
    /// <summary>旧版的「单 PR head 实验安装」。已不再产生：合并只走 <see cref="LocalPullRequestStack"/>。
    /// 保留此值是为了能读回旧安装记录的 manifest，并把它们当作普通提交重装。</summary>
    PullRequest,
    LocalPullRequestStack,
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
    public const int CurrentSchemaVersion = 2;
    public string ShortSha => CommitSha.Length > 7 ? CommitSha[..7] : CommitSha;

    /// <summary>本地 PR 组合重建时所依据的远端基线提交。普通安装为 null。</summary>
    public string? BaseCommitSha { get; init; }

    /// <summary>本地 Git 合并产生的 synthetic commit。它不能当作远端分支 HEAD。</summary>
    public string? LocalMergeSha { get; init; }

    /// <summary>按用户确认顺序记录的本地 PR 来源。</summary>
    public IReadOnlyList<CorePullRequestSource> PullRequests { get; init; } = Array.Empty<CorePullRequestSource>();

    public bool IsLocalPullRequestStack =>
        InstallKind == CoreInstallKind.LocalPullRequestStack || PullRequests.Count > 0;
}

public sealed record CorePullRequestSource(
    int Number,
    string HeadRepository,
    string HeadBranch,
    string HeadSha,
    string? BaseSha);

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

/// <summary>隔离 Git 工作区生成的核心目录；停服与替换仍由宿主编排。</summary>
public sealed record CorePreparedInstallRequest(
    ManagedCoreVariant Variant,
    string StagingDirectory,
    CoreInstallationManifest ExpectedManifest,
    string BaseCommitSha,
    string LocalMergeSha,
    IReadOnlyList<CorePullRequestSource> PullRequests);

public sealed record CorePreparedInstallation(
    CoreInstallationInfo Installation,
    string? BackupDirectory);

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

public interface ICorePreparedInstaller
{
    Task<CorePreparedInstallation> InstallPreparedAsync(
        CorePreparedInstallRequest request,
        IProgress<CoreInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<CoreInstallationInfo> RestorePreparedBackupAsync(
        ManagedCoreVariant variant,
        string backupDirectory,
        CancellationToken cancellationToken = default);
    Task ConfirmPreparedBackupAsync(
        ManagedCoreVariant variant,
        string? backupDirectory,
        CancellationToken cancellationToken = default);
}

public static class ManagedCoreVariantExtensions
{
    public static string ToStorageKey(this ManagedCoreVariant variant) => variant switch
    {
        ManagedCoreVariant.Stable => "stable",
        ManagedCoreVariant.Dev => "dev",
        ManagedCoreVariant.Custom => "custom",
        _ => throw new ArgumentOutOfRangeException(nameof(variant)),
    };

    public static string ToDirectoryName(this ManagedCoreVariant variant) =>
        $"danmu_api_{variant.ToStorageKey()}";

    /// <summary>界面文案里的变体名。漏掉任何一个变体都会显示成枚举名，所以不给默认分支。</summary>
    public static string ToLabel(this ManagedCoreVariant variant) => variant switch
    {
        ManagedCoreVariant.Stable => "稳定核心",
        ManagedCoreVariant.Dev => "开发核心",
        ManagedCoreVariant.Custom => "自定义核心",
        _ => throw new ArgumentOutOfRangeException(nameof(variant)),
    };

    /// <summary>单一事实来源的默认上游仓库（自定义核心由用户自己填，返回 null）。</summary>
    public static string? DefaultRepositoryFullName(this ManagedCoreVariant variant) => variant switch
    {
        ManagedCoreVariant.Stable => GithubRepositoryReference.OfficialOwner + "/" + GithubRepositoryReference.OfficialRepository,
        ManagedCoreVariant.Dev => GithubRepositoryReference.DevOwner + "/" + GithubRepositoryReference.DevRepository,
        ManagedCoreVariant.Custom => null,
        _ => throw new ArgumentOutOfRangeException(nameof(variant)),
    };

    public static ManagedCoreVariant ParseManagedVariant(string value) => value switch
    {
        "stable" => ManagedCoreVariant.Stable,
        "dev" => ManagedCoreVariant.Dev,
        "development" => ManagedCoreVariant.Dev,
        "custom" => ManagedCoreVariant.Custom,
        _ => throw new FormatException(
            $"不受支持的核心变体：{value}。宿主管理 stable / dev / custom 三种，" +
            "其他值请改成 stable 或 custom。"),
    };
}
