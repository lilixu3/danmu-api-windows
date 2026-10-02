using System.IO.Compression;
using System.Text;
using DanmuApi.Core;

namespace DanmuApi.Tests;

public sealed class CoreInstallerTests
{
    [Fact]
    public async Task InstallsNestedCoreAndPreservesExternalUserData()
    {
        using var directory = new TemporaryDirectory();
        var project = Path.Combine(directory.Path, "runtime", "nodejs-project");
        var cache = Path.Combine(directory.Path, "core-cache");
        Directory.CreateDirectory(Path.Combine(project, "config"));
        Directory.CreateDirectory(Path.Combine(project, "logs"));
        var env = Path.Combine(project, "config", ".env");
        var log = Path.Combine(project, "logs", "sentinel.log");
        File.WriteAllText(env, "TOKEN=keep-me");
        File.WriteAllText(log, "keep-log");
        var archive = CreateCoreArchive(directory.Path, "1.2.3", "new-core");
        var installer = new CoreInstaller(project, cache, new CopyingDownloader(archive));

        var result = await installer.InstallAsync(Request("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));

        Assert.True(result.IsValid);
        Assert.Equal("1.2.3", result.Version);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", result.Manifest!.CommitSha);
        Assert.Equal("TOKEN=keep-me", File.ReadAllText(env));
        Assert.Equal("keep-log", File.ReadAllText(log));
        Assert.Equal("new-core", File.ReadAllText(Path.Combine(result.Directory, "payload.txt")));
        Assert.Contains("\"type\": \"module\"", File.ReadAllText(Path.Combine(result.Directory, "package.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateArchivesPreviousCoreAndHistoryCanBeRestored()
    {
        using var directory = new TemporaryDirectory();
        var project = Path.Combine(directory.Path, "project");
        var cache = Path.Combine(directory.Path, "cache");
        var firstArchive = CreateCoreArchive(directory.Path, "1.0.0", "first");
        var downloader = new SwitchingDownloader(firstArchive);
        var installer = new CoreInstaller(project, cache, downloader);
        await installer.InstallAsync(Request("1111111111111111111111111111111111111111"));
        downloader.ArchivePath = CreateCoreArchive(directory.Path, "2.0.0", "second");

        var updated = await installer.InstallAsync(Request("2222222222222222222222222222222222222222"));
        var history = installer.GetHistory(ManagedCoreVariant.Stable);

        Assert.Equal("second", File.ReadAllText(Path.Combine(updated.Directory, "payload.txt")));
        var previous = Assert.Single(history);
        Assert.Equal("1111111111111111111111111111111111111111", previous.Manifest.CommitSha);

        var restored = await installer.RestoreHistoryAsync(ManagedCoreVariant.Stable, previous.Id);

        Assert.Equal(CoreInstallKind.Rollback, restored.Manifest!.InstallKind);
        Assert.Equal("1111111111111111111111111111111111111111", restored.Manifest.CommitSha);
        Assert.Equal("first", File.ReadAllText(Path.Combine(restored.Directory, "payload.txt")));
    }

    [Fact]
    public async Task ZipSlipIsRejectedWithoutCreatingEscapedFile()
    {
        using var directory = new TemporaryDirectory();
        var archive = Path.Combine(directory.Path, "malicious.zip");
        using (var file = File.Create(archive))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "owner-repo/root/../../escaped.txt", new string('x', 2048));
        }
        var project = Path.Combine(directory.Path, "project");
        var installer = new CoreInstaller(project, Path.Combine(directory.Path, "cache"), new CopyingDownloader(archive));

        var error = await Assert.ThrowsAsync<IOException>(() =>
            installer.InstallAsync(Request("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")));

        Assert.Contains("非法路径", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(project, "escaped.txt")));
        Assert.False(installer.Inspect(ManagedCoreVariant.Stable).IsInstalled);
    }

    [Fact]
    public async Task MissingRequiredFileIsRejectedBeforeReplacingOldCore()
    {
        using var directory = new TemporaryDirectory();
        var project = Path.Combine(directory.Path, "project");
        var cache = Path.Combine(directory.Path, "cache");
        var first = CreateCoreArchive(directory.Path, "1.0.0", "old");
        var downloader = new SwitchingDownloader(first);
        var installer = new CoreInstaller(project, cache, downloader);
        await installer.InstallAsync(Request("1111111111111111111111111111111111111111"));
        downloader.ArchivePath = CreateCoreArchive(directory.Path, "2.0.0", "bad", includeEnvs: false);

        var error = await Assert.ThrowsAsync<IOException>(() =>
            installer.InstallAsync(Request("2222222222222222222222222222222222222222")));

        Assert.Contains("envs.js", error.Message, StringComparison.Ordinal);
        Assert.Equal("old", File.ReadAllText(Path.Combine(project, "danmu_api_stable", "payload.txt")));
        Assert.Equal("1111111111111111111111111111111111111111", installer.Inspect(ManagedCoreVariant.Stable).Manifest!.CommitSha);
    }

    [Fact]
    public async Task FailureAtReplacingStageLeavesOldCoreUntouched()
    {
        using var directory = new TemporaryDirectory();
        var project = Path.Combine(directory.Path, "project");
        var cache = Path.Combine(directory.Path, "cache");
        var first = CreateCoreArchive(directory.Path, "1.0.0", "old");
        var downloader = new SwitchingDownloader(first);
        var installer = new CoreInstaller(project, cache, downloader);
        await installer.InstallAsync(Request("1111111111111111111111111111111111111111"));
        downloader.ArchivePath = CreateCoreArchive(directory.Path, "2.0.0", "new");
        var failing = new CoreInstaller(project, cache, downloader, stage =>
        {
            if (stage == CoreInstallStage.Replacing)
            {
                throw new IOException("injected replacement failure");
            }
        });

        var error = await Assert.ThrowsAsync<IOException>(() =>
            failing.InstallAsync(Request("2222222222222222222222222222222222222222")));

        Assert.Contains("injected replacement failure", error.Message, StringComparison.Ordinal);
        Assert.Equal("old", File.ReadAllText(Path.Combine(project, "danmu_api_stable", "payload.txt")));
    }

    [Fact]
    public async Task DeleteRemovesOnlyManagedCoreDirectory()
    {
        using var directory = new TemporaryDirectory();
        var project = Path.Combine(directory.Path, "project");
        var config = Path.Combine(project, "config");
        Directory.CreateDirectory(config);
        var env = Path.Combine(config, ".env");
        File.WriteAllText(env, "TOKEN=preserved");
        var firstArchive = CreateCoreArchive(directory.Path, "1.0.0", "core-one");
        var downloader = new SwitchingDownloader(firstArchive);
        var installer = new CoreInstaller(
            project,
            Path.Combine(directory.Path, "cache"),
            downloader);
        await installer.InstallAsync(Request("1111111111111111111111111111111111111111"));
        downloader.ArchivePath = CreateCoreArchive(directory.Path, "2.0.0", "core-two");
        await installer.InstallAsync(Request("2222222222222222222222222222222222222222"));
        Assert.NotEmpty(installer.GetHistory(ManagedCoreVariant.Stable));

        installer.Delete(ManagedCoreVariant.Stable);

        Assert.False(Directory.Exists(Path.Combine(project, "danmu_api_stable")));
        Assert.Equal("TOKEN=preserved", File.ReadAllText(env));
        Assert.NotEmpty(installer.GetHistory(ManagedCoreVariant.Stable));
    }

    [Fact]
    public async Task PreparedMergeKeepsPreviousCoreUntilConfirmedAndRecordsOrderedStack()
    {
        using var directory = new TemporaryDirectory();
        var project = Path.Combine(directory.Path, "project");
        var cache = Path.Combine(directory.Path, "cache");
        var archive = CreateCoreArchive(directory.Path, "1.0.0", "old");
        var installer = new CoreInstaller(project, cache, new CopyingDownloader(archive));
        var previous = await installer.InstallAsync(Request(new string('a', 40)));
        var staging = CreateMergeStaging(project, "new");
        var sources = new[]
        {
            new CorePullRequestSource(12, "fork/core", "feature-1", new string('c', 40), null),
            new CorePullRequestSource(13, "fork/core", "feature-2", new string('d', 40), null),
        };
        var prepared = new CorePreparedInstallRequest(ManagedCoreVariant.Stable, staging, previous.Manifest!,
            previous.Manifest!.CommitSha, new string('b', 40), sources);

        var applied = await installer.InstallPreparedAsync(prepared);
        Assert.NotNull(applied.BackupDirectory);
        Assert.True(Directory.Exists(applied.BackupDirectory));
        Assert.Equal("old", File.ReadAllText(Path.Combine(applied.BackupDirectory!, "payload.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(applied.Installation.Directory, "payload.txt")));
        Assert.Equal(CoreInstallKind.LocalPullRequestStack, applied.Installation.Manifest!.InstallKind);
        Assert.Equal(new string('a', 40), applied.Installation.Manifest.BaseCommitSha);
        Assert.Equal(new string('b', 40), applied.Installation.Manifest.LocalMergeSha);
        Assert.Equal([12, 13], applied.Installation.Manifest.PullRequests.Select(source => source.Number));
        Assert.Equal([12, 13], CoreManifestStore.Read(applied.Installation.Directory)!.PullRequests.Select(source => source.Number));

        await installer.ConfirmPreparedBackupAsync(ManagedCoreVariant.Stable, applied.BackupDirectory);
        Assert.False(Directory.Exists(applied.BackupDirectory));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Assert.Single(installer.GetHistory(ManagedCoreVariant.Stable)).Directory, "payload.txt")));
    }

    [Fact]
    public async Task PreparedMergeRestoresPreviousCoreAfterCandidateFailsHealth()
    {
        using var directory = new TemporaryDirectory();
        var project = Path.Combine(directory.Path, "project");
        var archive = CreateCoreArchive(directory.Path, "1.0.0", "old");
        var installer = new CoreInstaller(project, Path.Combine(directory.Path, "cache"), new CopyingDownloader(archive));
        var previous = await installer.InstallAsync(Request(new string('a', 40)));
        var staging = CreateMergeStaging(project, "candidate");
        var request = new CorePreparedInstallRequest(ManagedCoreVariant.Stable, staging, previous.Manifest!,
            previous.Manifest!.CommitSha, new string('b', 40),
            [new CorePullRequestSource(12, "fork/core", "feature", new string('c', 40), null)]);

        var applied = await installer.InstallPreparedAsync(request);
        var restored = await installer.RestorePreparedBackupAsync(ManagedCoreVariant.Stable, applied.BackupDirectory!);

        Assert.Equal("old", File.ReadAllText(Path.Combine(restored.Directory, "payload.txt")));
        Assert.Equal(previous.Manifest, restored.Manifest);
        Assert.False(Directory.Exists(applied.BackupDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuditUpdateOnlyRetainsStackBackupUntilExplicitConfirmationOrRollback(bool confirm)
    {
        using var directory = new TemporaryDirectory();
        var project = Path.Combine(directory.Path, "project");
        var downloader = new SwitchingDownloader(CreateCoreArchive(directory.Path, "1.0.0", "old-stack"));
        var installer = new CoreInstaller(project, Path.Combine(directory.Path, "cache"), downloader);
        var previous = await installer.InstallAsync(Request(new string('a', 40)));
        var stack = previous.Manifest! with
        {
            InstallKind = CoreInstallKind.LocalPullRequestStack,
            BaseCommitSha = previous.Manifest.CommitSha,
            LocalMergeSha = new string('c', 40),
            PullRequests = [new CorePullRequestSource(12, "fork/core", "feature", new string('d', 40), null)],
        };
        CoreManifestStore.Write(previous.Directory, stack);
        var originalManifestBytes = File.ReadAllBytes(Path.Combine(previous.Directory, ".danmuapi-core-source.json"));
        downloader.ArchivePath = CreateCoreArchive(directory.Path, "2.0.0", "branch-candidate");

        var applied = await installer.InstallUpdateCandidateAsync(Request(new string('b', 40)), stack);

        Assert.NotNull(applied.BackupDirectory);
        Assert.True(Directory.Exists(applied.BackupDirectory));
        Assert.Empty(installer.GetHistory(ManagedCoreVariant.Stable));
        Assert.Equal(CoreInstallKind.Branch, applied.Installation.Manifest!.InstallKind);
        Assert.False(applied.Installation.Manifest.IsLocalPullRequestStack);
        Assert.Null(applied.Installation.Manifest.BaseCommitSha);
        Assert.Null(applied.Installation.Manifest.LocalMergeSha);
        Assert.Empty(applied.Installation.Manifest.PullRequests);
        Assert.True(CoreInstallationManifest.SourcesEqual(stack, CoreManifestStore.Read(applied.BackupDirectory!)));
        if (confirm)
        {
            await installer.ConfirmPreparedBackupAsync(ManagedCoreVariant.Stable, applied.BackupDirectory);
            Assert.True(CoreInstallationManifest.SourcesEqual(stack,
                Assert.Single(installer.GetHistory(ManagedCoreVariant.Stable)).Manifest));
        }
        else
        {
            var restored = await installer.RestorePreparedBackupAsync(ManagedCoreVariant.Stable, applied.BackupDirectory!);
            Assert.True(CoreInstallationManifest.SourcesEqual(stack, restored.Manifest));
            Assert.Equal("old-stack", File.ReadAllText(Path.Combine(restored.Directory, "payload.txt")));
            Assert.Equal(originalManifestBytes, File.ReadAllBytes(Path.Combine(restored.Directory, ".danmuapi-core-source.json")));
            Assert.Empty(installer.GetHistory(ManagedCoreVariant.Stable));
        }
        Assert.False(Directory.Exists(applied.BackupDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuditArchiveUpdateRechecksCompleteSourceAtReplacementAfterDownload(bool retainedCandidate)
    {
        using var directory = new TemporaryDirectory();
        var project = Path.Combine(directory.Path, "project");
        var cache = Path.Combine(directory.Path, "cache");
        var downloader = new SwitchingDownloader(CreateCoreArchive(directory.Path, "1.0.0", "old"));
        var initialInstaller = new CoreInstaller(project, cache, downloader);
        var previous = await initialInstaller.InstallAsync(Request(new string('a', 40)));
        downloader.ArchivePath = CreateCoreArchive(directory.Path, "2.0.0", "candidate");
        var changed = previous.Manifest! with { DisplayName = "changed while downloading" };
        var installer = new CoreInstaller(project, cache, downloader, stage =>
        {
            if (stage == CoreInstallStage.Replacing) CoreManifestStore.Write(previous.Directory, changed);
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (retainedCandidate)
                await installer.InstallUpdateCandidateAsync(Request(new string('b', 40)), previous.Manifest!);
            else
                await installer.InstallAsync(Request(new string('b', 40)) with { ExpectedManifest = previous.Manifest });
        });

        Assert.Contains("完整来源", error.Message);
        Assert.True(CoreInstallationManifest.SourcesEqual(changed, installer.Inspect(ManagedCoreVariant.Stable).Manifest));
        Assert.Equal("old", File.ReadAllText(Path.Combine(previous.Directory, "payload.txt")));
        Assert.Empty(Directory.GetDirectories(project, ".danmu_api_stable.backup-*"));
    }

    [Theory]
    [InlineData(false, "throw")]
    [InlineData(true, "throw")]
    [InlineData(false, "throwCanceled")]
    [InlineData(true, "throwCanceled")]
    [InlineData(false, "cancelToken")]
    [InlineData(true, "cancelToken")]
    [InlineData(false, "inspectFailure")]
    [InlineData(true, "inspectFailure")]
    public async Task AuditPostReplacementFailureBeforeBackupHandoffRestoresOldDisk(
        bool archiveCandidate, string failureMode)
    {
        using var directory = new TemporaryDirectory();
        var project = Path.Combine(directory.Path, "project");
        var downloader = new SwitchingDownloader(CreateCoreArchive(directory.Path, "1.0.0", "old"));
        var installer = new CoreInstaller(project, Path.Combine(directory.Path, "cache"), downloader);
        var previous = await installer.InstallAsync(Request(new string('a', 40)));
        var oldManifestBytes = File.ReadAllBytes(Path.Combine(previous.Directory, CoreManifestStore.ManifestFileName));
        downloader.ArchivePath = CreateCoreArchive(directory.Path, "2.0.0", "candidate");
        var staging = CreateMergeStaging(project, "candidate");
        using var cancellation = new CancellationTokenSource();
        Exception original = failureMode == "throwCanceled"
            ? new OperationCanceledException("Completed callback canceled", cancellation.Token)
            : new IOException("Completed callback rejected");
        var completedCalls = 0;
        var progress = new SynchronousProgress(value =>
        {
            if (value.Stage != CoreInstallStage.Completed) return;
            completedCalls++;
            Assert.Equal("candidate", File.ReadAllText(Path.Combine(previous.Directory, "payload.txt")));
            if (failureMode == "cancelToken") cancellation.Cancel();
            else if (failureMode == "inspectFailure") File.Delete(Path.Combine(previous.Directory, CoreManifestStore.ManifestFileName));
            else throw original;
        });
        var prepared = new CorePreparedInstallRequest(ManagedCoreVariant.Stable, staging, previous.Manifest!,
            previous.Manifest!.CommitSha, new string('b', 40),
            [new CorePullRequestSource(12, "fork/core", "feature", new string('c', 40), null)]);
        Func<Task> apply = async () =>
        {
            if (archiveCandidate)
                await installer.InstallUpdateCandidateAsync(Request(new string('b', 40)), previous.Manifest!, progress, cancellation.Token);
            else
                await installer.InstallPreparedAsync(prepared, progress, cancellation.Token);
        };

        Exception error;
        if (failureMode is "throwCanceled" or "cancelToken")
        {
            var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(apply);
            Assert.Equal(cancellation.Token, canceled.CancellationToken);
            error = canceled;
        }
        else error = await Assert.ThrowsAsync<IOException>(apply);
        if (failureMode is "throw" or "throwCanceled") Assert.Same(original, error);
        Assert.Equal(1, completedCalls);
        var restored = installer.Inspect(ManagedCoreVariant.Stable);
        Assert.True(CoreInstallationManifest.SourcesEqual(previous.Manifest, restored.Manifest));
        Assert.Equal(oldManifestBytes, File.ReadAllBytes(Path.Combine(previous.Directory, CoreManifestStore.ManifestFileName)));
        Assert.Equal("old", File.ReadAllText(Path.Combine(previous.Directory, "payload.txt")));
        Assert.Empty(Directory.GetDirectories(project, ".danmu_api_stable.backup-*"));
        Assert.Empty(installer.GetHistory(ManagedCoreVariant.Stable));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AuditPostReplacementRollbackFailurePreservesOriginalAndRecoveryErrors(
        bool archiveCandidate, bool canceled)
    {
        using var directory = new TemporaryDirectory();
        var project = Path.Combine(directory.Path, "project");
        var downloader = new SwitchingDownloader(CreateCoreArchive(directory.Path, "1.0.0", "old"));
        var installer = new CoreInstaller(project, Path.Combine(directory.Path, "cache"), downloader);
        var previous = await installer.InstallAsync(Request(new string('a', 40)));
        downloader.ArchivePath = CreateCoreArchive(directory.Path, "2.0.0", "candidate");
        var staging = CreateMergeStaging(project, "candidate");
        using var cancellation = new CancellationTokenSource();
        Exception original = canceled ? new OperationCanceledException("original handoff canceled", cancellation.Token)
            : new IOException("original handoff failed");
        var progress = new SynchronousProgress(value =>
        {
            if (value.Stage != CoreInstallStage.Completed) return;
            // Deterministic synthetic loss of the recovery point at the exact ownership handoff.
            Directory.Delete(Assert.Single(Directory.GetDirectories(project, ".danmu_api_stable.backup-*")), recursive: true);
            throw original;
        });
        var prepared = new CorePreparedInstallRequest(ManagedCoreVariant.Stable, staging, previous.Manifest!,
            previous.Manifest!.CommitSha, new string('b', 40),
            [new CorePullRequestSource(12, "fork/core", "feature", new string('c', 40), null)]);
        Func<Task> apply = async () =>
        {
            if (archiveCandidate)
                await installer.InstallUpdateCandidateAsync(Request(new string('b', 40)), previous.Manifest!, progress, cancellation.Token);
            else
                await installer.InstallPreparedAsync(prepared, progress, cancellation.Token);
        };

        var error = canceled ? await Assert.ThrowsAnyAsync<OperationCanceledException>(apply)
            : (Exception)await Assert.ThrowsAsync<IOException>(apply);

        var aggregate = Assert.IsType<AggregateException>(error.InnerException);
        Assert.Same(original, aggregate.InnerExceptions[0]);
        Assert.Contains("恢复点已不存在", aggregate.InnerExceptions[1].Message);
        Assert.Contains(original.Message, error.Message);
        Assert.Contains("恢复原核心也失败", error.Message);
        if (canceled) Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(error).CancellationToken);
        Assert.Equal("candidate", File.ReadAllText(Path.Combine(previous.Directory, "payload.txt")));
        Assert.Empty(installer.GetHistory(ManagedCoreVariant.Stable));
    }

    [Theory]
    [InlineData(false, "sink", "none")]
    [InlineData(false, "progress", "none")]
    [InlineData(true, "sink", "none")]
    [InlineData(true, "progress", "none")]
    [InlineData(false, "sink", "throw")]
    [InlineData(false, "progress", "throw")]
    [InlineData(true, "sink", "throw")]
    [InlineData(true, "progress", "throw")]
    [InlineData(false, "sink", "cancel")]
    [InlineData(false, "progress", "cancel")]
    [InlineData(true, "sink", "cancel")]
    [InlineData(true, "progress", "cancel")]
    public async Task AuditCleanupDiagnosticCallbackFailureCannotDestroyCandidateHandoffOrOriginalFailure(
        bool archiveCandidate, string failedCallback, string initialFailure)
    {
        using var directory = new TemporaryDirectory();
        var project = Path.Combine(directory.Path, "project");
        var cache = Path.Combine(directory.Path, "cache");
        var downloader = new SwitchingDownloader(CreateCoreArchive(directory.Path, "1.0.0", "old"));
        var initialInstaller = new CoreInstaller(project, cache, downloader);
        var previous = await initialInstaller.InstallAsync(Request(new string('a', 40)));
        var staging = CreateMergeStaging(project, "candidate");
        downloader.ArchivePath = CreateCoreArchive(directory.Path, "2.0.0", "candidate");
        var reportingFailure = new InvalidOperationException("maintenance callback rejected");
        var original = initialFailure == "cancel" ? (Exception)new OperationCanceledException("original canceled")
            : new IOException("original mutation failed");
        string? readonlyFile = null;
        var sinkCalls = 0;
        var cleanupProgressCalls = 0;
        var installer = new CoreInstaller(project, cache, downloader, maintenanceDiagnostic: _ =>
        {
            sinkCalls++;
            if (failedCallback == "sink") throw reportingFailure;
        });
        var progress = new SynchronousProgress(value =>
        {
            if (value.Stage != CoreInstallStage.Completed) return;
            if (value.Detail.StartsWith("安装临时文件清理失败", StringComparison.Ordinal))
            {
                cleanupProgressCalls++;
                if (failedCallback == "progress") throw reportingFailure;
                return;
            }
            if (archiveCandidate)
                readonlyFile = Assert.Single(Directory.GetFiles(cache, "core-stable-*.zip"));
            else
            {
                // Recreate the consumed staging path to exercise its cleanup before ownership handoff.
                Directory.CreateDirectory(staging);
                readonlyFile = Path.Combine(staging, "readonly-cleanup-marker.txt");
                File.WriteAllText(readonlyFile, "fixture");
            }
            File.SetAttributes(readonlyFile!, File.GetAttributes(readonlyFile!) | FileAttributes.ReadOnly);
            if (initialFailure != "none") throw original;
        });
        var prepared = new CorePreparedInstallRequest(ManagedCoreVariant.Stable, staging, previous.Manifest!,
            previous.Manifest!.CommitSha, new string('b', 40),
            [new CorePullRequestSource(12, "fork/core", "feature", new string('c', 40), null)]);
        Func<Task> apply = async () =>
        {
            if (archiveCandidate)
                await installer.InstallUpdateCandidateAsync(Request(new string('b', 40)), previous.Manifest!, progress);
            else
                await installer.InstallPreparedAsync(prepared, progress);
        };
        try
        {
            var failure = await Assert.ThrowsAnyAsync<Exception>(apply);
            var chain = Assert.IsType<AggregateException>(failure is AggregateException aggregate ? aggregate : failure.InnerException);
            var errors = chain.Flatten().InnerExceptions;
            Assert.Contains(errors, error => ReferenceEquals(error, reportingFailure));
            Assert.Contains(errors, error => (error is IOException or UnauthorizedAccessException) && !ReferenceEquals(error, original));
            if (initialFailure != "none") Assert.Contains(errors, error => ReferenceEquals(error, original));
            if (initialFailure == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(failure);
            Assert.Equal(1, sinkCalls);
            Assert.Equal(failedCallback == "progress" ? 1 : 0, cleanupProgressCalls);
            Assert.Equal("old", File.ReadAllText(Path.Combine(previous.Directory, "payload.txt")));
            Assert.True(CoreInstallationManifest.SourcesEqual(previous.Manifest, installer.Inspect(ManagedCoreVariant.Stable).Manifest));
            Assert.Empty(Directory.GetDirectories(project, ".danmu_api_stable.backup-*"));
            Assert.Empty(installer.GetHistory(ManagedCoreVariant.Stable));
        }
        finally
        {
            if (readonlyFile is not null && File.Exists(readonlyFile)) File.SetAttributes(readonlyFile, FileAttributes.Normal);
        }
    }

    private sealed class SynchronousProgress(Action<CoreInstallProgress> report) : IProgress<CoreInstallProgress>
    {
        public void Report(CoreInstallProgress value) => report(value);
    }

    private static string CreateMergeStaging(string project, string payload)
    {
        var staging = Path.Combine(project, $".danmu_api_stable.merge-staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(staging, "configs"));
        File.WriteAllText(Path.Combine(staging, "worker.js"), "export default {};\n");
        File.WriteAllText(Path.Combine(staging, "package.json"), "{\"type\":\"module\"}");
        File.WriteAllText(Path.Combine(staging, "configs", "envs.js"), "export const envVarConfig = {};\n");
        File.WriteAllText(Path.Combine(staging, "configs", "globals.js"), "export const VERSION = '2.0.0';\n");
        File.WriteAllText(Path.Combine(staging, "payload.txt"), payload);
        return staging;
    }

    [Fact]
    public void UnknownDownloadRouteIsRejected()
    {
        Assert.Throws<ArgumentException>(() => GithubProxyCatalog.GetById("missing-route"));
    }

    private static CoreInstallRequest Request(string sha) => new(
        ManagedCoreVariant.Stable,
        GithubRepositoryReference.Official("main"),
        "main",
        sha,
        "稳定核心",
        CoreInstallKind.Branch,
        GithubProxyCatalog.OriginalId);

    private static string CreateCoreArchive(
        string root,
        string version,
        string payload,
        bool includeEnvs = true)
    {
        var path = Path.Combine(root, $"core-{Guid.NewGuid():N}.zip");
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        WriteEntry(zip, "owner-repo/package.json", "{\"name\":\"fixture\",\"padding\":\"" + new string('p', 2048) + "\"}");
        WriteEntry(zip, "owner-repo/danmu_api/worker.js", "export default {};\n");
        if (includeEnvs)
        {
            WriteEntry(zip, "owner-repo/danmu_api/configs/envs.js", "export const envVarConfig = {};\n");
        }
        WriteEntry(zip, "owner-repo/danmu_api/configs/globals.js", $"export const VERSION = '{version}';\n");
        WriteEntry(zip, "owner-repo/danmu_api/payload.txt", payload);
        return path;
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private sealed class CopyingDownloader(string archivePath) : IGithubFileDownloader
    {
        public Task DownloadAsync(
            Uri originalUri,
            string proxyId,
            string targetPath,
            IProgress<GithubDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(archivePath, targetPath);
            progress?.Report(new GithubDownloadProgress(
                new FileInfo(targetPath).Length,
                new FileInfo(targetPath).Length,
                "test",
                originalUri));
            return Task.CompletedTask;
        }
    }

    private sealed class SwitchingDownloader(string archivePath) : IGithubFileDownloader
    {
        public string ArchivePath { get; set; } = archivePath;

        public Task DownloadAsync(
            Uri originalUri,
            string proxyId,
            string targetPath,
            IProgress<GithubDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(ArchivePath, targetPath);
            return Task.CompletedTask;
        }
    }
}
