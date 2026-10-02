using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace DanmuApi.Core;

public sealed class CoreInstaller : ICoreInstaller, ICorePreparedInstaller, ICoreUpdateCandidateInstaller
{
    private const long MinArchiveBytes = 1024;
    private const long MaxExtractedBytes = 512L * 1024L * 1024L;
    private const int MaxArchiveEntries = 20_000;
    private const int MaxHistoryEntries = 3;
    private readonly string _nodeProjectDirectory;
    private readonly string _cacheDirectory;
    private readonly IGithubFileDownloader _downloader;
    private readonly Action<CoreInstallStage>? _stageHook;

    /// <summary>
    /// 「核心已装好，但善后没做完」这类故障的出口（旧版本历史归档、临时目录清理）。
    /// 它们不许把成功的安装判成失败，但也不能静默丢掉，所以交给宿主的诊断汇记录下来。
    /// </summary>
    private readonly Action<string>? _maintenanceDiagnostic;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CoreInstaller(
        string nodeProjectDirectory,
        string cacheDirectory,
        IGithubFileDownloader downloader,
        Action<CoreInstallStage>? stageHook = null,
        Action<string>? maintenanceDiagnostic = null)
    {
        _nodeProjectDirectory = Path.GetFullPath(nodeProjectDirectory);
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _stageHook = stageHook;
        _maintenanceDiagnostic = maintenanceDiagnostic;
    }

