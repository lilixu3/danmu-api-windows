using DanmuApi.App.Services;

namespace DanmuApi.Tests;

public sealed class StartupExitTests
{
    [Fact]
    public void ShutdownIsDeferredUntilThePostedDispatcherTurnRuns()
    {
        Action? posted = null;
        var shutdownCode = -1;
        StartupExit.Request(action => posted = action, code => shutdownCode = code, 1);
        // Shutting down synchronously during Avalonia initialization kills the dispatcher and
        // makes the following MainLoop throw; the request must stay queued until the loop runs.
        Assert.NotNull(posted);
        Assert.Equal(-1, shutdownCode);
        posted!();
        Assert.Equal(1, shutdownCode);
    }

    [Fact]
    public void ExitCodeIsPreservedAndOnlyOneShutdownIsRequested()
    {
        var posted = new List<Action>();
        var codes = new List<int>();
        StartupExit.Request(posted.Add, codes.Add, 7);
        Assert.Single(posted);
        Assert.Empty(codes);
        posted[0]();
        Assert.Equal([7], codes);
    }

    [Fact]
    public void MissingDelegatesAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => StartupExit.Request(null!, _ => { }, 0));
        Assert.Throws<ArgumentNullException>(() => StartupExit.Request(_ => { }, null!, 0));
    }
}
