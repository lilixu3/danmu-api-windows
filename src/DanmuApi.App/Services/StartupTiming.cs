using System.Diagnostics;
using System.Text;
using DanmuApi.Platform;

namespace DanmuApi.App.Services;

internal static class StartupTiming
{
    public static IDisposable Measure(AppPaths paths, string stage) => new Measurement(paths, stage);
    public static void Record(AppPaths paths, string stage, TimeSpan duration)
    {
        Directory.CreateDirectory(paths.HostLogsDirectory);
        File.AppendAllText(Path.Combine(paths.HostLogsDirectory, "startup-timing.log"), $"{DateTimeOffset.Now:o} stage={stage} elapsedMs={duration.TotalMilliseconds:0.0}{Environment.NewLine}", new UTF8Encoding(false));
    }
    public static void RecordFailure(AppPaths paths, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var text = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("o"))
            .Append(" startup-failed type=").Append(error.GetType().FullName)
            .Append(" HRESULT=0x").Append(error.HResult.ToString("X8"))
            .AppendLine()
            .AppendLine(error.ToString())
            .ToString();
        try
        {
            Directory.CreateDirectory(paths.HostLogsDirectory);
            File.AppendAllText(Path.Combine(paths.HostLogsDirectory, "startup-timing.log"), text, new UTF8Encoding(false));
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
        {
            // The process is already terminating; the failure must still be reported somewhere.
            Console.Error.WriteLine($"写入启动失败日志失败: {logError.Message}{Environment.NewLine}{text}");
        }
    }
    private sealed class Measurement(AppPaths paths, string stage) : IDisposable
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        public void Dispose() => Record(paths, stage, Stopwatch.GetElapsedTime(_start));
    }
}
