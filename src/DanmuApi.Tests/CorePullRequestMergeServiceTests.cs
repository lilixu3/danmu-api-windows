using DanmuApi.App.Services;
using DanmuApi.Core;
using DanmuApi.Platform;

namespace DanmuApi.Tests;

public sealed class CorePullRequestMergeServiceTests
{
    [SkippableFact]
    public async Task OnePullRequestProducesAnIsolatedPreparedTree()
    {
        using var fixture = new GitFixture();
        var first = fixture.CreatePullRequest(1, "one", "first.txt", "first PR");
        var result = await fixture.Service().PrepareAsync(
            ManagedCoreVariant.Stable, fixture.Installed(), fixture.Repository, [first],
            GithubProxyCatalog.OriginalId, "fixture");

        Assert.Equal(fixture.BaseSha, result.BaseCommitSha);
        Assert.Equal(first.HeadSha, Assert.Single(result.PullRequests).HeadSha);
        Assert.Equal("first PR", File.ReadAllText(Path.Combine(result.StagingDirectory, "first.txt")));
        Assert.Equal(40, result.LocalMergeSha.Length);
        Assert.Contains("\"type\":\"module\"", File.ReadAllText(Path.Combine(result.StagingDirectory, "package.json")));
        fixture.AssertInstalledUnchanged();
        fixture.AssertWorktreesRemoved();
    }

    [SkippableFact]
    public async Task IndependentPullRequestsMergeInOrderAndExistingStackIsReverified()
    {
        using var fixture = new GitFixture();
        var first = fixture.CreatePullRequest(1, "one", "first.txt", "first PR");
        var second = fixture.CreatePullRequest(2, "two", "second.txt", "second PR");
        var prepared = await fixture.Service().PrepareAsync(
            ManagedCoreVariant.Stable, fixture.Installed(), fixture.Repository, [first, second],
            GithubProxyCatalog.OriginalId, "fixture");
        Assert.Equal(new[] { 1, 2 }, prepared.PullRequests.Select(source => source.Number));
        Assert.Equal("first PR", File.ReadAllText(Path.Combine(prepared.StagingDirectory, "first.txt")));
        Assert.Equal("second PR", File.ReadAllText(Path.Combine(prepared.StagingDirectory, "second.txt")));

        var inherited = await fixture.Service().PrepareAsync(
            ManagedCoreVariant.Stable, fixture.InstalledStack(prepared.BaseCommitSha, prepared.LocalMergeSha, [prepared.PullRequests[0]]),
            fixture.Repository, [second], GithubProxyCatalog.OriginalId, "fixture");
        Assert.Equal(new[] { 1, 2 }, inherited.PullRequests.Select(source => source.Number));
        Assert.Equal("first PR", File.ReadAllText(Path.Combine(inherited.StagingDirectory, "first.txt")));
        Assert.Equal("second PR", File.ReadAllText(Path.Combine(inherited.StagingDirectory, "second.txt")));
        fixture.AssertInstalledUnchanged();
        fixture.AssertWorktreesRemoved();
    }

    [SkippableFact]
    public async Task ConflictFailsWithPathsAndLeavesInstalledCoreIntact()
    {
        using var fixture = new GitFixture();
        var first = fixture.CreatePullRequest(1, "one", "payload.txt", "one");
        var second = fixture.CreatePullRequest(2, "two", "payload.txt", "two");

        var error = await Assert.ThrowsAsync<PullRequestMergeConflictException>(() => fixture.Service().PrepareAsync(
            ManagedCoreVariant.Stable, fixture.Installed(), fixture.Repository, [first, second],
            GithubProxyCatalog.OriginalId, "fixture"));
        Assert.Equal(2, error.PullRequestNumber);
        Assert.Contains(error.ConflictFiles, path => path.EndsWith("payload.txt", StringComparison.Ordinal));
        fixture.AssertInstalledUnchanged();
        fixture.AssertWorktreesRemoved();
        fixture.AssertNoStaging();
    }

    [SkippableFact]
    public async Task ChangedSelectedHeadIsRejectedInsteadOfMergingAnOlderSha()
    {
        using var fixture = new GitFixture();
        var stale = fixture.CreatePullRequest(1, "one", "first.txt", "old");
        fixture.AdvancePullRequest(stale, "first.txt", "new");

        var error = await Assert.ThrowsAsync<IOException>(() => fixture.Service().PrepareAsync(
            ManagedCoreVariant.Stable, fixture.Installed(), fixture.Repository, [stale],
            GithubProxyCatalog.OriginalId, "fixture"));
        Assert.Contains("已更新", error.Message, StringComparison.Ordinal);
        fixture.AssertInstalledUnchanged();
        fixture.AssertWorktreesRemoved();
        fixture.AssertNoStaging();
    }

