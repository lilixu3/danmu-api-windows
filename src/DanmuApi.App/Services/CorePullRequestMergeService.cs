using DanmuApi.Core;
using DanmuApi.Platform;

namespace DanmuApi.App.Services;

public sealed class PullRequestMergeConflictException : IOException
{
    public PullRequestMergeConflictException(int pullRequestNumber, IReadOnlyList<string> conflictFiles, string diagnostic)
        : base($"PR #{pullRequestNumber} 合并冲突：{(conflictFiles.Count == 0 ? "无法确定冲突文件" : string.Join("、", conflictFiles))}。{diagnostic}")
    {
        PullRequestNumber = pullRequestNumber;
        ConflictFiles = conflictFiles;
    }

    public int PullRequestNumber { get; }
    public IReadOnlyList<string> ConflictFiles { get; }
}

public interface ICorePullRequestMergeService
{
    Task<CorePreparedInstallRequest> PrepareAsync(
        ManagedCoreVariant variant,
        CoreInstallationInfo installed,
        GithubRepositoryReference repository,
        IReadOnlyList<GithubPullRequest> pullRequests,
        string proxyId,
        string displayName,
        IProgress<CoreInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Builds a local PR stack in a disposable Git worktree. It never changes the installed
/// core; the returned sibling staging directory is consumed by the prepared installer.
/// </summary>
public sealed class CorePullRequestMergeService : ICorePullRequestMergeService
{
    private const int ShallowDepth = 128;
    private const int MaxPullRequests = 100;
    private const int MaxFiles = 30_000;
    private const long MaxBytes = 512L * 1024L * 1024L;
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(3);
    private static readonly IReadOnlyDictionary<string, string> GitEnvironment = new Dictionary<string, string>
    {
        ["GIT_CONFIG_NOSYSTEM"] = "1",
        ["GIT_CONFIG_GLOBAL"] = "NUL",
        ["GIT_CONFIG_COUNT"] = "0",
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_ASKPASS"] = "",
        ["GCM_INTERACTIVE"] = "never",
        ["GIT_LFS_SKIP_SMUDGE"] = "1",
    };
    private readonly string _cacheDirectory;
    private readonly IPlatformCommandExecutor _commands;
    private readonly Action<string>? _diagnostics;
    private readonly string? _testGitExecutable;
    private readonly Uri? _testRemote;

    public CorePullRequestMergeService(
        string cacheDirectory,
        IPlatformCommandExecutor commands,
        Action<string>? diagnosticSink = null)
    {
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _diagnostics = diagnosticSink;
    }

    // Test-only local transport; the application constructor cannot redirect Git to a file or untrusted host.
    internal CorePullRequestMergeService(string cacheDirectory, IPlatformCommandExecutor commands,
        string gitExecutable, Uri localRemote)
        : this(cacheDirectory, commands)
    {
        ArgumentNullException.ThrowIfNull(localRemote);
        if (!localRemote.IsAbsoluteUri || !localRemote.IsFile || localRemote.IsUnc ||
            !string.IsNullOrEmpty(localRemote.UserInfo) || !string.IsNullOrEmpty(localRemote.Query) ||
            !string.IsNullOrEmpty(localRemote.Fragment))
        {
            throw new ArgumentException("测试 Git 远端必须是本机 file URI", nameof(localRemote));
        }
        _testGitExecutable = Path.GetFullPath(gitExecutable);
        _testRemote = localRemote;
    }

    public async Task<CorePreparedInstallRequest> PrepareAsync(
        ManagedCoreVariant variant,
        CoreInstallationInfo installed,
        GithubRepositoryReference repository,
        IReadOnlyList<GithubPullRequest> pullRequests,
        string proxyId,
        string displayName,
        IProgress<CoreInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(pullRequests);
        if (pullRequests.Count is < 1 or > MaxPullRequests)
        {
            throw new ArgumentException($"一次本地合并必须选择 1 到 {MaxPullRequests} 个 PR", nameof(pullRequests));
        }
        if (!installed.IsInstalled || !installed.IsValid || installed.Manifest is null)
        {
            throw new InvalidOperationException("当前目标核心未安装或来源 manifest 无效，不能在其上合并 PR");
        }
        if (repository.Branch is null)
        {
            throw new ArgumentException("本地 PR 合并需要明确的目标分支", nameof(repository));
        }
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("核心显示名称不能为空", nameof(displayName));
        }

        ValidatePullRequests(repository, pullRequests);
        var git = ResolveGitExecutable();
        var operationId = Guid.NewGuid().ToString("N");
        var workRoot = Path.Combine(_cacheDirectory, "pull-request-lab");
        var workTree = Path.Combine(workRoot, $"merge-{operationId}");
        var staging = BuildStagingPath(installed.Directory, operationId);
        Directory.CreateDirectory(workRoot);
        DeleteIfExists(workTree);
        DeleteIfExists(staging);

        try
        {
            var remoteCandidates = BuildRemoteCandidates(repository, proxyId);
            var clonedRemote = CloneBase(git, remoteCandidates, repository.Branch, workTree, progress, cancellationToken);
            ConfigureRepository(git, workTree);

            var preferredBase = installed.Manifest.IsLocalPullRequestStack
                ? installed.Manifest.BaseCommitSha
                : installed.Manifest.CommitSha;
            Report(progress, CoreInstallStage.Extracting, "正在定位当前核心基线");
            var baseCommit = CheckoutBase(
                git,
                workTree,
                clonedRemote,
                repository.Branch,
                preferredBase,
                progress,
                cancellationToken);

            var effectivePullRequests = InheritExistingStack(installed.Manifest, repository, pullRequests);
            var sources = new List<CorePullRequestSource>(effectivePullRequests.Count);
            for (var index = 0; index < effectivePullRequests.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pullRequest = effectivePullRequests[index];
                Report(progress, CoreInstallStage.Extracting,
                    $"正在获取 PR #{pullRequest.Number}（{index + 1}/{effectivePullRequests.Count}）");
                var reference = $"refs/remotes/danmu-pr/{pullRequest.Number}/head";
                FetchPullRequest(git, workTree, clonedRemote, pullRequest.Number, reference, cancellationToken);
                var commit = ReadRevision(git, workTree, reference, $"读取 PR #{pullRequest.Number} 提交", cancellationToken);
                if (!ShaMatches(commit, pullRequest.HeadSha))
                {
                    throw new IOException($"PR #{pullRequest.Number} 已更新（GitHub={pullRequest.HeadSha}，实际获取={commit}），请刷新列表后重试");
                }

                Report(progress, CoreInstallStage.Validating, $"正在合并 PR #{pullRequest.Number}");
                var merge = Run(git, ["merge", "--no-ff", "--no-edit", "--no-verify", reference], workTree, cancellationToken, allowFailure: true);
                if (!merge.Succeeded)
                {
                    var conflicts = ReadConflictFiles(git, workTree, cancellationToken);
                    var abort = Run(git, ["merge", "--abort"], workTree, cancellationToken, allowFailure: true);
                    var abortDiagnostic = abort.Succeeded ? string.Empty : $"；清理合并状态失败：{Describe(abort)}";
                    throw new PullRequestMergeConflictException(pullRequest.Number, conflicts, Describe(merge) + abortDiagnostic);
                }

                sources.Add(new CorePullRequestSource(
                    pullRequest.Number,
                    NormalizeRepository(pullRequest.HeadRepository),
                    GithubRepositoryReference.ValidateBranch(pullRequest.HeadBranch),
                    NormalizeSha(pullRequest.HeadSha),
                    NormalizeOptionalSha(pullRequest.BaseSha)));
            }

            var localMergeSha = ReadRevision(git, workTree, "HEAD", "读取本地 PR 合并提交", cancellationToken);
            var coreDirectory = LocateCoreDirectory(workTree)
                ?? throw new InvalidDataException("PR 合并结果中未找到有效的 danmu_api 核心目录（缺少 worker.js）");
            Report(progress, CoreInstallStage.Validating, "正在整理本地 PR 合并结果");
            RejectGitLinks(git, workTree, cancellationToken);
            CopyCoreTree(coreDirectory, staging, cancellationToken);
            CoreInstaller.PrepareGitCoreStaging(staging, workTree);

            var expected = installed.Manifest;
            return new CorePreparedInstallRequest(
                variant,
                staging,
                expected,
                baseCommit,
                localMergeSha,
                sources);
        }
        catch
        {
            DeleteIfExists(staging);
            throw;
        }
        finally
        {
            DeleteIfExists(workTree);
        }
    }

    private string CloneBase(
        string git,
        IReadOnlyList<Uri> remotes,
        string branch,
        string workTree,
        IProgress<CoreInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var index = 0; index < remotes.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteIfExists(workTree);
            Report(progress, CoreInstallStage.Downloading,
                index == 0 ? $"正在获取 {branch} 基线" : "当前 GitHub Git 线路失败，正在尝试下一条线路");
            var result = Run(git,
                ["clone", "--no-tags", "--depth", ShallowDepth.ToString(), "--single-branch", "--branch", branch, remotes[index].AbsoluteUri, workTree],
                Environment.CurrentDirectory,
                cancellationToken,
                allowFailure: true);
            if (result.Succeeded)
            {
                return remotes[index].AbsoluteUri;
            }
            last = new IOException($"Git clone 失败：{remotes[index].Host}；{Describe(result)}");
        }

        throw new IOException($"无法获取 {branch} 基线：{last?.Message ?? "没有可用 Git 线路"}", last);
    }

