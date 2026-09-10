using System.Text;
using DanmuApi.App.Services;
using DanmuApi.Platform;

namespace DanmuApi.Tests;

public sealed class DesktopNotificationServiceTests
{
    [Fact]
    public async Task UsesAbsolutePowerShellAndDoesNotIncludeApiToken()
    {
        var executor = new RecordingExecutor();
        var service = new WindowsToastNotificationService(executor, () => @"C:\Windows");

        var result = await service.ShowAsync("弹幕 API", "服务已在后台启动，端口 9321");

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Single(executor.Calls);
        var call = executor.Calls[0];
        Assert.EndsWith(@"System32\WindowsPowerShell\v1.0\powershell.exe", call.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["-NoProfile", "-NonInteractive", "-EncodedCommand"], call.Arguments.Take(3));
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(call.Arguments[3]));
        Assert.Contains("ToastNotificationManager", script, StringComparison.Ordinal);
        Assert.Contains("9321", script, StringComparison.Ordinal);
        Assert.DoesNotContain("87654321", script, StringComparison.Ordinal);
    }

    private sealed class RecordingExecutor : IPlatformCommandExecutor
    {
        public List<Call> Calls { get; } = [];

        public CommandExecutionResult Execute(string executablePath, IReadOnlyList<string> arguments)
        {
            Calls.Add(new Call(executablePath, arguments.ToArray()));
            return new(true, 0, string.Empty, string.Empty);
        }
    }

    private sealed record Call(string Path, IReadOnlyList<string> Arguments);
}