    [SkippableFact]
    public async Task ChangedInheritedHeadIsAlsoRejected()
    {
        using var fixture = new GitFixture();
        var first = fixture.CreatePullRequest(1, "one", "first.txt", "old");
        var second = fixture.CreatePullRequest(2, "two", "second.txt", "second");
        var installed = fixture.InstalledStack(fixture.BaseSha, new string('a', 40),
            [new CorePullRequestSource(1, fixture.Repository.FullName, first.HeadBranch, first.HeadSha, null)]);
        fixture.AdvancePullRequest(first, "first.txt", "new");

        var error = await Assert.ThrowsAsync<IOException>(() => fixture.Service().PrepareAsync(
            ManagedCoreVariant.Stable, installed, fixture.Repository, [second],
            GithubProxyCatalog.OriginalId, "fixture"));
        Assert.Contains("已更新", error.Message, StringComparison.Ordinal);
        fixture.AssertInstalledUnchanged();
        fixture.AssertWorktreesRemoved();
        fixture.AssertNoStaging();
    }

    [SkippableFact]
    public async Task MissingBaseInShallowCloneIsFetchedFromActualHistory()
    {
        using var fixture = new GitFixture();
        fixture.AdvanceMain(130);
        var first = fixture.CreatePullRequest(1, "one", "first.txt", "first PR");
        var result = await fixture.Service().PrepareAsync(
            ManagedCoreVariant.Stable, fixture.Installed(), fixture.Repository, [first],
            GithubProxyCatalog.OriginalId, "fixture");
        Assert.Equal(fixture.BaseSha, result.BaseCommitSha);
        Assert.Equal("first PR", File.ReadAllText(Path.Combine(result.StagingDirectory, "first.txt")));
        fixture.AssertInstalledUnchanged();
    }

    [SkippableFact]
    public async Task GitProcessFailuresAndTimeoutsAreExplicitAndDoNotLeaveStaging()
    {
        using var fixture = new GitFixture();
        var first = fixture.CreatePullRequest(1, "one", "first.txt", "first PR");
        foreach (var failure in new[]
        {
            new CommandExecutionResult(true, 42, "", "remote unavailable"),
            CommandExecutionResult.Failure("命令超时（180 秒）"),
        })
        {
            var executor = new InterceptingExecutor(args => args[0] == "fetch" ? failure : null);
            var error = await Assert.ThrowsAsync<IOException>(() => fixture.Service(executor).PrepareAsync(
                ManagedCoreVariant.Stable, fixture.Installed(), fixture.Repository, [first],
                GithubProxyCatalog.OriginalId, "fixture"));
            Assert.Contains(failure.Diagnostic ?? failure.StandardError, error.Message, StringComparison.Ordinal);
            Assert.All(executor.WorkingDirectories, Assert.True);
            fixture.AssertNoStaging();
            fixture.AssertWorktreesRemoved();
        }
        fixture.AssertInstalledUnchanged();
    }

    [SkippableFact]
    public async Task CancellationIsPropagatedAndDoesNotLeaveStaging()
    {
        using var fixture = new GitFixture();
        var first = fixture.CreatePullRequest(1, "one", "first.txt", "first PR");
        using var cancellation = new CancellationTokenSource();
        var executor = new InterceptingExecutor(args =>
        {
            if (args[0] != "rev-parse") return null;
            cancellation.Cancel();
            return CommandExecutionResult.Failure("命令已取消");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service(executor).PrepareAsync(
            ManagedCoreVariant.Stable, fixture.Installed(), fixture.Repository, [first],
            GithubProxyCatalog.OriginalId, "fixture", cancellationToken: cancellation.Token));
        fixture.AssertInstalledUnchanged();
        fixture.AssertNoStaging();
        fixture.AssertWorktreesRemoved();
    }

    [SkippableFact]
    public async Task GitIndexLinkIsRejectedEvenIfWindowsChecksItOutAsPlainText()
    {
        using var fixture = new GitFixture();
        var link = fixture.CreateLinkedPullRequest(1);
        var error = await Assert.ThrowsAsync<IOException>(() => fixture.Service().PrepareAsync(
            ManagedCoreVariant.Stable, fixture.Installed(), fixture.Repository, [link],
            GithubProxyCatalog.OriginalId, "fixture"));
        Assert.Contains("链接", error.Message, StringComparison.Ordinal);
        fixture.AssertInstalledUnchanged();
        fixture.AssertNoStaging();
    }

    [SkippableFact]
    public void CopyRejectsReparsePointsAndNeverDescendsIntoGitMetadata()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "source");
        var destination = Path.Combine(directory.Path, "destination");
        Directory.CreateDirectory(Path.Combine(source, ".git", "objects"));
        File.WriteAllText(Path.Combine(source, ".git", "objects", "private"), "metadata");
        File.WriteAllText(Path.Combine(source, "worker.js"), "safe");
        CorePullRequestMergeService.CopyCoreTree(source, destination, CancellationToken.None);
        Assert.False(Directory.Exists(Path.Combine(destination, ".git")));
        Assert.Equal("safe", File.ReadAllText(Path.Combine(destination, "worker.js")));