    private string CheckoutBase(
        string git,
        string workTree,
        string remote,
        string branch,
        string? preferredBase,
        IProgress<CoreInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var requested = preferredBase?.Trim();
        if (string.IsNullOrWhiteSpace(requested))
        {
            return ReadRevision(git, workTree, "HEAD", "读取目标分支基线", cancellationToken);
        }
        ValidateShortOrFullSha(requested, "当前核心基线");
        var resolved = TryReadRevision(git, workTree, $"{requested}^{{commit}}", cancellationToken);
        if (resolved is null)
        {
            Report(progress, CoreInstallStage.Downloading, "当前核心较旧，正在补充基线历史");
            var fetch = Run(git, ["fetch", "--no-tags", "--unshallow", remote, branch], workTree, cancellationToken, allowFailure: true);
            if (!fetch.Succeeded)
            {
                throw new IOException($"无法补充当前核心基线历史：{Describe(fetch)}");
            }
            resolved = TryReadRevision(git, workTree, $"{requested}^{{commit}}", cancellationToken);
        }
        if (resolved is null)
        {
            throw new IOException("当前核心版本已不在仓库历史中，无法在该版本上并入 PR");
        }

        var checkout = Run(git, ["checkout", "--detach", resolved], workTree, cancellationToken, allowFailure: true);
        if (!checkout.Succeeded)
        {
            throw new IOException($"无法切换到当前核心基线，PR 尚未应用：{Describe(checkout)}");
        }
        return resolved;
    }

