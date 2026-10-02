namespace DanmuApi.Core;

/// <summary>一个已并入本地的 PR，在远端最新提交里到底在不在。</summary>
public enum CorePullRequestPresence
{
    /// <summary>远端历史里确实包含它——有具体的比对提交作证据。</summary>
    Contained,

    /// <summary>远端历史里找不到它的改动：按远端更新会把这些改动丢掉。</summary>
    Missing,

    /// <summary>拿不到能证明"包含"的提交（GitHub 读不到、提交已不存在）。
    /// 既不能当成"包含"（会悄悄丢改动），也不能当成"不包含"（会误导用户重并一遍）。</summary>
    Unknown,
}

/// <summary>
/// 一个 PR 的核对结论。Evidence 是给用户看的**证据**（比对了哪个提交、GitHub 原话），
/// 不是结论的同义反复——用户要能自己判断这个结论可不可信。
/// </summary>
public sealed record CorePullRequestPresenceEntry(
    int Number,
    string HeadSha,
    string? MergeCommitSha,
    string State,
    bool IsMerged,
    bool BaseBranchMatches,
    bool HeadMatchesRecorded,
    CorePullRequestPresence Presence,
    string Evidence)
{
    /// <summary>
    /// 更新之后能不能自动把它并回来：必须仍是可合并状态（未合并、仍 open、目标分支没变）。
    /// 已合并/已关闭的 PR 不允许再并（合并服务会直接拒绝），只能在界面上如实说明。
    /// </summary>
    public bool CanReMerge =>
        Presence != CorePullRequestPresence.Contained &&
        !IsMerged &&
        string.Equals(State, "open", StringComparison.OrdinalIgnoreCase) &&
        BaseBranchMatches;
}

/// <summary>一次"远端到底包不包含这些 PR"的核对结果。</summary>
public sealed record CorePullRequestPresenceReport(
    string Repository,
    string Branch,
    string RemoteSha,
    IReadOnlyList<CorePullRequestPresenceEntry> Entries,
    IReadOnlyList<string> Diagnostics)
{
    public string RemoteShortSha => Shorten(RemoteSha);

    public bool HasPullRequests => Entries.Count > 0;

    /// <summary>不是"已包含"的那些：包括明确的 Missing 和无法确认的 Unknown。</summary>
    public IReadOnlyList<CorePullRequestPresenceEntry> NotContained =>
        Entries.Where(entry => entry.Presence != CorePullRequestPresence.Contained).ToArray();

    public bool AllContained =>
        Entries.Count > 0 && Entries.All(entry => entry.Presence == CorePullRequestPresence.Contained);

    /// <summary>更新后可以自动重新并入的 PR 编号（按原顺序）。</summary>
    public IReadOnlyList<int> ReMergeableNumbers =>
        NotContained.Where(entry => entry.CanReMerge).Select(entry => entry.Number).ToArray();

    /// <summary>远端不包含、又已经不能自动重新并入的 PR 编号（已合并到别处/已关闭/目标分支变了）。</summary>
    public IReadOnlyList<int> UnmergeableNumbers =>
        NotContained.Where(entry => !entry.CanReMerge).Select(entry => entry.Number).ToArray();

    public static string Shorten(string sha) => sha.Length > 7 ? sha[..7] : sha;
}

/// <summary>
/// 核对"远端最新提交是否已经包含本地并进来的这些 PR"。
///
/// 判定顺序（每一步都有可复述的证据）：
/// 1. PR 已合并且有合并提交 → 比对该合并提交是不是远端 head 的祖先
///    （压缩合并时 head 提交不在主干历史里，只有合并提交靠得住）；
/// 2. 否则比对**本地记录的** head 提交是不是远端 head 的祖先；
/// 3. 都证明不了：PR 明确未合并 → Missing；读不到/比对不了 → Unknown，并把原因带出来。
/// </summary>
public interface ICorePullRequestPresenceAnalyzer
{
    Task<CorePullRequestPresenceReport> AnalyzeAsync(
        GithubRepositoryReference repository,
        string remoteSha,
        IReadOnlyList<CorePullRequestSource> sources,
        CancellationToken cancellationToken = default);
}

