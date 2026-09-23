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

    /// <summary>结论变化时触发；参数为 <c>null</c> 表示当前没有结论（旧结论已作废）。</summary>
    event EventHandler<CoreUpdateCheckResult?>? ResultChanged;

    /// <summary>
    /// 拿磁盘上的当前安装重新对账「有更新」这条结论：启动时用它把落盘记录接回内存；
    /// 安装被改动后（装完更新、回退、重装、删核心）用它把已作废的结论撤掉并广播，
    /// 免得侧栏卡片和托盘菜单还挂着一个已经装上的「新版本」。
    /// </summary>
    void ReconcileDiscovery(ManagedCoreVariant variant);

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
    private readonly ICoreUpdateDiscoveryStore _discoveryStore;
    private readonly Action<string>? _diagnosticSink;
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
        ICoreUpdateDiscoveryStore discoveryStore,
        TimeSpan? automaticInterval = null,
        TimeProvider? timeProvider = null,
        Action<string>? diagnosticSink = null)
    {
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _timestampStore = timestampStore ?? throw new ArgumentNullException(nameof(timestampStore));
        _discoveryStore = discoveryStore ?? throw new ArgumentNullException(nameof(discoveryStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _diagnosticSink = diagnosticSink;
        AutomaticInterval = automaticInterval ?? TimeSpan.FromMinutes(10);
        if (AutomaticInterval < TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(automaticInterval), "自动检查间隔不能短于 5 分钟");
        }
    }

    public TimeSpan AutomaticInterval { get; }

    /// <summary>
    /// 最近一次**有结论**的检查结果：<see cref="CoreUpdateCheckStatus.Checked"/>、
    /// <see cref="CoreUpdateCheckStatus.NotInstalled"/> 或 <see cref="CoreUpdateCheckStatus.MissingSource"/>。
    /// 冷却跳过与网络失败都只是这一次调用的结果，不构成关于「有没有新提交」的信息，
    /// 因此既不覆盖这里，也不广播 —— 否则一次冷却命中就会把侧栏刚亮起的「核心可更新」抹掉。
    /// 调用方仍然能从 <see cref="CheckAsync"/> 的返回值里拿到这些临时结果去显示原因。
    /// </summary>
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

    /// <summary>
    /// 结论变化时广播，<c>null</c> 表示「当前没有结论」（例如装完核心后旧结论作废、等待重新检查）。
    /// 订阅方必须把 null 当成一次真实的状态变化处理，而不是忽略。
    /// </summary>
    public event EventHandler<CoreUpdateCheckResult?>? ResultChanged;

    /// <summary>
    /// 拿磁盘上的当前安装重新对账「有更新」这条结论。两条路径共用：
    /// ① 进程启动时把落盘记录接回内存（此时内存里还没有结论）；
    /// ② 安装被改动后（装完更新、回退、重装、删核心）把已经作废的结论撤掉，
    /// 让侧栏卡片与托盘菜单立刻跟上，而不是等下一次联网检查。
    ///
    /// 结论仍然成立时不广播（改名只改 manifest、不动提交，不该打扰订阅方）；
    /// 作废时广播 <c>null</c>。读不出磁盘状态则保留现状并记诊断 —— 读不到不等于没有更新。
    /// </summary>
    public void ReconcileDiscovery(ManagedCoreVariant variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        CoreUpdateCheckResult? reconciled;
        bool conclusionChanged;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!TryReconcileLocked(variant, out reconciled))
            {
                // 这次问不出结论（磁盘读失败等），保留现状；原因已经进诊断。
                return;
            }

            conclusionChanged = !SameConclusion(_lastResult, reconciled);
            // 结论没变也要写回：Local 是要显示给用户的"当前提交"，改名后得跟着更新。
            _lastResult = reconciled;
        }

        if (conclusionChanged) ResultChanged?.Invoke(this, reconciled);
    }

    /// <summary>调用方需持有 <see cref="_sync"/>。返回 false 表示「无法判断，保留现状」。</summary>
    private bool TryReconcileLocked(ManagedCoreVariant variant, out CoreUpdateCheckResult? reconciled)
    {
        var current = _lastResult;
        if (current is not null && current.Variant != variant)
        {
            // 内存里这条结论属于**别的**变体（槽位只有一个，是"最后检查过的那个变体"）。
            // 别的变体的安装变动跟它无关，绝不能顺手清掉 —— 否则装一个自定义核心
            // 会把稳定核心刚发现的那条更新一起弄没。
            reconciled = current;
            return false;
        }

        if (current is { Status: CoreUpdateCheckStatus.Checked, UpdateAvailable: true, Remote: { } remote })
        {
            // 内存里就有结论，直接拿它跟磁盘对账，不必再读落盘记录。
            if (!TryInspect(variant, out var installed)) { reconciled = null; return false; }
            if (IsConclusionVoid(installed, current.Local?.CommitSha, remote.Sha, out var reason))
            {
                ClearDiscovery(variant, reason);
                reconciled = null;
                return true;
            }

            reconciled = current with { Local = installed.Manifest };
            return true;
        }

        // 内存里没有结论：按落盘记录恢复（启动路径，或结论已被撤掉后的再次对账）。
        if (!TryReadDiscovery(variant, out var discovery)) { reconciled = null; return false; }
        if (discovery is null) { reconciled = null; return true; }
        if (!TryInspect(variant, out var currentInstall)) { reconciled = null; return false; }
        if (IsConclusionVoid(currentInstall, discovery.LocalSha, discovery.RemoteSha, out var staleReason))
        {
            ClearDiscovery(variant, staleReason);
            reconciled = null;
            return true;
        }

        var recordRemote = new GithubCommit(discovery.RemoteSha, discovery.RemoteTitle, string.Empty, null, null, []);
        reconciled = new CoreUpdateCheckResult(
            variant,
            CoreUpdateCheckStatus.Checked,
            true,
            currentInstall.Manifest,
            recordRemote,
            null,
            discovery.CheckedAt,
            $"发现新提交 {recordRemote.ShortSha}（上次检查于 {discovery.CheckedAt.LocalDateTime:yyyy-MM-dd HH:mm}）");
        return true;
    }

    /// <summary>
    /// 这条结论还成不成立。远端提交已经装上说明更新已被应用；本地提交对不上说明安装被换过
    /// （回退、重装、换分支）；核心不可用则谈不上更新。三种情况结论都作废，必须显式清除。
    /// </summary>
    private static bool IsConclusionVoid(
        CoreInstallationInfo installed,
        string? conclusionLocalSha,
        string conclusionRemoteSha,
        out string reason)
    {
        if (!installed.IsInstalled || !installed.IsValid || installed.Manifest is null)
        {
            reason = $"核心已不可用（{installed.Diagnostic ?? "未安装"}）";
            return true;
        }

        if (installed.Manifest.IsLocalPullRequestStack)
        {
            reason = "当前核心是本地 PR 组合，普通分支更新结论不适用";
            return true;
        }

        if (CommitShasEquivalent(installed.Manifest.CommitSha, conclusionRemoteSha))
        {
            reason = "记录里的那个新提交已经装上";
            return true;
        }

        if (conclusionLocalSha is null || !CommitShasEquivalent(installed.Manifest.CommitSha, conclusionLocalSha))
        {
            reason = "核心安装已被替换（重装、回退或换分支），原结论不再成立";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private bool TryInspect(ManagedCoreVariant variant, out CoreInstallationInfo installed)
    {
        try
        {
            installed = _installer.Inspect(variant);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
        {
            // 读不到本地状态时保留现有结论：这次只是没读到，不是"没有更新"。
            _diagnosticSink?.Invoke($"读取本地核心状态失败，本次不对账更新结论：{error.Message}");
            installed = null!;
            return false;
        }
    }

    private bool TryReadDiscovery(ManagedCoreVariant variant, out CoreUpdateDiscovery? discovery)
    {
        try
        {
            discovery = _discoveryStore.Read(variant);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
        {
            _diagnosticSink?.Invoke($"读取核心更新发现记录失败，本次不恢复更新提示：{error.Message}");
            discovery = null;
            return false;
        }
    }

    /// <summary>两次结论是否指向同一件事（订阅方据此决定要不要重画卡片与托盘菜单）。</summary>
    private static bool SameConclusion(CoreUpdateCheckResult? left, CoreUpdateCheckResult? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.Status == right.Status &&
               left.UpdateAvailable == right.UpdateAvailable &&
               string.Equals(left.Remote?.Sha, right.Remote?.Sha, StringComparison.OrdinalIgnoreCase);
    }

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
            // 冷却命中的原因只回给这次调用方（核心页会显示它），不进 LastResult、也不广播。
            return Task.FromResult(skipped).WaitAsync(cancellationToken);
        }
        if (start) _ = CompleteCheckAsync(variant, check!);
        return WaitForCheckAsync(variant, check!, cancellationToken);
    }

    private void ClearDiscovery(ManagedCoreVariant variant, string reason)
    {
        try
        {
            _discoveryStore.Clear(variant);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
        {
            _diagnosticSink?.Invoke($"清除过期的核心更新发现记录失败（{reason}）：{error.Message}");
        }
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
                _activeChecks.Remove(variant);
                var conclusive = PublishResult(variant, result);
                if (conclusive) _lastResult = result;
            }
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

    /// <summary>
    /// 只在结果**真的说明了「有没有新提交」**时更新 <see cref="LastResult"/> 并广播：
    /// 检查成功（有/没有更新）、未安装、缺来源，都算结论，卡片要跟着变；
    /// 网络失败不是结论 —— 保留上一条已知发现，否则一次网络抖动就能让卡片消失。
    /// 返回值表示这次结果是否属于结论（由调用方在持锁状态下写入 <c>_lastResult</c>）。
    /// </summary>
    private bool PublishResult(ManagedCoreVariant variant, CoreUpdateCheckResult result)
    {
        if (result.Status is not (CoreUpdateCheckStatus.Checked
            or CoreUpdateCheckStatus.NotInstalled
            or CoreUpdateCheckStatus.MissingSource))
        {
            return false;
        }

        PersistDiscovery(variant, result);
        ResultChanged?.Invoke(this, result);
        return true;
    }

    /// <summary>
    /// 把结论落盘，让「有更新」跨重启还在。写入失败只记诊断：
    /// 落盘是让卡片活过重启的手段，不该反过来让一次成功的检查变成失败。
    /// </summary>
    private void PersistDiscovery(ManagedCoreVariant variant, CoreUpdateCheckResult result)
    {
        try
        {
            if (result is { Status: CoreUpdateCheckStatus.Checked, UpdateAvailable: true, Remote: { } remote } &&
                result.Local is { } local)
            {
                _discoveryStore.Write(new CoreUpdateDiscovery(
                    variant,
                    local.CommitSha,
                    remote.Sha,
                    remote.Title,
                    result.CheckedAt));
            }
            else
            {
                // 确认已是最新、或核心状态变化（未安装/缺来源）时，旧结论必须撤掉。
                _discoveryStore.Clear(variant);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
        {
            _diagnosticSink?.Invoke($"保存核心更新发现结果失败（不影响本次检查结论）：{error.Message}");
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
                var localStack = installed.Manifest.IsLocalPullRequestStack;
                var available = !CommitShasEquivalent(installed.Manifest.CommitSha, remote.Sha);
                result = new CoreUpdateCheckResult(
                    variant,
                    CoreUpdateCheckStatus.Checked,
                    localStack ? false : available,
                    installed.Manifest,
                    remote,
                    _remote.LastRateLimit,
                    checkedAt,
                    localStack
                        ? available
                            ? $"本地 PR 组合的基线有新提交 {remote.ShortSha}；请在 PR 实验室重新构建，不会自动覆盖组合"
                            : "本地 PR 组合的基线未变化；如需检测 PR head 更新，请在 PR 实验室刷新"
                        : available ? $"发现新提交 {remote.ShortSha}" : "当前核心已是所选分支最新提交");
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
