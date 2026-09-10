using System.Text;
using DanmuApi.Platform;

namespace DanmuApi.App.Services;

public interface IAppDiagnostics
{
    string? LastDiagnostic { get; }
    void Record(string message, Exception? error = null);
}

public sealed class AppDiagnostics : IAppDiagnostics
{
    private readonly AppPaths _paths;
    private string? _lastDiagnostic;

    public AppDiagnostics(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public string? LastDiagnostic => Volatile.Read(ref _lastDiagnostic);

    public void Record(string message, Exception? error = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var diagnostic = error is null ? message : $"{message}: {error.Message}";
        Volatile.Write(ref _lastDiagnostic, diagnostic);

        try
        {
            var directory = Path.GetDirectoryName(_paths.LifecycleLogFile)
                ?? throw new IOException($"生命周期日志路径没有父目录: {_paths.LifecycleLogFile}");
            Directory.CreateDirectory(directory);
            var line = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("O"))
                .Append("  ")
                .Append(diagnostic)
                .AppendLine()
                .ToString();
            File.AppendAllText(_paths.LifecycleLogFile, line, new UTF8Encoding(false));
        }
        catch (Exception logError)
        {
            Volatile.Write(ref _lastDiagnostic, $"{diagnostic}; 写入生命周期日志失败: {logError.Message}");
        }
    }
}
