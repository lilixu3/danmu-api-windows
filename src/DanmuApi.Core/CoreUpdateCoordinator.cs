namespace DanmuApi.Core;

public enum CoreUpdateCheckStatus
{
    SkippedCooldown,
    NotInstalled,
    MissingSource,
    Checked,
    Failed,
}

public sealed record CoreUpdateCheckResult(
    ManagedCoreVariant Variant,
    CoreUpdateCheckStatus Status,
    bool UpdateAvailable,
    CoreInstallationManifest? Local,
    GithubCommit? Remote,
    GithubRateLimit? RateLimit,
    DateTimeOffset CheckedAt,
    string Diagnostic,
    GithubFailureKind? FailureKind = null);

public interface ICoreUpdateTimestampStore
{
    DateTimeOffset? ReadLastCheck(ManagedCoreVariant variant);
    void WriteLastCheck(ManagedCoreVariant variant, DateTimeOffset checkedAt);
}

public interface ICoreUpdateCoordinator
{
    TimeSpan AutomaticInterval { get; }
    CoreUpdateCheckResult? LastResult { get; }
    event EventHandler<CoreUpdateCheckResult>? ResultChanged;
    Task<CoreUpdateCheckResult> CheckAsync(
        ManagedCoreVariant variant,
        bool force,
        CancellationToken cancellationToken = default);
    Task<CoreUpdateCheckResult> CheckAsync(
        ManagedCoreVariant variant,
        bool force,
        TimeSpan automaticInterval,
        CancellationToken cancellationToken = default);
}

public sealed class CoreUpdateCoordinator : ICoreUpdateCoordinator, IDisposable
{
    private readonly object _sync = new();
    private readonly ICoreInstaller _installer;
    private readonly IGithubCoreRemote _remote;
    private readonly ICoreUpdateTimestampStore _timestampStore;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<ManagedCoreVariant, long> _lastMonotonicChecks = [];
    private readonly Dictionary<ManagedCoreVariant, ActiveCheck> _activeChecks = [];
    private CoreUpdateCheckResult? _lastResult;
    private bool _disposed;

