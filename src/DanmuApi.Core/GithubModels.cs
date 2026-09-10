namespace DanmuApi.Core;

public sealed record GithubRepositoryMetadata(
    string FullName,
    string DefaultBranch,
    string? Description,
    bool IsPrivate);

public sealed record GithubBranch(string Name, string CommitSha, bool IsProtected);

public sealed record GithubRateLimit(
    int Limit,
    int Remaining,
    int Used,
    DateTimeOffset ResetAt,
    bool Authenticated);

public sealed record GithubCommit(
    string Sha,
    string Title,
    string Message,
    string? Author,
    DateTimeOffset? CommittedAt,
    IReadOnlyList<string> Parents)
{
    public string ShortSha => Sha.Length > 7 ? Sha[..7] : Sha;
}

public sealed record GithubCommitPage(
    IReadOnlyList<GithubCommit> Items,
    int Page,
    bool HasPreviousPage,
    bool HasNextPage);

public sealed record GithubFileChange(
    string Path,
    string? PreviousPath,
    string Status,
    int Additions,
    int Deletions,
    int Changes,
    string? Patch,
    string? PatchUnavailableReason);

public sealed record GithubCommitDetails(
    GithubCommit Commit,
    IReadOnlyList<GithubFileChange> Files,
    int Additions,
    int Deletions,
    int ChangedFiles);

public sealed record GithubPullRequest(
    int Number,
    string Title,
    string Body,
    string State,
    string? Author,
    string BaseBranch,
    string HeadRepository,
    string HeadBranch,
    string HeadSha,
    bool IsDraft,
    bool IsMerged,
    DateTimeOffset? UpdatedAt,
    string? HtmlUrl,
    int? Additions,
    int? Deletions,
    int? ChangedFiles);

public sealed record GithubPullRequestPage(
    IReadOnlyList<GithubPullRequest> Items,
    int Page,
    bool HasPreviousPage,
    bool HasNextPage);

/// <summary>
/// GitHub compare（base...head）结果：更新详情的唯一数据来源，
/// 不用版本字符串猜测差异。Commits/Files 可能被 GitHub 截断，截断时必须显式标记。
/// </summary>
public sealed record GithubCompareResult(
    string Status,
    int AheadBy,
    int BehindBy,
    int TotalCommits,
    IReadOnlyList<GithubCommit> Commits,
    IReadOnlyList<GithubFileChange> Files,
    int Additions,
    int Deletions,
    bool CommitsTruncated,
    bool FilesTruncated);

public enum GithubFailureKind
{
    RouteSelectionRequired,
    Authentication,
    Forbidden,
    RateLimited,
    NotFound,
    Network,
    Http,
    Protocol,
}

public sealed class GithubRemoteException : Exception
{
    public GithubRemoteException(
        GithubFailureKind kind,
        string message,
        int? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    public GithubFailureKind Kind { get; }
    public int? StatusCode { get; }
}

public interface IGithubTokenProvider
{
    bool IsConfigured { get; }
    string? GetToken();
}

public interface IGithubTokenStore : IGithubTokenProvider
{
    void Save(string token);
    void Clear();
}

public interface IGithubCoreRemote
{
    GithubRateLimit? LastRateLimit { get; }
    Task<GithubRepositoryMetadata> GetRepositoryAsync(
        GithubRepositoryReference repository,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GithubBranch>> GetAllBranchesAsync(
        GithubRepositoryReference repository,
        CancellationToken cancellationToken = default);
    Task<GithubCommit> GetCommitAsync(
        GithubRepositoryReference repository,
        string reference,
        CancellationToken cancellationToken = default);
    Task<GithubCommitPage> GetCommitsAsync(
        GithubRepositoryReference repository,
        string reference,
        int page = 1,
        int pageSize = 30,
        CancellationToken cancellationToken = default);
    Task<GithubCommitDetails> GetCommitDetailsAsync(
        GithubRepositoryReference repository,
        string sha,
        CancellationToken cancellationToken = default);
    Task<GithubPullRequestPage> GetPullRequestsAsync(
        GithubRepositoryReference repository,
        string baseBranch,
        string state = "open",
        int page = 1,
        int pageSize = 30,
        CancellationToken cancellationToken = default);
    Task<GithubPullRequest> GetPullRequestAsync(
        GithubRepositoryReference repository,
        int number,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GithubFileChange>> GetAllPullRequestFilesAsync(
        GithubRepositoryReference repository,
        int number,
        CancellationToken cancellationToken = default);
    Task<GithubCompareResult> GetCompareAsync(
        GithubRepositoryReference repository,
        string baseSha,
        string headSha,
        CancellationToken cancellationToken = default);
    Task<GithubRateLimit> GetRateLimitAsync(CancellationToken cancellationToken = default);
    Task<string?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default);
}
