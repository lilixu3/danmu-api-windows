using System.Runtime.InteropServices;
using DanmuApi.Core;
using DanmuApi.Core.Frp;
using DanmuApi.Platform;
using DanmuApi.Platform.Frp;
using DanmuApi.Runtime;
using DanmuApi.Runtime.Frp;

namespace DanmuApi.App.Services;

/// <summary>穿透操作的结果。失败必须给出可执行的下一步，而不是一句"失败"。</summary>
public sealed record FrpOperationResult(bool Succeeded, string Message)
{
    public static FrpOperationResult Success(string message) => new(true, message);

    public static FrpOperationResult Failure(string message) => new(false, message);
}

/// <summary>
/// 内网穿透编排：设置读写 → 二进制安装 → 生成 frpc.toml / frps.toml → 监督进程 → 状态对账。
/// 进程监督本身在 <see cref="FrpSupervisor"/>；这里只负责把界面上的配置变成一份可运行的配置，
/// 并把每一步的失败如实带出来。
/// </summary>
public interface IFrpTunnelService : IAsyncDisposable
{
    FrpSettings Settings { get; }

    /// <summary>Saved-source metadata, or the captured source for an active owned process.</summary>
    FrpSettings EffectiveSettings { get; }

    Task<FrpOperationResult> SaveTextAsync(string text, CancellationToken cancellationToken = default);
    Task<FrpOperationResult> ValidateTextAsync(string text, CancellationToken cancellationToken = default);

    IReadOnlyList<string> SettingsProblems { get; }

    bool HasToken { get; }

    /// <summary>读取 frp 的 auth token（明文，仅用于写入配置与导出 JSON；永不进日志）。</summary>
    string ReadAuthToken();

    FrpSnapshot Snapshot { get; }

    string? InstalledVersion { get; }

    /// <summary>宿主是否支持内网穿透（32 位宿主没有可用的 frp 发行包）。</summary>
    bool IsSupportedPlatform { get; }

    /// <summary>不支持时的原因；支持时为空串。</summary>
    string PlatformDiagnostic { get; }

    string BinaryDirectory { get; }

    string ConfigDirectory { get; }

    /// <summary>当前生效的配置文件路径（未生成过时为预期路径）。</summary>
    string ConfigPath { get; }

    /// <summary>状态变化通知；可能来自后台线程，订阅方自行切回 UI 线程。</summary>
    event EventHandler<FrpSnapshot>? Changed;

    /// <summary>弹幕服务是否在运行（穿透启动的前置条件）。</summary>
    bool IsServiceRunning { get; }

    /// <summary>启动状态对账循环。由组合根调用一次。</summary>
    void Start();

    /// <summary>设置"随弹幕服务启动而启动"。立即生效并落盘；开启时若服务正在运行，会当场把穿透拉起来。</summary>
    Task<FrpOperationResult> SetFollowServiceAsync(bool follow, CancellationToken cancellationToken = default);

    Task<FrpOperationResult> ReloadSettingsAsync(CancellationToken cancellationToken = default);

    Task<FrpOperationResult> SaveAsync(
        FrpSettings settings,
        string? newToken,
        bool clearToken,
        CancellationToken cancellationToken = default);

    Task<FrpOperationResult> InstallAsync(
        string version,
        IProgress<GithubDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<FrpOperationResult> StartTunnelAsync(CancellationToken cancellationToken = default);

    Task<FrpOperationResult> StopTunnelAsync(string reason = "user", CancellationToken cancellationToken = default);

    Task<FrpOperationResult> RestartTunnelAsync(CancellationToken cancellationToken = default);

    bool IsShutdownRequested { get; }
    bool HasOwnedProcess { get; }
    /// <summary>同步暂停新启动，排空生命周期/进行中操作，并确认全部受管穿透进程已停止。</summary>
    Task<FrpOperationResult> ShutdownAsync();
    void ResumeAfterFailedShutdown();

    /// <summary>最近一次穿透进程日志尾部（含标准错误），供界面直接显示。</summary>
    string ReadLogTail(int lines = 60);
}

public sealed class FrpTunnelService : IFrpTunnelService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(5);

    private readonly FrpSettingsStore _store;
    private readonly IFrpBinaryInstaller _installer;
    private readonly IFrpSupervisor _supervisor;
    private readonly IFrpNativeVerifier _nativeVerifier;
    private readonly IAppDiagnostics _diagnostics;
    private readonly AppPaths _paths;
    private readonly Func<int> _defaultLocalPort;
    private readonly IRuntimeController _runtime;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _sync = new();
    private Task? _reconcileLoop;
    private Task? _disposeTask;
    private Task<FrpOperationResult>? _shutdownTask;
    private volatile bool _shutdownRequested;
    private long _startRequestEpoch;
    private bool _disposed;

    public bool IsShutdownRequested => _shutdownRequested;
    public bool HasOwnedProcess => _supervisor.HasOwnedProcess;
    private Task _pendingLifecycleTask = Task.CompletedTask;
    private FrpSettings _settings;
    private FrpSettings _effectiveSettings;
    private IReadOnlyList<string> _settingsProblems = [];
    private FrpSnapshot _snapshot = new(FrpTunnelState.Stopped);
    private FrpRunPlan? _lastPlan;

    public FrpTunnelService(
        FrpSettingsStore store,
        IFrpBinaryInstaller installer,
        IFrpSupervisor supervisor,
        AppPaths paths,
        IAppDiagnostics diagnostics,
        IRuntimeController runtime,
        Func<int>? defaultLocalPort = null,
        IFrpNativeVerifier? nativeVerifier = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _nativeVerifier = nativeVerifier ?? new FrpNativeVerifier();
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _defaultLocalPort = defaultLocalPort ?? (() => RuntimeDefaults.Port);
        _supervisor.SnapshotChanged += OnSupervisorSnapshotChanged;
        _runtime.SnapshotChanged += OnRuntimeSnapshotChanged;
        _settings = FrpSettings.Default(RuntimeDefaults.Port);
        _effectiveSettings = _settings;
    }

