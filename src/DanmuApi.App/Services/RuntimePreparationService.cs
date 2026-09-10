using DanmuApi.Platform;

namespace DanmuApi.App.Services;

public enum RuntimePreparationState { Pending, Preparing, Ready, Failed, Canceled }
public sealed record RuntimePreparationSnapshot(RuntimePreparationState State, string Message, BundledRuntimeProgress? Progress = null);

/// <summary>Owns startup preparation and serializes dependency mutations for the lifetime of the app.</summary>
public sealed class RuntimePreparationService : IAsyncDisposable
{
    private readonly Func<bool, Action<BundledRuntimeProgress>, CancellationToken, Task> _prepare;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _sync = new();
    private Task? _task;
    private RuntimePreparationSnapshot _snapshot = new(RuntimePreparationState.Pending, "运行环境等待准备；其他页面可以查看。");

    public RuntimePreparationService(Func<bool, Action<BundledRuntimeProgress>, CancellationToken, Task> prepare) => _prepare = prepare;
    public RuntimePreparationSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public string? StartBlockedReason => Snapshot.State == RuntimePreparationState.Ready ? null : Snapshot.Message;
    public event EventHandler<RuntimePreparationSnapshot>? Changed;

    /// <summary>可选 Redis 依赖的铺开结果。核心独立于运行环境准备，因此失败只作为提醒上报，不阻塞启动。</summary>
    public OptionalRedisStageResult? OptionalRedis { get; private set; }

    public void ReportOptionalRedis(OptionalRedisStageResult result) => OptionalRedis = result;

    public Task PrepareAsync(bool forceRepair = false)
    {
        lock (_sync)
        {
            if (_shutdown.IsCancellationRequested) return Task.FromCanceled(_shutdown.Token);
            if (_task is { IsCompleted: false }) return _task;
            _task = RunAsync(forceRepair);
            return _task;
        }
    }

    private async Task RunAsync(bool forceRepair)
    {
        try
        {
            // Existing mutations finish with their normal stop/write/restore semantics before readiness changes.
            await _mutationGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                Publish(new(RuntimePreparationState.Preparing,
                    "正在准备运行环境；首次准备需要复制随包 Node 与生产依赖，约需 10–30 秒（取决于磁盘与杀毒软件），之后启动只需几十毫秒。准备期间可以查看其他页面，但不能启动服务。"));
                await _prepare(forceRepair, p => Publish(new(RuntimePreparationState.Preparing,
                    $"正在准备运行环境：{PhaseText(p.Phase)} {p.Completed}/{p.Total}", p)), _shutdown.Token).ConfigureAwait(false);
                _shutdown.Token.ThrowIfCancellationRequested();
                Publish(new(RuntimePreparationState.Ready, "运行环境已就绪。"));
            }
            finally { _mutationGate.Release(); }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            Publish(new(RuntimePreparationState.Canceled, "运行环境准备已取消。"));
        }
        catch (Exception error)
        {
            Publish(new(RuntimePreparationState.Failed, $"运行环境准备失败：{error.Message}"));
        }
    }

    /// <summary>Maps preparer phase identifiers to the wording shown in the preparation banner.</summary>
    public static string PhaseText(string phase) => phase switch
    {
        "ReadingManifest" => "读取运行环境清单",
        "ReadingState" => "读取已有运行环境记录",
        "CheckingTargetMetadata" => "检查已有文件信息",
        "VerifyingTarget" => "校验已有依赖",
        "VerifyingSource" => "校验内置运行环境",
        "CheckingConflicts" => "检查依赖冲突",
        "BackingUp" => "备份原文件",
        "Staging" => "暂存并校验新依赖",
        "WritingJournal" => "保存恢复记录",
        "Replacing" => "替换依赖文件",
        "Snapshotting" => "记录文件信息",
        "Committing" => "提交运行环境记录",
        "InspectingCleanup" => "检查待清理的备份",
        "CleaningUp" => "清理备份文件",
        "Recovering" => "恢复运行环境",
        "Completed" => "运行环境准备完成",
        _ => $"处理运行环境（{phase}）",
    };

    public async ValueTask<IAsyncDisposable> AcquireReadyLeaseAsync(CancellationToken cancellationToken = default)
    {
        if (StartBlockedReason is { } reason) throw new InvalidOperationException(reason);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _mutationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        if (StartBlockedReason is { } changedReason)
        {
            _mutationGate.Release();
            throw new InvalidOperationException(changedReason);
        }
        return new Lease(_mutationGate);
    }

    public async ValueTask<IAsyncDisposable> AcquireMutationLeaseAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _mutationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        return new Lease(_mutationGate);
    }

    public async Task CancelAndWaitAsync()
    {
        Task? task;
        lock (_sync) { _shutdown.Cancel(); task = _task; }
        if (task is not null) await task.ConfigureAwait(false);
    }

    private void Publish(RuntimePreparationSnapshot snapshot)
    {
        Volatile.Write(ref _snapshot, snapshot);
        Changed?.Invoke(this, snapshot);
    }

    public async ValueTask DisposeAsync() => await CancelAndWaitAsync().ConfigureAwait(false);

    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
    {
        private int _disposed;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
