using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace DanmuApi.App.Services;

/// <summary>
/// Requests application exit from startup code that runs before Avalonia enters its main loop.
/// Avalonia's Shutdown() tears the dispatcher down synchronously, while StartCore still enters
/// Dispatcher.MainLoop afterwards, so shutting down during initialization aborts the process with
/// "Cannot perform requested operation because the Dispatcher shut down". Deferring the request to
/// the first dispatcher turn keeps the exit clean and preserves the requested exit code.
/// </summary>
public static class StartupExit
{
    public static void Request(Action<Action> post, Action<int> shutdown, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(shutdown);
        post(() => shutdown(exitCode));
    }

    public static void Request(IClassicDesktopStyleApplicationLifetime lifetime, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        Request(action => Dispatcher.UIThread.Post(action), lifetime.Shutdown, exitCode);
    }
}