    /// <summary>Serializes dependency mutations with install, delete, rename and rollback.
    /// Acquire the runtime stopped lease first; release this lease before starting the runtime.</summary>
    public async ValueTask<IAsyncDisposable> AcquireMaintenanceLeaseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new CoreMaintenanceLease(_gate);
    }

    private sealed class CoreMaintenanceLease(SemaphoreSlim gate) : IAsyncDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }

    public CoreInstallationInfo Inspect(ManagedCoreVariant variant)
    {
        var directory = CoreDirectory(variant);
        if (!Directory.Exists(directory))
        {
            return new CoreInstallationInfo(variant, directory, false, false, null, null, "核心尚未安装");
        }

        if (!File.Exists(Path.Combine(directory, "worker.js")))
        {
            return new CoreInstallationInfo(
                variant,
                directory,
                true,
                false,
                null,
                null,
                $"核心目录缺少 worker.js：{directory}");
        }

        try
        {
            var manifest = CoreManifestStore.Read(directory);
            var version = CoreVersionReader.ReadVersion(directory);
            return new CoreInstallationInfo(
                variant,
                directory,
                true,
                true,
                version,
                manifest,
                manifest is null ? "核心可运行，但缺少来源 manifest；更新前需要重新安装" : null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new CoreInstallationInfo(variant, directory, true, true, null, null, error.Message);
        }
    }

    public IReadOnlyList<CoreVersionRecord> GetHistory(ManagedCoreVariant variant)
    {
        var root = HistoryRoot(variant);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var records = new List<CoreVersionRecord>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                var manifest = CoreManifestStore.Read(directory)
                    ?? throw new IOException($"历史核心缺少来源 manifest：{directory}");
                if (manifest.Variant != variant || !File.Exists(Path.Combine(directory, "worker.js")))
                {
                    throw new IOException($"历史核心内容与变体不匹配：{directory}");
                }

                records.Add(new CoreVersionRecord(Path.GetFileName(directory), variant, directory, manifest));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"读取核心历史失败：{error.Message}", error);
            }
        }

        return records.OrderByDescending(record => record.Manifest.InstalledAt).ToArray();
    }

    public async Task<CoreInstallationInfo> InstallAsync(
        CoreInstallRequest request,
        IProgress<CoreInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var applied = await InstallArchiveAsync(request, null, progress, cancellationToken).ConfigureAwait(false);
        return applied.Installation;
    }

    public Task<CorePreparedInstallation> InstallUpdateCandidateAsync(
        CoreInstallRequest request,
        CoreInstallationManifest expectedManifest,
        IProgress<CoreInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(expectedManifest);
        if (request.Kind != CoreInstallKind.Branch || request.Variant != expectedManifest.Variant ||
            !string.Equals(request.Repository.FullName, expectedManifest.Repository, StringComparison.Ordinal) ||
            !string.Equals(request.Branch, expectedManifest.Branch, StringComparison.Ordinal))
        {
            throw new ArgumentException("分支更新候选与预期的安装来源不匹配", nameof(request));
        }
        return InstallArchiveAsync(request, expectedManifest, progress, cancellationToken);
    }

    private async Task<CorePreparedInstallation> InstallArchiveAsync(
        CoreInstallRequest request,
        CoreInstallationManifest? expectedManifest,
        IProgress<CoreInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        var retainBackup = expectedManifest is not null;
        expectedManifest ??= request.ExpectedManifest;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (expectedManifest is not null) EnsureCurrentManifest(request.Variant, expectedManifest);
            Directory.CreateDirectory(_nodeProjectDirectory);
            Directory.CreateDirectory(_cacheDirectory);
            EnsureSameVolume(_nodeProjectDirectory, _cacheDirectory);
            var operationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            var archive = Path.Combine(_cacheDirectory, $"core-{request.Variant.ToStorageKey()}-{operationId}.zip");
            var extractRoot = Path.Combine(_nodeProjectDirectory, $".core-extract-{operationId}");
            var staging = Path.Combine(_nodeProjectDirectory, $".{request.Variant.ToDirectoryName()}.staging-{operationId}");
            var backup = Path.Combine(_nodeProjectDirectory, $".{request.Variant.ToDirectoryName()}.backup-{operationId}");
            var replacementApplied = false;
            var cleanupAttempted = false;
            void CleanupCandidate()
            {
                if (cleanupAttempted) return;
                cleanupAttempted = true;
                TryCleanup(staging, extractRoot, archive, string.Empty, request.Variant, progress);
            }
            try
            {
                Report(progress, CoreInstallStage.Downloading, "正在下载核心压缩包");
                _stageHook?.Invoke(CoreInstallStage.Downloading);
                var downloadProgress = progress is null ? null : new Progress<GithubDownloadProgress>(value =>
                    progress.Report(new CoreInstallProgress(
                        CoreInstallStage.Downloading,
                        "正在下载核心压缩包",
                        value.DownloadedBytes,
                        value.TotalBytes,
                        value.RouteLabel)));
                var archiveUri = new Uri(
                    $"https://api.github.com/repos/{Uri.EscapeDataString(request.Repository.Owner)}/{Uri.EscapeDataString(request.Repository.Repository)}/zipball/{Uri.EscapeDataString(request.CommitSha)}",
                    UriKind.Absolute);
                await _downloader.DownloadAsync(
                    archiveUri,
                    request.ProxyId,
                    archive,
                    downloadProgress,
                    cancellationToken).ConfigureAwait(false);
                if (new FileInfo(archive).Length <= MinArchiveBytes)
                {
                    throw new IOException("核心压缩包小于或等于 1KB，拒绝应用");
                }

                cancellationToken.ThrowIfCancellationRequested();
                Report(progress, CoreInstallStage.Extracting, "正在安全解压核心文件");
                _stageHook?.Invoke(CoreInstallStage.Extracting);
                ExtractArchive(archive, extractRoot, cancellationToken);
                PrepareCoreDirectory(extractRoot, staging);

                cancellationToken.ThrowIfCancellationRequested();
                Report(progress, CoreInstallStage.Validating, "正在校验核心入口与来源");
                _stageHook?.Invoke(CoreInstallStage.Validating);
                ValidatePreparedCore(staging);
                var manifest = new CoreInstallationManifest(
                    CoreInstallationManifest.CurrentSchemaVersion,
                    request.Variant,
                    request.Repository.FullName,
                    request.Branch,
                    request.CommitSha.ToLowerInvariant(),
                    CoreVersionReader.ReadVersion(staging),
                    request.DisplayName.Trim(),
                    request.Kind,
                    request.PullRequestNumber,
                    DateTimeOffset.UtcNow);
                CoreManifestStore.Write(staging, manifest);

                cancellationToken.ThrowIfCancellationRequested();
                Report(progress, CoreInstallStage.Replacing, "正在替换核心目录");
                _stageHook?.Invoke(CoreInstallStage.Replacing);
                if (expectedManifest is not null) EnsureCurrentManifest(request.Variant, expectedManifest);
                ReplaceWithRollback(request.Variant, staging, backup);
                replacementApplied = true;
                var installation = InspectOrThrow(request.Variant);
                _stageHook?.Invoke(CoreInstallStage.Completed);
                if (retainBackup)
                {
                    Report(progress, CoreInstallStage.Completed, "分支更新候选已应用，等待服务健康确认");
                    CleanupCandidate(); // 回调和清理仍在交接保护范围内；finally 不得破坏待返回的恢复点句柄。
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Directory.Exists(backup)) throw new IOException("分支更新候选交接前恢复点已不存在");
                    return new CorePreparedInstallation(InspectOrThrow(request.Variant), backup);
                }
                // 替换已经提交，旧版本历史只是善后：这里失败不能把「已装好的新核心」判成安装失败
                // （那样 CoreUpdateResultHandler 会保留待更新状态并无限重试通知）。
                try
                {
                    ArchivePreviousVersion(request.Variant, backup);
                    PruneHistory(request.Variant);
                }
                catch (Exception historyError) when (historyError is IOException or UnauthorizedAccessException)
                {
                    ReportMaintenance(
                        $"新核心已安装并生效，但旧版本历史维护失败：{historyError.Message}",
                        progress);
                }
                Report(progress, CoreInstallStage.Completed, "核心安装完成");
                return new CorePreparedInstallation(installation, null);
            }
            catch (OperationCanceledException error)
            {
                if (retainBackup)
                {
                    RecoverUnreturnedCandidate(error, request.Variant, backup, expectedManifest!,
                        replacementApplied, CleanupCandidate);
                }
                else if (!replacementApplied)
                {
                    try
                    {
                        RestoreBackupIfNeeded(request.Variant, backup);
                    }
                    catch (Exception rollbackError)
                    {
                        throw new OperationCanceledException(
                            $"核心安装已取消：{error.Message}；恢复原核心也失败：{rollbackError.Message}",
                            new AggregateException(error, rollbackError), error.CancellationToken);
                    }
                }
                throw;
            }
            catch (Exception error) when (retainBackup)
            {
                RecoverUnreturnedCandidate(error, request.Variant, backup, expectedManifest!,
                    replacementApplied, CleanupCandidate);
                throw;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                if (replacementApplied && !retainBackup)
                {
                    throw;
                }

                try
                {
                    RestoreBackupIfNeeded(request.Variant, backup);
                }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
                {
                    throw new IOException(
                        $"核心安装失败：{error.Message}；回滚也失败：{rollbackError.Message}",
                        new AggregateException(error, rollbackError));
                }

                throw new IOException($"核心安装失败，原核心已保留：{error.Message}", error);
            }
            finally
            {
                // finally 里抛异常会替换掉正在传播的那个异常：清理失败只留临时目录，
                // 绝不让它盖掉安装/回滚的真正原因。
                if (!retainBackup)
                    TryCleanup(staging, extractRoot, archive, backup, request.Variant, progress);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CorePreparedInstallation> InstallPreparedAsync(
        CorePreparedInstallRequest request,
        IProgress<CoreInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExpectedManifest.Variant != request.Variant)
        {
            throw new ArgumentException("待应用的核心变体与基线 manifest 不一致", nameof(request));
        }
        if (!Directory.Exists(request.StagingDirectory))
        {
            throw new DirectoryNotFoundException($"待应用的核心暂存目录不存在：{request.StagingDirectory}");
        }

        var staging = Path.GetFullPath(request.StagingDirectory);
        var target = CoreDirectory(request.Variant);
        var parent = Directory.GetParent(target)?.FullName
            ?? throw new IOException("核心目录路径没有父目录");
        EnsureContained(parent, staging, "待应用的核心暂存目录越界");
        EnsureSameVolume(parent, staging);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Report(progress, CoreInstallStage.Validating, "正在校验本地 PR 合并结果");
            EnsureCurrentManifest(request.Variant, request.ExpectedManifest);
            ValidatePreparedCore(staging);
            ValidateLocalMergeRequest(request);
            var manifest = request.ExpectedManifest with
            {
                SchemaVersion = CoreInstallationManifest.CurrentSchemaVersion,
                CommitSha = request.BaseCommitSha.ToLowerInvariant(),
                InstallKind = CoreInstallKind.LocalPullRequestStack,
                PullRequestNumber = null,
                BaseCommitSha = request.BaseCommitSha.ToLowerInvariant(),
                LocalMergeSha = request.LocalMergeSha.ToLowerInvariant(),
                PullRequests = request.PullRequests.ToArray(),
                Version = CoreVersionReader.ReadVersion(staging),
                InstalledAt = DateTimeOffset.UtcNow,
            };
            CoreManifestStore.Write(staging, manifest);

            var operationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            var backup = Path.Combine(parent, $".{request.Variant.ToDirectoryName()}.backup-{operationId}");
            var replacementApplied = false;
            var cleanupAttempted = false;
            void CleanupCandidate()
            {
                if (cleanupAttempted) return;
                cleanupAttempted = true;
                TryCleanup(staging, string.Empty, string.Empty, string.Empty, request.Variant, progress);
            }
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Report(progress, CoreInstallStage.Replacing, "正在原子替换目标核心");
                ReplaceWithRollback(request.Variant, staging, backup);
                replacementApplied = true;
                Report(progress, CoreInstallStage.Completed, "本地 PR 合并已应用，等待服务健康确认");
                CleanupCandidate();
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(backup)) throw new IOException("本地 PR 候选交接前恢复点已不存在");
                return new CorePreparedInstallation(InspectOrThrow(request.Variant), backup);
            }
            catch (Exception error)
            {
                // 直到成功返回句柄前，包含清理/诊断回调在内的所有代码都由安装器负责恢复。
                RecoverUnreturnedCandidate(error, request.Variant, backup, request.ExpectedManifest,
                    replacementApplied, CleanupCandidate);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CoreInstallationInfo> RestorePreparedBackupAsync(
        ManagedCoreVariant variant,
        string backupDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        var backup = ValidatePreparedBackupPath(variant, backupDirectory);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(backup))
            {
                throw new IOException($"本地 PR 恢复点不存在：{backup}");
            }
            var manifest = CoreManifestStore.Read(backup)
                ?? throw new IOException("本地 PR 恢复点缺少来源 manifest");
            if (manifest.Variant != variant)
            {
                throw new IOException("本地 PR 恢复点变体不匹配");
            }
            ValidatePreparedCore(backup);
            var target = CoreDirectory(variant);
            var failed = Path.Combine(_nodeProjectDirectory, $".{variant.ToDirectoryName()}.failed-{Guid.NewGuid():N}");
            if (Directory.Exists(target))
            {
                Directory.Move(target, failed);
            }
            try
            {
                Directory.Move(backup, target);
                var restored = InspectOrThrow(variant);
                try
                {
                    DeleteIfExists(failed);
                }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
                {
                    ReportMaintenance($"旧核心已恢复，但候选核心清理失败：{cleanupError.Message}", null);
                }
                return restored;
            }
            catch (Exception restoreError)
            {
                if (Directory.Exists(failed) && !Directory.Exists(target))
                {
                    try
                    {
                        Directory.Move(failed, target);
                    }
                    catch (Exception returnError) when (returnError is IOException or UnauthorizedAccessException)
                    {
                        throw new IOException($"恢复旧核心失败：{restoreError.Message}；重新放回候选核心也失败：{returnError.Message}",
                            new AggregateException(restoreError, returnError));
                    }
                }
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ConfirmPreparedBackupAsync(
        ManagedCoreVariant variant,
        string? backupDirectory,
        CancellationToken cancellationToken = default)
    {
        if (backupDirectory is null)
        {
            return;
        }
        var backup = ValidatePreparedBackupPath(variant, backupDirectory);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ArchivePreviousVersion(variant, backup);
            PruneHistory(variant);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string ValidatePreparedBackupPath(ManagedCoreVariant variant, string path)
    {
        var backup = Path.GetFullPath(path);
        EnsureContained(_nodeProjectDirectory, backup, "本地 PR 恢复点路径越界");
        if (!string.Equals(Path.GetDirectoryName(backup), _nodeProjectDirectory, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(backup).StartsWith($".{variant.ToDirectoryName()}.backup-", StringComparison.Ordinal))
        {
            throw new IOException("本地 PR 恢复点路径无效");
        }
        return backup;
    }

    public async Task<CoreInstallationInfo> RestoreHistoryAsync(
        ManagedCoreVariant variant,
        string historyId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(historyId);
        if (historyId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || historyId.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("核心历史 ID 无效", nameof(historyId));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var historyDirectory = Path.Combine(HistoryRoot(variant), historyId);
            EnsureContained(HistoryRoot(variant), historyDirectory, "核心历史路径越界");
            if (!Directory.Exists(historyDirectory))
            {
                throw new IOException($"核心历史不存在：{historyId}");
            }

            var historyManifest = CoreManifestStore.Read(historyDirectory)
                ?? throw new IOException("核心历史缺少来源 manifest");
            if (historyManifest.Variant != variant || !File.Exists(Path.Combine(historyDirectory, "worker.js")))
            {
                throw new IOException("核心历史内容无效");
            }

            var operationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            var staging = Path.Combine(_nodeProjectDirectory, $".{variant.ToDirectoryName()}.restore-{operationId}");
            var backup = Path.Combine(_nodeProjectDirectory, $".{variant.ToDirectoryName()}.backup-{operationId}");
            var rollbackApplied = false;
            try
            {
                CopyDirectoryStrict(historyDirectory, staging);
                var restoredManifest = historyManifest with
                {
                    InstallKind = CoreInstallKind.Rollback,
                    InstalledAt = DateTimeOffset.UtcNow,
                };
                CoreManifestStore.Write(staging, restoredManifest);
                cancellationToken.ThrowIfCancellationRequested();
                ReplaceWithRollback(variant, staging, backup);
                rollbackApplied = true;
                try
                {
                    ArchivePreviousVersion(variant, backup);
                    PruneHistory(variant);
                }
                catch (Exception historyError) when (historyError is IOException or UnauthorizedAccessException)
                {
                    ReportMaintenance($"已回滚到历史版本，但旧版本历史维护失败：{historyError.Message}", null);
                }
                return InspectOrThrow(variant);
            }
            catch (Exception error)
            {
                if (rollbackApplied)
                {
                    // 回滚已经生效：这时候再去动备份会把刚恢复好的版本又换掉，
                    // 而且它的异常会盖掉真正的根因。
                    throw;
                }

                try
                {
                    RestoreBackupIfNeeded(variant, backup);
                }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
                {
                    throw new IOException(
                        $"回滚历史版本失败：{error.Message}；恢复原核心也失败：{rollbackError.Message}",
                        new AggregateException(error, rollbackError));
                }

                throw;
            }
            finally
            {
                // finally 里抛异常会替换正在传播的那个异常：清理失败只记诊断。
                try
                {
                    DeleteIfExists(staging);
                    if (Directory.Exists(backup) && Directory.Exists(CoreDirectory(variant)))
                    {
                        DeleteIfExists(backup);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    ReportMaintenance($"回滚的临时文件清理失败（不影响已恢复的核心）：{error.Message}", null);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Delete(ManagedCoreVariant variant)
    {
        _gate.Wait();
        try
        {
            DeleteIfExists(CoreDirectory(variant));
        }
        finally
        {
            _gate.Release();
        }
    }

    public void UpdateDisplayName(ManagedCoreVariant variant, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        _gate.Wait();
        try
        {
            var directory = CoreDirectory(variant);
            if (!Directory.Exists(directory))
            {
                throw new IOException($"核心尚未安装：{directory}");
            }

            var manifest = CoreManifestStore.Read(directory)
                ?? throw new IOException("当前核心缺少来源 manifest，无法修改显示名称");
            CoreManifestStore.Write(directory, manifest with { DisplayName = displayName.Trim() });
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ReplaceWithRollback(ManagedCoreVariant variant, string staging, string backup)
    {
        var target = CoreDirectory(variant);
        if (Directory.Exists(backup))
        {
            throw new IOException($"核心备份路径已存在：{backup}");
        }

        if (Directory.Exists(target))
        {
            Directory.Move(target, backup);
        }

        try
        {
            Directory.Move(staging, target);
            ValidatePreparedCore(target);
            _ = CoreManifestStore.Read(target) ?? throw new IOException("替换后的核心缺少来源 manifest");
        }
        catch
        {
            DeleteIfExists(target);
            if (Directory.Exists(backup))
            {
                Directory.Move(backup, target);
            }
            throw;
        }
    }

    private void RecoverUnreturnedCandidate(
        Exception original,
        ManagedCoreVariant variant,
        string backup,
        CoreInstallationManifest expectedManifest,
        bool replacementApplied,
        Action cleanup)
    {
        var failures = new List<Exception> { original };
        var diagnostics = new List<string> { $"候选核心应用失败：{original.Message}" };
        try
        {
            RestoreUnreturnedCandidate(variant, backup, expectedManifest, replacementApplied);
        }
        catch (Exception rollbackError)
        {
            failures.Add(rollbackError);
            diagnostics.Add($"恢复原核心也失败：{rollbackError.Message}");
        }
        try
        {
            cleanup();
        }
        catch (Exception cleanupError)
        {
            failures.Add(cleanupError);
            diagnostics.Add($"候选清理或诊断也失败：{cleanupError.Message}");
        }
        if (failures.Count == 1) return; // 调用方 throw; 保留原异常对象和堆栈。
        var combined = new AggregateException(failures);
        var diagnostic = string.Join("；", diagnostics);
        if (original is OperationCanceledException canceled)
            throw new OperationCanceledException(diagnostic, combined, canceled.CancellationToken);
        throw new IOException(diagnostic, combined);
    }

    private void RestoreUnreturnedCandidate(
        ManagedCoreVariant variant,
        string backup,
        CoreInstallationManifest expectedManifest,
        bool replacementApplied)
    {
        // 替换前被来源预检拒绝时没有归属本事务的磁盘改动；不得把外部新来源当作恢复失败。
        if (!replacementApplied && !Directory.Exists(backup))
        {
            return;
        }
        if (replacementApplied && !Directory.Exists(backup))
        {
            throw new IOException($"候选核心交接失败，但原核心恢复点已不存在：{backup}");
        }
        RestoreBackupIfNeeded(variant, backup);
        EnsureCurrentManifest(variant, expectedManifest);
    }

    private void RestoreBackupIfNeeded(ManagedCoreVariant variant, string backup)
    {
        if (!Directory.Exists(backup))
        {
            return;
        }

        var target = CoreDirectory(variant);
        DeleteIfExists(target);
        Directory.Move(backup, target);
    }

    private void ArchivePreviousVersion(ManagedCoreVariant variant, string backup)
    {
        if (!Directory.Exists(backup))
        {
            return;
        }

        var manifest = CoreManifestStore.Read(backup);
        if (manifest is null)
        {
            DeleteIfExists(backup);
            return;
        }

        var historyRoot = HistoryRoot(variant);
        Directory.CreateDirectory(historyRoot);
        var id = $"{manifest.InstalledAt:yyyyMMddHHmmssfff}-{manifest.ShortSha}-{Guid.NewGuid():N}";
        var target = Path.Combine(historyRoot, id);
        Directory.Move(backup, target);
    }

    private void PruneHistory(ManagedCoreVariant variant)
    {
        var records = GetHistory(variant);
        foreach (var record in records.Skip(MaxHistoryEntries))
        {
            DeleteIfExists(record.Directory);
        }
    }

    private static void ExtractArchive(string archivePath, string destination, CancellationToken cancellationToken)
    {
        DeleteIfExists(destination);
        Directory.CreateDirectory(destination);
        var destinationPrefix = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is 0 or > MaxArchiveEntries)
        {
            throw new InvalidDataException("核心压缩包文件数量无效");
        }

        long totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinkEntry(entry);
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal) || normalized.Contains('\0'))
            {
                throw new InvalidDataException($"核心压缩包含非法路径：{entry.FullName}");
            }

            var separator = normalized.IndexOf('/');
            if (separator < 0)
            {
                continue;
            }

            var stripped = normalized[(separator + 1)..];
            if (stripped.Length == 0)
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(destination, stripped.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"核心压缩包含非法路径：{entry.FullName}");
            }

            totalBytes = checked(totalBytes + entry.Length);
            if (totalBytes > MaxExtractedBytes)
            {
                throw new InvalidDataException("核心压缩解压后超过 512MB 上限");
            }

            if (stripped.EndsWith("/", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? destination);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }

    private static void RejectLinkEntry(ZipArchiveEntry entry)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixMode == 0xA000 || (entry.ExternalAttributes & 0x400) != 0)
        {
            throw new InvalidDataException($"核心压缩包含链接或重解析点：{entry.FullName}");
        }
    }

    private static void PrepareCoreDirectory(string extractRoot, string staging)
    {
        DeleteIfExists(staging);
        var source = LocateCoreSource(extractRoot);
        CopyDirectoryStrict(source, staging);
        EnsureEsmPackageJson(staging, extractRoot);
    }

    private static string LocateCoreSource(string extractRoot)
    {
        if (File.Exists(Path.Combine(extractRoot, "worker.js")))
        {
            return extractRoot;
        }

        var candidates = Directory.EnumerateDirectories(extractRoot, "*", SearchOption.AllDirectories)
            .Where(directory =>
                RelativeDepth(extractRoot, directory) <= 3 &&
                (string.Equals(Path.GetFileName(directory), "danmu_api", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Path.GetFileName(directory), "danmu-api", StringComparison.OrdinalIgnoreCase)) &&
                File.Exists(Path.Combine(directory, "worker.js")))
            .Take(2)
            .ToArray();
        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new InvalidDataException("核心压缩包中找不到 danmu_api/worker.js"),
            _ => throw new InvalidDataException("核心压缩包包含多个候选核心目录"),
        };
    }

    public static void PrepareGitCoreStaging(string coreDirectory, string repositoryRoot)
    {
        EnsureEsmPackageJson(coreDirectory, repositoryRoot);
        ValidatePreparedCore(coreDirectory);
        _ = CoreVersionReader.ReadVersion(coreDirectory);
    }

    private static void EnsureEsmPackageJson(string coreDirectory, string repositoryRoot)
    {
        var packagePath = Path.Combine(coreDirectory, "package.json");
        if (!File.Exists(packagePath))
        {
            var repositoryPackage = Path.Combine(repositoryRoot, "package.json");
            if (File.Exists(repositoryPackage))
            {
                File.Copy(repositoryPackage, packagePath);
            }
            else
            {
                File.WriteAllText(packagePath, "{\n  \"type\": \"module\",\n  \"version\": \"0.0.0\"\n}\n", new UTF8Encoding(false));
            }
        }

        var text = File.ReadAllText(packagePath, new UTF8Encoding(false, true));
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("核心 package.json 根节点必须是对象");
        }

        if (document.RootElement.TryGetProperty("type", out var type))
        {
            if (type.ValueKind != JsonValueKind.String || !string.Equals(type.GetString(), "module", StringComparison.Ordinal))
            {
                throw new InvalidDataException("核心 package.json 的 type 必须是 module");
            }
            return;
        }

        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "module");
            foreach (var property in document.RootElement.EnumerateObject())
            {
                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        File.WriteAllBytes(packagePath, output.ToArray());
    }

    private static void ValidatePreparedCore(string directory)
    {
        foreach (var relative in new[]
        {
            "worker.js",
            Path.Combine("configs", "envs.js"),
            Path.Combine("configs", "globals.js"),
            "package.json",
        })
        {
            if (!File.Exists(Path.Combine(directory, relative)))
            {
                throw new InvalidDataException($"核心缺少必要文件：{relative}");
            }
        }
    }

    private static void CopyDirectoryStrict(string source, string target)
    {
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(source);
        }
        if (Directory.Exists(target))
        {
            throw new IOException($"复制目标已存在：{target}");
        }

        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var info = new DirectoryInfo(directory);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"核心目录包含重解析点：{directory}");
            }
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"核心目录包含重解析点：{file}");
            }
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    private void EnsureCurrentManifest(ManagedCoreVariant variant, CoreInstallationManifest expected)
    {
        var current = InspectOrThrow(variant).Manifest;
        if (expected.Variant != variant || !CoreInstallationManifest.SourcesEqual(current, expected))
        {
            throw new InvalidOperationException("当前核心完整来源在准备后已变化，拒绝替换；请重新检查或准备");
        }
    }

    private CoreInstallationInfo InspectOrThrow(ManagedCoreVariant variant)
    {
        var info = Inspect(variant);
        if (!info.IsValid || info.Manifest is null)
        {
            throw new IOException(info.Diagnostic ?? "安装后的核心校验失败");
        }
        return info;
    }

    private static void ValidateLocalMergeRequest(CorePreparedInstallRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ExpectedManifest.Repository) ||
            string.IsNullOrWhiteSpace(request.ExpectedManifest.Branch) ||
            !ValidFullSha(request.BaseCommitSha) ||
            !ValidFullSha(request.LocalMergeSha) ||
            request.PullRequests.Count is 0 or > 100)
        {
            throw new ArgumentException("本地 PR 合并来源信息无效", nameof(request));
        }

        _ = GithubRepositoryReference.Parse(request.ExpectedManifest.Repository);
        GithubRepositoryReference.ValidateBranch(request.ExpectedManifest.Branch);
        var numbers = new HashSet<int>();
        foreach (var pullRequest in request.PullRequests)
        {
            var headRepository = GithubRepositoryReference.Parse(pullRequest.HeadRepository);
            if (pullRequest.Number <= 0 || !numbers.Add(pullRequest.Number) ||
                headRepository.Branch is not null ||
                !string.Equals(headRepository.FullName, pullRequest.HeadRepository, StringComparison.Ordinal) ||
                !ValidFullSha(pullRequest.HeadSha) ||
                (pullRequest.BaseSha is not null && !ValidFullSha(pullRequest.BaseSha)))
            {
                throw new ArgumentException("本地 PR 合并队列包含无效来源", nameof(request));
            }
            GithubRepositoryReference.ValidateBranch(pullRequest.HeadBranch);
        }
    }

    private static bool ValidFullSha(string value) =>
        value.Length == 40 && value.All(Uri.IsHexDigit);

    private static void ValidateRequest(CoreInstallRequest request)
    {
        if (!request.Repository.Source.Matches(request.Variant))
        {
            throw new ArgumentException(
                request.Repository.Source switch
                {
                    CoreRepositorySource.Official => "官方上游只能安装为稳定核心",
                    CoreRepositorySource.Dev => "开发上游（lilixu3/danmu_api）只能安装为开发核心",
                    _ => "自定义仓库只能安装为自定义核心；要装官方或开发上游，请在对应变体页用它们的安装入口",
                },
                nameof(request));
        }
        GithubRepositoryReference.ValidateBranch(request.Branch);
        if (request.CommitSha.Length is < 7 or > 64 || !request.CommitSha.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("安装提交 SHA 无效", nameof(request));
        }
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 80)
        {
            throw new ArgumentException("核心显示名称不能为空且不能超过 80 个字符", nameof(request));
        }
        if (request.Kind == CoreInstallKind.PullRequest && request.PullRequestNumber is null or <= 0)
        {
            throw new ArgumentException("安装 PR 核心必须提供有效 PR 编号", nameof(request));
        }
        if (request.Kind != CoreInstallKind.PullRequest && request.PullRequestNumber is not null)
        {
            throw new ArgumentException("非 PR 安装不能包含 PR 编号", nameof(request));
        }
        _ = GithubProxyCatalog.GetById(request.ProxyId);
    }

    private string CoreDirectory(ManagedCoreVariant variant) =>
        Path.Combine(_nodeProjectDirectory, variant.ToDirectoryName());

    private string HistoryRoot(ManagedCoreVariant variant) =>
        Path.Combine(_cacheDirectory, "versions", variant.ToStorageKey());

    private static int RelativeDepth(string root, string path) =>
        Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length;

    private static void EnsureSameVolume(string first, string second)
    {
        if (!string.Equals(Path.GetPathRoot(first), Path.GetPathRoot(second), StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("核心运行目录与版本缓存不在同一卷，无法保证事务替换");
        }
    }

    private static void EnsureContained(string root, string path, string message)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(path);
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(message);
        }
    }

    private static void Report(IProgress<CoreInstallProgress>? progress, CoreInstallStage stage, string detail) =>
        progress?.Report(new CoreInstallProgress(stage, detail));

    private static void DeleteIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <summary>
    /// 安装/回滚收尾的清理：失败只留下临时目录，绝不在 finally 里抛——那会替换掉正在传播的
    /// 真正的安装或回滚故障，用户就只能看到一句与根因无关的清理错误。
    /// </summary>
    private void TryCleanup(
        string staging,
        string extractRoot,
        string archive,
        string backup,
        ManagedCoreVariant variant,
        IProgress<CoreInstallProgress>? progress)
    {
        try
        {
            DeleteIfExists(staging);
            DeleteIfExists(extractRoot);
            DeleteFileIfExists(archive);
            if (Directory.Exists(backup) && Directory.Exists(CoreDirectory(variant)))
            {
                DeleteIfExists(backup);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try
            {
                ReportMaintenance($"安装临时文件清理失败（不影响已安装的核心）：{error.Message}", progress);
            }
            catch (Exception reportError)
            {
                throw new AggregateException("安装临时文件清理失败，且报告清理诊断也失败", error, reportError);
            }
        }
    }

    /// <summary>善后类故障的统一的出口：既进 UI 的进度文案，也进宿主诊断汇。</summary>
    private void ReportMaintenance(string message, IProgress<CoreInstallProgress>? progress)
    {
        _maintenanceDiagnostic?.Invoke(message);
        Report(progress, CoreInstallStage.Completed, message);
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
