using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.App.Services;

public sealed record OutboundDirectSnapshot(
    string Status,
    string Reason,
    OutboundSettings? Settings = null,
    long? NodePid = null,
    int? HelperPid = null,
    string? HelperVersion = null,
    bool ServiceRunning = false,
    bool Applied = false,
    int? ProtocolVersion = null,
    string? RuntimeIdentity = null,
    long RuntimeEpoch = 0,
    long Revision = 0);

public sealed record OutboundDiagnosticRow(
    string Source,
    string Host,
    bool EchRequired,
    string? Protocol,
    bool? EchAccepted,
    int? HttpStatus,
    TimeSpan Duration,
    string? FailurePhase,
    string Diagnostic)
{
    public bool Succeeded => FailurePhase is null;
    public double DurationMs => Duration.TotalMilliseconds;
}

public enum OutboundDiagnosticRunStatus { Completed, Failed, Cancelled }

/// <summary>Only a complete, postvalidated run has a completion time. Rows are actual requests, never preflight placeholders.</summary>
public sealed record OutboundDiagnosticRun
{
    public OutboundDiagnosticRun(OutboundDiagnosticRunStatus status, OutboundSettings? settings,
        IEnumerable<OutboundDiagnosticRow> rows, DateTimeOffset? completedAt, string diagnostic)
    {
        if (settings is not null) OutboundSettings.Validate(settings);
        Status = status;
        Settings = settings is null ? null : settings with { Sources = Array.AsReadOnly(settings.Sources.ToArray()) };
        Rows = Array.AsReadOnly(rows.ToArray());
        if (status == OutboundDiagnosticRunStatus.Completed && (Settings is null || Rows.Count == 0 || completedAt is null))
            throw new ArgumentException("完整测速必须包含已验证配置、实际域名结果和完成时间。");
        if (status != OutboundDiagnosticRunStatus.Completed && completedAt is not null)
            throw new ArgumentException("未完成测速不能携带完成时间。");
        CompletedAt = completedAt;
        Diagnostic = diagnostic;
    }

    public OutboundDiagnosticRunStatus Status { get; }
    public OutboundSettings? Settings { get; }
    public IReadOnlyList<OutboundDiagnosticRow> Rows { get; }
    public DateTimeOffset? CompletedAt { get; }
    public string Diagnostic { get; }
}

public sealed record OutboundOperationResult(bool Succeeded, string Diagnostic);