    public bool IsServiceRunning => _runtime.Snapshot.State == DesktopRuntimeState.Running;

    public FrpSettings Settings
    {
        get
        {
            lock (_sync)
            {
                return _settings;
            }
        }
    }

    public FrpSettings EffectiveSettings
    {
        get
        {
            lock (_sync)
            {
                return _snapshot.RequiresStop && _lastPlan?.CapturedSettings is { } captured
                    ? captured : _effectiveSettings;
            }
        }
    }

    public IReadOnlyList<string> SettingsProblems
    {
        get
        {
            lock (_sync)
            {
                return _settingsProblems;
            }
        }
    }

    public bool HasToken => _store.HasToken();

    public string ReadAuthToken() => _store.ReadToken();

    public FrpSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return _snapshot;
            }
        }
    }

    public string? InstalledVersion => _installer.InstalledVersion;

    public string PlatformDiagnostic
    {
        get
        {
            try
            {
                FrpReleaseCatalog.ResolveArchitecture(RuntimeInformation.ProcessArchitecture);
                return string.Empty;
            }
            catch (NotSupportedException error)
            {
                return error.Message;
            }
        }
    }

    public bool IsSupportedPlatform => PlatformDiagnostic.Length == 0;

    public string BinaryDirectory => _installer.BinaryDirectory;

    public string ConfigDirectory => _paths.FrpConfigDirectory;

    public string ConfigPath
    {
        get
        {
            lock (_sync)
            {
                if (_snapshot.RequiresStop && _lastPlan is not null) return _lastPlan.ConfigPath;
                return ConfigFilePath(_effectiveSettings.Role, _settings.ConfigMode);
            }
        }
    }

    private string ConfigFilePath(FrpRole role, FrpConfigMode mode) => Path.Combine(_paths.FrpConfigDirectory,
        mode == FrpConfigMode.Text ? role == FrpRole.Server ? "frps.json" : "frpc.json"
            : role == FrpRole.Server ? FrpConfigWriter.ServerFileName : FrpConfigWriter.ClientFileName);

    public event EventHandler<FrpSnapshot>? Changed;

    public void Start()
    {
        lock (_sync)
        {
            _reconcileLoop ??= ReconcileLoopAsync(_lifetime.Token);
        }
    }

    public async Task<FrpOperationResult> ReloadSettingsAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return ReloadCore();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<FrpOperationResult> SaveAsync(
        FrpSettings settings,
        string? newToken,
        bool clearToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 「随服务启动」是行为开关，归设置页与监控页的开关管；这里保存的是穿透参数本身。
            // 取开关值时以**磁盘**为准（先重读一遍），否则刚启动还没读过盘、或别处刚改过时，
            // 「保存配置」会顺手把开关改回内存里的旧值。
            var reload = ReloadCore();
            if (!reload.Succeeded)
            {
                return FrpOperationResult.Failure($"保存穿透设置前读取失败：{reload.Message}");
            }

            if (clearToken && newToken is not null)
            {
                return FrpOperationResult.Failure("Token 修改不能同时指定设置与清除。");
            }

            if (newToken is not null && string.IsNullOrWhiteSpace(newToken))
            {
                return FrpOperationResult.Failure("新的 Token 不能为空；保持不变请不传入 Token，清除请明确选择清除。");
            }

            var normalized = settings with
            {
                ConfigMode = FrpConfigMode.Visual,
                RawConfig = Settings.RawConfig,
                FollowService = Settings.FollowService,
                InstalledVersion = Settings.InstalledVersion,
            };
            var problems = normalized.Validate();
            if (problems.Count > 0)
            {
                return FrpOperationResult.Failure(string.Join("；", problems));
            }

            try
            {
                _store.Save(normalized);
                if (clearToken)
                {
                    _store.ClearToken();
                }
                else if (!string.IsNullOrWhiteSpace(newToken))
                {
                    _store.SaveToken(newToken);
                }
            }
            catch (Exception error) when (IsStorageError(error))
            {
                var safe = RuntimeManagementClient.Redact(error.Message, newToken);
                _diagnostics.Record($"保存穿透设置失败：{safe}");
                return FrpOperationResult.Failure($"保存穿透设置失败：{safe}");
            }

            lock (_sync)
            {
                _settings = normalized;
                _effectiveSettings = normalized with { RawConfig = string.Empty };
                _settingsProblems = [];
            }

            NotifyChanged();
            // Failed 也算"有配置在生效"：它对应一份已经落盘的配置，改完设置同样需要重启才对得上。
            var needsRestart = Snapshot.IsActive || Snapshot.State == FrpTunnelState.Failed;
            return FrpOperationResult.Success(needsRestart
                ? "设置已保存。穿透当前处于运行或失败状态，重启穿透后新配置才会生效。"
                : "设置已保存。");
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<FrpOperationResult> SaveTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var reload = ReloadCore();
            if (!reload.Succeeded) return FrpOperationResult.Failure($"保存配置文本前读取失败：{reload.Message}");
            FrpOperationResult? verification = null;
            try
            {
                var saved = await Task.Run(() => _store.SaveTextAsync(text, _defaultLocalPort(),
                    async (persisted, token) =>
                    {
                        verification = await ValidateNativeTextCoreAsync(persisted, token).ConfigureAwait(false);
                        if (!verification.Succeeded) throw new IOException(verification.Message);
                    }, cancellationToken), cancellationToken).ConfigureAwait(false);
                var effective = FrpNativeConfig.Parse(saved.Settings.RawConfig).Describe(saved.Settings);
                lock (_sync)
                {
                    _settings = saved.Settings with { InstalledVersion = _settings.InstalledVersion };
                    _effectiveSettings = effective with { InstalledVersion = _settings.InstalledVersion, RawConfig = string.Empty };
                    _settingsProblems = [];
                }
                NotifyChanged();
                var restart = Snapshot.RequiresStop ? " 当前穿透不会重启；新文本将在下一次启动时生效。" : string.Empty;
                return FrpOperationResult.Success($"配置文本已保存（本机加密保护），已激活文本模式。{verification!.Message}{restart}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (FrpConfigurationException error)
            {
                return FrpOperationResult.Failure(string.Join("；", error.Problems));
            }
            catch (Exception error) when (IsStorageError(error))
            {
                _diagnostics.Record($"保存配置文本失败：{error.Message}");
                return FrpOperationResult.Failure($"保存配置文本失败：{error.Message}");
            }
            catch (Exception error)
            {
                var diagnostic = SafeUnexpected("保存配置文本失败", error);
                _diagnostics.Record(diagnostic);
                return FrpOperationResult.Failure(diagnostic);
            }
        }
        finally { _operationGate.Release(); }
    }

    public Task<FrpOperationResult> ValidateTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return RunGuardedAsync(async token =>
        {
            try { return await ValidateNativeTextCoreAsync(text, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                var diagnostic = SafeUnexpected("配置文本校验/清理失败", error);
                _diagnostics.Record(diagnostic);
                return FrpOperationResult.Failure(diagnostic);
            }
        }, cancellationToken);
    }

    private Task<FrpOperationResult> ValidateNativeTextCoreAsync(string text, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            string? temporary = null;
            try
            {
                var native = FrpNativeConfig.Parse(text);
                if (!IsSupportedPlatform) return FrpOperationResult.Failure(PlatformDiagnostic);
                var version = _installer.InstalledVersion;
                if (string.IsNullOrEmpty(version))
                    return FrpOperationResult.Success("文本结构校验通过；尚未安装 frp，尚未通过原生校验，启动前必须安装并通过原生校验。");
                version = FrpReleaseCatalog.NormalizeVersion(version);
                if (!_installer.IsInstalled(version)) return FrpOperationResult.Failure("已记录的 frp 二进制不完整，请重新安装后进行原生校验。");
                var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
                var metadata = native.Describe(Settings with { ConfigMode = FrpConfigMode.Text, RawConfig = text });
                temporary = Path.Combine(EnsureDirectory(_paths.FrpConfigDirectory), $"verify-{Guid.NewGuid():N}.json");
                WriteConfigAtomically(temporary, native.CreateRuntimeConfig(FrpSettingsStore.AdminUser, password));
                var plan = new FrpRunPlan(native.Role, _installer.ExecutablePath(version, native.Role == FrpRole.Server),
                    temporary, EnsureDirectory(_paths.FrpDirectory), _paths.FrpLogsDirectory, native.AdminPort,
                    FrpSettingsStore.AdminUser, password, "verify", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5))
                {
                    ExpectedProxies = native.Proxies.ToArray(),
                    Secrets = native.Secrets.Append(password).ToArray(),
                    CapturedSettings = MetadataOnly(metadata),
                };
                var result = await _nativeVerifier.VerifyAsync(plan, cancellationToken).ConfigureAwait(false);
                return result.Succeeded ? FrpOperationResult.Success(Sanitize(result.Diagnostic, plan))
                    : FrpOperationResult.Failure(Sanitize(result.Diagnostic, plan));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (FrpConfigurationException error) { return FrpOperationResult.Failure(string.Join("；", error.Problems)); }
            catch (Exception error) when (IsStorageError(error))
            {
                return FrpOperationResult.Failure($"配置文本校验失败：{error.GetType().Name}, HResult=0x{error.HResult:X8}");
            }
            finally
            {
                // Validation artifacts contain credentials: never keep them or silently ignore deletion failure.
                if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
            }
        }, cancellationToken);

    private static FrpSettings MetadataOnly(FrpSettings settings) => settings with
    {
        RawConfig = string.Empty,
        Client = settings.Client with { User = string.Empty, CustomDomains = settings.Client.CustomDomains.ToArray() },
    };

    private static string SafeUnexpected(string prefix, Exception error) =>
        $"{prefix}：{error.GetType().Name}, HResult=0x{error.HResult:X8}" +
        (error.StackTrace is { } stack ? $"；堆栈：{stack[..Math.Min(stack.Length, 4000)]}" : string.Empty);

    private static bool IsStorageError(Exception error) => error is IOException or UnauthorizedAccessException
        or FormatException or ArgumentException or System.Security.Cryptography.CryptographicException;

    private static string Sanitize(string message, FrpRunPlan plan) => RuntimeManagementClient.Redact(message,
        plan.Secrets.Append(plan.AdminPassword).ToArray());

    public async Task<FrpOperationResult> SetFollowServiceAsync(
        bool follow,
        CancellationToken cancellationToken = default)
    {
        var requestEpoch = follow ? CaptureStartRequestEpoch() : null;
        if (follow && requestEpoch is null) return InvalidStartRequest();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (follow && !IsStartRequestCurrent(requestEpoch)) return InvalidStartRequest();
            var reload = ReloadCore();
            if (!reload.Succeeded)
            {
                return FrpOperationResult.Failure($"保存「随服务启动」前读取失败：{reload.Message}");
            }

            FrpSettingsReadResult patched;
            try
            {
                patched = _store.SetFollowService(follow, _defaultLocalPort());
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
            {
                _diagnostics.Record("保存「随服务启动」失败", error);
                return FrpOperationResult.Failure($"保存「随服务启动」失败：{error.Message}");
            }

            if (!patched.Succeeded)
            {
                lock (_sync)
                {
                    _settingsProblems = patched.Problems;
                }
                NotifyChanged();
                return FrpOperationResult.Failure($"保存「随服务启动」前读取失败：{string.Join("；", patched.Problems)}");
            }

            lock (_sync)
            {
                _settings = patched.Settings with { InstalledVersion = _settings.InstalledVersion };
                _effectiveSettings = _effectiveSettings with { FollowService = follow };
                _settingsProblems = [];
            }

            NotifyChanged();
            if (!follow)
            {
                return FrpOperationResult.Success("已关闭「随弹幕服务启动」。穿透停止仍会跟随服务。");
            }

            if (!IsServiceRunning)
            {
                return FrpOperationResult.Success("已开启「随弹幕服务启动」。弹幕服务运行时会自动拉起穿透。");
            }

            // 已经在跑就别再启动一次：状态机会拒绝"Running 时启动"，而这属于正常操作顺序
            // （用户先手动开了穿透、再来打开这个开关），不该被当成失败，更不该把异常抛到 UI 线程。
            if (Snapshot.IsActive)
            {
                return FrpOperationResult.Success($"已开启「随弹幕服务启动」。穿透当前已在运行（{StatusText(Snapshot.State)}），无需重复启动。");
            }

            // 用户刚把这个开关打开、服务又正在跑：当场生效，否则要等到下一次服务重启才看到效果。
            var start = await StartCoreAsync(cancellationToken, requestEpoch!.Value).ConfigureAwait(false);
            return start.Succeeded
                ? FrpOperationResult.Success($"已开启「随弹幕服务启动」，并已启动穿透。{start.Message}")
                : FrpOperationResult.Failure($"「随弹幕服务启动」已保存，但立即启动穿透失败：{start.Message}");
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static string StatusText(FrpTunnelState state) => state switch
    {
        FrpTunnelState.Starting => "正在启动",
        FrpTunnelState.Running => "运行中",
        FrpTunnelState.Reconnecting => "重连中",
        FrpTunnelState.Stopping => "正在停止",
        _ => state.ToString(),
    };

    public async Task<FrpOperationResult> InstallAsync(
        string version,
        IProgress<GithubDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsSupportedPlatform)
            {
                return FrpOperationResult.Failure(PlatformDiagnostic);
            }

            if (_shutdownRequested || Snapshot.RequiresStop || _supervisor.HasOwnedProcess)
            {
                return FrpOperationResult.Failure("穿透正在运行或清理未完成，请先停止穿透再安装或更新 frp。");
            }

            // 线路类失败交给界面去走"测速并选择线路"这条路，不在这里吞成一句普通失败。
            var result = await _installer.InstallAsync(version, progress, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                _diagnostics.Record($"安装 frp 失败：{result.Diagnostic}");
                return FrpOperationResult.Failure(result.Diagnostic);
            }

            var settings = Settings with { InstalledVersion = result.Version };
            try
            {
                _store.Save(settings);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
            {
                return FrpOperationResult.Failure($"frp 已安装到 {result.BinaryDirectory}，但记录版本失败：{error.Message}");
            }

            lock (_sync)
            {
                _settings = settings;
                _effectiveSettings = _effectiveSettings with { InstalledVersion = settings.InstalledVersion };
                _settingsProblems = [];
            }

            var removed = _installer.RemoveOtherVersions(result.Version);
            if (removed.Count > 0)
            {
                _diagnostics.Record($"已清理旧版 frp 目录：{string.Join("、", removed)}");
            }

            NotifyChanged();
            return FrpOperationResult.Success(result.Diagnostic);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public Task<FrpOperationResult> StartTunnelAsync(CancellationToken cancellationToken = default) =>
        RunStartGuardedAsync((token, epoch) => StartCoreAsync(token, epoch), cancellationToken);

    public Task<FrpOperationResult> StopTunnelAsync(string reason = "user", CancellationToken cancellationToken = default) =>
        RunGuardedAsync(
            async token =>
            {
                var snapshot = await StopGuardedAsync(reason, token).ConfigureAwait(false);
                Publish(snapshot);
                return snapshot.State == FrpTunnelState.Stopped && !_supervisor.HasOwnedProcess
                    ? FrpOperationResult.Success("穿透已停止。")
                    : FrpOperationResult.Failure(snapshot.Diagnostic ?? "停止穿透失败。");
            },
            cancellationToken);

    public Task<FrpOperationResult> RestartTunnelAsync(CancellationToken cancellationToken = default) =>
        RunStartGuardedAsync(
            async (token, epoch) =>
            {
                var stopped = await StopGuardedAsync("restart", token).ConfigureAwait(false);
                if (stopped.State != FrpTunnelState.Stopped || _supervisor.HasOwnedProcess)
                {
                    return FrpOperationResult.Failure(stopped.Diagnostic ?? "重启前停止穿透失败。");
                }

                return await StartCoreAsync(token, epoch).ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>停止的统一入口：把监督器抛出的异常统一变成失败结果（界面命令 await 它，抛出即闪退）。</summary>
    private async Task<FrpSnapshot> StopGuardedAsync(string reason, CancellationToken cancellationToken)
    {
        try
        {
            return await _supervisor.StopAsync(reason, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            var message = $"停止穿透失败：{error.Message}";
            if (_lastPlan is { } plan) message = Sanitize(message, plan);
            _diagnostics.Record(message);
            return Snapshot with { State = FrpTunnelState.Failed, Diagnostic = message };
        }
    }

    public string ReadLogTail(int lines = 60)
    {
        var plan = _lastPlan;
        if (plan is null)
        {
            return "（穿透尚未启动过，暂无日志）";
        }

        var stdout = LogTail.Read(Path.Combine(plan.LogDirectory, $"{plan.LogFileName}-stdout.log"), lines);
        var stderr = LogTail.Read(Path.Combine(plan.LogDirectory, $"{plan.LogFileName}-stderr.log"), Math.Min(lines, 20));
        var tail = stderr == LogTail.MissingFileText
            ? stdout
            : $"{stdout}{Environment.NewLine}--- 标准错误 ---{Environment.NewLine}{stderr}";
        return string.Join(Environment.NewLine, tail.Split('\n').Select(line => Sanitize(line.TrimEnd('\r'), plan)));
    }

    public Task<FrpOperationResult> ShutdownAsync()
    {
        lock (_sync)
        {
            // 必须在返回 Task 之前暂停；恢复 bool 不能恢复暂停前/期间已排队的启动请求。
            if (!_shutdownRequested) _startRequestEpoch++;
            _shutdownRequested = true;
            if (_shutdownTask is null || _shutdownTask.IsCompleted && !_shutdownTask.Result.Succeeded)
                _shutdownTask = ShutdownCoreAsync(_pendingLifecycleTask);
            return _shutdownTask;
        }
    }

    public void ResumeAfterFailedShutdown()
    {
        lock (_sync)
        {
            if (_shutdownTask is { IsCompleted: false })
                throw new InvalidOperationException("穿透退出清理尚未完成，不能恢复启动");
            if (_disposed) throw new ObjectDisposedException(nameof(FrpTunnelService));
            _startRequestEpoch++;
            _shutdownRequested = false;
            _shutdownTask = null;
        }
        NotifyChanged();
    }

    private async Task<FrpOperationResult> ShutdownCoreAsync(Task pendingLifecycle)
    {
        try
        {
            await pendingLifecycle.ConfigureAwait(false);
            await _operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var stopped = await StopGuardedAsync("application-exit", CancellationToken.None).ConfigureAwait(false);
                Publish(stopped);
                if (stopped.State != FrpTunnelState.Stopped || _supervisor.HasOwnedProcess)
                    return FrpOperationResult.Failure(stopped.Diagnostic ?? "退出清理失败：穿透仍有受管进程");
                return FrpOperationResult.Success("穿透退出清理已完成。");
            }
            finally { _operationGate.Release(); }
        }
        catch (Exception error)
        {
            _diagnostics.Record("穿透退出屏障失败", error);
            return FrpOperationResult.Failure($"穿透退出屏障失败：{error.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task task;
        lock (_sync)
        {
            if (_disposed) return;
            if (_disposeTask is null || _disposeTask.IsCompleted && !_disposeTask.IsCompletedSuccessfully)
                _disposeTask = DisposeCoreAsync();
            task = _disposeTask;
        }
        await task.ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
    {
        var shutdown = await ShutdownAsync().ConfigureAwait(false);
        if (!shutdown.Succeeded || _supervisor.HasOwnedProcess)
            throw new InvalidOperationException(shutdown.Message);
        // 清理失败时不取消/释放控制资源：仍允许重试 Stop 或 Dispose。
        _lifetime.Cancel();
        if (_reconcileLoop is not null) await _reconcileLoop.ConfigureAwait(false);
        await _supervisor.DisposeAsync().ConfigureAwait(false);
        _supervisor.SnapshotChanged -= OnSupervisorSnapshotChanged;
        _runtime.SnapshotChanged -= OnRuntimeSnapshotChanged;
        _disposed = true;
        _lifetime.Dispose();
        _operationGate.Dispose();
    }

    private async Task<FrpOperationResult> RunGuardedAsync(
        Func<CancellationToken, Task<FrpOperationResult>> operation,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private long? CaptureStartRequestEpoch()
    {
        lock (_sync) return _shutdownRequested || _disposed ? null : _startRequestEpoch;
    }

    private bool IsStartRequestCurrent(long? requestEpoch)
    {
        lock (_sync) return requestEpoch is not null && !_shutdownRequested && !_disposed
            && requestEpoch.Value == _startRequestEpoch;
    }

    private static FrpOperationResult InvalidStartRequest() =>
        FrpOperationResult.Failure("穿透启动请求已被应用退出屏障拒绝或失效，请在恢复后重新发起操作。");

    private async Task<FrpOperationResult> RunStartGuardedAsync(
        Func<CancellationToken, long, Task<FrpOperationResult>> operation,
        CancellationToken cancellationToken)
    {
        // 请求抵达即记录代次；暂停期间不进入等待队列，暂停前排队的请求在恢复后仍失效。
        var requestEpoch = CaptureStartRequestEpoch();
        if (requestEpoch is null) return InvalidStartRequest();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsStartRequestCurrent(requestEpoch)) return InvalidStartRequest();
            return await operation(cancellationToken, requestEpoch.Value).ConfigureAwait(false);
        }
        finally { _operationGate.Release(); }
    }

    private async Task<FrpOperationResult> StartCoreAsync(CancellationToken cancellationToken, long requestEpoch)
    {
        if (!IsStartRequestCurrent(requestEpoch)) return InvalidStartRequest();
        if (_supervisor.HasOwnedProcess || Snapshot.RequiresStop)
            return FrpOperationResult.Failure("仍有受管穿透进程，请先重试停止，确认清理后再启动。");
        // 启动前先把落盘设置重读一遍：本服务的内存副本可能落后于磁盘（界面保存过、另一个入口改过、
        // 用户手工编辑过 settings.properties）。拿旧副本启动会出现"界面写的是 A、实际跑的是 B"。
        var reload = ReloadCore();
        if (!reload.Succeeded)
        {
            _diagnostics.Record($"穿透未启动：{reload.Message}");
            return FrpOperationResult.Failure(reload.Message);
        }

        // 先把"配置本身能不能用"报清楚（设置非法、没装 frp、目录不可写……），
        // 再谈"现在能不能起"：两件事的顺序反了会把"没装 frp"的用户引到去启动服务。
        var (plan, problems) = await BuildPlanAsync(cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            var message = string.Join("；", problems);
            _diagnostics.Record($"穿透未启动：{message}");
            return FrpOperationResult.Failure(message);
        }

        // 穿透的意义就是把本机弹幕服务送出去。服务没跑时启动穿透，只会得到一条"看着正常、
        // 访问必然失败"的隧道，因此这里直接拒绝并说明原因（服务一停穿透也会自动停，见 OnRuntimeSnapshotChanged）。
        if (!IsServiceRunning)
        {
            const string reason = "弹幕服务未运行：先启动服务再启动穿透（也可以在设置里开启「随弹幕服务启动」，让两者一起起来）。";
            _diagnostics.Record($"穿透未启动：{reason}");
            return FrpOperationResult.Failure(reason);
        }

        if (!IsStartRequestCurrent(requestEpoch)) return InvalidStartRequest();
        lock (_sync) { _lastPlan = plan; }
        FrpSnapshot snapshot;
        try
        {
            snapshot = await _supervisor.StartAsync(plan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // 监督器的状态机拒绝（例如状态不允许启动）与任何意外异常都必须变成"失败结果"：
            // 这些方法会被界面命令直接 await，抛出去就是未处理异常 → 应用闪退。
            var safe = Sanitize($"启动穿透失败：{error.Message}", plan);
            _diagnostics.Record(safe);
            return FrpOperationResult.Failure(safe);
        }

        Publish(snapshot);
        snapshot = Snapshot;
        if (snapshot.State != FrpTunnelState.Running)
        {
            return FrpOperationResult.Failure(snapshot.Diagnostic ?? "穿透未能启动。");
        }

        return FrpOperationResult.Success(DescribeRunning(snapshot));
    }

    private string DescribeRunning(FrpSnapshot snapshot) => EffectiveSettings.Role == FrpRole.Client
        ? snapshot.RemoteAddress is null
            ? "穿透已启动；没有已确认指向本机弹幕服务的远端地址。"
            : $"穿透已启动：{snapshot.RemoteAddress}"
        : "穿透服务已启动："
          + $"监听端口 {EffectiveSettings.Server.BindPort.ToString(System.Globalization.CultureInfo.InvariantCulture)}，"
          + $"在线客户端 {snapshot.Server?.ClientCounts.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "未报告"}";

    /// <summary>
    /// 把当前设置变成一份可运行的配置。任何一步不成立都返回 null 与原因清单，绝不"先启动再说"：
    /// frpc 拿着半份配置只会以退出或 start error 收场，用户看到的将是一句无从下手的报错。
    /// </summary>
    private Task<(FrpRunPlan? Plan, IReadOnlyList<string> Problems)> BuildPlanAsync(CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            var settings = Settings;
            var problems = new List<string>(SettingsProblems);
            problems.AddRange(settings.Validate());
            if (!IsSupportedPlatform) problems.Add(PlatformDiagnostic);
            FrpNativeConfig? native = null;
            var effective = settings;
            try
            {
                if (settings.ConfigMode == FrpConfigMode.Text)
                {
                    // ReloadCore strictly reread the protected source immediately before this plan. Never use the form.
                    native = FrpNativeConfig.Parse(settings.RawConfig);
                    effective = native.Describe(settings);
                }
            }
            catch (FrpConfigurationException error) { problems.AddRange(error.Problems); }
            var version = string.Empty;
            try
            {
                if (settings.InstalledVersion.Length == 0) problems.Add("尚未安装 frp，请先在上方下载安装。");
                else
                {
                    version = FrpReleaseCatalog.NormalizeVersion(settings.InstalledVersion);
                    if (!_installer.IsInstalled(version)) problems.Add($"frp {version} 的可执行文件不在位，请重新安装。");
                }
            }
            catch (Exception error) when (IsStorageError(error)) { problems.Add($"已记录的 frp 版本无法使用：{error.GetType().Name}"); }
            var token = string.Empty;
            var adminPassword = string.Empty;
            try
            {
                if (settings.ConfigMode == FrpConfigMode.Visual) token = _store.ReadToken();
                adminPassword = _store.EnsureAdminPassword();
            }
            catch (Exception error) when (IsStorageError(error)) { problems.Add($"读取穿透凭据失败：{error.GetType().Name}, HResult=0x{error.HResult:X8}"); }
            if (problems.Count > 0) return ((FrpRunPlan?)null, (IReadOnlyList<string>)problems);

            var isServer = effective.Role == FrpRole.Server;
            var configPath = ConfigFilePath(effective.Role, settings.ConfigMode);
            FrpRunPlan? plan = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = native is not null
                    ? native.CreateRuntimeConfig(FrpSettingsStore.AdminUser, adminPassword)
                    : isServer ? FrpConfigWriter.WriteServer(settings.Server, token, FrpSettingsStore.AdminUser, adminPassword)
                        : FrpConfigWriter.WriteClient(settings.Client, token, FrpSettingsStore.AdminUser, adminPassword);
                WriteConfigAtomically(configPath, content);
                var registeredName = string.IsNullOrEmpty(effective.Client.User) ? effective.Client.ProxyName
                    : effective.Client.User + "." + effective.Client.ProxyName;
                var expected = native?.Proxies.ToArray() ?? (isServer ? [] :
                    new[] { new FrpNativeProxy(registeredName, effective.Client.ProxyKind.ToFrpText(), effective.Client.LocalAddress, effective.Client.LocalPort)
                        { AdminName = effective.Client.ProxyName } });
                plan = new FrpRunPlan(effective.Role, _installer.ExecutablePath(version, isServer), configPath,
                    EnsureDirectory(_paths.FrpDirectory), EnsureDirectory(_paths.FrpLogsDirectory),
                    isServer ? effective.Server.AdminPort : effective.Client.AdminPort,
                    FrpSettingsStore.AdminUser, adminPassword, isServer ? "server" : registeredName,
                    TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(10))
                {
                    ExpectedProxies = expected,
                    Secrets = (native?.Secrets ?? new[] { token }).Append(adminPassword).Where(secret => secret.Length > 0).ToArray(),
                    CapturedSettings = MetadataOnly(effective),
                    CoreServicePort = _runtime.Snapshot.Port ?? _defaultLocalPort(),
                    OwnedConfigPaths = [ConfigFilePath(effective.Role, FrpConfigMode.Visual), ConfigFilePath(effective.Role, FrpConfigMode.Text)],
                };
                if (native is not null)
                {
                    var verification = await _nativeVerifier.VerifyAsync(plan, cancellationToken).ConfigureAwait(false);
                    if (!verification.Succeeded) return ((FrpRunPlan?)null, (IReadOnlyList<string>)new[] { Sanitize(verification.Diagnostic, plan) });
                }
                return (plan, (IReadOnlyList<string>)Array.Empty<string>());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                return ((FrpRunPlan?)null, (IReadOnlyList<string>)new[] { plan is null
                    ? $"准备 frp 运行配置失败：{error.GetType().Name}, HResult=0x{error.HResult:X8}"
                    : Sanitize($"准备 frp 运行配置失败：{error.Message}", plan) });
            }
        }, cancellationToken);

    private FrpOperationResult ReloadCore()
    {
        FrpSettingsReadResult read;
        try
        {
            read = _store.Read(_defaultLocalPort());
        }
        catch (Exception error) when (IsStorageError(error))
        {
            lock (_sync)
            {
                _settingsProblems = [$"读取穿透设置失败：{error.Message}"];
                if (_settings.ConfigMode == FrpConfigMode.Text)
                    _effectiveSettings = _effectiveSettings with
                    {
                        Client = _effectiveSettings.Client with { LocalPort = 0, LocalAddress = string.Empty, ProxyName = string.Empty, ServerAddress = string.Empty },
                        Server = _effectiveSettings.Server with { BindPort = 0 },
                    };
            }

            NotifyChanged();
            return FrpOperationResult.Failure(SettingsProblems[0]);
        }

        var problems = new List<string>(read.Problems);
        var settings = read.Settings;
        // 二进制的事实以磁盘为准：设置里记的版本只在目录读不出来时才有意义。两者不一致时按磁盘更新并留诊断，
        // 免得界面显示"已安装 0.71.0"而 frp\bin\0.71.0\frpc.exe 其实并不存在。
        var onDisk = _installer.InstalledVersion;
        if (!string.Equals(settings.InstalledVersion, onDisk ?? string.Empty, StringComparison.Ordinal))
        {
            _diagnostics.Record(
                $"穿透设置记录的版本（{Describe(settings.InstalledVersion)}）与磁盘上的安装（{Describe(onDisk)}）不一致，已按磁盘内容对齐");
            settings = settings with { InstalledVersion = onDisk ?? string.Empty };
        }

        var effective = settings with { RawConfig = string.Empty };
        if (settings.ConfigMode == FrpConfigMode.Text)
        {
            if (problems.Count == 0)
            {
                try { effective = FrpNativeConfig.Parse(settings.RawConfig).Describe(settings) with { RawConfig = string.Empty }; }
                catch (FrpConfigurationException error) { problems.AddRange(error.Problems); }
            }
            if (problems.Count > 0)
                effective = effective with
                {
                    Client = effective.Client with { LocalPort = 0, LocalAddress = string.Empty, ProxyName = string.Empty, ServerAddress = string.Empty },
                    Server = effective.Server with { BindPort = 0 },
                };
        }
        lock (_sync)
        {
            _settings = settings;
            _effectiveSettings = effective;
            _settingsProblems = problems;
        }

        NotifyChanged();
        return problems.Count == 0
            ? FrpOperationResult.Success(string.Empty)
            : FrpOperationResult.Failure(string.Join("；", problems));
    }

    private async Task ReconcileLoopAsync(CancellationToken cancellationToken)
    {
        // 起手先按当前服务状态对齐一次（自启/接管路径下服务此时可能已经在跑）。
        await QueueLifecycleAsync(_runtime.Snapshot, cancellationToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(ReconcileInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var requestEpoch = CaptureStartRequestEpoch();
                    if (requestEpoch is null) continue;
                    await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (IsStartRequestCurrent(requestEpoch))
                        {
                            var snapshot = await _supervisor.RefreshAsync(cancellationToken).ConfigureAwait(false);
                            Publish(snapshot);
                        }
                    }
                    finally { _operationGate.Release(); }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception error)
                {
                    _diagnostics.Record("穿透状态对账失败", error);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 宿主退出时的正常取消。
        }
    }

    /// <summary>
    /// 弹幕服务状态变化时的穿透联动：**停必停、起可选起**。
    /// 停止方向不看开关：服务停了还挂着一条指向死端口的隧道，界面上的地址就是假的。
    /// </summary>
    private void OnRuntimeSnapshotChanged(object? sender, RuntimeSnapshot snapshot) =>
        _ = QueueLifecycleAsync(snapshot, _lifetime.Token);

    private Task QueueLifecycleAsync(RuntimeSnapshot snapshot, CancellationToken cancellationToken)
    {
        // 事件可能连发（Stopping → Stopped），排队串行处理，避免两次并发启停打在同一个状态机上。
        lock (_sync)
        {
            if (_shutdownRequested) return Task.CompletedTask;
            var requestEpoch = _startRequestEpoch;
            var previous = _pendingLifecycleTask;
            _pendingLifecycleTask = RunAsync(previous, snapshot, requestEpoch, cancellationToken);
            return _pendingLifecycleTask;
        }

        async Task RunAsync(Task previous, RuntimeSnapshot state, long requestEpoch, CancellationToken token)
        {
            try
            {
                await previous.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                _diagnostics.Record("上一次穿透生命周期联动失败", error);
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await ApplyServiceStateAsync(state, requestEpoch, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                _diagnostics.Record("穿透生命周期联动失败", error);
            }
        }
    }

    private async Task ApplyServiceStateAsync(RuntimeSnapshot state, long requestEpoch, CancellationToken cancellationToken)
    {
        if (state.State == DesktopRuntimeState.Running && !IsStartRequestCurrent(requestEpoch))
        {
            _diagnostics.Record(InvalidStartRequest().Message);
            return;
        }
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_shutdownRequested) return; // 退出屏障负责最终停止，旧 Running 事件不能重新启动。
            if (state.State == DesktopRuntimeState.Running && !IsStartRequestCurrent(requestEpoch))
            {
                _diagnostics.Record(InvalidStartRequest().Message);
                return;
            }
            if (state.State != DesktopRuntimeState.Running)
            {
                // 停必停只依赖受管运行计划，绝不被新设置损坏阻断；Failed 清理也必须重试。
                if (Snapshot.RequiresStop || _supervisor.HasOwnedProcess)
                {
                    var stopped = await StopGuardedAsync("service-stopped", cancellationToken).ConfigureAwait(false);
                    Publish(stopped);
                    _diagnostics.Record(stopped.State == FrpTunnelState.Stopped && !_supervisor.HasOwnedProcess
                        ? "弹幕服务已停止，穿透已随之停止"
                        : $"弹幕服务已停止，但停止穿透失败：{stopped.Diagnostic}");
                }
                return;
            }

            // 只在启动方向读取并严格验证落盘设置；忽略队列中已过时的 Running 事件。
            if (!IsServiceRunning) return;
            var reload = ReloadCore();
            if (!reload.Succeeded)
            {
                _diagnostics.Record($"穿透生命周期联动跳过：{reload.Message}");
                return;
            }
            if (!Settings.FollowService || Snapshot.RequiresStop || _supervisor.HasOwnedProcess) return;
            var result = await StartCoreAsync(cancellationToken, requestEpoch).ConfigureAwait(false);
            _diagnostics.Record(result.Succeeded
                ? $"穿透随服务启动成功：{result.Message}"
                : $"穿透随服务启动失败：{result.Message}");
        }
        finally { _operationGate.Release(); }
    }

    private void OnSupervisorSnapshotChanged(object? sender, FrpSnapshot snapshot) => Publish(snapshot);

    private void Publish(FrpSnapshot snapshot)
    {
        lock (_sync)
        {
            if (_lastPlan is { } plan)
                snapshot = snapshot with
                {
                    Diagnostic = snapshot.Diagnostic is null ? null : Sanitize(snapshot.Diagnostic, plan),
                    RemoteAddress = snapshot.RemoteAddress is null ? null : Sanitize(snapshot.RemoteAddress, plan),
                    Proxies = snapshot.Proxies?.Select(proxy => proxy with
                    {
                        Name = Sanitize(proxy.Name, plan), Error = Sanitize(proxy.Error, plan),
                        Status = proxy.State == FrpProxyState.Unknown ? Sanitize(proxy.Status, plan) : proxy.Status,
                        LocalAddress = Sanitize(proxy.LocalAddress, plan), RemoteAddress = Sanitize(proxy.RemoteAddress, plan),
                    }).ToArray(),
                    ActiveSettings = snapshot.RequiresStop ? plan.CapturedSettings : null,
                    ExpectedProxies = snapshot.RequiresStop ? plan.ExpectedProxies?.Select(target => target with
                    {
                        Name = Sanitize(target.Name, plan), AdminName = Sanitize(target.AdminName, plan),
                        LocalAddress = Sanitize(target.LocalAddress, plan),
                    }).ToArray() ?? [] : [],
                    CoreServicePort = snapshot.RequiresStop ? plan.CoreServicePort : null,
                };
            _snapshot = snapshot;
        }

        NotifyChanged();
    }

    private void NotifyChanged()
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        var snapshot = Snapshot;
        foreach (EventHandler<FrpSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, snapshot);
            }
            catch (Exception error)
            {
                _diagnostics.Record("穿透状态订阅者抛出异常", error);
            }
        }
    }

    /// <summary>原子替换配置文件并回读校验：写了一半的配置会让 frpc 直接起不来。</summary>
    private static void WriteConfigAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new IOException($"配置路径没有父目录：{path}");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(temporary, content, new System.Text.UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
            var roundTrip = File.ReadAllText(path, System.Text.Encoding.UTF8);
            if (!string.Equals(roundTrip, content, StringComparison.Ordinal))
            {
                throw new IOException($"frp 配置回读校验失败：{path}");
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Describe(string? version) => string.IsNullOrWhiteSpace(version) ? "未安装" : version;
}