    private void FetchPullRequest(
        string git,
        string workTree,
        string remote,
        int number,
        string reference,
        CancellationToken cancellationToken)
    {
        var result = Run(git,
            ["fetch", "--no-tags", remote, $"+refs/pull/{number}/head:{reference}"],
            workTree,
            cancellationToken,
            allowFailure: true);
        if (!result.Succeeded)
        {
            throw new IOException($"无法获取 PR #{number} 的 head：{Describe(result)}");
        }
    }

    private void ConfigureRepository(string git, string workTree)
    {
        Run(git, ["config", "user.name", "Danmu API App"], workTree, CancellationToken.None);
        Run(git, ["config", "user.email", "local-pr-lab@localhost"], workTree, CancellationToken.None);
        Run(git, ["config", "core.hooksPath", "NUL"], workTree, CancellationToken.None);
    }

    private IReadOnlyList<GithubPullRequest> InheritExistingStack(
        CoreInstallationManifest manifest,
        GithubRepositoryReference repository,
        IReadOnlyList<GithubPullRequest> requested)
    {
        if (!manifest.IsLocalPullRequestStack || manifest.PullRequests.Count == 0 ||
            !string.Equals(manifest.Repository, repository.FullName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.Branch, repository.Branch, StringComparison.Ordinal))
        {
            return requested;
        }

        var existing = manifest.PullRequests.Select(source => new GithubPullRequest(
            source.Number,
            $"已安装 PR #{source.Number}",
            string.Empty,
            "open",
            null,
            repository.Branch!,
            source.HeadRepository,
            source.HeadBranch,
            source.HeadSha,
            false,
            false,
            null,
            null,
            null,
            null,
            null)).ToList();
        existing.AddRange(requested);
        return existing
            .GroupBy(item => item.Number)
            .Select(group => group.First())
            .ToArray();
    }

