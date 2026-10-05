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
        if (args.Length >= 1 && args[0] == Services.InstallerManualExit.Argument)
        {
            Environment.ExitCode = Services.InstallerManualExit.Run(args);
            return;
        }
        if (args.Length >= 1 && args[0] == Services.InstallerUpdateLease.Argument)
        {
            Environment.ExitCode = Services.InstallerUpdateLease.Run(args);
            return;
        }
        // Manual installer relay: no Avalonia initialization or user-provided executable.
        // The update lease validates and impersonates the pinned original application's token.
        if (args.Length >= 1 && args[0] is Services.RunningInstanceExitRequester.InstallerProbeArgument
            or Services.RunningInstanceExitRequester.InstallerExitArgument)
        {
            var probeOnly = args[0] == Services.RunningInstanceExitRequester.InstallerProbeArgument;
            if ((probeOnly && args.Length != 1) || (!probeOnly && (args.Length != 2 ||
                !int.TryParse(args[1], out var limit) || limit is <= 0 or > 600)))
            {
                Console.Error.WriteLine("安装器中继参数无效");
                Environment.ExitCode = 1;
                return;
            }
            var timeout = probeOnly ? TimeSpan.Zero : TimeSpan.FromSeconds(int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture));
            Environment.ExitCode = Services.RunningInstanceExitRequester.RunInstaller(probeOnly, timeout);
            return;
        }
        // 安装器专用：请正在运行的实例安全退出，并等它真正释放单实例锁。
        // 必须在 Avalonia 启动之前处理——本进程只是一个"传话并等待"的替身，不建窗口、不拿锁。
        if (args.Length >= 1 && args[0] == Services.RunningInstanceExitRequester.Argument)
        {
            var timeout = args.Length >= 2 && int.TryParse(args[1], out var seconds) && seconds is > 0 and <= 600
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.FromSeconds(60);
            Environment.ExitCode = Services.RunningInstanceExitRequester.Run(timeout);
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
