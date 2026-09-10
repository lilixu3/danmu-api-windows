using DanmuApi.Core;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.App.Services;

public sealed class BackupRestoreGuard(IRuntimeController controller, ICoreUpdateScheduler scheduler, IPendingCoreUpdateService pending) : IBackupRestoreGuard
{
    public async ValueTask<IAsyncDisposable> AcquireStoppedLeaseAsync(CancellationToken cancellationToken)
    {
        if (controller is not RuntimeController runtime) throw new InvalidOperationException("运行控制器不支持独占恢复");
        await scheduler.PauseForApplicationUpdateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (pending.IsApplying) throw new InvalidOperationException("核心正在更新，暂不能恢复备份");
            var lease = await runtime.AcquireStoppedMaintenanceLeaseAsync(cancellationToken).ConfigureAwait(false);
            return new GuardLease(lease, scheduler);
        }
        catch { scheduler.ResumeAfterApplicationUpdate(); throw; }
    }
    private sealed class GuardLease(IAsyncDisposable inner, ICoreUpdateScheduler scheduler) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await inner.DisposeAsync().ConfigureAwait(false); }
            finally { scheduler.ResumeAfterApplicationUpdate(); }
        }
    }
}