    private static void ValidatePullRequests(
        GithubRepositoryReference repository,
        IReadOnlyList<GithubPullRequest> pullRequests)
    {
        var numbers = new HashSet<int>();
        foreach (var pullRequest in pullRequests)
        {
            if (pullRequest.Number <= 0 || !numbers.Add(pullRequest.Number))
            {
                throw new IOException("PR 合并队列包含无效或重复的 PR 编号");
            }
            if (!pullRequest.State.Equals("open", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"PR #{pullRequest.Number} 已关闭，不能本地合并");
            }
            if (!string.Equals(pullRequest.BaseBranch, repository.Branch, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"PR #{pullRequest.Number} 的目标分支已变为 {pullRequest.BaseBranch}");
            }
            _ = GithubRepositoryReference.Parse(pullRequest.HeadRepository);
            GithubRepositoryReference.ValidateBranch(pullRequest.HeadBranch);
            ValidateShortOrFullSha(pullRequest.HeadSha, $"PR #{pullRequest.Number} head");
        }
    }

    private IReadOnlyList<Uri> BuildRemoteCandidates(GithubRepositoryReference repository, string proxyId)
    {
        if (_testRemote is not null) return [_testRemote];
        var original = new Uri($"https://github.com/{repository.Owner}/{repository.Repository}.git", UriKind.Absolute);
        if (string.Equals(proxyId, GithubProxyCatalog.OriginalId, StringComparison.Ordinal))
        {
            return [original];
        }
        var option = GithubProxyCatalog.GetById(proxyId);
        return GithubProxyCatalog.BuildProxyCandidates(option.BaseUrl, original)
            .Prepend(original)
            .Distinct()
            .ToArray();
    }

    private string ResolveGitExecutable()
    {
        var executable = _testGitExecutable ?? Path.Combine(AppContext.BaseDirectory, "git", "cmd", "git.exe");
        return File.Exists(executable) ? executable : throw new FileNotFoundException(
            "未找到随应用发布的 Git，无法进行本地 PR 合并。", executable);
    }