    private sealed class ActiveCheck
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<CoreUpdateCheckResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Waiters { get; set; }
    }

    public CoreUpdateCoordinator(
        ICoreInstaller installer,
        IGithubCoreRemote remote,
        ICoreUpdateTimestampStore timestampStore,
        TimeSpan? automaticInterval = null,
        TimeProvider? timeProvider = null)
    {
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _timestampStore = timestampStore ?? throw new ArgumentNullException(nameof(timestampStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
        AutomaticInterval = automaticInterval ?? TimeSpan.FromMinutes(10);
        if (AutomaticInterval < TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(automaticInterval), "自动检查间隔不能短于 5 分钟");
        }
    }

    public TimeSpan AutomaticInterval { get; }

    public CoreUpdateCheckResult? LastResult
    {
        get
        {
            lock (_sync)
            {
                return _lastResult;
            }
        }
    }

    public event EventHandler<CoreUpdateCheckResult>? ResultChanged;

    public Task<CoreUpdateCheckResult> CheckAsync(
        ManagedCoreVariant variant,
        bool force,
        CancellationToken cancellationToken = default) =>
        CheckAsync(variant, force, AutomaticInterval, cancellationToken);

    public Task<CoreUpdateCheckResult> CheckAsync(
        ManagedCoreVariant variant,
        bool force,
        TimeSpan automaticInterval,
        CancellationToken cancellationToken = default)
    {
        ValidateAutomaticInterval(automaticInterval);
        cancellationToken.ThrowIfCancellationRequested();
        ActiveCheck? check = null;
        var start = false;
        CoreUpdateCheckResult? skipped = null;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeChecks.TryGetValue(variant, out var active))
            {
                check = active;
            }
            else if (!force && !IsDueLocked(variant, automaticInterval))
            {
                skipped = new CoreUpdateCheckResult(
                    variant,
                    CoreUpdateCheckStatus.SkippedCooldown,
                    false,
                    _installer.Inspect(variant).Manifest,
                    null,
                    _remote.LastRateLimit,
                    _timeProvider.GetUtcNow(),
                    $"距离上次检查未满 {automaticInterval.TotalMinutes:0} 分钟");
                _lastResult = skipped;
            }
            else
            {
                check = new ActiveCheck();
                _activeChecks.Add(variant, check);
                start = true;
            }
            if (check is not null) check.Waiters++;
        }

        if (skipped is not null)
        {
            ResultChanged?.Invoke(this, skipped);
            return Task.FromResult(skipped).WaitAsync(cancellationToken);
        }
        if (start) _ = CompleteCheckAsync(variant, check!);
        return WaitForCheckAsync(variant, check!, cancellationToken);
    }

    private async Task<CoreUpdateCheckResult> WaitForCheckAsync(
        ManagedCoreVariant variant, ActiveCheck check, CancellationToken cancellationToken)
    {
        try
        {
            return await check.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                check.Waiters--;
                if (check.Waiters == 0 && IsCurrentLocked(variant, check))
                {
                    _activeChecks.Remove(variant);
                    check.Cancellation.Cancel();
                }
            }
        }
    }

    private bool IsCurrentLocked(ManagedCoreVariant variant, ActiveCheck check) =>
        _activeChecks.TryGetValue(variant, out var current) && ReferenceEquals(current, check);

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            var checks = _activeChecks.Values.ToArray();
            _activeChecks.Clear();
            List<Exception> errors = [];
            foreach (var check in checks)
            {
                try { check.Cancellation.Cancel(); }
                catch (Exception error) { errors.Add(error); }
            }
            if (errors.Count > 0) throw new AggregateException(errors);
        }
    }

    private async Task CompleteCheckAsync(ManagedCoreVariant variant, ActiveCheck check)
    {
        try
        {
            var result = await RunCheckCoreAsync(variant, check.Cancellation.Token).ConfigureAwait(false);
            lock (_sync)
            {
                check.Cancellation.Token.ThrowIfCancellationRequested();
                if (!IsCurrentLocked(variant, check)) throw new OperationCanceledException(check.Cancellation.Token);
                try
                {
                    _timestampStore.WriteLastCheck(variant, result.CheckedAt);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
                {
                    result = result with { Status = CoreUpdateCheckStatus.Failed,
                        Diagnostic = $"{result.Diagnostic}；保存检查时间失败：{error.Message}" };
                }
                _lastMonotonicChecks[variant] = _timeProvider.GetTimestamp();
                _lastResult = result;
                _activeChecks.Remove(variant);
            }
            ResultChanged?.Invoke(this, result);
            check.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException error)
        {
            check.Completion.TrySetCanceled(error.CancellationToken);
        }
        catch (Exception error)
        {
            check.Completion.TrySetException(error);
            // Observe faults even if every caller already canceled its wait.
            _ = check.Completion.Task.Exception;
        }
        finally
        {
            lock (_sync)
            {
                if (IsCurrentLocked(variant, check)) _activeChecks.Remove(variant);
                check.Cancellation.Dispose();
            }
        }
    }

    private async Task<CoreUpdateCheckResult> RunCheckCoreAsync(ManagedCoreVariant variant, CancellationToken cancellationToken)
    {
        var checkedAt = _timeProvider.GetUtcNow();
        CoreUpdateCheckResult result;
        try
        {
            var installed = _installer.Inspect(variant);
            if (!installed.IsInstalled || !installed.IsValid)
            {
                result = new CoreUpdateCheckResult(
                    variant,
                    CoreUpdateCheckStatus.NotInstalled,
                    false,
                    installed.Manifest,
                    null,
                    _remote.LastRateLimit,
                    checkedAt,
                    installed.Diagnostic ?? "核心尚未安装");
            }
            else if (installed.Manifest is null)
            {
                result = new CoreUpdateCheckResult(
                    variant,
                    CoreUpdateCheckStatus.MissingSource,
                    false,
                    null,
                    null,
                    _remote.LastRateLimit,
                    checkedAt,
                    "当前核心缺少来源 manifest，无法检查更新；请重新安装");
            }
            else
            {
                var source = GithubRepositoryReference.Parse(installed.Manifest.Repository)
                    .WithBranch(installed.Manifest.Branch);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45), _timeProvider);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
                GithubCommit remote;
                try
                {
                    remote = await _remote.GetCommitAsync(source, installed.Manifest.Branch, request.Token)
                        .WaitAsync(request.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
                {
                    throw new GithubRemoteException(GithubFailureKind.Network, "核心更新检查超过 45 秒，已终止本次请求。", innerException: error);
                }
                var available = !CommitShasEquivalent(installed.Manifest.CommitSha, remote.Sha);
                result = new CoreUpdateCheckResult(
                    variant,
                    CoreUpdateCheckStatus.Checked,
                    available,
                    installed.Manifest,
                    remote,
                    _remote.LastRateLimit,
                    checkedAt,
                    available ? $"发现新提交 {remote.ShortSha}" : "当前核心已是所选分支最新提交");
            }
        }
        catch (Exception error) when (error is GithubRemoteException or IOException or FormatException or UnauthorizedAccessException)
        {
            CoreInstallationManifest? manifest;
            try
            {
                manifest = _installer.Inspect(variant).Manifest;
            }
            catch (Exception inspectError) when (inspectError is IOException or UnauthorizedAccessException or FormatException)
            {
                manifest = null;
                error = new IOException($"{error.Message}；读取本地核心状态也失败：{inspectError.Message}", error);
            }

            result = new CoreUpdateCheckResult(
                variant,
                CoreUpdateCheckStatus.Failed,
                false,
                manifest,
                null,
                _remote.LastRateLimit,
                checkedAt,
                $"核心更新检查失败：{error.Message}",
                (error as GithubRemoteException)?.Kind);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private bool IsDueLocked(ManagedCoreVariant variant, TimeSpan automaticInterval)
    {
        if (_lastMonotonicChecks.TryGetValue(variant, out var timestamp))
        {
            var elapsed = _timeProvider.GetElapsedTime(timestamp, _timeProvider.GetTimestamp());
            if (elapsed >= TimeSpan.Zero && elapsed < automaticInterval)
            {
                return false;
            }
        }

        var persisted = _timestampStore.ReadLastCheck(variant);
        if (persisted is null)
        {
            return true;
        }

        var now = _timeProvider.GetUtcNow();
        return now < persisted.Value || now - persisted.Value >= automaticInterval;
    }

    private static void ValidateAutomaticInterval(TimeSpan automaticInterval)
    {
        if (automaticInterval < TimeSpan.FromMinutes(5) || automaticInterval > TimeSpan.FromDays(30))
        {
            throw new ArgumentOutOfRangeException(
                nameof(automaticInterval),
                "自动检查间隔必须在 5 分钟到 30 天之间");
        }
    }

    private static bool CommitShasEquivalent(string left, string right)
    {
        var first = left.Trim();
        var second = right.Trim();
        if (first.Length < 7 || second.Length < 7)
        {
            return false;
        }

        return first.StartsWith(second, StringComparison.OrdinalIgnoreCase) ||
               second.StartsWith(first, StringComparison.OrdinalIgnoreCase);
    }
}
