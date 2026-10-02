using System.Security.Cryptography;
using DanmuApi.Core;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.App.Services;

public sealed record RuntimeDependencyCheckResult(int Total, int Missing, int Damaged)
{
    public bool IsHealthy => Missing == 0 && Damaged == 0;
}

public interface IRuntimeMaintenanceService
{
    Task<RuntimeDependencyCheckResult> CheckAsync(IProgress<BundledRuntimeProgress>? progress = null, CancellationToken cancellationToken = default);
    Task RepairAsync(IProgress<BundledRuntimeProgress>? progress = null, CancellationToken cancellationToken = default);
}

public sealed class RuntimeMaintenanceService(string bundleDirectory, AppPaths paths, IRuntimeController controller,
    ICoreUpdateScheduler scheduler, IPendingCoreUpdateService pending, RuntimePreparationService? preparation = null) : IRuntimeMaintenanceService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<RuntimeDependencyCheckResult> CheckAsync(IProgress<BundledRuntimeProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => RuntimeDependencyInspector.Check(bundleDirectory, paths, progress, cancellationToken), cancellationToken);

    public async Task RepairAsync(IProgress<BundledRuntimeProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireStoppedLeaseAsync(cancellationToken).ConfigureAwait(false);
        await Task.Run(() => BundledRuntimePreparer.Prepare(bundleDirectory, paths,
            () => IsStopped(), forceRepair: true, progress: p => progress?.Report(p), cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IAsyncDisposable> AcquireStoppedLeaseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureStopped();
            if (controller is not RuntimeController runtime) throw new InvalidOperationException("运行控制器不支持运行依赖维护。");
            await scheduler.PauseForApplicationUpdateAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (pending.IsApplying) throw new InvalidOperationException("核心正在更新，请等待更新结束后修复运行依赖。");
                EnsureStopped();
                var preparationLease = preparation is null ? null : await preparation.AcquireMutationLeaseAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var lease = await runtime.AcquireStoppedMaintenanceLeaseAsync(cancellationToken).ConfigureAwait(false);
                    return new MaintenanceLease(lease, scheduler, _gate, preparationLease);
                }
                catch
                {
                    if (preparationLease is not null) await preparationLease.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            catch { scheduler.ResumeAfterApplicationUpdate(); throw; }
        }
        catch { _gate.Release(); throw; }
    }

    private sealed class MaintenanceLease(IAsyncDisposable inner, ICoreUpdateScheduler scheduler, SemaphoreSlim gate, IAsyncDisposable? preparationLease) : IAsyncDisposable
    {
        private int _disposed;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { await inner.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                try
                {
                    if (preparationLease is not null) await preparationLease.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    try { scheduler.ResumeAfterApplicationUpdate(); }
                    finally { gate.Release(); }
                }
            }
        }
    }

    private bool IsStopped()
    {
        var snapshot = controller.Snapshot;
        return snapshot.State is DesktopRuntimeState.Stopped or DesktopRuntimeState.CoreSetupRequired && snapshot.Pid is null;
    }
    private void EnsureStopped()
    {
        if (!IsStopped()) throw new InvalidOperationException("修复运行依赖前，请先停止服务；不会自动停止正在运行的服务。");
    }
}

/// <summary>Read-only full payload verification, deliberately independent of startup metadata shortcuts.</summary>
public static class RuntimeDependencyInspector
{
    public static RuntimeDependencyCheckResult Check(string bundleDirectory, AppPaths paths,
        IProgress<BundledRuntimeProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new("ReadingManifest", 0, 0));
        var manifest = Path.Combine(bundleDirectory, "SHA256SUMS.txt");
        CheckPath(manifest);
        // Preparation and maintenance share one exact managed-path and manifest contract.
        // This check still hashes every target independently of startup metadata shortcuts.
        var entries = BundledRuntimePreparer.ParseManagedManifest(File.ReadAllText(manifest));
        cancellationToken.ThrowIfCancellationRequested();
        var missing = 0;
        var damaged = 0;
        var completed = 0;
        progress?.Report(new("VerifyingTarget", 0, entries.Count));
        var buffer = new byte[128 * 1024];
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = entry.Path;
            var hash = entry.Hash;
            var target = Path.Combine(paths.RuntimeDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            CheckPath(target);
            if (Directory.Exists(target)) damaged++;
            else
            {
                try
                {
                    using var stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    int count;
                    while ((count = stream.Read(buffer)) != 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        sha.AppendData(buffer, 0, count);
                    }
                    if (!Convert.ToHexString(sha.GetHashAndReset()).Equals(hash, StringComparison.OrdinalIgnoreCase)) damaged++;
                }
                catch (FileNotFoundException) { missing++; }
                catch (DirectoryNotFoundException) { missing++; }
            }
            completed++;
            if (completed % 64 == 0 || completed == entries.Count)
                progress?.Report(new("VerifyingTarget", completed, entries.Count));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(entries.Count, missing, damaged);
    }

    private static void CheckPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("运行依赖路径含链接或重解析点，无法安全校验。");
            }
            catch (FileNotFoundException) { /* Absence is counted by the caller. */ }
            catch (DirectoryNotFoundException) { /* A missing parent means the target is missing. */ }
        }
    }
}