    private CommandExecutionResult Run(
        string git,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        bool allowFailure = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = _commands.Execute(git, arguments, workingDirectory, GitTimeout, cancellationToken, GitEnvironment);
        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException($"Git 命令已取消：{Describe(result)}", cancellationToken);
        }
        if (!result.Started || result.ExitCode is null)
        {
            throw new IOException($"Git 进程未完成：{arguments[0]}；{Describe(result)}");
        }
        if (result.Succeeded || allowFailure)
        {
            return result;
        }
        throw new IOException($"Git 命令失败：{arguments[0]}；{Describe(result)}");
    }

    private string ReadRevision(string git, string workTree, string reference, string action,
        CancellationToken cancellationToken)
    {
        var result = Run(git, ["rev-parse", "--verify", $"{reference}^{{commit}}"],
            workTree, cancellationToken);
        var revision = result.StandardOutput.Trim().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (revision is null || revision.Length != 40 || revision.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new IOException($"{action}失败：Git 未返回完整提交 SHA；{Describe(result)}");
        }
        return revision.ToLowerInvariant();
    }

    private string? TryReadRevision(string git, string workTree, string reference, CancellationToken cancellationToken)
    {
        var result = Run(git, ["rev-parse", reference], workTree, cancellationToken, allowFailure: true);
        if (!result.Succeeded)
        {
            return null;
        }
        var revision = result.StandardOutput.Trim().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return revision is { Length: 40 } && revision.All(Uri.IsHexDigit) ? revision.ToLowerInvariant() : null;
    }

    private void RejectGitLinks(string git, string workTree, CancellationToken cancellationToken)
    {
        var result = Run(git, ["ls-files", "--stage", "-z"], workTree, cancellationToken);
        foreach (var entry in result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = entry.IndexOf(' ');
            if (separator < 0)
            {
                throw new IOException("Git 索引文件模式无效，不能安全应用 PR 合并结果");
            }
            var mode = entry[..separator];
            if (mode is "120000" or "160000")
            {
                throw new IOException($"PR 合并结果包含不支持的链接或子模块：{entry}");
            }
        }
    }

    private IReadOnlyList<string> ReadConflictFiles(string git, string workTree, CancellationToken cancellationToken)
    {
        var result = Run(git, ["diff", "--name-only", "--diff-filter=U"], workTree, cancellationToken, allowFailure: true);
        return result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? LocateCoreDirectory(string workTree) =>
        new[] { Path.Combine(workTree, "danmu_api"), Path.Combine(workTree, "danmu-api"), workTree }
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "worker.js")));

    internal static void CopyCoreTree(
        string source,
        string destination,
        CancellationToken cancellationToken,
        int maxFiles = MaxFiles,
        long maxBytes = MaxBytes)
    {
        Directory.CreateDirectory(destination);
        var files = 0;
        long bytes = 0;
        CopyDirectory(source);

        void CopyDirectory(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"PR 合并结果包含 Windows 重解析点：{entry}");
                }
                if (string.Equals(Path.GetFileName(entry), ".git", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var relative = Path.GetRelativePath(source, entry);
                var target = Path.Combine(destination, relative);
                if (Directory.Exists(entry))
                {
                    Directory.CreateDirectory(target);
                    CopyDirectory(entry);
                    continue;
                }
                if (!File.Exists(entry))
                {
                    throw new IOException($"PR 合并结果包含不支持的文件类型：{entry}");
                }
                files++;
                bytes += new FileInfo(entry).Length;
                if (files > maxFiles || bytes > maxBytes)
                {
                    throw new IOException("PR 合并结果体积异常，已停止处理");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(entry, target, overwrite: false);
            }
        }
    }

    private static string BuildStagingPath(string installedDirectory, string operationId)
    {
        var target = Path.GetFullPath(installedDirectory);
        var parent = Directory.GetParent(target)?.FullName ?? throw new IOException("核心目录没有父目录");
        return Path.Combine(parent, $".{Path.GetFileName(target)}.merge-staging-{operationId}");
    }

    private static string NormalizeRepository(string value) => GithubRepositoryReference.Parse(value).FullName;

    private static string NormalizeSha(string value)
    {
        ValidateShortOrFullSha(value, "提交 SHA");
        return value.Trim().ToLowerInvariant();
    }

    private static string? NormalizeOptionalSha(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : NormalizeSha(value);

    private static bool ShaMatches(string actual, string expected) =>
        expected.Length == 40
            ? string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
            : actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase);

    private static void ValidateShortOrFullSha(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length is < 7 or > 40 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new IOException($"{label}不是有效的 Git 提交 SHA");
        }
    }

    private static string Describe(CommandExecutionResult result)
    {
        var output = string.Join(" | ", new[] { result.Diagnostic, result.StandardError, result.StandardOutput }
            .Where(text => !string.IsNullOrWhiteSpace(text)).Select(text => text!.Trim()));
        if (output.Length > 4000) output = output[^4000..];
        return $"退出码={result.ExitCode?.ToString() ?? "未启动"}；{output}";
    }

    private static void Report(IProgress<CoreInstallProgress>? progress, CoreInstallStage stage, string detail) =>
        progress?.Report(new CoreInstallProgress(stage, detail));

    private void DeleteIfExists(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                    }
                }
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _diagnostics?.Invoke($"清理本地 PR 临时目录失败：{path}；{error.Message}");
            throw;
        }
    }
}
