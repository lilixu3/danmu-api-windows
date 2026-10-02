using DanmuApi.Core;
using DanmuApi.Runtime;

namespace DanmuApi.App.Services;

public sealed record CoreManagementOperationResult(
    bool Succeeded,
    bool DiskChangeApplied,
    bool ServiceRestored,
    CoreInstallationInfo? Installation,
    string Diagnostic,
    bool RouteInvalid = false)
{
    public Exception? MutationError { get; init; }
    public Exception? RestorationError { get; init; }
    public Exception? DiskInspectionError { get; init; }
    public bool DiskStateKnown => DiskInspectionError is null;
}

public sealed class CoreManagementCanceledException : OperationCanceledException
{
    public CoreManagementOperationResult Result { get; }

    public CoreManagementCanceledException(CoreManagementOperationResult result, OperationCanceledException original)
        : base(result.Diagnostic,
            new AggregateException(new[] { original, result.RestorationError, result.DiskInspectionError }
                .OfType<Exception>()), original.CancellationToken)
    {
        Result = result;
    }
}

/// <summary>磁盘上的核心安装状态被改动后（安装、更新、回退、删除、改名）发出的通知，
/// 参数是被改动的变体。托盘「立即更新核心」与后台自动更新都不经过核心页，
/// 订阅方必须据此回读磁盘，否则版本号会一直停在更新前的值。</summary>
public sealed class CoreInstallationChangedEventArgs(ManagedCoreVariant variant) : EventArgs
{
    public ManagedCoreVariant Variant { get; } = variant;
}

/// <summary>
/// 磁盘上的核心安装被改动后，由本服务负责把「有更新」这条结论重新对账一次。
/// 之所以放在这里而不是让各订阅方自己判断：安装变更的入口有安装/更新/回退/重装/删除多个，
/// 每个订阅方各自去比对提交容易漏掉其中一条；结论是否还成立只有协调器知道。
/// </summary>
public interface ICoreUpdateConclusionReconciler
{
    void ReconcileDiscovery(ManagedCoreVariant variant);
}

