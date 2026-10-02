using DanmuApi.Core;

namespace DanmuApi.Tests;

/// <summary>
/// 「远端最新提交到底包不包含已并入的 PR」的判定规则。
///
/// 这是本轮最容易出错的一处：判成"包含"会悄悄丢掉用户的改动，判成"不包含"会让人白重并一遍。
/// 所以每个用例都断言**结论 + 证据**，尤其是压缩合并（head 提交不在主干历史里）这一种。
/// </summary>
public sealed class CorePullRequestPresenceTests
{
    private const string RemoteHead = "1111111111111111111111111111111111111111";
    private const string HeadSha = "2222222222222222222222222222222222222222";
    private const string MergeSha = "3333333333333333333333333333333333333333";

    private static readonly GithubRepositoryReference Repository =
        GithubRepositoryReference.Parse("huangxd-/danmu_api").WithBranch("main");

    [Fact]
    public async Task MergedPullRequestIsContainedWhenItsMergeCommitIsInTheRemoteHistory()
    {
        var remote = new FakeRemote();
        remote.PullRequests[12] = PullRequest(12, merged: true, mergeCommit: MergeSha);
        remote.Compare[$"{MergeSha}...{RemoteHead}"] = "ahead";

        var report = await AnalyzeAsync(remote);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CorePullRequestPresence.Contained, entry.Presence);
        Assert.Contains("合并提交", entry.Evidence, StringComparison.Ordinal);
        Assert.False(entry.CanReMerge);
        Assert.True(report.AllContained);
        Assert.Empty(report.ReMergeableNumbers);
    }

    /// <summary>
    /// 压缩合并：PR 的 head 提交**不在**主干历史里，只有合并提交在。
    /// 只看 head 会把这种已经进主干的 PR 误判成"没包含"，进而让用户白重并一遍（还可能冲突）。
    /// </summary>
    [Fact]
    public async Task SquashMergedPullRequestIsContainedEvenThoughItsHeadIsNotInHistory()
    {
        var remote = new FakeRemote();
        remote.PullRequests[15] = PullRequest(15, merged: true, mergeCommit: MergeSha);
        remote.Compare[$"{MergeSha}...{RemoteHead}"] = "ahead";
        remote.Compare[$"{HeadSha}...{RemoteHead}"] = "diverged";

        var report = await AnalyzeAsync(remote, 15);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CorePullRequestPresence.Contained, entry.Presence);
        Assert.Equal(MergeSha, entry.MergeCommitSha);
        Assert.True(report.AllContained);
    }

    [Fact]
    public async Task OpenPullRequestIsMissingAndCanBeReMerged()
    {
        var remote = new FakeRemote();
        remote.PullRequests[20] = PullRequest(20, merged: false, state: "open");
        remote.Compare[$"{HeadSha}...{RemoteHead}"] = "diverged";

        var report = await AnalyzeAsync(remote, 20);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CorePullRequestPresence.Missing, entry.Presence);
        Assert.True(entry.CanReMerge);
        Assert.Contains("仍是 open", entry.Evidence, StringComparison.Ordinal);
        Assert.Equal([20], report.ReMergeableNumbers);
        Assert.Empty(report.UnmergeableNumbers);
    }

    [Fact]
    public async Task ClosedUnmergedPullRequestIsMissingButCannotBeReMerged()
    {
        var remote = new FakeRemote();
        remote.PullRequests[21] = PullRequest(21, merged: false, state: "closed");
        remote.Compare[$"{HeadSha}...{RemoteHead}"] = "diverged";

        var report = await AnalyzeAsync(remote, 21);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CorePullRequestPresence.Missing, entry.Presence);
        Assert.False(entry.CanReMerge);
        Assert.Contains("已关闭但没有被合并", entry.Evidence, StringComparison.Ordinal);
        Assert.Equal([21], report.UnmergeableNumbers);
    }

    /// <summary>标记为已合并，但合并提交不在远端历史里（分支被回退/强推覆盖过）——这部分改动确实丢了。</summary>
    [Fact]
    public async Task MergedPullRequestWhoseMergeCommitIsNotInHistoryIsMissing()
    {
        var remote = new FakeRemote();
        remote.PullRequests[30] = PullRequest(30, merged: true, mergeCommit: MergeSha);
        remote.Compare[$"{MergeSha}...{RemoteHead}"] = "diverged";
        remote.Compare[$"{HeadSha}...{RemoteHead}"] = "diverged";

        var report = await AnalyzeAsync(remote, 30);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CorePullRequestPresence.Missing, entry.Presence);
        Assert.Contains("不在远端", entry.Evidence, StringComparison.Ordinal);
        Assert.Contains("回退或强推", entry.Evidence, StringComparison.Ordinal);
    }

    /// <summary>
    /// 标记为已合并、但没有合并提交可比对，head 也不在历史里：**不能**判 Missing。
    /// 压缩合并过的 PR 正是这个形状，判 Missing 会让用户白重并一遍。
    /// </summary>
    [Fact]
    public async Task MergedWithoutMergeCommitAndHeadNotInHistoryStaysUnknown()
    {
        var remote = new FakeRemote();
        remote.PullRequests[31] = PullRequest(31, merged: true, mergeCommit: null);
        remote.Compare[$"{HeadSha}...{RemoteHead}"] = "diverged";

        var report = await AnalyzeAsync(remote, 31);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CorePullRequestPresence.Unknown, entry.Presence);
        Assert.Contains("无法确认", entry.Evidence, StringComparison.Ordinal);
        Assert.False(report.AllContained);
    }

    /// <summary>
    /// 未合并的 PR 是**确定**不包含（跟能不能比对无关），失败照样要记进诊断。
    /// </summary>
    [Fact]
    public async Task UnmergedPullRequestsStayMissingEvenWhenTheComparisonFails()
    {
        var remote = new FakeRemote();
        remote.PullRequests[40] = PullRequest(40, merged: false, state: "open");
        remote.PullRequests[41] = PullRequest(41, merged: false, state: "open");
        remote.CompareFailures.Add($"{HeadSha}...{RemoteHead}");

        var report = await AnalyzeAsync(remote, 40, 41);

        Assert.Equal(2, report.Entries.Count);
        Assert.All(report.Entries, entry => Assert.Equal(CorePullRequestPresence.Missing, entry.Presence));
        Assert.All(report.Entries, entry => Assert.True(entry.CanReMerge));
        Assert.Contains(report.Diagnostics, item => item.Contains("比对", StringComparison.Ordinal));
        Assert.Contains(report.Diagnostics, item => item.Contains("GitHub 读取失败", StringComparison.Ordinal));
    }

    /// <summary>已合并 + 比对全失败：只能如实说"无法确认"，不能猜成包含或不包含。</summary>
    [Fact]
    public async Task MergedPullRequestWithFailedComparisonsStaysUnknownWithDiagnostics()
    {
        var remote = new FakeRemote();
        remote.PullRequests[42] = PullRequest(42, merged: true, mergeCommit: MergeSha);
        remote.CompareFailures.Add($"{MergeSha}...{RemoteHead}");
        remote.CompareFailures.Add($"{HeadSha}...{RemoteHead}");

        var report = await AnalyzeAsync(remote, 42);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CorePullRequestPresence.Unknown, entry.Presence);
        Assert.Equal(2, report.Diagnostics.Count);
    }

    /// <summary>读不到 PR 状态时不能"当作没有这个 PR"：退回比对本地记录的 head 提交。</summary>
    [Fact]
    public async Task UnreadablePullRequestFallsBackToTheRecordedHeadSha()
    {
        var remote = new FakeRemote();
        remote.PullRequestFailures.Add(50);
        remote.Compare[$"{HeadSha}...{RemoteHead}"] = "ahead";

        var report = await AnalyzeAsync(remote, 50);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CorePullRequestPresence.Contained, entry.Presence);
        Assert.Contains("head 提交", entry.Evidence, StringComparison.Ordinal);
        Assert.Contains(report.Diagnostics, item => item.Contains("读取 PR #50 失败", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownCompareStatusIsNeverGuessed()
    {
        var remote = new FakeRemote();
        remote.PullRequests[60] = PullRequest(60, merged: true, mergeCommit: MergeSha);
        remote.Compare[$"{MergeSha}...{RemoteHead}"] = "sideways";
        remote.Compare[$"{HeadSha}...{RemoteHead}"] = "sideways";

        var report = await AnalyzeAsync(remote, 60);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CorePullRequestPresence.Unknown, entry.Presence);
        Assert.Contains(report.Diagnostics, item => item.Contains("未知状态「sideways」", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MovedHeadIsCalledOutInTheEvidence()
    {
        var remote = new FakeRemote();
        const string newHead = "9999999999999999999999999999999999999999";
        remote.PullRequests[70] = PullRequest(70, merged: false, state: "open", headSha: newHead);
        remote.Compare[$"{HeadSha}...{RemoteHead}"] = "diverged";

        var report = await AnalyzeAsync(remote, 70);

        var entry = Assert.Single(report.Entries);
        Assert.False(entry.HeadMatchesRecorded);
        Assert.Contains("PR head 已更新为 9999999", entry.Evidence, StringComparison.Ordinal);
    }

    // Recorded public GitHub parent/child responses from the independent release audit (2026-10-01).
    // Keep the real SHA order and ahead/behind counts so the fixture cannot silently invert the API contract.
    [Theory]
    [InlineData("c42877ccf0deeda2ed394a83903b4b2ab3dac41c", "fc1b7ff6add61d8af24c9bf978253273833f5afc", "ahead", CorePullRequestPresence.Contained)]
    [InlineData("fc1b7ff6add61d8af24c9bf978253273833f5afc", "c42877ccf0deeda2ed394a83903b4b2ab3dac41c", "behind", CorePullRequestPresence.Missing)]
    [InlineData("c42877ccf0deeda2ed394a83903b4b2ab3dac41c", "c42877ccf0deeda2ed394a83903b4b2ab3dac41c", "identical", CorePullRequestPresence.Contained)]
    [InlineData("c42877ccf0deeda2ed394a83903b4b2ab3dac41c", "fc1b7ff6add61d8af24c9bf978253273833f5afc", "diverged", CorePullRequestPresence.Missing)]
    public async Task AuditCompareDirectionMatchesGitHubHeadRelativeToBase(
        string candidate, string remoteSha, string status, CorePullRequestPresence expected)
    {
        var remote = new FakeRemote();
        remote.PullRequests[12] = PullRequest(12, merged: true, mergeCommit: candidate, headSha: candidate);
        remote.Compare[$"{candidate}...{remoteSha}"] = status;
        var report = await new CorePullRequestPresenceAnalyzer(remote).AnalyzeAsync(
            GithubRepositoryReference.Dev("main"), remoteSha,
            [new CorePullRequestSource(12, "contributor/danmu_api", "feature", candidate, null)]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(expected, entry.Presence);
        Assert.Contains(expected == CorePullRequestPresence.Contained ? candidate[..7] : remoteSha[..7], entry.Evidence);
        Assert.All(remote.CompareCalls, pair => Assert.Equal((candidate, remoteSha), pair));
        Assert.NotEmpty(remote.CompareCalls);
    }

    private static Task<CorePullRequestPresenceReport> AnalyzeAsync(FakeRemote remote, params int[] numbers)
    {
        var sources = (numbers.Length == 0 ? [12] : numbers)
            .Select(number => new CorePullRequestSource(number, "contributor/danmu_api", "feature", HeadSha, null))
            .ToArray();
        return new CorePullRequestPresenceAnalyzer(remote).AnalyzeAsync(Repository, RemoteHead, sources);
    }

    private static GithubPullRequest PullRequest(
        int number,
        bool merged,
        string? mergeCommit = null,
        string state = "closed",
        string headSha = HeadSha) =>
        new(number, $"PR {number}", string.Empty, state, "contributor", "main",
            "contributor/danmu_api", "feature", headSha, false, merged,
            null, null, null, null, null)
        {
            MergeCommitSha = mergeCommit,
        };

    /// <summary>只实现核对真正用到的那两条读取；其余成员不该被调用。</summary>
    private sealed class FakeRemote : IGithubCoreRemote
    {
        public Dictionary<int, GithubPullRequest> PullRequests { get; } = [];

        public Dictionary<string, string> Compare { get; } = [];
        public List<(string Base, string Head)> CompareCalls { get; } = [];

        public HashSet<string> CompareFailures { get; } = [];

        public HashSet<int> PullRequestFailures { get; } = [];

        public GithubRateLimit? LastRateLimit => null;

        public Task<GithubPullRequest> GetPullRequestAsync(
            GithubRepositoryReference repository,
            int number,
            CancellationToken cancellationToken = default)
        {
            if (PullRequestFailures.Contains(number) || !PullRequests.TryGetValue(number, out var pullRequest))
            {
                return Task.FromException<GithubPullRequest>(
                    new GithubRemoteException(GithubFailureKind.NotFound, $"GitHub 读取失败：找不到 PR #{number}", 404));
            }

            return Task.FromResult(pullRequest);
        }

        public Task<GithubCompareResult> GetCompareAsync(
            GithubRepositoryReference repository,
            string baseSha,
            string headSha,
            CancellationToken cancellationToken = default)
        {
            CompareCalls.Add((baseSha, headSha));
            var key = $"{baseSha}...{headSha}";
            if (CompareFailures.Contains(key))
            {
                return Task.FromException<GithubCompareResult>(
                    new GithubRemoteException(GithubFailureKind.Network, "GitHub 读取失败：网络不可达"));
            }

            if (!Compare.TryGetValue(key, out var status))
            {
                return Task.FromException<GithubCompareResult>(
                    new GithubRemoteException(GithubFailureKind.NotFound, $"GitHub 读取失败：提交 {baseSha[..7]} 不存在", 404));
            }

            return Task.FromResult(new GithubCompareResult(status,
                status == "ahead" ? 1 : 0, status == "behind" ? 1 : 0, 0, [], [], 0, 0, false, false));
        }

        public Task<GithubRepositoryMetadata> GetRepositoryAsync(GithubRepositoryReference repository, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GithubBranch>> GetAllBranchesAsync(GithubRepositoryReference repository, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GithubCommit> GetCommitAsync(GithubRepositoryReference repository, string reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GithubCommitPage> GetCommitsAsync(GithubRepositoryReference repository, string reference, int page = 1, int pageSize = 30, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GithubCommitDetails> GetCommitDetailsAsync(GithubRepositoryReference repository, string sha, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GithubPullRequestPage> GetPullRequestsAsync(GithubRepositoryReference repository, string baseBranch, string state = "open", int page = 1, int pageSize = 30, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GithubFileChange>> GetAllPullRequestFilesAsync(GithubRepositoryReference repository, int number, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GithubRateLimit> GetRateLimitAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
