using System.Security.Cryptography;
using System.Text.RegularExpressions;
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
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "node.exe", "NODE-LICENSE.txt", "nodejs-project/main.js", "nodejs-project/android-server.js",
        "nodejs-project/favorite-scheduler-host.js", "nodejs-project/package-lock.json", "nodejs-project/package.json",
        "nodejs-project/runtime-polyfills.js", "nodejs-project/runtime_asset_layout.txt",
        "nodejs-project/startup-failure.js", "nodejs-project/worker-proxy.js"
    };

    public static RuntimeDependencyCheckResult Check(string bundleDirectory, AppPaths paths,
        IProgress<BundledRuntimeProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new("ReadingManifest", 0, 0));
        var manifest = Path.Combine(bundleDirectory, "SHA256SUMS.txt");
        CheckPath(manifest);
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(manifest))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length < 67 || line.Substring(64, 2) != "  " || !Regex.IsMatch(line[..64], "\\A[0-9a-fA-F]{64}\\z"))
                throw new IOException("随包运行依赖清单格式无效。");
            var relative = line[66..];
            if (relative.Contains('\\') || relative.Contains(':') || relative.Split('/').Any(p =>
                p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') ||
                p.Any(c => c < 32 || "<>\"|?*".Contains(c)) || Regex.IsMatch(p, "\\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|$)", RegexOptions.IgnoreCase)) ||
                (!Hosts.Contains(relative) && !relative.StartsWith("nodejs-project/node_modules/", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("随包运行依赖清单包含非法或非受管路径。");
            if (!entries.TryAdd(relative, line[..64])) throw new IOException("随包运行依赖清单包含重复路径。");
        }
        if (!entries.ContainsKey("node.exe") || !entries.ContainsKey("nodejs-project/main.js"))
            throw new IOException("随包运行依赖清单缺少 Node 或启动入口。");
        foreach (var relative in entries.Keys)
        {
            var segments = relative.Split('/');
            for (var i = 1; i < segments.Length; i++)
                if (entries.ContainsKey(string.Join('/', segments.Take(i)))) throw new IOException("随包运行依赖清单存在文件与目录冲突。");
        }
        var missing = 0;
        var damaged = 0;
        var completed = 0;
        progress?.Report(new("VerifyingTarget", 0, entries.Count));
        var buffer = new byte[128 * 1024];
        foreach (var (relative, hash) in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
