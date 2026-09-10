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

public interface ICoreManagementService
{
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
    Task<CoreManagementOperationResult> InstallPullRequestAsync(
        GithubRepositoryReference baseRepository,
        int pullRequestNumber,
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

public sealed class CoreManagementService : ICoreManagementService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ICoreInstaller _installer;
    private readonly IGithubCoreRemote _remote;
    private readonly IRuntimeController _runtimeController;
    private readonly Func<CancellationToken, ValueTask<IAsyncDisposable>>? _preparationLeaseFactory;
    private readonly Func<ManagedCoreVariant> _activeVariantProvider;

    public CoreManagementService(
        ICoreInstaller installer,
        IGithubCoreRemote remote,
        IRuntimeController runtimeController,
        Func<ManagedCoreVariant> activeVariantProvider,
        Func<CancellationToken, ValueTask<IAsyncDisposable>>? preparationLeaseFactory = null)
    {
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _runtimeController = runtimeController ?? throw new ArgumentNullException(nameof(runtimeController));
        _activeVariantProvider = activeVariantProvider ?? throw new ArgumentNullException(nameof(activeVariantProvider));
        _preparationLeaseFactory = preparationLeaseFactory;
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

    public async Task<CoreManagementOperationResult> InstallPullRequestAsync(
        GithubRepositoryReference baseRepository,
        int pullRequestNumber,
        string displayName,
        string proxyId,
        IProgress<CoreInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseRepository);
        if (pullRequestNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pullRequestNumber));
        }

        ValidateDisplayName(displayName);
        _ = GithubProxyCatalog.GetById(proxyId);
        var pullRequest = await _remote.GetPullRequestAsync(
            baseRepository,
            pullRequestNumber,
            cancellationToken).ConfigureAwait(false);
        if (pullRequest.Number != pullRequestNumber)
        {
            throw new GithubRemoteException(
                GithubFailureKind.Protocol,
                $"GitHub PR 编号不匹配：请求 #{pullRequestNumber}，返回 #{pullRequest.Number}");
        }

        var headRepository = GithubRepositoryReference.Parse(pullRequest.HeadRepository)
            .WithBranch(pullRequest.HeadBranch);
        return await InstallResolvedAsync(
            new CoreInstallRequest(
                ManagedCoreVariant.Custom,
                headRepository,
                pullRequest.HeadBranch,
                ValidateCommitSha(pullRequest.HeadSha),
                displayName,
                CoreInstallKind.PullRequest,
                proxyId,
                pullRequest.Number),
            progress,
            cancellationToken).ConfigureAwait(false);
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
                    proxyId),
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
        var repository = GithubRepositoryReference.Parse(installed.Repository)
            .WithBranch(installed.Branch);
        return InstallResolvedAsync(
            new CoreInstallRequest(
                variant,
                repository,
                installed.Branch,
                installed.CommitSha,
                installed.DisplayName,
                CoreInstallKind.Reinstall,
                proxyId,
                installed.PullRequestNumber),
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
            return new CoreManagementOperationResult(
                true,
                false,
                true,
                _installer.Inspect(variant),
                "核心显示名称已更新。");
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
        Action? precondition = null)
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
            Exception? inspectionError = null;
            try
            {
                // Inspect actual disk state even when the mutation failed after committing.
                var current = _installer.Inspect(variant);
                diskApplied = deleting
                    ? !current.IsInstalled
                    : expectedSha is not null
                        ? IsExpectedInstallationApplied(variant, expectedSha, current) &&
                          (mutationError is null || !Equals(initialInstallation, current))
                        : mutationError is null || !Equals(initialInstallation, current);
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

    private void EnsureUpdateIsCurrent(CoreUpdateCheckResult update)
    {
        var current = _installer.Inspect(update.Variant).Manifest
            ?? throw new InvalidOperationException("应用更新前当前核心缺少来源 manifest");
        var checkedLocal = update.Local
            ?? throw new InvalidOperationException("更新检查缺少本地 manifest");
        if (!string.Equals(current.Repository, checkedLocal.Repository, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.Branch, checkedLocal.Branch, StringComparison.Ordinal) ||
            !CommitShasEquivalent(current.CommitSha, checkedLocal.CommitSha))
        {
            throw new InvalidOperationException("核心来源或版本在更新检查后已变化，请重新检查更新");
        }
    }

    private CoreInstallationManifest RequireInstalledManifest(ManagedCoreVariant variant)
    {
        var installed = _installer.Inspect(variant);
        if (!installed.IsInstalled || !installed.IsValid)
        {
            throw new InvalidOperationException(installed.Diagnostic ?? "核心尚未安装");
        }

        return installed.Manifest
            ?? throw new InvalidOperationException("当前核心缺少来源 manifest，无法重新安装");
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
