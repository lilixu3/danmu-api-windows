using DanmuApi.App.Services;
using DanmuApi.Core;
using DanmuApi.Core.Frp;
using DanmuApi.Platform;
using DanmuApi.Platform.Frp;
using DanmuApi.Runtime;
using DanmuApi.Runtime.Frp;

namespace DanmuApi.Tests;

/// <summary>frp 测试替身：安装器与监督器都用假对象，编排层测试与视图测试共用。</summary>
internal sealed class FakeFrpInstaller : IFrpBinaryInstaller
{
    public string? Version { get; set; }

    public string? InstallVersion { get; set; }

    public string InstallDiagnostic { get; set; } = "frp 已安装";

    public string BinaryDirectory { get; set; } = string.Empty;

    public int InstallCalls { get; private set; }

    public string? InstalledVersion => Version;

    public bool IsInstalled(string version) =>
        Version is not null && string.Equals(Version, version, StringComparison.Ordinal);

    public string ExecutablePath(string version, bool serverRole)
    {
        var name = serverRole ? "frps.exe" : "frpc.exe";
        return Path.Combine(BinaryDirectory, version, name);
    }

    public Task<FrpInstallResult> InstallAsync(
        string version,
        IProgress<GithubDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        InstallCalls++;
        if (InstallVersion is null)
        {
            return Task.FromResult(FrpInstallResult.Failure("测试替身未配置安装版本"));
        }

        Version = InstallVersion;
        return Task.FromResult(
            new FrpInstallResult(true, InstallVersion, ExecutablePath(InstallVersion, false), InstallDiagnostic));
    }

    public IReadOnlyList<string> RemoveOtherVersions(string keepVersion) => [];
}

internal sealed class FakeFrpSupervisor : IFrpSupervisor
{
    public FrpSnapshot Snapshot { get; private set; } = new(FrpTunnelState.Stopped);

    public FrpRunPlan? LastPlan { get; private set; }

    public int StartCalls { get; private set; }

    public FrpSnapshot StartResult { get; set; } =
        new(FrpTunnelState.Running, Pid: 4321, RemoteAddress: "203.0.113.10:19321");

    public FrpSnapshot StopResult { get; set; } = new(FrpTunnelState.Stopped);

    /// <summary>让 StartAsync 抛指定异常，用来验证"监督器抛错不会把界面弄崩"。</summary>
    public Exception? StartException { get; set; }

    /// <summary>让 StopAsync 抛指定异常。</summary>
    public Exception? StopException { get; set; }

    public event EventHandler<FrpSnapshot>? SnapshotChanged;

    public Task<FrpSnapshot> StartAsync(FrpRunPlan plan, CancellationToken cancellationToken = default)
    {
        StartCalls++;
        LastPlan = plan;
        if (StartException is not null)
        {
            throw StartException;
        }

        Publish(StartResult);
        return Task.FromResult(StartResult);
    }

    public Task<FrpSnapshot> StopAsync(string reason = "user", CancellationToken cancellationToken = default)
    {
        if (StopException is not null)
        {
            throw StopException;
        }

        Publish(StopResult);
        return Task.FromResult(StopResult);
    }

    public Task<FrpSnapshot> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);

    public string? LivenessFailure() => null;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Publish(FrpSnapshot snapshot)
    {
        Snapshot = snapshot;
        SnapshotChanged?.Invoke(this, snapshot);
    }
}

/// <summary>
/// 可驱动的弹幕服务状态源：穿透的启停跟随它（服务停 → 穿透停；服务起且开关打开 → 穿透起）。
/// </summary>
internal sealed class FakeRuntimeController : IRuntimeController
{
    public RuntimeSnapshot Snapshot { get; private set; } = new(DesktopRuntimeState.Stopped);

    public event EventHandler<RuntimeSnapshot>? SnapshotChanged;

    public void SetState(DesktopRuntimeState state, int? port = 9321, string? identity = "desktop-test")
    {
        Snapshot = new RuntimeSnapshot(state, port, state == DesktopRuntimeState.Running ? 4242 : null, identity);
        SnapshotChanged?.Invoke(this, Snapshot);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        SetState(DesktopRuntimeState.Running);
        return Task.CompletedTask;
    }

    public Task<AdoptionResult> AdoptAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(AdoptionResult.Success(Snapshot, "测试替身"));

    public string? ReconcileLiveness() => null;

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        SetState(DesktopRuntimeState.Stopped, port: null);
        return Task.CompletedTask;
    }

    public Task RefreshCoreSetupRequiredAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RestartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class FrpTestDiagnostics : IAppDiagnostics
{
    public List<string> Messages { get; } = [];

    public string? LastDiagnostic => Messages.Count == 0 ? null : Messages[^1];

    public void Record(string message, Exception? error = null) =>
        Messages.Add(error is null ? message : $"{message}: {error.Message}");
}

/// <summary>一套指向临时目录的 frp 编排环境（真实设置存储 + 假安装器/监督器）。</summary>
internal sealed class FrpTestHarness : IDisposable
{
    public required AppPaths Paths { get; init; }

    public required FrpSettingsStore Store { get; init; }

    public required FakeFrpInstaller Installer { get; init; }

    public required FakeFrpSupervisor Supervisor { get; init; }

    public required FrpTestDiagnostics Diagnostics { get; init; }

    public required FakeRuntimeController Runtime { get; init; }

    public required FrpTunnelService Service { get; init; }

    public string Directory { get; init; } = string.Empty;

    public static FrpTestHarness Create(int localPort = 9321)
    {
        var root = Path.Combine(Path.GetTempPath(), "danmu-api-tests", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        System.IO.Directory.CreateDirectory(data);
        var paths = new AppPaths(Path.Combine(root, "runtime-root"), data);
        var settings = new SettingsStore(paths.SettingsFile);
        var store = new FrpSettingsStore(
            settings,
            new WindowsProtectedStringStore(paths.FrpTokenFile, "DanmuApi.Windows.FrpToken.v1"),
            new WindowsProtectedStringStore(paths.FrpAdminPasswordFile, "DanmuApi.Windows.FrpAdminPassword.v1"));
        var installer = new FakeFrpInstaller { BinaryDirectory = paths.FrpBinaryDirectory };
        var supervisor = new FakeFrpSupervisor();
        var diagnostics = new FrpTestDiagnostics();
        var runtime = new FakeRuntimeController();
        return new FrpTestHarness
        {
            Paths = paths,
            Store = store,
            Installer = installer,
            Supervisor = supervisor,
            Diagnostics = diagnostics,
            Runtime = runtime,
            Service = new FrpTunnelService(store, installer, supervisor, paths, diagnostics, runtime, () => localPort),
            Directory = root,
        };
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