public interface IOutboundDirectService
{
    OutboundSettings Settings { get; }
    OutboundDirectSnapshot Snapshot { get; }
    event EventHandler<OutboundDirectSnapshot>? Changed;
    OutboundSettings ReadSettings();
    Task<OutboundOperationResult> SaveAsync(OutboundSettings settings, CancellationToken cancellationToken = default);
    Task<OutboundDirectSnapshot> RefreshAsync(CancellationToken cancellationToken = default);
    Task<OutboundDiagnosticRun> DiagnoseAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads Node's public status and authenticates its private loopback helper session.
/// Does not start Node/helper, read core secrets, or poll in the background.
/// </summary>
public sealed class OutboundDirectService : IOutboundDirectService, IDisposable
{
    private static readonly TimeSpan HealthDeadline = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan QueryDeadline = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan OverallDeadline = TimeSpan.FromSeconds(30);
    private const int MaxBytes = 1_048_576;
    private static readonly Regex EndpointPattern = new(@"^http://127\.0\.0\.1:([0-9]{1,5})/?$", RegexOptions.CultureInvariant);
    private static readonly Regex HexToken = new(@"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant);
    private static readonly Regex TokenInText = new(@"[0-9a-fA-F]{64}", RegexOptions.CultureInvariant);
    private static readonly Regex UrlInText = new(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private const string SecretFieldName = @"(?:[a-z0-9_-]*(?:token|cookies?|api[_-]?key|password|passwd|secret|authorization|credentials?)[a-z0-9_-]*|[a-z0-9_-]*[_-]key|key)";
    private static readonly Regex QuotedSecretInText = new(
        $"""(?<![a-z0-9_-])["']?{SecretFieldName}["']?\s*[:=]\s*(?:"(?:\\.|[^"\\])*"?|'(?:\\.|[^'\\])*'?)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CredentialHeaderInText = new(
        @"(?<![a-z0-9_-])(?:[a-z0-9_-]*cookies?[a-z0-9_-]*|[a-z0-9_-]*authorization)\s*[:=]\s*[^\r\n]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SecretInText = new(
        $"""(?<![a-z0-9_-])["']?{SecretFieldName}["']?\s*[:=]\s*(?:(?:Basic|Bearer)\s+)?[^\s,;\x7d\]]+|\b(?:Basic|Bearer)\s+[^\s,;\x7d\]]+""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Probe[] Probes =
    [
        new("bahamut", "api.gamer.com.tw", true, "/mobile_app/anime/v1/search.php?kw=%E8%91%AC%E9%80%81%E7%9A%84%E8%8A%99%E8%8E%89%E8%93%AE"),
        new("tmdb", "api.tmdb.org", false, "/3/configuration"),
        new("tmdb", "api.themoviedb.org", false, "/3/configuration"),
        new("dandan", "api.danmaku.weeblify.app", true, "/ddp/v1?path=/v2/search/anime?keyword=%E8%91%AC%E9%80%81%E7%9A%84%E8%8A%99%E8%8E%89%E8%8E%B2"),
        new("dandan", "nipaplay.aimes-soft.com", false, "/"),
        new("animeko", "api.animeko.org", false, "/v2/subjects/400602"),
        new("animeko", "danmaku-global.myani.org", true, "/v2/subjects/400602"),
        new("animeko", "danmaku-cn.myani.org", false, "/v2/subjects/400602"),
        new("animeko", "s1.animeko.openani.org", false, "/v2/subjects/400602"),
        new("animeko", "api.bangumi.vip", true, "/v0/episodes?subject_id=400602&limit=1"),
    ];
    private readonly IOutboundSettingsStore _store;
    private readonly IRuntimeController _runtime;
    private readonly IAppDiagnostics _diagnostics;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<int, bool> _processAlive;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly object _publishGate = new();
    private OutboundDirectSnapshot _snapshot = new("off", "尚未刷新增强直连状态。");
    private int _disposed;
    private string? _lastFailure;
    private RuntimeSnapshot _observedRuntime;
    private long _runtimeEpoch;
    private long _revision;

    // Injected clients must also disable proxies and redirects; production normally uses the owned client.
    public OutboundDirectService(
        IOutboundSettingsStore store,
        IRuntimeController runtime,
        IAppDiagnostics diagnostics,
        HttpClient? httpClient = null,
        Func<int, bool>? processAlive = null,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient(new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _processAlive = processAlive ?? IsProcessAlive;
        _clock = timeProvider ?? TimeProvider.System;
        _lifetimeToken = _lifetime.Token;
        _observedRuntime = _runtime.Snapshot;
        _runtime.SnapshotChanged += OnRuntimeChanged;
    }

    public OutboundSettings Settings => ReadSettings();
    public OutboundDirectSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public event EventHandler<OutboundDirectSnapshot>? Changed;

    public OutboundSettings ReadSettings()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        try
        {
            var settings = _store.Read();
            OutboundSettings.Validate(settings);
            return settings;
        }
        catch (Exception error)
        {
            var reason = SafeError("读取增强直连配置失败", error);
            _diagnostics.Record(reason);
            Publish(new("failed", reason, ServiceRunning: _runtime.Snapshot.State == DesktopRuntimeState.Running));
            // Re-throw a safe diagnostic, never an input document or HttpClient URL/credential.
            throw new IOException(reason);
        }
    }

    public async Task<OutboundOperationResult> SaveAsync(OutboundSettings settings, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
        var entered = false;
        var written = false;
        try
        {
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            linked.Token.ThrowIfCancellationRequested();
            OutboundSettings.Validate(settings);
            var expected = settings with { Sources = Array.AsReadOnly(settings.Sources.ToArray()) };
            _store.Write(expected);
            written = true;
            var actual = _store.Read();
            OutboundSettings.Validate(actual);
            if (!OutboundSettings.Equivalent(expected, actual)) throw new IOException("增强直连配置回读校验失败。");
            var running = _runtime.Snapshot.State == DesktopRuntimeState.Running;
            var message = running
                ? "增强直连配置已保存；Node 将重新加载辅助进程，请刷新确认应用状态。"
                : "增强直连配置已保存；弹幕服务未运行，启动服务后应用。";
            Publish(new(running && actual.Enabled ? "starting" : "off", message, actual,
                NodePid: _runtime.Snapshot.Pid, ServiceRunning: running));
            return new(true, message);
        }
        catch (OperationCanceledException)
        {
            var reason = written ? "增强直连配置已写入，但回读校验已取消；请重新读取确认保存结果。"
                : "保存增强直连配置已取消，未执行写入。";
            _diagnostics.Record(reason);
            Publish(new("failed", reason, ServiceRunning: _runtime.Snapshot.State == DesktopRuntimeState.Running));
            return new(false, reason);
        }
        catch (Exception error)
        {
            var reason = SafeError("保存增强直连配置失败", error);
            _diagnostics.Record(reason);
            Publish(new("failed", reason, ServiceRunning: _runtime.Snapshot.State == DesktopRuntimeState.Running));
            return new(false, reason);
        }
        finally { if (entered) _gate.Release(); }
    }

    public Task<OutboundDirectSnapshot> RefreshStatusAsync(CancellationToken cancellationToken = default) => RefreshAsync(cancellationToken);

    public async Task<OutboundDirectSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
        var entered = false;
        var epoch = Volatile.Read(ref _runtimeEpoch);
        try
        {
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            epoch = Volatile.Read(ref _runtimeEpoch);
            var result = await RefreshCoreAsync(linked.Token).ConfigureAwait(false);
            return Publish(result.Snapshot, result.RuntimeEpoch);
        }
        catch (OperationCanceledException)
        {
            var cancelled = new OutboundDirectSnapshot("failed", "增强直连状态刷新已取消。",
                ServiceRunning: _runtime.Snapshot.State == DesktopRuntimeState.Running);
            _diagnostics.Record(cancelled.Reason);
            return Publish(cancelled, epoch);
        }
        finally { if (entered) _gate.Release(); }
    }

    private async Task<Verification> RefreshCoreAsync(CancellationToken cancellationToken, OutboundSettings? expectedSettings = null)
    {
        OutboundSettings? settings = null;
        RuntimeSnapshot runtime;
        long epoch;
        lock (_publishGate) { runtime = _runtime.Snapshot; epoch = _runtimeEpoch; }
        var applied = false;
        Verification Result(OutboundDirectSnapshot snapshot, Session? session, string? phase = null) =>
            new(snapshot, session, epoch, phase);
        try
        {
            // Running is the first gate; stale files can never make a stopped service appear ready.
            var candidate = _store.Read();
            OutboundSettings.Validate(candidate);
            settings = candidate with { Sources = Array.AsReadOnly(candidate.Sources.ToArray()) };
            if (expectedSettings is not null && !OutboundSettings.Equivalent(settings, expectedSettings))
                throw new VerificationException("config", "测速期间增强配置已改变，本次结果不属于同一配置。");
            if (runtime.State != DesktopRuntimeState.Running)
                return Result(new("off", "弹幕服务未运行，增强直连未运行。", settings), null);
            RequireRuntime(runtime);
            var statusPath = Path.Combine(_store.DirectoryPath, "status.json");
            JsonDocument statusDocument;
            try { statusDocument = await ReadFileAsync(statusPath, cancellationToken).ConfigureAwait(false); }
            catch (FileNotFoundException) { return Unapplied(settings, runtime, epoch); }
            catch (DirectoryNotFoundException) { return Unapplied(settings, runtime, epoch); }
            using var statusOwner = statusDocument;
            var status = ParseStatus(statusDocument.RootElement);
            if (status.NodePid != runtime.Pid || status.Identity != runtime.RuntimeIdentity)
                throw new VerificationException("identity", "增强状态不属于当前 Node 实例（PID 或运行身份不匹配）。");
            if (!OutboundSettings.Equivalent(settings, status.Config))
                throw new VerificationException("config", "当前实例尚未应用保存的增强配置，请刷新确认；旧宿主需要重启服务。");
            ValidateHeartbeat(status.Heartbeat);
            if (!_processAlive(checked((int)status.NodePid)))
                throw new VerificationException("process", "当前 Node 进程已退出；不能报告增强直连正在运行。");
            RequireSameRuntime(runtime, epoch);
            applied = true;
            if (status.State != "ready")
            {
                if (status.State == "off" && status.HelperPid is not null)
                    throw new VerificationException("status", "关闭状态仍携带 helper PID，状态文件不一致。");
                var reason = status.Reason.Length == 0 ? status.State switch
                {
                    "off" => "增强直连已关闭。",
                    "starting" => "增强直连辅助进程正在启动。",
                    _ => "增强直连辅助进程失败，Node 未提供失败原因。",
                } : SafeText(status.Reason);
                if (status.State == "failed") RecordFailure(reason);
                return Result(new(status.State, reason, settings, status.NodePid, status.HelperPid,
                    ServiceRunning: true, Applied: true, RuntimeIdentity: runtime.RuntimeIdentity), null);
            }
            if (!settings.Enabled) throw new VerificationException("config", "增强配置已关闭但 Node 报告 ready，状态文件不一致。");
            if (status.HelperPid is null) throw new VerificationException("status", "ready 状态缺少 helper PID。");
            using var sessionDocument = await ReadFileAsync(Path.Combine(_store.DirectoryPath, "session.json"), cancellationToken).ConfigureAwait(false);
            var session = ParseSession(sessionDocument.RootElement);
            session.RuntimeEpoch = epoch;
            if (session.NodePid != status.NodePid || session.HelperPid != status.HelperPid || session.Identity != status.Identity)
                throw new VerificationException("identity", "辅助进程会话不属于当前 Node 状态（PID 或运行身份不匹配）。");
            ValidateLiveSession(session, runtime);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(HealthDeadline);
            using var request = AuthenticatedRequest(HttpMethod.Get, session, "/health");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            CheckResponseTarget(response, request.RequestUri!);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new VerificationException(response.StatusCode == HttpStatusCode.Unauthorized ? "auth" : "health",
                    $"辅助进程健康接口返回 HTTP {(int)response.StatusCode}，认证或会话验证失败。");
            using var health = await ReadResponseAsync(response, deadline.Token).ConfigureAwait(false);
            var root = Object(health.RootElement, "helper 健康响应");
            if (!Boolean(root, "success"))
                throw new VerificationException("health", "辅助进程健康响应 success=false。");
            if (Integer(root, "protocolVersion") != 1)
                throw new VerificationException("protocol", "辅助进程 protocolVersion 不受支持；要求版本 1。");
            if (Integer(root, "pid") != session.HelperPid)
                throw new VerificationException("identity", "辅助进程健康响应 PID 与会话不匹配，可能是过期或复用的端口。");
            var version = Text(root, "version");
            if (string.IsNullOrWhiteSpace(version) || version.Length > 128 || version.Any(char.IsControl))
                throw new VerificationException("protocol", "辅助进程健康响应缺少有效版本。");
            // File snapshots may change during the HTTP request (Node restarts its helper on saves).
            var persisted = _store.Read();
            OutboundSettings.Validate(persisted);
            if (!OutboundSettings.Equivalent(settings, persisted))
                throw new VerificationException("config", "健康验证期间增强配置已改变，请刷新确认新配置应用状态。");
            using var latestDocument = await ReadFileAsync(statusPath, cancellationToken).ConfigureAwait(false);
            var latest = ParseStatus(latestDocument.RootElement);
            if (latest.State != "ready" || latest.NodePid != status.NodePid || latest.HelperPid != status.HelperPid ||
                latest.Identity != status.Identity || !OutboundSettings.Equivalent(settings, latest.Config))
                throw new VerificationException("session", "健康验证期间辅助进程状态或配置已改变，请重新刷新。");
            ValidateHeartbeat(latest.Heartbeat);
            using var latestSessionDocument = await ReadFileAsync(Path.Combine(_store.DirectoryPath, "session.json"), cancellationToken).ConfigureAwait(false);
            var latestSession = ParseSession(latestSessionDocument.RootElement);
            if (latestSession.Token != session.Token || latestSession.Endpoint != session.Endpoint || latestSession.NodePid != session.NodePid ||
                latestSession.HelperPid != session.HelperPid || latestSession.Identity != session.Identity)
                throw new VerificationException("session", "健康验证期间辅助进程会话已改变，拒绝使用旧会话。");
            ValidateLiveSession(session, runtime);
            var readyReason = "增强直连辅助进程已通过身份、配置、心跳及协议验证。";
            if (latest.Reason.Length != 0) readyReason += " " + SafeText(latest.Reason, session.Token);
            return Result(new("ready", readyReason, settings,
                status.NodePid, status.HelperPid, SafeText(version, session.Token), true, true, 1, runtime.RuntimeIdentity), session);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed("辅助进程健康验证超时（3 秒）。", settings, runtime, applied, "timeout", epoch);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return Failed(SafeError("增强直连状态验证失败", error), settings, runtime, applied,
                error is VerificationException verification ? verification.Phase : "status", epoch);
        }
    }

    public Task<OutboundDiagnosticRun> RunDiagnosticsAsync(CancellationToken cancellationToken = default) => DiagnoseAsync(cancellationToken);

    public async Task<OutboundDiagnosticRun> DiagnoseAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        overall.CancelAfter(OverallDeadline);
        var entered = false;
        OutboundSettings? settings = null;
        var actualRows = new System.Collections.Concurrent.ConcurrentDictionary<string, OutboundDiagnosticRow>(StringComparer.Ordinal);
        OutboundDiagnosticRow[] Rows() => Probes.Where(probe => actualRows.ContainsKey(probe.Host))
            .Select(probe => actualRows[probe.Host]).ToArray();
        OutboundDiagnosticRun Incomplete(OutboundDiagnosticRunStatus status, string reason)
        {
            _diagnostics.Record(reason);
            return new(status, settings, Rows(), null, reason);
        }
        try
        {
            await _gate.WaitAsync(overall.Token).ConfigureAwait(false);
            entered = true;
            var verification = await RefreshCoreAsync(overall.Token).ConfigureAwait(false);
            var published = Publish(verification.Snapshot, verification.RuntimeEpoch);
            // Settings only becomes the run's configuration after full session/health prevalidation.
            if (verification.Session is null || published.Status != "ready" || published.RuntimeEpoch != verification.RuntimeEpoch)
                return Incomplete(OutboundDiagnosticRunStatus.Failed, verification.Snapshot.Reason);
            settings = verification.Snapshot.Settings!;
            var selected = Probes.Where(probe => settings.Sources.Contains(probe.Source, StringComparer.Ordinal)).ToArray();
            if (selected.Length == 0)
                return Incomplete(OutboundDiagnosticRunStatus.Failed, "增强直连诊断未执行：没有选择来源。");
            // Independent exact hosts: only rows whose requests actually started enter the envelope.
            await Task.WhenAll(selected.Select(async probe =>
            {
                var row = await ProbeAsync(probe, verification.Session, settings, overall.Token, lifetime.Token).ConfigureAwait(false);
                actualRows[probe.Host] = row;
                if (!row.Succeeded)
                    _diagnostics.Record($"增强直连诊断 {row.Source}/{row.Host} [{row.FailurePhase}]：{row.Diagnostic}");
            })).ConfigureAwait(false);
            overall.Token.ThrowIfCancellationRequested();
            // A full revalidation binds the whole measurement to the original configuration AND session.
            var latest = await RefreshCoreAsync(overall.Token, settings).ConfigureAwait(false);
            var final = Publish(latest.Snapshot, latest.RuntimeEpoch);
            if (latest.Session is null || latest.RuntimeEpoch != verification.RuntimeEpoch ||
                !SameSession(verification.Session, latest.Session) || final.Status != "ready")
                return Incomplete(OutboundDiagnosticRunStatus.Failed,
                    "测速后验证未通过，拒绝将本次记录为完整测速。 " + latest.Snapshot.Reason);
            lock (_publishGate)
            {
                overall.Token.ThrowIfCancellationRequested();
                RequireSameRuntime(_runtime.Snapshot, verification.RuntimeEpoch);
                return new(OutboundDiagnosticRunStatus.Completed, settings, Rows(), _clock.GetUtcNow(), "逐域名测速及配置、会话复核已完成。");
            }
        }
        catch (OperationCanceledException)
        {
            return Incomplete(lifetime.IsCancellationRequested ? OutboundDiagnosticRunStatus.Cancelled : OutboundDiagnosticRunStatus.Failed,
                lifetime.IsCancellationRequested ? "增强直连诊断已取消。" : "增强直连诊断超过 30 秒总限时。");
        }
        catch (Exception error)
        {
            return Incomplete(OutboundDiagnosticRunStatus.Failed, SafeError("增强直连诊断失败", error));
        }
        finally { if (entered) _gate.Release(); }
    }

    private async Task<OutboundDiagnosticRow> ProbeAsync(Probe probe, Session session, OutboundSettings settings,
        CancellationToken overall, CancellationToken userCancellation)
    {
        var started = _clock.GetTimestamp();
        string? protocol = null;
        bool? ech = null;
        int? httpStatus = null;
        OutboundDiagnosticRow Row(string? phase, string diagnostic) => new(probe.Source, probe.Host, probe.EchRequired,
            protocol, ech, httpStatus, _clock.GetElapsedTime(started), phase, SafeText(diagnostic, session.Token));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(overall);
        deadline.CancelAfter(QueryDeadline);
        deadline.Token.ThrowIfCancellationRequested();
        ValidateLiveSession(session, _runtime.Snapshot);
        var currentSettings = _store.Read();
        OutboundSettings.Validate(currentSettings);
        if (!OutboundSettings.Equivalent(settings, currentSettings))
            throw new VerificationException("config", "发起域名请求前增强配置已改变，拒绝混用测速配置。");
        try
        {
            using var request = AuthenticatedRequest(HttpMethod.Post, session, "/request");
            // URL is built solely from this private allowlist; no caller can supply credentials or query parameters.
            var payload = new
            {
                url = $"https://{probe.Host}{probe.Path}", method = "GET",
                headers = new[] { new[] { "User-Agent", "DanmuApi.Windows/connectivity" }, new[] { "Accept", "application/json" } },
                body = "", timeoutMs = (int)QueryDeadline.TotalMilliseconds,
            };
            request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            CheckResponseTarget(response, request.RequestUri!);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return Row("auth", "辅助进程返回 HTTP 401，会话认证失败。");
            if ((int)response.StatusCode is >= 300 and < 400)
                return Row("redirect", $"辅助进程返回 HTTP {(int)response.StatusCode}，拒绝跟随重定向。");
            using var document = await ReadResponseAsync(response, deadline.Token).ConfigureAwait(false);
            var root = Object(document.RootElement, "helper 请求响应");
            if (root.TryGetProperty("host", out _) && Text(root, "host") != probe.Host)
                throw new VerificationException("identity", "helper 响应域名与精确探测目标不匹配。");
            if (!Boolean(root, "success"))
            {
                // A failed body read may still carry the actual established transport and HTTP status.
                if (root.TryGetProperty("protocol", out _))
                {
                    var actual = Text(root, "protocol");
                    if (actual is not ("" or "h2" or "h3" or "http/1.1"))
                        throw new VerificationException("protocol", "helper 失败响应包含未知实际协议。");
                    protocol = actual.Length == 0 ? null : actual;
                }
                if (root.TryGetProperty("ech", out _)) ech = Boolean(root, "ech");
                if (root.TryGetProperty("status", out _))
                {
                    var actual = Integer(root, "status");
                    if (actual != 0 && actual is < 100 or > 599)
                        throw new VerificationException("document", "helper 失败响应包含无效 HTTP 状态。");
                    httpStatus = actual == 0 ? null : (int)actual;
                }
                var phase = OptionalText(root, "phase") ?? "request";
                var code = OptionalText(root, "errorCode") ?? OptionalText(root, "code");
                var message = OptionalText(root, "message") ?? OptionalText(root, "error");
                if (string.IsNullOrWhiteSpace(message) || string.IsNullOrWhiteSpace(phase))
                    throw new VerificationException("document", "helper 失败响应缺少错误原因或阶段。");
                return Row(SafeText(phase, session.Token), $"helper HTTP {(int)response.StatusCode}；{code}；{message}");
            }
            if (response.StatusCode != HttpStatusCode.OK)
                return Row("rpc", $"helper 返回 HTTP {(int)response.StatusCode}，与成功响应不一致。");
            protocol = Text(root, "protocol");
            if (protocol is not ("h2" or "h3" or "http/1.1"))
                throw new VerificationException("protocol", "helper 返回未知实际传输协议。");
            ech = Boolean(root, "ech");
            var status = Integer(root, "status");
            if (status is < 100 or > 599) throw new VerificationException("document", "helper HTTP 状态不在 100 到 599 范围。");
            httpStatus = (int)status;
            ValidateResponseHeaders(root);
            var encoded = Text(root, "body");
            if (encoded.Length > ((MaxBytes + 2) / 3) * 4) throw new VerificationException("body", "源站响应体超过 1 MiB。");
            byte[] body;
            try { body = Convert.FromBase64String(encoded); }
            catch (FormatException) { throw new VerificationException("document", "helper body 不是有效 Base64。"); }
            if (body.Length > MaxBytes) throw new VerificationException("body", "源站响应体超过 1 MiB。");
            ValidateLiveSession(session, _runtime.Snapshot);
            if (probe.EchRequired && !ech.Value)
                return Row("ech", $"HTTP {httpStatus}，但源站要求 ECH，当前连接没有接受 ECH。");
            if (!probe.EchRequired && ech.Value)
                return Row("ech", "普通 TLS 域名意外返回 ECH=true，helper 策略或响应不一致。");
            if (settings.HttpVersion == "h3" && protocol != "h3")
                return Row("protocol", $"要求 HTTP/3，实际协议为 {protocol}，拒绝将其记为成功。");
            if (settings.HttpVersion == "h2" && protocol != "h2")
                return Row("protocol", $"要求 HTTP/2，实际协议为 {protocol}，拒绝将其记为成功。");
            var transport = probe.EchRequired ? "ECH 已接受" : "普通 TLS，此域名不使用 ECH";
            if (httpStatus is < 200 or >= 300)
            {
                var reason = probe.Source == "tmdb" && httpStatus == 401
                    ? "HTTP 401：连接已建立；公开无凭据探测未获得业务授权。"
                    : $"HTTP {httpStatus}：连接已建立，源站业务响应未通过；不跟随源站重定向。";
                return Row("http", $"{reason}实际协议 {protocol}；{transport}。");
            }
            // Check public business response shapes without exposing response bodies to UI or logs.
            if (probe.Path != "/")
            {
                using var business = ParseDocument(body);
                if (!ValidBusiness(probe.Source, business.RootElement))
                    return Row("response", $"HTTP {httpStatus}，实际协议 {protocol}；{transport}；公开探测响应结构不符合预期。");
            }
            return Row(null, $"HTTP {httpStatus}；实际协议 {protocol}；{transport}。" +
                (probe.Path == "/" ? " 此公开根路径仅验证连通性。" : " 公开探测响应已验证。"));
        }
        catch (OperationCanceledException)
        {
            return Row(userCancellation.IsCancellationRequested ? "cancelled" : "timeout",
                userCancellation.IsCancellationRequested ? "该域名诊断已取消。" : "该域名诊断超时（单请求 15 秒，总限时 30 秒）。");
        }
        catch (Exception error)
        {
            return Row(error is VerificationException verification ? verification.Phase :
                error is HttpRequestException ? "connection" : "document", SafeError("该域名诊断失败", error, session.Token));
        }
    }

    private static bool ValidBusiness(string source, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        return source switch
        {
            "bahamut" => root.TryGetProperty("anime", out var anime) && anime.ValueKind == JsonValueKind.Array,
            "dandan" => root.TryGetProperty("animes", out var animes) && animes.ValueKind == JsonValueKind.Array,
            "animeko" => (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var number) && number > 0) ||
                (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array),
            "tmdb" => root.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Object,
            _ => false,
        };
    }

    private Verification Failed(string reason, OutboundSettings? settings, RuntimeSnapshot runtime, bool applied, string phase, long epoch)
    {
        var safe = SafeText(reason);
        RecordFailure(safe);
        if (_runtime.Snapshot.State != DesktopRuntimeState.Running)
            return new(new("off", "弹幕服务未运行，增强直连未运行。 " + safe, settings), null, epoch, phase);
        return new(new("failed", safe, settings, runtime.Pid, ServiceRunning: runtime.State == DesktopRuntimeState.Running,
            Applied: applied, RuntimeIdentity: runtime.RuntimeIdentity), null, epoch, phase);
    }

    private Verification Unapplied(OutboundSettings settings, RuntimeSnapshot runtime, long epoch) =>
        Failed("当前实例尚未应用增强，重启服务。", settings, runtime, false, "unapplied", epoch);

    private void ValidateHeartbeat(long heartbeat)
    {
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (heartbeat <= 0 || heartbeat > now || now - heartbeat > 15000)
            throw new VerificationException("heartbeat", "增强状态心跳已过期或时间无效（最多 15 秒）。");
    }

    private static void RequireRuntime(RuntimeSnapshot runtime)
    {
        if (runtime.State != DesktopRuntimeState.Running || runtime.Pid is null or <= 0 or > int.MaxValue || string.IsNullOrWhiteSpace(runtime.RuntimeIdentity))
            throw new VerificationException("identity", "运行状态缺少有效 Node PID 或运行身份。");
    }

    private void RequireSameRuntime(RuntimeSnapshot expected, long epoch)
    {
        var current = _runtime.Snapshot;
        RequireRuntime(current);
        if (Volatile.Read(ref _runtimeEpoch) != epoch || current.Pid != expected.Pid || current.RuntimeIdentity != expected.RuntimeIdentity)
            throw new VerificationException("identity", "验证期间 Node 实例已改变，请重新刷新状态。");
    }

    private void ValidateLiveSession(Session session, RuntimeSnapshot expected)
    {
        var current = _runtime.Snapshot;
        RequireRuntime(current);
        if (Volatile.Read(ref _runtimeEpoch) != session.RuntimeEpoch || current.Pid != expected.Pid || current.RuntimeIdentity != expected.RuntimeIdentity ||
            current.Pid != session.NodePid || current.RuntimeIdentity != session.Identity)
            throw new VerificationException("identity", "诊断期间 Node 实例已改变；拒绝使用旧辅助进程会话。");
        if (!_processAlive(checked((int)session.NodePid)) || !_processAlive(session.HelperPid))
            throw new VerificationException("process", "Node 或辅助进程已退出；拒绝使用过期会话。");
    }

    private static bool IsProcessAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; } // Explicit failed state is produced by the caller.
    }