public sealed class CorePullRequestPresenceAnalyzer : ICorePullRequestPresenceAnalyzer
{
    private readonly IGithubCoreRemote _remote;

    public CorePullRequestPresenceAnalyzer(IGithubCoreRemote remote) =>
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));

    public async Task<CorePullRequestPresenceReport> AnalyzeAsync(
        GithubRepositoryReference repository,
        string remoteSha,
        IReadOnlyList<CorePullRequestSource> sources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteSha);
        ArgumentNullException.ThrowIfNull(sources);
        if (repository.Branch is null)
        {
            throw new ArgumentException("核对 PR 是否在远端必须指定分支", nameof(repository));
        }

        var entries = new List<CorePullRequestPresenceEntry>(sources.Count);
        var diagnostics = new List<string>();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(await AnalyzeOneAsync(repository, remoteSha, source, diagnostics, cancellationToken)
                .ConfigureAwait(false));
        }

        return new CorePullRequestPresenceReport(
            repository.FullName,
            repository.Branch,
            remoteSha,
            entries,
            diagnostics);
    }

    private async Task<CorePullRequestPresenceEntry> AnalyzeOneAsync(
        GithubRepositoryReference repository,
        string remoteSha,
        CorePullRequestSource source,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        GithubPullRequest? current = null;
        string? readFailure = null;
        try
        {
            current = await _remote.GetPullRequestAsync(repository, source.Number, cancellationToken).ConfigureAwait(false);
        }
        catch (GithubRemoteException error)
        {
            // 读不到 PR 不是"没有这个 PR"：结论落到 Unknown 并把原话带出去，绝不猜。
            readFailure = $"读取 PR #{source.Number} 失败：{error.Message}";
            diagnostics.Add(readFailure);
        }

        var baseBranchMatches = current is null ||
            string.Equals(current.BaseBranch, repository.Branch, StringComparison.OrdinalIgnoreCase);
        var headMatchesRecorded = current is null ||
            string.Equals(current.HeadSha, source.HeadSha, StringComparison.OrdinalIgnoreCase);
        var state = current?.State ?? "unknown";
        var isMerged = current?.IsMerged ?? false;

        // 先看合并提交（压缩合并只有它在主干里），再看本地记录的那个 head 提交。
        // 每个候选记下"比过了、结论是什么"：只有**明确的否定**才能下 Missing 的结论。
        var candidates = new List<Candidate>();
        if (current is { IsMerged: true, MergeCommitSha: { Length: > 0 } mergeCommit })
        {
            candidates.Add(new Candidate(mergeCommit, $"合并提交 {CorePullRequestPresenceReport.Shorten(mergeCommit)}", IsMergeCommit: true));
        }

        if (source.HeadSha is { Length: > 0 } headSha)
        {
            candidates.Add(new Candidate(headSha, $"head 提交 {CorePullRequestPresenceReport.Shorten(headSha)}", IsMergeCommit: false));
        }

        var unproven = new List<string>();
        var mergeCommitRejected = false;
        foreach (var candidate in candidates)
        {
            var contained = await IsAncestorAsync(repository, candidate.Sha, remoteSha, diagnostics, cancellationToken)
                .ConfigureAwait(false);
            if (contained == true)
            {
                return Create(CorePullRequestPresence.Contained,
                    $"{candidate.Label} 已在远端 {CorePullRequestPresenceReport.Shorten(remoteSha)} 的历史中");
            }

            if (contained is null)
            {
                unproven.Add(candidate.Label);
            }
            else if (candidate.IsMergeCommit)
            {
                mergeCommitRejected = true;
            }
        }

        // 明确"未合并"的 PR，其改动不可能在主干历史里 —— 这是结论，不是猜测。
        if (current is { IsMerged: false })
        {
            var reason = string.Equals(current.State, "open", StringComparison.OrdinalIgnoreCase)
                ? $"PR #{source.Number} 仍是 open：改动还没进远端 {repository.Branch}"
                : $"PR #{source.Number} 已关闭但没有被合并";
            return Create(CorePullRequestPresence.Missing, AppendHeadNote(reason, current, source));
        }

        if (mergeCommitRejected)
        {
            return Create(CorePullRequestPresence.Missing, AppendHeadNote(
                $"PR #{source.Number} 已标记为合并，但它的合并提交不在远端 {CorePullRequestPresenceReport.Shorten(remoteSha)} 的历史中（分支可能被回退或强推覆盖过）",
                current, source));
        }

        if (candidates.Count == 0)
        {
            return Create(CorePullRequestPresence.Unknown,
                readFailure ?? $"PR #{source.Number} 没有可比对的提交（本地记录里没有 head 提交）");
        }

        if (unproven.Count == candidates.Count)
        {
            return Create(CorePullRequestPresence.Unknown,
                readFailure ?? $"无法比对{string.Join("、", unproven)}与远端 {CorePullRequestPresenceReport.Shorten(remoteSha)} 的关系");
        }

        // 走到这里只剩一种情况：head 提交确实不在远端，但也没有合并提交能证明"已合并"。
        // 压缩合并过的 PR 正是这个形状，所以这里**不能**判 Missing，只能如实说无法确认。
        return Create(CorePullRequestPresence.Unknown, AppendHeadNote(
            $"无法确认 PR #{source.Number} 是否已进远端：只能比对的 head 提交不在远端 {CorePullRequestPresenceReport.Shorten(remoteSha)} 的历史中，GitHub 也没有给出可用的合并提交",
            current, source));

        CorePullRequestPresenceEntry Create(CorePullRequestPresence presence, string evidence) =>
            new(source.Number, source.HeadSha, current?.MergeCommitSha, state, isMerged, baseBranchMatches,
                headMatchesRecorded, presence, evidence);
    }

    private readonly record struct Candidate(string Sha, string Label, bool IsMergeCommit);

    private static string AppendHeadNote(string reason, GithubPullRequest? current, CorePullRequestSource source) =>
        current is not null && !string.Equals(current.HeadSha, source.HeadSha, StringComparison.OrdinalIgnoreCase)
            ? $"{reason}（PR head 已更新为 {CorePullRequestPresenceReport.Shorten(current.HeadSha)}，本地并入的是 {CorePullRequestPresenceReport.Shorten(source.HeadSha)}）"
            : reason;

    /// <summary>
    /// <paramref name="ancestorSha"/> 是不是 <paramref name="descendantSha"/> 的祖先。
    /// GitHub compare 的方向是 <c>base...head</c>，状态描述 head 相对 base：head 前进（ahead）或两者相同（identical），
    /// 就说明 base 已经在 head 的历史里。判定不了返回 null，并把原因记进诊断。
    /// </summary>
    private async Task<bool?> IsAncestorAsync(
        GithubRepositoryReference repository,
        string ancestorSha,
        string descendantSha,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            var comparison = await _remote
                .GetCompareAsync(repository, ancestorSha, descendantSha, cancellationToken)
                .ConfigureAwait(false);
            switch (comparison.Status)
            {
                case "ahead":
                case "identical":
                    return true;
                case "behind":
                case "diverged":
                    return false;
                default:
                    diagnostics.Add(
                        $"比对 {CorePullRequestPresenceReport.Shorten(ancestorSha)}…{CorePullRequestPresenceReport.Shorten(descendantSha)} 时 GitHub 返回了未知状态「{comparison.Status}」");
                    return null;
            }
        }
        catch (GithubRemoteException error)
        {
            diagnostics.Add(
                $"比对 {CorePullRequestPresenceReport.Shorten(ancestorSha)}…{CorePullRequestPresenceReport.Shorten(descendantSha)} 失败：{error.Message}");
            return null;
        }
    }
}