public interface ICorePullRequestManagementService
{
    Task<CorePreparedInstallRequest> PreparePullRequestMergeAsync(
        ManagedCoreVariant variant,
        IReadOnlyList<GithubPullRequest> pullRequests,
        string proxyId,
        IProgress<CoreInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<CoreManagementOperationResult> ApplyPreparedPullRequestMergeAsync(
        CorePreparedInstallRequest request,
        IProgress<CoreInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);

    void DiscardPreparedPullRequestMerge(CorePreparedInstallRequest request);

    /// <summary>
    /// 核对"远端最新提交 <paramref name="remoteSha"/> 到底包不包含已并入的这些 PR"。
    /// 结论只描述事实：包含（有证据）、不包含（有原因）、无法确认（GitHub 读不到）。
    /// </summary>
    Task<CorePullRequestPresenceReport> AnalyzePullRequestPresenceAsync(
        ManagedCoreVariant variant,
        string remoteSha,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 本地 PR 组合的更新：把基线换成远端最新提交，再把远端没有的 PR 按当前 head 重新并进去。
    /// <paramref name="remergeNumbers"/> 为空表示"只更新、不重新并入"（远端已包含全部 PR，
    /// 或用户在对话框里选择放弃这些本地改动）——此时组合身份结束，变回普通分支安装。
    /// </summary>
    Task<CoreManagementOperationResult> ApplyPullRequestStackUpdateAsync(
        CoreUpdateCheckResult update,
        IReadOnlyList<int> remergeNumbers,
        string proxyId,
        IProgress<CoreInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface ICoreManagementService
{
    /// <summary>磁盘上的核心安装状态确实发生变化后发出；在操作线程上触发，订阅方需自行回到 UI 线程。</summary>
    event EventHandler<CoreInstallationChangedEventArgs>? InstallationChanged;
    CoreInstallationInfo Inspect(ManagedCoreVariant variant);
    IReadOnlyList<CoreVersionRecord> GetHistory(ManagedCoreVariant variant);
    Task<GithubRepositoryReference> ResolveRepositoryAsync(
        GithubRepositoryReference repository,
        CancellationToken cancellationToken = default);
    Task<CoreManagementOperationResult> InstallBranchAsync(
        ManagedCoreVariant variant,
        GithubRepositoryReference repository,
        string displayName,
        string proxyId,
        IProgress<CoreInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<CoreManagementOperationResult> InstallCommitAsync(
        ManagedCoreVariant variant,
        GithubRepositoryReference repository,
        string branch,
        string commitSha,
        string displayName,
        string proxyId,
        IProgress<CoreInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<CoreManagementOperationResult> ApplyUpdateAsync(
        CoreUpdateCheckResult update,
        string proxyId,
        IProgress<CoreInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<CoreManagementOperationResult> ReinstallAsync(
        ManagedCoreVariant variant,
        string proxyId,
        IProgress<CoreInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<CoreManagementOperationResult> RollbackAsync(
        ManagedCoreVariant variant,
        string historyId,
        CancellationToken cancellationToken = default);
    Task<CoreManagementOperationResult> RenameAsync(
        ManagedCoreVariant variant,
        string displayName,
        CancellationToken cancellationToken = default);
    Task<CoreManagementOperationResult> DeleteAsync(
        ManagedCoreVariant variant,
        CancellationToken cancellationToken = default);
}

public sealed class CoreManagementService : ICoreManagementService, ICorePullRequestManagementService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ICoreInstaller _installer;
    private readonly ICorePreparedInstaller? _preparedInstaller;
    private readonly ICoreUpdateCandidateInstaller? _candidateInstaller;
    private readonly ICorePullRequestMergeService? _pullRequestMerge;
    private readonly IGithubCoreRemote _remote;
    private readonly ICorePullRequestPresenceAnalyzer _presenceAnalyzer;
    private readonly IRuntimeController _runtimeController;
    private readonly Func<CancellationToken, ValueTask<IAsyncDisposable>>? _preparationLeaseFactory;
    private readonly Func<ManagedCoreVariant> _activeVariantProvider;
    private readonly Func<ICoreUpdateConclusionReconciler>? _conclusionReconciler;
    private readonly Action<string>? _diagnostics;

    public event EventHandler<CoreInstallationChangedEventArgs>? InstallationChanged;

    public CoreManagementService(
        ICoreInstaller installer,
        IGithubCoreRemote remote,
        IRuntimeController runtimeController,
        Func<ManagedCoreVariant> activeVariantProvider,
        Func<CancellationToken, ValueTask<IAsyncDisposable>>? preparationLeaseFactory = null,
        Func<ICoreUpdateConclusionReconciler>? conclusionReconciler = null,
        Action<string>? diagnosticSink = null,
        ICorePullRequestMergeService? pullRequestMerge = null,
        ICorePullRequestPresenceAnalyzer? presenceAnalyzer = null)
    {
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _preparedInstaller = installer as ICorePreparedInstaller;
        _candidateInstaller = installer as ICoreUpdateCandidateInstaller;
        _pullRequestMerge = pullRequestMerge;
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        // 默认实现直接复用同一个 GitHub 客户端：核对 PR 是否存在与其它读取走同一条线路与令牌。
        _presenceAnalyzer = presenceAnalyzer ?? new CorePullRequestPresenceAnalyzer(_remote);
        _runtimeController = runtimeController ?? throw new ArgumentNullException(nameof(runtimeController));
        _activeVariantProvider = activeVariantProvider ?? throw new ArgumentNullException(nameof(activeVariantProvider));
        _preparationLeaseFactory = preparationLeaseFactory;
        // 用 Func 延迟解析：结论的持有者（更新结果处理器）本身依赖本服务，构造期直接注入会成环。
        _conclusionReconciler = conclusionReconciler;
        _diagnostics = diagnosticSink;
    }

    public CoreInstallationInfo Inspect(ManagedCoreVariant variant) => _installer.Inspect(variant);

    public IReadOnlyList<CoreVersionRecord> GetHistory(ManagedCoreVariant variant) =>
        _installer.GetHistory(variant);

    public async Task<GithubRepositoryReference> ResolveRepositoryAsync(
        GithubRepositoryReference repository,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (repository.HasExplicitBranch)
        {
            return repository;
        }

        var metadata = await _remote.GetRepositoryAsync(repository, cancellationToken).ConfigureAwait(false);
        var resolved = GithubRepositoryReference.Parse(metadata.FullName);
        if (!string.Equals(resolved.FullName, repository.FullName, StringComparison.OrdinalIgnoreCase))
        {
            throw new GithubRemoteException(
                GithubFailureKind.Protocol,
                $"GitHub 仓库身份不匹配：请求 {repository.FullName}，返回 {metadata.FullName}");
        }

        return repository.WithBranch(metadata.DefaultBranch);
    }

    public async Task<CoreManagementOperationResult> InstallBranchAsync(
        ManagedCoreVariant variant,
        GithubRepositoryReference repository,
        string displayName,
        string proxyId,
        IProgress<CoreInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateDisplayName(displayName);
        _ = GithubProxyCatalog.GetById(proxyId);
        var resolved = await ResolveRepositoryAsync(repository, cancellationToken).ConfigureAwait(false);
        var branch = resolved.Branch
            ?? throw new InvalidOperationException("仓库默认分支解析完成后仍为空");
        var commit = await _remote.GetCommitAsync(resolved, branch, cancellationToken).ConfigureAwait(false);
        return await InstallResolvedAsync(
            new CoreInstallRequest(
                variant,
                resolved,
                branch,
                commit.Sha,
                displayName,
                CoreInstallKind.Branch,
                proxyId),
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<CoreManagementOperationResult> InstallCommitAsync(
        ManagedCoreVariant variant,
        GithubRepositoryReference repository,
        string branch,
        string commitSha,
        string displayName,
        string proxyId,
        IProgress<CoreInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ValidateDisplayName(displayName);
        var normalizedBranch = GithubRepositoryReference.ValidateBranch(branch);
        var normalizedSha = ValidateCommitSha(commitSha);
        _ = GithubProxyCatalog.GetById(proxyId);
        return InstallResolvedAsync(
            new CoreInstallRequest(
                variant,
                repository.WithBranch(normalizedBranch),
                normalizedBranch,
                normalizedSha,
                displayName,
                CoreInstallKind.Commit,
                proxyId),
            progress,
            cancellationToken);
    }

    public async Task<CorePreparedInstallRequest> PreparePullRequestMergeAsync(
        ManagedCoreVariant variant,
        IReadOnlyList<GithubPullRequest> pullRequests,
        string proxyId,
        IProgress<CoreInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_pullRequestMerge is null)
        {
            throw new InvalidOperationException("本地 PR 合并服务尚未配置");
        }
        ArgumentNullException.ThrowIfNull(pullRequests);
        _ = GithubProxyCatalog.GetById(proxyId);
        var installed = RequireInstalledManifestInfo(variant);
        var repository = GithubRepositoryReference.Parse(installed.Manifest!.Repository)
            .WithBranch(installed.Manifest.Branch);
        if (pullRequests.Count is < 1 or > 100 || pullRequests.Any(pr => pr is null || pr.Number <= 0) ||
            pullRequests.Select(pr => pr.Number).Distinct().Count() != pullRequests.Count)
        {
            throw new ArgumentException("PR 合并队列必须包含 1 到 100 个不重复的有效编号", nameof(pullRequests));
        }
        var requested = pullRequests.ToDictionary(pr => pr.Number);
        var sources = installed.Manifest.IsLocalPullRequestStack
            ? installed.Manifest.PullRequests.Where(source => !requested.ContainsKey(source.Number))
            : Enumerable.Empty<CorePullRequestSource>();
        foreach (var source in sources)
        {
            var current = await _remote.GetPullRequestAsync(repository, source.Number, cancellationToken).ConfigureAwait(false);
            ValidateCurrentPullRequest(current, repository, source.Number, source.HeadSha,
                source.HeadRepository, source.HeadBranch);
        }
        foreach (var selected in pullRequests)
        {
            var current = await _remote.GetPullRequestAsync(repository, selected.Number, cancellationToken).ConfigureAwait(false);
            ValidateCurrentPullRequest(current, repository, selected.Number, selected.HeadSha,
                selected.HeadRepository, selected.HeadBranch);
        }
        return await _pullRequestMerge.PrepareAsync(
            variant,
            installed,
            repository,
            pullRequests,
            proxyId,
            installed.Manifest.DisplayName,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CoreManagementOperationResult> ApplyPreparedPullRequestMergeAsync(
        CorePreparedInstallRequest request,
        IProgress<CoreInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_preparedInstaller is null)
        {
            throw new InvalidOperationException("当前核心安装器不支持应用本地 PR 合并结果");
        }

        CorePreparedInstallation? applied = null;
        return await MutateAsync(
            request.Variant,
            request.BaseCommitSha,
            async token =>
            {
                applied = await _preparedInstaller.InstallPreparedAsync(request, progress, token).ConfigureAwait(false);
                return applied.Installation;
            },
            cancellationToken,
            precondition: () => EnsurePreparedMergeIsCurrent(request),
            postMutation: (result, restartRequired) => CompleteCandidateAsync(
                request.Variant, request.ExpectedManifest, applied, result, restartRequired)).ConfigureAwait(false);
    }

    private async Task<CoreManagementOperationResult> CompleteCandidateAsync(
        ManagedCoreVariant variant,
        CoreInstallationManifest expectedManifest,
        CorePreparedInstallation? applied,
        CoreManagementOperationResult result,
        bool restartRequired)
    {
        if (applied is null) return result;
        if (applied.BackupDirectory is null)
        {
            return result with
            {
                Succeeded = false,
                Diagnostic = $"{result.Diagnostic}；替换后未保留旧核心恢复点，无法确认候选核心",
            };
        }
        if (!result.Succeeded)
        {
            return await RestoreFailedCandidateAsync(variant, expectedManifest, applied.BackupDirectory,
                result, restartRequired).ConfigureAwait(false);
        }
        try
        {
            await _preparedInstaller!.ConfirmPreparedBackupAsync(
                variant, applied.BackupDirectory, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception archiveError) when (archiveError is IOException or UnauthorizedAccessException)
        {
            _diagnostics?.Invoke($"候选核心已确认成功，但归档旧核心失败：{archiveError.Message}");
            return result with { Diagnostic = $"{result.Diagnostic}；旧核心历史归档失败：{archiveError.Message}" };
        }
        return result;
    }

    private async Task<CoreManagementOperationResult> RestoreFailedCandidateAsync(
        ManagedCoreVariant variant,
        CoreInstallationManifest expectedManifest,
        string backupDirectory,
        CoreManagementOperationResult result,
        bool restartRequired)
    {
        var diagnostic = result.Diagnostic;
        var diskRestored = false;
        try
        {
            if (restartRequired)
            {
                await _runtimeController.StopAsync(CancellationToken.None).ConfigureAwait(false);
                var stopped = _runtimeController.Snapshot;
                if (stopped.State != DesktopRuntimeState.Stopped || stopped.Pid is not null)
                {
                    throw new InvalidOperationException($"候选核心停止失败：{stopped.State}；{stopped.FailureReason}");
                }
            }
            await _preparedInstaller!.RestorePreparedBackupAsync(
                variant, backupDirectory, CancellationToken.None).ConfigureAwait(false);
            var restored = RequireInstalledManifestInfo(variant);
            if (!CoreInstallationManifest.SourcesEqual(restored.Manifest, expectedManifest))
            {
                throw new InvalidOperationException("恢复后的核心完整来源与原安装不一致，拒绝报告恢复成功");
            }
            diskRestored = true;
            if (restartRequired)
            {
                await _runtimeController.StartAsync(CancellationToken.None).ConfigureAwait(false);
                if (_runtimeController.Snapshot.State != DesktopRuntimeState.Running)
                {
                    throw new InvalidOperationException(_runtimeController.Snapshot.FailureReason
                        ?? $"旧核心恢复后未运行：{_runtimeController.Snapshot.State}");
                }
            }
            return result with
            {
                Succeeded = false,
                DiskChangeApplied = false,
                ServiceRestored = true,
                Installation = restored,
                Diagnostic = $"{diagnostic}；已恢复旧核心{(restartRequired ? "并重新启动原服务" : string.Empty)}",
            };
        }
        catch (Exception recoveryError)
        {
            try
            {
                var current = _installer.Inspect(variant);
                return result with
                {
                    Succeeded = false,
                    DiskChangeApplied = result.DiskChangeApplied &&
                        CoreInstallationManifest.SourcesEqual(current.Manifest, result.Installation?.Manifest),
                    ServiceRestored = false,
                    Installation = current,
                    Diagnostic = $"{diagnostic}；旧核心恢复失败：{recoveryError.Message}",
                    RestorationError = result.RestorationError is null ? recoveryError :
                        new AggregateException(result.RestorationError, recoveryError),
                };
            }
            catch (Exception inspectionError)
            {
                return result with
                {
                    Succeeded = false,
                    ServiceRestored = false,
                    Installation = null,
                    DiskChangeApplied = !diskRestored && result.DiskChangeApplied,
                    Diagnostic = $"{diagnostic}；旧核心恢复失败：{recoveryError.Message}；读取磁盘状态失败：{inspectionError.Message}",
                    RestorationError = result.RestorationError is null ? recoveryError :
                        new AggregateException(result.RestorationError, recoveryError),
                    DiskInspectionError = inspectionError,
                };
            }
        }
    }

    public void DiscardPreparedPullRequestMerge(CorePreparedInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var staging = Path.GetFullPath(request.StagingDirectory);
        if (!Directory.Exists(staging))
        {
            return;
        }
        Directory.Delete(staging, recursive: true);
    }

    public async Task<CorePullRequestPresenceReport> AnalyzePullRequestPresenceAsync(
        ManagedCoreVariant variant,
        string remoteSha,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteSha);
        var installed = RequireInstalledManifestInfo(variant);
        var manifest = installed.Manifest!;
        if (!manifest.IsLocalPullRequestStack || manifest.PullRequests.Count == 0)
        {
            throw new InvalidOperationException("当前核心不是本地 PR 组合，没有需要核对的 PR");
        }

        var repository = GithubRepositoryReference.Parse(manifest.Repository).WithBranch(manifest.Branch);
        return await _presenceAnalyzer
            .AnalyzeAsync(repository, remoteSha.Trim(), manifest.PullRequests, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<CoreManagementOperationResult> ApplyPullRequestStackUpdateAsync(
        CoreUpdateCheckResult update,
        IReadOnlyList<int> remergeNumbers,
        string proxyId,
        IProgress<CoreInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(remergeNumbers);
        if (update.Status != CoreUpdateCheckStatus.Checked || !update.UpdateAvailable ||
            update.Local is null || update.Remote is null)
        {
            throw new InvalidOperationException("更新结果没有可应用的新提交");
        }

        if (!update.Local.IsLocalPullRequestStack)
        {
            throw new InvalidOperationException("当前核心不是本地 PR 组合，请走普通分支更新");
        }

        if (remergeNumbers.Count > 0 &&
            (remergeNumbers.Count != remergeNumbers.Distinct().Count() || remergeNumbers.Any(number => number <= 0)))
        {
            throw new ArgumentException("要重新并入的 PR 编号必须是 1 个以上不重复的有效编号", nameof(remergeNumbers));
        }

        _ = GithubProxyCatalog.GetById(proxyId);
        var installed = RequireInstalledManifestInfo(update.Variant);
        var manifest = installed.Manifest!;
        // 同基线的组合也可能已换 PR 队列；核验完整快照，不能仅按基线 SHA 放行。
        if (!CoreInstallationManifest.SourcesEqual(manifest, update.Local) || manifest.Variant != update.Variant)
        {
            throw new InvalidOperationException("已安装的核心在核对之后发生了变化，请重新检查更新");
        }

        var repository = GithubRepositoryReference.Parse(manifest.Repository).WithBranch(manifest.Branch);
        var remoteSha = ValidateCommitSha(update.Remote.Sha);
        if (remergeNumbers.Count == 0)
        {
            // 普通分支来源仍然结束组合身份，但健康确认前保留恢复点，不走已提交的普通安装路径。
            if (_candidateInstaller is null)
            {
                throw new InvalidOperationException("当前核心安装器不支持保留恢复点的分支更新候选");
            }
            CorePreparedInstallation? applied = null;
            return await MutateAsync(
                update.Variant,
                remoteSha,
                async token =>
                {
                    applied = await _candidateInstaller.InstallUpdateCandidateAsync(
                        new CoreInstallRequest(
                            update.Variant, repository, manifest.Branch, remoteSha,
                            manifest.DisplayName, CoreInstallKind.Branch, proxyId),
                        manifest, progress, token).ConfigureAwait(false);
                    return applied.Installation;
                },
                cancellationToken,
                precondition: () => EnsureUpdateIsCurrent(update, allowStack: true),
                postMutation: (result, restartRequired) => CompleteCandidateAsync(
                    update.Variant, manifest, applied, result, restartRequired)).ConfigureAwait(false);
        }

        if (_pullRequestMerge is null || _preparedInstaller is null)
        {
            throw new InvalidOperationException("当前核心安装器不支持本地 PR 合并结果");
        }

        // 重新并入按"当前 head"进行：核对时可能已经发现 head 前进过，
        // 这里逐条重新读一次并确认它仍然可合并（open、目标分支没变），不行就明确报出来。
        var requested = new List<GithubPullRequest>(remergeNumbers.Count);
        foreach (var number in remergeNumbers)
        {
            var current = await _remote.GetPullRequestAsync(repository, number, cancellationToken).ConfigureAwait(false);
            ValidateReMergeablePullRequest(current, repository, number);
            requested.Add(current);
        }

        var prepared = await _pullRequestMerge.PrepareAsync(
            update.Variant,
            installed,
            repository,
            requested,
            proxyId,
            manifest.DisplayName,
            progress,
            cancellationToken,
            baseShaOverride: remoteSha,
            inheritExistingStack: false).ConfigureAwait(false);
        try
        {
            return await ApplyPreparedPullRequestMergeAsync(prepared, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DiscardPreparedPullRequestMerge(prepared);
        }
    }

    public Task<CoreManagementOperationResult> ApplyUpdateAsync(
        CoreUpdateCheckResult update,
        string proxyId,
        IProgress<CoreInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.Status != CoreUpdateCheckStatus.Checked || !update.UpdateAvailable ||
            update.Local is null || update.Remote is null)
        {
            throw new InvalidOperationException("更新结果没有可应用的新提交");
        }

        _ = GithubProxyCatalog.GetById(proxyId);
        if (update.Local.IsLocalPullRequestStack)
        {
            throw new InvalidOperationException("当前核心包含本地 PR 组合，请走「更新并核对 PR」流程：先确认远端是否已包含这些 PR，再决定是否重新并入。");
        }
        var repository = GithubRepositoryReference.Parse(update.Local.Repository)
            .WithBranch(update.Local.Branch);
        return MutateAsync(
            update.Variant,
            ValidateCommitSha(update.Remote.Sha),
            async token => await _installer.InstallAsync(
                new CoreInstallRequest(
                    update.Variant,
                    repository,
                    update.Local.Branch,
                    ValidateCommitSha(update.Remote.Sha),
                    update.Local.DisplayName,
                    CoreInstallKind.Branch,
                    proxyId) { ExpectedManifest = update.Local },
                progress,
                token).ConfigureAwait(false),
            cancellationToken,
            precondition: () => EnsureUpdateIsCurrent(update));
    }

    public Task<CoreManagementOperationResult> ReinstallAsync(
        ManagedCoreVariant variant,
        string proxyId,
        IProgress<CoreInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var installed = RequireInstalledManifest(variant);
        _ = GithubProxyCatalog.GetById(proxyId);
        if (installed.IsLocalPullRequestStack)
        {
            throw new InvalidOperationException("当前是本地 PR 组合，不能用分支压缩包重新安装；请在 PR 实验室重新构建组合，或从本地历史恢复。");
        }
        var repository = GithubRepositoryReference.Parse(installed.Repository)
            .WithBranch(installed.Branch);
        // 旧版单 PR 实验安装记录的 manifest 带 PR 编号，但那个安装来源已被移除：
        // 重装按记录的提交做普通重装，并把 PR 字段清掉（不猜测、也不假装还是 PR 安装）。
        return InstallResolvedAsync(
            new CoreInstallRequest(
                variant,
                repository,
                installed.Branch,
                installed.CommitSha,
                installed.DisplayName,
                CoreInstallKind.Reinstall,
                proxyId),
            progress,
            cancellationToken);
    }

    public Task<CoreManagementOperationResult> RollbackAsync(
        ManagedCoreVariant variant,
        string historyId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            variant,
            expectedSha: null,
            async token => (CoreInstallationInfo?)await _installer
                .RestoreHistoryAsync(variant, historyId, token)
                .ConfigureAwait(false),
            cancellationToken);

    public async Task<CoreManagementOperationResult> RenameAsync(
        ManagedCoreVariant variant,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        ValidateDisplayName(displayName);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var preparationLease = _preparationLeaseFactory is null ? null : await _preparationLeaseFactory(cancellationToken).ConfigureAwait(false);
            _ = RequireInstalledManifest(variant);
            _installer.UpdateDisplayName(variant, displayName.Trim());
            var installation = _installer.Inspect(variant);
            var result = new CoreManagementOperationResult(
                true,
                false,
                true,
                installation,
                "核心显示名称已更新。");
            // 改名也改了磁盘上的来源 manifest，来源带与版本带同样要立刻跟上。
            // 改名不动提交，所以更新结论仍然成立（对账会因此不广播，只刷新本地 manifest）。
            InstallationChanged?.Invoke(this, new CoreInstallationChangedEventArgs(variant));
            ReconcileUpdateConclusion(variant);
            return result;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new CoreManagementOperationResult(
                false,
                false,
                true,
                null,
                $"保存核心设置失败：{error.Message}");
        }
    }

    public Task<CoreManagementOperationResult> DeleteAsync(
        ManagedCoreVariant variant,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            variant,
            expectedSha: null,
            token =>
            {
                token.ThrowIfCancellationRequested();
                _installer.Delete(variant);
                return Task.FromResult<CoreInstallationInfo?>(null);
            },
            cancellationToken,
            deleting: true);

    private Task<CoreManagementOperationResult> InstallResolvedAsync(
        CoreInstallRequest request,
        IProgress<CoreInstallProgress>? progress,
        CancellationToken cancellationToken) =>
        MutateAsync(
            request.Variant,
            request.CommitSha,
            async token => await _installer.InstallAsync(request, progress, token).ConfigureAwait(false),
            cancellationToken);

    private async Task<CoreManagementOperationResult> MutateAsync(
        ManagedCoreVariant variant,
        string? expectedSha,
        Func<CancellationToken, Task<CoreInstallationInfo?>> mutation,
        CancellationToken cancellationToken,
        bool deleting = false,
        Action? precondition = null,
        Func<CoreManagementOperationResult, bool, Task<CoreManagementOperationResult>>? postMutation = null)
    {
        await using var preparationLease = _preparationLeaseFactory is null ? null : await _preparationLeaseFactory(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            precondition?.Invoke();
            var affectsActiveRuntime = variant == _activeVariantProvider();
            var before = _runtimeController.Snapshot;
            ValidateRuntimeState(before, affectsActiveRuntime);
            var initialInstallation = _installer.Inspect(variant);
            var restartRequired = affectsActiveRuntime && before.State == DesktopRuntimeState.Running;
            if (restartRequired)
            {
                await _runtimeController.StopAsync(cancellationToken).ConfigureAwait(false);
                var stopped = _runtimeController.Snapshot;
                if (stopped.State != DesktopRuntimeState.Stopped || stopped.Pid is not null)
                {
                    return new CoreManagementOperationResult(
                        false,
                        false,
                        false,
                        null,
                        $"核心操作前停止服务失败：{stopped.State}；{stopped.FailureReason ?? "未提供失败原因"}");
                }
            }

            CoreInstallationInfo? installation = null;
            Exception? mutationError = null;
            try
            {
                installation = await mutation(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                mutationError = error;
            }

            var diskApplied = false;
            var installationChanged = false;
            Exception? inspectionError = null;
            try
            {
                // Inspect actual disk state even when the mutation failed after committing.
                var current = _installer.Inspect(variant);
                // 与操作前的快照比对：只有磁盘真的变了才通知。"期望的新提交没装上"不算变更，
                // 但"期望的没装上、磁盘却变成别的样子"（例如装到一半失败）必须通知，否则界面会一直
                // 显示一个已经不存在的安装状态。
                installationChanged = !SameInstallation(initialInstallation, current);
                diskApplied = deleting
                    ? !current.IsInstalled
                    : expectedSha is not null
                        ? IsExpectedInstallationApplied(variant, expectedSha, current) &&
                          (mutationError is null || !SameInstallation(initialInstallation, current))
                        : mutationError is null || !SameInstallation(initialInstallation, current);
                installation = current;
            }
            catch (Exception error)
            {
                inspectionError = error;
            }

            var serviceRestored = !restartRequired;
            Exception? restorationError = null;
            var deletedActiveCore = deleting && affectsActiveRuntime && diskApplied;
            try
            {
                if (deletedActiveCore)
                {
                    serviceRestored = false;
                    await _runtimeController.StartAsync(CancellationToken.None).ConfigureAwait(false);
                    if (_runtimeController.Snapshot.State != DesktopRuntimeState.CoreSetupRequired)
                    {
                        throw new InvalidOperationException(
                            $"核心已删除，但运行时未进入 CoreSetupRequired：{_runtimeController.Snapshot.State}；{_runtimeController.Snapshot.FailureReason}");
                    }
                }
                else
                {
                    serviceRestored = await RestoreServiceAfterMutationAsync(restartRequired).ConfigureAwait(false);
                    if (!serviceRestored)
                    {
                        throw new InvalidOperationException(
                            $"服务恢复失败：{_runtimeController.Snapshot.FailureReason ?? _runtimeController.Snapshot.State.ToString()}");
                    }

                    // 删除当前核心会把运行时停在 CoreSetupRequired，而该状态下主窗口与托盘的启动入口都被禁用。
                    // 本次变更前服务没在运行（restartRequired=false）时上面不会启动服务，这个停驻状态就会一直留着，
                    // 用户装回核心也点不动"启动服务"——所以影响活动运行时的变更结束后要重新评估一次。
                    if (!restartRequired && affectsActiveRuntime)
                    {
                        await _runtimeController.RefreshCoreSetupRequiredAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception error)
            {
                restorationError = error;
                serviceRestored = !deletedActiveCore &&
                    (!restartRequired || _runtimeController.Snapshot.State == DesktopRuntimeState.Running);
            }

            var diagnostics = new List<string>();
            if (mutationError is not null) diagnostics.Add($"核心操作失败：{mutationError.Message}");
            if (inspectionError is not null) diagnostics.Add($"读取磁盘状态失败：{inspectionError.Message}");
            if (restorationError is not null) diagnostics.Add($"服务恢复失败：{restorationError.Message}");
            if (inspectionError is null && !diskApplied) diagnostics.Add("磁盘变更未确认应用，当前核心状态未发生预期变更");
            if (diagnostics.Count == 0) diagnostics.Add(deleting ? "核心已删除" : "核心操作已完成");
            var result = new CoreManagementOperationResult(
                mutationError is null && inspectionError is null && restorationError is null && diskApplied,
                diskApplied,
                serviceRestored,
                installation,
                BuildDiagnostic(string.Join("；", diagnostics), restartRequired && !deletedActiveCore, serviceRestored),
                mutationError is GithubRouteException or GithubRouteSelectionRequiredException)
            {
                MutationError = mutationError,
                RestorationError = restorationError,
                DiskInspectionError = inspectionError,
            };
            if (postMutation is not null)
            {
                result = await postMutation(result, restartRequired).ConfigureAwait(false);
                if (result.DiskStateKnown)
                {
                    installationChanged = !SameInstallation(initialInstallation, result.Installation);
                }
            }
            if (installationChanged)
            {
                // 服务恢复失败也要通知：磁盘已经变了，界面显示必须反映真实状态，不能停在旧版本。
                InstallationChanged?.Invoke(this, new CoreInstallationChangedEventArgs(variant));
                ReconcileUpdateConclusion(variant);
            }

            if (mutationError is OperationCanceledException canceled)
            {
                throw new CoreManagementCanceledException(result, canceled);
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 磁盘上的安装变了，「有更新」这条结论可能已经不成立（刚把新提交装上、回退到旧版、
    /// 重装成别的提交、核心被删）。这里立刻让结论持有人重新对账，而不是等下一次联网检查 ——
    /// 否则侧栏卡片与托盘菜单会一直挂着一个其实已经装上的「新版本」。
    /// </summary>
    private void ReconcileUpdateConclusion(ManagedCoreVariant variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        if (_conclusionReconciler is null)
        {
            return;
        }

        try
        {
            _conclusionReconciler().ReconcileDiscovery(variant);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            // 对账失败不能反过来影响安装结果：安装已经落盘成功，这里只是刷新提示。
            // 但绝不静默 —— 丢掉的是一条诊断，也必须留下。
            _diagnostics?.Invoke($"核心安装已变更，但重新对账更新结论失败：{error.Message}");
        }
    }

    private async Task<bool> RestoreServiceAfterMutationAsync(bool restartRequired)
    {
        if (!restartRequired)
        {
            return true;
        }

        await _runtimeController.StartAsync(CancellationToken.None).ConfigureAwait(false);
        return _runtimeController.Snapshot.State == DesktopRuntimeState.Running;
    }

    private bool IsExpectedInstallationApplied(
        ManagedCoreVariant variant,
        string? expectedSha,
        CoreInstallationInfo? returned)
    {
        var current = returned ?? _installer.Inspect(variant);
        if (!current.IsInstalled || !current.IsValid)
        {
            return false;
        }

        if (expectedSha is null)
        {
            return true;
        }

        return current.Manifest is not null && CommitShasEquivalent(current.Manifest.CommitSha, expectedSha);
    }

    private void EnsureUpdateIsCurrent(CoreUpdateCheckResult update, bool allowStack = false)
    {
        var current = RequireInstalledManifest(update.Variant);
        var checkedLocal = update.Local
            ?? throw new InvalidOperationException("更新检查缺少本地 manifest");
        if (!allowStack && current.IsLocalPullRequestStack)
        {
            throw new InvalidOperationException("当前核心已变化为本地 PR 组合，请重新检查更新并核对 PR；拒绝应用旧普通更新结果");
        }
        if (current.Variant != update.Variant ||
            !CoreInstallationManifest.SourcesEqual(current, checkedLocal))
        {
            throw new InvalidOperationException("核心来源或版本在更新检查后已变化，请重新检查更新");
        }
    }

    private CoreInstallationInfo RequireInstalledManifestInfo(ManagedCoreVariant variant)
    {
        var installed = _installer.Inspect(variant);
        if (!installed.IsInstalled || !installed.IsValid)
        {
            throw new InvalidOperationException(installed.Diagnostic ?? "核心尚未安装");
        }
        _ = installed.Manifest
            ?? throw new InvalidOperationException("当前核心缺少来源 manifest，无法进行本地 PR 合并");
        return installed;
    }

    private CoreInstallationManifest RequireInstalledManifest(ManagedCoreVariant variant) =>
        RequireInstalledManifestInfo(variant).Manifest!;

    private static void ValidateCurrentPullRequest(
        GithubPullRequest current,
        GithubRepositoryReference repository,
        int number,
        string expectedSha,
        string expectedHeadRepository,
        string expectedHeadBranch)
    {
        if (current.Number != number ||
            !string.Equals(current.State, "open", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.BaseBranch, repository.Branch, StringComparison.Ordinal) ||
            !string.Equals(current.HeadRepository, expectedHeadRepository, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.HeadBranch, expectedHeadBranch, StringComparison.Ordinal) ||
            !string.Equals(current.HeadSha, expectedSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"PR #{number} 的状态、目标分支或 head 已变化，请刷新 PR 列表后重新准备");
        }
    }

    private static void ValidateReMergeablePullRequest(
        GithubPullRequest current,
        GithubRepositoryReference repository,
        int number)
    {
        if (current.Number != number)
        {
            throw new InvalidOperationException($"GitHub 返回的是 PR #{current.Number}，与请求的 #{number} 不一致");
        }

        if (!string.Equals(current.State, "open", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"PR #{number} 已经不处于可合并状态（state={current.State}），无法在更新后重新并入；请在核对结果里取消勾选它，或稍后在 PR 实验室处理");
        }

        if (!string.Equals(current.BaseBranch, repository.Branch, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"PR #{number} 的目标分支已变为 {current.BaseBranch}，与本变体的分支 {repository.Branch} 不一致，无法重新并入");
        }
    }

    private void EnsurePreparedMergeIsCurrent(CorePreparedInstallRequest request)
    {
        var current = RequireInstalledManifestInfo(request.Variant);
        var actual = current.Manifest!;
        var expected = request.ExpectedManifest;
        if (actual.Variant != request.Variant || !CoreInstallationManifest.SourcesEqual(actual, expected))
        {
            throw new InvalidOperationException("准备合并期间当前核心来源已变化，请重新准备 PR 合并");
        }
    }

    private static void ValidateRuntimeState(RuntimeSnapshot snapshot, bool affectsActiveRuntime)
    {
        if (!affectsActiveRuntime)
        {
            return;
        }

        if (snapshot.State is DesktopRuntimeState.Preparing or DesktopRuntimeState.Starting or DesktopRuntimeState.Stopping)
        {
            throw new InvalidOperationException($"运行时正在切换状态，暂不能修改当前核心：{snapshot.State}");
        }

        if (snapshot.State == DesktopRuntimeState.Failed && snapshot.Pid is not null)
        {
            throw new InvalidOperationException("运行时失败状态仍跟踪 Node PID，必须先完成停止清理");
        }
    }

    private static bool SameInstallation(CoreInstallationInfo? left, CoreInstallationInfo? right) =>
        left is null || right is null
            ? left is null && right is null
            : left with { Manifest = null } == right with { Manifest = null } &&
              CoreInstallationManifest.SourcesEqual(left.Manifest, right.Manifest);

    private static string BuildDiagnostic(string diagnostic, bool restartRequired, bool serviceRestored) =>
        restartRequired && !serviceRestored
            ? $"{diagnostic}；原服务恢复也失败"
            : restartRequired
                ? $"{diagnostic}；原服务已恢复"
                : diagnostic;

    private static string ValidateCommitSha(string sha)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);
        var value = sha.Trim();
        if (value.Length is < 7 or > 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new FormatException("GitHub commit SHA 无效");
        }

        return value.ToLowerInvariant();
    }

    private static void ValidateDisplayName(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        var value = displayName.Trim();
        if (value.Length > 80 || value.Any(char.IsControl))
        {
            throw new ArgumentException("核心显示名称必须是 1 到 80 个可显示字符", nameof(displayName));
        }
    }

    private static bool CommitShasEquivalent(string left, string right)
    {
        var first = left.Trim();
        var second = right.Trim();
        return first.Length >= 7 && second.Length >= 7 &&
               (first.StartsWith(second, StringComparison.OrdinalIgnoreCase) ||
                second.StartsWith(first, StringComparison.OrdinalIgnoreCase));
    }
}
