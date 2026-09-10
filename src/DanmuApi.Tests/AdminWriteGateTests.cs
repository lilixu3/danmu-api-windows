using DanmuApi.App.Services;

namespace DanmuApi.Tests;

public sealed class AdminWriteGateTests
{
    [Fact]
    public async Task RefusesWithoutAdminModeAndOffersSecurityNavigation()
    {
        var session = new StubAdminSessionService(adminMode: false, configured: true);
        var dialogs = new RecordingDialogService { Confirmation = true };
        var diagnostics = new TestDiagnostics();
        var gate = new AdminWriteGate(session, dialogs, diagnostics);
        var navigated = false;
        gate.NavigateToSecurity = () => navigated = true;

        var allowed = await gate.EnsureAsync("写入配置");

        Assert.False(allowed);
        Assert.True(navigated);
        Assert.Equal(1, session.RefreshCalls);
        Assert.Contains("前往设置", dialogs.Confirmations);
    }

    [Fact]
    public async Task AllowsCurrentAdminModeAfterRefreshingSession()
    {
        var session = new StubAdminSessionService(adminMode: true, configured: true, sessionToken: "session");
        var dialogs = new RecordingDialogService();
        var gate = new AdminWriteGate(session, dialogs, new TestDiagnostics());

        var allowed = await gate.EnsureAsync("写入配置");

        Assert.True(allowed);
        Assert.Equal(1, session.RefreshCalls);
        Assert.Empty(dialogs.Confirmations);
    }

    private sealed class TestDiagnostics : IAppDiagnostics
    {
        public string? LastDiagnostic { get; private set; }
        public void Record(string message, Exception? error = null) => LastDiagnostic = message;
    }
}
