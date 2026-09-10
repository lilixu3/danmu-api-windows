using System.Text;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class FirewallManagerTests
{
    [Fact]
    public async Task ExistingMatchingProgramSkipsElevation()
    {
        using var executor = new RecordingExecutor
        {
            Handler = (_, _) => new CommandExecutionResult(
                true,
                0,
                "Rule Name: existing\r\nProgram: C:\\Tools\\node.exe\r\n",
                string.Empty),
        };
        var manager = new FirewallManager(executor, () => @"C:\Windows");

        var result = await manager.EnsureInboundRuleAsync(@"C:\Tools\node.exe");

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.False(result.AuthorizationAttempted);
        Assert.Single(executor.Calls);
        Assert.DoesNotContain(executor.Calls, call => call.Path.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MissingRuleAddsWithElevatedEncodedCommandAndVerifies()
    {
        using var executor = new RecordingExecutor();
        var queryCount = 0;
        executor.Handler = (path, arguments) =>
        {
            if (path.EndsWith("netsh.exe", StringComparison.OrdinalIgnoreCase))
            {
                queryCount++;
                return new CommandExecutionResult(
                    true,
                    0,
                    queryCount == 1 ? "Rule Name: other\r\nProgram: C:\\Other\\node.exe" : "Program: C:\\Tools\\node.exe",
                    string.Empty);
            }

            return new CommandExecutionResult(true, 0, "added", string.Empty);
        };
        var manager = new FirewallManager(executor, () => @"C:\Windows");

        var result = await manager.EnsureInboundRuleAsync(@"C:\Tools\node.exe");

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.True(result.AuthorizationAttempted);
        Assert.Equal(2, executor.Calls.Count(call => call.Path.EndsWith("netsh.exe", StringComparison.OrdinalIgnoreCase)));
        var powershell = Assert.Single(executor.Calls, call => call.Path.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("-EncodedCommand", powershell.Arguments[2]);
        var outerScript = Encoding.Unicode.GetString(Convert.FromBase64String(powershell.Arguments[3]));
        Assert.Contains("Start-Process", outerScript, StringComparison.Ordinal);
        Assert.Contains("-Verb RunAs", outerScript, StringComparison.Ordinal);
        var innerBase64 = outerScript.Split("'-EncodedCommand','", 2, StringSplitOptions.None)[1].Split("'", 2, StringSplitOptions.None)[0];
        var innerScript = Encoding.Unicode.GetString(Convert.FromBase64String(innerBase64));
        Assert.Contains("node.exe", innerScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("profile=any", innerScript, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QueryFailureRetainsExitCodeAndOutput()
    {
        using var executor = new RecordingExecutor
        {
            Handler = (_, _) => new CommandExecutionResult(true, 5, "access denied", "firewall unavailable"),
        };
        var manager = new FirewallManager(executor, () => @"C:\Windows");

        var result = await manager.EnsureInboundRuleAsync(@"C:\Tools\node.exe");

        Assert.False(result.Succeeded);
        Assert.Contains("exitCode=5", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("access denied", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("firewall unavailable", result.Diagnostic, StringComparison.Ordinal);
    }

    private sealed class RecordingExecutor : IPlatformCommandExecutor, IDisposable
    {
        public List<Call> Calls { get; } = [];
        public Func<string, IReadOnlyList<string>, CommandExecutionResult>? Handler { get; set; }

        public CommandExecutionResult Execute(string executablePath, IReadOnlyList<string> arguments)
        {
            Calls.Add(new Call(executablePath, arguments.ToArray()));
            return Handler?.Invoke(executablePath, arguments)
                ?? new CommandExecutionResult(true, 0, string.Empty, string.Empty);
        }

        public void Dispose() => Calls.Clear();
    }

    private sealed record Call(string Path, IReadOnlyList<string> Arguments);
}