        var target = Path.Combine(directory.Path, "outside.txt");
        File.WriteAllText(target, "outside");
        var link = Path.Combine(source, "link.txt");
        try { File.CreateSymbolicLink(link, target); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Skip.If(true, $"Symbolic links unavailable: {error.Message}");
        }
        Assert.Throws<IOException>(() => CorePullRequestMergeService.CopyCoreTree(source,
            Path.Combine(directory.Path, "rejected"), CancellationToken.None));
    }

    [Fact]
    public void CopyEnforcesFileAndByteBudgets()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "one"), "1234");
        File.WriteAllText(Path.Combine(source, "two"), "56");
        Assert.Throws<IOException>(() => CorePullRequestMergeService.CopyCoreTree(source,
            Path.Combine(directory.Path, "too-many"), CancellationToken.None, maxFiles: 1, maxBytes: 20));
        Assert.Throws<IOException>(() => CorePullRequestMergeService.CopyCoreTree(source,
            Path.Combine(directory.Path, "too-large"), CancellationToken.None, maxFiles: 3, maxBytes: 5));
    }

    private sealed class InterceptingExecutor(Func<IReadOnlyList<string>, CommandExecutionResult?> intercept) : IPlatformCommandExecutor
    {
        private readonly ProcessCommandExecutor _inner = new();
        public List<bool> WorkingDirectories { get; } = [];
        public CommandExecutionResult Execute(string executablePath, IReadOnlyList<string> arguments) =>
            throw new NotSupportedException("Git must use the working-directory overload");
        public CommandExecutionResult Execute(string executablePath, IReadOnlyList<string> arguments,
            string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string> environment)
        {
            WorkingDirectories.Add(Directory.Exists(workingDirectory));
            Assert.Equal("0", environment["GIT_TERMINAL_PROMPT"]);
            Assert.Equal("1", environment["GIT_CONFIG_NOSYSTEM"]);
            return intercept(arguments) ?? _inner.Execute(executablePath, arguments, workingDirectory, timeout, cancellationToken, environment);
        }
    }

    private sealed class GitFixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        private readonly string _git;
        private readonly string _bare;
        private readonly string _seed;
        private readonly string _installed;
        private readonly string _cache;
        private readonly ProcessCommandExecutor _commands = new();
        public GithubRepositoryReference Repository { get; } = GithubRepositoryReference.Official("main");
        public string BaseSha { get; }

        public GitFixture()
        {
            _git = Environment.GetEnvironmentVariable("DANMU_TEST_GIT_EXE") ?? @"C:\Tools\MinGit\cmd\git.exe";
            Skip.IfNot(File.Exists(_git), $"Real Git is required: {_git}");
            _bare = Path.Combine(_directory.Path, "remote repo.git");
            _seed = Path.Combine(_directory.Path, "seed repo");
            _installed = Path.Combine(_directory.Path, "project", "danmu_api_stable");
            _cache = Path.Combine(_directory.Path, "cache");
            Directory.CreateDirectory(_installed);
            File.WriteAllText(Path.Combine(_installed, "installed.txt"), "original");
            Git(_directory.Path, "init", "--bare", _bare);
            Git(_directory.Path, "init", "-b", "main", _seed);
            Git(_seed, "config", "user.name", "Git Fixture");
            Git(_seed, "config", "user.email", "fixture@localhost");
            Git(_seed, "config", "core.autocrlf", "false");
            Write("worker.js", "worker");
            Write("package.json", "{\"name\":\"fixture\",\"type\":\"module\"}");
            Write(Path.Combine("configs", "envs.js"), "envs");
            Write(Path.Combine("configs", "globals.js"), "globals");
            Write("payload.txt", "base");
            Git(_seed, "add", ".");
            Git(_seed, "commit", "-m", "base");
            BaseSha = Git(_seed, "rev-parse", "HEAD");
            Git(_seed, "push", _bare, "HEAD:refs/heads/main");
        }

        public CorePullRequestMergeService Service(IPlatformCommandExecutor? executor = null) =>
            new(_cache, executor ?? _commands, _git, new Uri(_bare, UriKind.Absolute));

        public CoreInstallationInfo Installed() => new(
            ManagedCoreVariant.Stable, _installed, true, true, "1", Manifest(BaseSha), null);

        public CoreInstallationInfo InstalledStack(string baseSha, string localSha, IReadOnlyList<CorePullRequestSource> sources) => new(
            ManagedCoreVariant.Stable, _installed, true, true, "1",
            Manifest(baseSha) with { InstallKind = CoreInstallKind.LocalPullRequestStack,
                BaseCommitSha = baseSha, LocalMergeSha = localSha, PullRequests = sources }, null);

        private CoreInstallationManifest Manifest(string sha) => new(
            CoreInstallationManifest.CurrentSchemaVersion, ManagedCoreVariant.Stable, Repository.FullName,
            "main", sha, "1", "fixture", CoreInstallKind.Branch, null, DateTimeOffset.UtcNow);

        public GithubPullRequest CreatePullRequest(int number, string branch, string file, string content)
        {
            Git(_seed, "checkout", "-B", branch, "main");
            Write(file, content);
            Git(_seed, "add", ".");
            Git(_seed, "commit", "-m", $"PR {number}");
            var sha = Git(_seed, "rev-parse", "HEAD");
            Git(_seed, "push", "--force", _bare, $"HEAD:refs/pull/{number}/head");
            return new GithubPullRequest(number, $"PR {number}", "", "open", null, "main",
                Repository.FullName, branch, sha, false, false, null, null, null, null, null);
        }

        public GithubPullRequest CreateLinkedPullRequest(int number)
        {
            Git(_seed, "checkout", "-B", "linked", "main");
            var content = Path.Combine(_directory.Path, "target-name.txt");
            File.WriteAllText(content, "payload.txt");
            var blob = Git(_seed, "hash-object", "-w", content);
            Git(_seed, "update-index", "--add", "--cacheinfo", $"120000,{blob},danmu_api/link.txt");
            Git(_seed, "commit", "-m", "linked file");
            var sha = Git(_seed, "rev-parse", "HEAD");
            Git(_seed, "push", _bare, $"HEAD:refs/pull/{number}/head");
            return new GithubPullRequest(number, "linked", "", "open", null, "main",
                Repository.FullName, "linked", sha, false, false, null, null, null, null, null);
        }

        public void AdvancePullRequest(GithubPullRequest request, string file, string content)
        {
            Git(_seed, "checkout", request.HeadBranch);
            Write(file, content);
            Git(_seed, "add", ".");
            Git(_seed, "commit", "-m", "moved head");
            Git(_seed, "push", "--force", _bare, $"HEAD:refs/pull/{request.Number}/head");
        }

        public void AdvanceMain(int count)
        {
            Git(_seed, "checkout", "main");
            for (var index = 0; index < count; index++)
            {
                Git(_seed, "commit", "--allow-empty", "-m", $"history {index}");
            }
            Git(_seed, "push", _bare, "HEAD:refs/heads/main");
        }

        private void Write(string relativePath, string content)
        {
            var target = Path.Combine(_seed, "danmu_api", relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content);
        }

        private string Git(string cwd, params string[] args)
        {
            var result = _commands.Execute(_git, args, cwd, TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.True(result.Succeeded, $"git {string.Join(' ', args)}: {result.Diagnostic} {result.StandardError}");
            return result.StandardOutput.Trim();
        }

        public void AssertInstalledUnchanged() =>
            Assert.Equal(new[] { "installed.txt" }, Directory.GetFiles(_installed).Select(Path.GetFileName));
        public void AssertNoStaging() => Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(_installed)!, "*.merge-staging-*"));
        public void AssertWorktreesRemoved()
        {
            var root = Path.Combine(_cache, "pull-request-lab");
            if (Directory.Exists(root)) Assert.Empty(Directory.GetDirectories(root, "merge-*"));
        }
        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(_directory.Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            _directory.Dispose();
        }
    }
}
