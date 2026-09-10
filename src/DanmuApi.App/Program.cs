using Avalonia;
using Avalonia.Controls;
using System;

namespace DanmuApi.App;

sealed class Program
{
    internal static readonly long StartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--verify-app-release")
        {
            Environment.ExitCode = Services.ApplicationReleaseVerifier.Run(System.IO.Path.GetFullPath(args[1]));
            return;
        }
        if (args.Length == 2 && args[0] == "--app-update-recover")
        {
            Environment.ExitCode = Services.ApplicationUpdateHelper.Recover(args[1]);
            return;
        }
        if (args.Length == 2 && args[0] == "--apply-app-update")
        {
            Environment.ExitCode = Services.ApplicationUpdateHelper.Run(args[1]);
            return;
        }
        if (args.Length == 2 && args[0] == "--release-self-test")
        {
            Environment.ExitCode = ReleaseSelfTest.Run(System.IO.Path.GetFullPath(args[1]));
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