    private static HttpRequestMessage AuthenticatedRequest(HttpMethod method, Session session, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(session.Endpoint, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        return request;
    }

    private static void CheckResponseTarget(HttpResponseMessage response, Uri expected)
    {
        if (response.RequestMessage?.RequestUri is { } actual && actual != expected)
            throw new VerificationException("redirect", "辅助进程请求目标发生变化；拒绝重定向响应。");
    }

    private static async Task<JsonDocument> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            8192, FileOptions.Asynchronous);
        return ParseDocument(await ReadBoundedAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<JsonDocument> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxBytes)
            throw new VerificationException("body", "helper JSON 响应超过 1 MiB。");
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return ParseDocument(await ReadBoundedAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (result.Length + count > MaxBytes) throw new VerificationException("body", "JSON 或响应体超过 1 MiB。");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }

    private static JsonDocument ParseDocument(byte[] bytes)
    {
        try { return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException error)
        {
            throw new VerificationException("document", $"不是有效 JSON（行 {error.LineNumber}，字节 {error.BytePositionInLine}）。");
        }
    }

    private static JsonElement Object(JsonElement value, string label)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new VerificationException("document", $"{label}必须是对象。");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name)) throw new VerificationException("document", $"{label}不允许重复字段。");
        return value;
    }

    private static void RejectUnknownFields(JsonElement root, params string[] allowed)
    {
        foreach (var field in root.EnumerateObject())
            if (!allowed.Contains(field.Name, StringComparer.Ordinal))
                throw new VerificationException("document", "状态或会话包含未知字段。");
    }

    private static JsonElement Field(JsonElement root, string name, JsonValueKind kind)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != kind)
            throw new VerificationException("document", $"{name} 缺失或类型错误。");
        return value;
    }
    private static string Text(JsonElement root, string name) => Field(root, name, JsonValueKind.String).GetString()!;
    private static string? OptionalText(JsonElement root, string name) => root.TryGetProperty(name, out _) ? Text(root, name) : null;
    private static long Integer(JsonElement root, string name)
    {
        if (!Field(root, name, JsonValueKind.Number).TryGetInt64(out var result))
            throw new VerificationException("document", $"{name} 必须是整数。");
        return result;
    }
    private static bool Boolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new VerificationException("document", $"{name} 缺失或类型错误。");
        return value.GetBoolean();
    }
    private static int Pid(JsonElement root, string name)
    {
        var value = Integer(root, name);
        if (value is <= 0 or > int.MaxValue) throw new VerificationException("identity", $"{name} 不是有效 PID。");
        return (int)value;
    }

    private static NodeStatus ParseStatus(JsonElement document)
    {
        var root = Object(document, "Node 增强状态");
        RejectUnknownFields(root, "schemaVersion", "status", "reason", "nodePid", "helperPid", "runtimeIdentity", "heartbeatUnixMs", "config");
        if (Integer(root, "schemaVersion") != 1) throw new VerificationException("protocol", "Node 增强状态 schemaVersion 不受支持。");
        var state = Text(root, "status");
        if (state is not ("off" or "starting" or "ready" or "failed")) throw new VerificationException("status", "Node 增强状态未知。");
        if (!root.TryGetProperty("helperPid", out var helper)) throw new VerificationException("document", "helperPid 缺失。");
        int? helperPid = helper.ValueKind == JsonValueKind.Null ? null : Pid(root, "helperPid");
        var identity = Text(root, "runtimeIdentity");
        if (string.IsNullOrWhiteSpace(identity)) throw new VerificationException("identity", "Node 增强状态缺少运行身份。");
        return new(state, Text(root, "reason"), Pid(root, "nodePid"), helperPid, identity,
            Integer(root, "heartbeatUnixMs"), OutboundSettings.Parse(Field(root, "config", JsonValueKind.Object)));
    }

    private static Session ParseSession(JsonElement document)
    {
        var root = Object(document, "helper 会话");
        RejectUnknownFields(root, "schemaVersion", "endpoint", "token", "nodePid", "helperPid", "runtimeIdentity");
        if (Integer(root, "schemaVersion") != 1) throw new VerificationException("protocol", "helper 会话 schemaVersion 不受支持。");
        var endpoint = Text(root, "endpoint");
        if (!EndpointPattern.IsMatch(endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Port is < 1 or > 65535 || uri.Host != "127.0.0.1" || uri.AbsolutePath != "/" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new VerificationException("endpoint", "helper endpoint 必须为 http://127.0.0.1:端口，不允许凭据、查询、片段或额外路径。");
        var token = Text(root, "token");
        if (!HexToken.IsMatch(token)) throw new VerificationException("auth", "helper 会话 token 必须是 64 位十六进制值。");
        var nodePid = Pid(root, "nodePid");
        var helperPid = Pid(root, "helperPid");
        if (nodePid == helperPid) throw new VerificationException("identity", "Node 与 helper PID 不允许相同。");
        return new(uri, token, nodePid, helperPid, Text(root, "runtimeIdentity"));
    }

    private static void ValidateResponseHeaders(JsonElement root)
    {
        foreach (var pair in Field(root, "headers", JsonValueKind.Array).EnumerateArray())
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2 ||
                pair[0].ValueKind != JsonValueKind.String || pair[1].ValueKind != JsonValueKind.String)
                throw new VerificationException("document", "helper headers 必须是字符串二元数组。");
        }
    }

    private static bool SameSession(Session left, Session right) => left.Token == right.Token &&
        left.Endpoint == right.Endpoint && left.NodePid == right.NodePid && left.HelperPid == right.HelperPid &&
        left.Identity == right.Identity && left.RuntimeEpoch == right.RuntimeEpoch;

    private static string SafeError(string operation, Exception error, string? token = null)
    {
        var messages = new List<string>();
        for (Exception? current = error; current is not null && messages.Count < 5; current = current.InnerException)
            messages.Add($"{current.GetType().Name}：{current.Message}");
        return SafeText($"{operation}：{string.Join(" → ", messages)}", token);
    }

    private static string SafeText(string text, string? token = null)
    {
        if (token is not null) text = text.Replace(token, "[已脱敏]", StringComparison.OrdinalIgnoreCase);
        text = TokenInText.Replace(text, "[已脱敏]");
        text = UrlInText.Replace(text, match =>
        {
            // Retain the host for DNS/TLS diagnosis, never an arbitrary URL's path/query/userinfo.
            return Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)
                ? $"{uri.Scheme}://{uri.Host}/[地址已脱敏]" : "[地址已脱敏]";
        });
        // Consume whole quoted values (including spaces/escaped quotes) before unquoted headers.
        // Headers can contain multiple cookies or an authentication scheme plus value; redact the full line.
        text = QuotedSecretInText.Replace(text, "[凭据已脱敏]");
        text = CredentialHeaderInText.Replace(text, "[凭据已脱敏]");
        text = SecretInText.Replace(text, "[凭据已脱敏]");
        text = new string(text.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        return text.Length <= 4096 ? text : text[..4096] + "…（诊断已截断）";
    }

    private void OnRuntimeChanged(object? sender, RuntimeSnapshot runtime)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_publishGate)
        {
            if (_observedRuntime.State == runtime.State && _observedRuntime.Pid == runtime.Pid &&
                _observedRuntime.RuntimeIdentity == runtime.RuntimeIdentity) return;
            _observedRuntime = runtime;
            _runtimeEpoch++;
            if (runtime.State != DesktopRuntimeState.Running)
            {
                Publish(new("off", "弹幕服务未运行，增强直连未运行。", Snapshot.Settings));
                return;
            }
            Publish(new("starting", "正在验证当前 Node 的增强直连状态。", Snapshot.Settings,
                runtime.Pid, ServiceRunning: true, RuntimeIdentity: runtime.RuntimeIdentity));
        }
        _ = RefreshAfterRuntimeChangeAsync();
    }

    private async Task RefreshAfterRuntimeChangeAsync()
    {
        try { await RefreshAsync(_lifetimeToken).ConfigureAwait(false); }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
            RecordFailure("增强直连运行状态刷新已因服务对象释放而终止。");
        }
        catch (OperationCanceledException)
        {
            RecordFailure("增强直连运行状态刷新已取消。");
        }
        catch (Exception error)
        {
            var reason = SafeError("增强直连运行状态刷新失败", error);
            RecordFailure(reason);
            Publish(new("failed", reason, ServiceRunning: _runtime.Snapshot.State == DesktopRuntimeState.Running));
        }
    }

    private void RecordFailure(string reason)
    {
        reason = SafeText(reason);
        if (!string.Equals(Interlocked.Exchange(ref _lastFailure, reason), reason, StringComparison.Ordinal))
            _diagnostics.Record(reason);
    }

    private OutboundDirectSnapshot Publish(OutboundDirectSnapshot snapshot, long? expectedEpoch = null)
    {
        lock (_publishGate)
        {
            if (Volatile.Read(ref _disposed) != 0 || expectedEpoch is not null && expectedEpoch != _runtimeEpoch) return Snapshot;
            var runtime = _runtime.Snapshot;
            if (snapshot.ServiceRunning && runtime.State != DesktopRuntimeState.Running)
                snapshot = new("off", "弹幕服务未运行，增强直连未运行。", snapshot.Settings);
            else if (snapshot.Status == "ready" && (snapshot.NodePid != runtime.Pid || snapshot.RuntimeIdentity != runtime.RuntimeIdentity))
                snapshot = new("failed", "当前 Node 实例已改变，请重新刷新增强状态。", snapshot.Settings,
                    runtime.Pid, ServiceRunning: true, RuntimeIdentity: runtime.RuntimeIdentity);
            snapshot = snapshot with
            {
                RuntimeEpoch = _runtimeEpoch,
                Settings = snapshot.Settings is null ? null : snapshot.Settings with { Sources = Array.AsReadOnly(snapshot.Settings.Sources.ToArray()) },
            };
            var previous = Snapshot;
            if (previous.Status == snapshot.Status && previous.Reason == snapshot.Reason && previous.NodePid == snapshot.NodePid &&
                previous.HelperPid == snapshot.HelperPid && previous.HelperVersion == snapshot.HelperVersion &&
                previous.ServiceRunning == snapshot.ServiceRunning && previous.Applied == snapshot.Applied &&
                previous.ProtocolVersion == snapshot.ProtocolVersion && previous.RuntimeIdentity == snapshot.RuntimeIdentity &&
                previous.RuntimeEpoch == snapshot.RuntimeEpoch &&
                (previous.Settings is null && snapshot.Settings is null || previous.Settings is not null && snapshot.Settings is not null &&
                 OutboundSettings.Equivalent(previous.Settings, snapshot.Settings))) return previous;
            if (snapshot.Status != "failed") Interlocked.Exchange(ref _lastFailure, null);
            snapshot = snapshot with { Revision = ++_revision };
            Volatile.Write(ref _snapshot, snapshot);
            Changed?.Invoke(this, snapshot);
            // Changed handlers may synchronously observe a stop/restart and publish a newer state.
            return Snapshot;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _runtime.SnapshotChanged -= OnRuntimeChanged;
        _lifetime.Cancel();
        if (_ownsHttp) _http.Dispose();
        _lifetime.Dispose();
        // In-flight operations release the gate and linked cancellation sources themselves.
    }

    private sealed record Probe(string Source, string Host, bool EchRequired, string Path);
    private sealed record NodeStatus(string State, string Reason, long NodePid, int? HelperPid, string Identity,
        long Heartbeat, OutboundSettings Config);
    // Keep authentication out of public records, snapshots, exceptions and ToString output.
    private sealed class Session(Uri endpoint, string token, long nodePid, int helperPid, string identity)
    {
        public Uri Endpoint { get; } = endpoint;
        public string Token { get; } = token;
        public long NodePid { get; } = nodePid;
        public int HelperPid { get; } = helperPid;
        public string Identity { get; } = identity;
        public long RuntimeEpoch { get; set; }
    }
    private sealed record Verification(OutboundDirectSnapshot Snapshot, Session? Session, long RuntimeEpoch, string? FailurePhase = null);
    private sealed class VerificationException(string phase, string message) : IOException(message)
    {
        public string Phase { get; } = phase;
    }
}
