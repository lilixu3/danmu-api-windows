using System.Reflection;
using Avalonia.Headless.XUnit;
using DanmuApi.App.Services;
using DanmuApi.Core;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class TrayServiceTests
{
    [AvaloniaFact]
    public async Task PullRequestPendingUpdateOpensTargetCorePageInsteadOfOverview()
    {
        var runtime = DispatchProxy.Create<IRuntimeController, RuntimeProxy>();
        var manifest = new CoreInstallationManifest(2, ManagedCoreVariant.Dev, "owner/repo", "main",
            new string('a', 40), null, "dev", CoreInstallKind.LocalPullRequestStack, null, DateTimeOffset.UtcNow);
        var pending = new PendingService(new(ManagedCoreVariant.Dev, CoreUpdateCheckStatus.Checked, true,
            manifest, new GithubCommit(new string('b', 40), "update", "update", null, null, []), null,
            DateTimeOffset.UtcNow, "available"));
        var diagnostics = new Diagnostics();
        var overviewCalls = 0;
        ManagedCoreVariant? openedCore = null;
        using var tray = new TrayService(runtime, pending, diagnostics, () => overviewCalls++,
            variant => openedCore = variant, () => throw new InvalidOperationException("Wrong page"),
            () => throw new InvalidOperationException("Wrong page"), () => Task.CompletedTask);

        await tray.ApplyPendingUpdateAsync();

        Assert.Equal(ManagedCoreVariant.Dev, openedCore);
        Assert.Equal(0, overviewCalls);
        Assert.Equal(0, pending.ApplyCalls);
        Assert.Null(diagnostics.LastDiagnostic);
    }

    [AvaloniaFact]
    public async Task OrdinaryPendingUpdateStillUsesPendingApply()
    {
        var runtime = DispatchProxy.Create<IRuntimeController, RuntimeProxy>();
        var pending = new PendingService(new(ManagedCoreVariant.Stable, CoreUpdateCheckStatus.Checked, true,
            null, new GithubCommit(new string('b', 40), "update", "update", null, null, []), null,
            DateTimeOffset.UtcNow, "available"));
        using var tray = new TrayService(runtime, pending, new Diagnostics(),
            () => throw new InvalidOperationException("Unexpected navigation"),
            _ => throw new InvalidOperationException("Unexpected navigation"),
            () => throw new InvalidOperationException("Unexpected navigation"),
            () => throw new InvalidOperationException("Unexpected navigation"), () => Task.CompletedTask);

        await tray.ApplyPendingUpdateAsync();

        Assert.Equal(1, pending.ApplyCalls);
    }

    public class RuntimeProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_Snapshot" => new RuntimeSnapshot(DesktopRuntimeState.Stopped),
            "add_SnapshotChanged" or "remove_SnapshotChanged" => null,
            _ => throw new InvalidOperationException($"Unexpected runtime call: {targetMethod?.Name}"),
        };
    }

    private sealed class PendingService(CoreUpdateCheckResult update) : IPendingCoreUpdateService
    {
        public CoreUpdateCheckResult? PendingUpdate => update;
        public bool IsApplying => false;
        public int ApplyCalls { get; private set; }
        public event EventHandler? StateChanged { add { } remove { } }
        public Task<CoreManagementOperationResult?> ApplyPendingAsync(CancellationToken cancellationToken = default)
        {
            ApplyCalls++;
            return Task.FromResult<CoreManagementOperationResult?>(new(true, true, false, null, "updated"));
        }
    }

    private sealed class Diagnostics : IAppDiagnostics
    {
        public string? LastDiagnostic { get; private set; }
        public void Record(string message, Exception? error = null) => LastDiagnostic = message;
    }
}
