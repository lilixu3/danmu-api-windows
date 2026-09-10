using System.Text;
using DanmuApi.Platform;

namespace DanmuApi.Tests;

public sealed class AutostartManagerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyAutostartIsMigratedBeforeOldEntryIsDeleted(bool failWrite)
    {
        using var directory = new TemporaryDirectory();
        var executable = Path.Combine(directory.Path, "DanmuApi.exe");
        File.WriteAllText(executable, "stub");
        using var executor = new RecordingCommandExecutor
        {
            Handler = (path, args) => path.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase)
                ? new CommandExecutionResult(true, failWrite ? 1 : 0, "", failWrite ? "write failed" : "")
                : new CommandExecutionResult(true, args.Contains("DanmuApiDesktop") ? 0 : 1, "", ""),
        };
        var result = new AutostartManager(executor, () => executable, () => @"C:\Windows").RefreshIfEnabled();
        Assert.Equal(!failWrite, result.Succeeded);
        var calls = executor.Calls.ToList();
        var write = calls.FindIndex(call => call.Path.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase));
        var delete = calls.FindIndex(call => call.Arguments.Contains("delete"));
        Assert.True(write >= 0);
        if (failWrite) Assert.Equal(-1, delete);
        else Assert.True(delete > write);
    }

    [Fact]
    public void DevelopmentDotnetProcessIsExplicitlyUnsupported()
    {
        using var executor = new RecordingCommandExecutor();
        var manager = new AutostartManager(
            executor,
            executablePathResolver: () => @"C:\Tools\dotnet.exe",
            systemRootResolver: () => @"C:\Windows");

        var result = manager.Enable();

        Assert.False(result.Succeeded);
        Assert.False(result.Supported);
        Assert.Contains("dotnet.exe", result.Diagnostic, StringComparison.Ordinal);
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public void MissingExecutableIsExplicitlyUnsupported()
    {
        var manager = new AutostartManager(
            new RecordingCommandExecutor(),
            executablePathResolver: () => @"C:\missing\DanmuApi.exe",
            systemRootResolver: () => @"C:\Windows");

        var result = manager.Enable();

        Assert.False(result.Succeeded);
        Assert.False(result.Supported);
    }

    [Fact]
    public void EnableUsesRegArgumentsAndUtf16LeBase64PowerShellCommand()
    {
        using var directory = new TemporaryDirectory();
        var executable = Path.Combine(directory.Path, "Program Files", "Danmu Api", "DanmuApi.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "stub", new UTF8Encoding(false));
        using var executor = new RecordingCommandExecutor
        {
            Handler = (path, arguments) => path.Equals("reg.exe", StringComparison.OrdinalIgnoreCase)
                ? new CommandExecutionResult(true, 1, string.Empty, "value not found")
                : new CommandExecutionResult(true, 0, "ok", string.Empty),
        };
        var manager = new AutostartManager(
            executor,
            executablePathResolver: () => executable,
            systemRootResolver: () => @"C:\Windows");

        var result = manager.Enable();

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal(1, executor.Calls.Count(call => call.Path.Equals("reg.exe", StringComparison.OrdinalIgnoreCase)));
        var legacyDelete = executor.Calls.Single(call => call.Arguments.Contains("DanmuApiDesktop"));
        Assert.Equal(["delete", @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "/v", "DanmuApiDesktop", "/f"], legacyDelete.Arguments);

        var powershell = executor.Calls.Single(call => call.Path.EndsWith(@"System32\WindowsPowerShell\v1.0\powershell.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["-NoProfile", "-NonInteractive", "-EncodedCommand"], powershell.Arguments.Take(3));
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(powershell.Arguments[3]));
        Assert.Contains("Set-ItemProperty", script, StringComparison.Ordinal);
        Assert.Contains("DanmuApi", script, StringComparison.Ordinal);
        Assert.Contains("Danmu Api", script, StringComparison.Ordinal);
        Assert.Contains("--autostart", script, StringComparison.Ordinal);
        Assert.Contains("\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void IsEnabledUsesExitCodeAndRetainsCommandDiagnostics()
    {
        using var executor = new RecordingCommandExecutor
        {
            Handler = (_, _) => new CommandExecutionResult(true, 1, "localized output", "localized error"),
        };
        var manager = new AutostartManager(executor, () => null, () => @"C:\Windows");

        var result = manager.IsEnabled();

        Assert.True(result.Succeeded);
        Assert.False(result.Enabled);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("localized output", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("localized error", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void IsEnabledReportsUnexpectedRegistryFailure()
    {
        using var executor = new RecordingCommandExecutor
        {
            Handler = (_, _) => new CommandExecutionResult(true, 5, string.Empty, "access denied"),
        };
        var manager = new AutostartManager(executor, () => null, () => @"C:\Windows");

        var result = manager.IsEnabled();

        Assert.False(result.Succeeded);
        Assert.Equal(5, result.ExitCode);
        Assert.Contains("access denied", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void RefreshIfEnabledRewritesCurrentPathAndCleansLegacyValue()
    {
        using var directory = new TemporaryDirectory();
        var executable = Path.Combine(directory.Path, "DanmuApi.exe");
        File.WriteAllText(executable, "stub", new UTF8Encoding(false));
        using var executor = new RecordingCommandExecutor
        {
            Handler = (path, arguments) =>
            {
                if (path.Equals("reg.exe", StringComparison.OrdinalIgnoreCase) && arguments.Contains("DanmuApi"))
                {
                    return new CommandExecutionResult(true, 0, "DanmuApi enabled", string.Empty);
                }

                return new CommandExecutionResult(true, 0, string.Empty, string.Empty);
            },
        };
        var manager = new AutostartManager(executor, () => executable, () => @"C:\Windows");

        var result = manager.RefreshIfEnabled();

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Contains(executor.Calls, call => call.Path.Equals("reg.exe", StringComparison.OrdinalIgnoreCase) && call.Arguments.Contains("DanmuApiDesktop"));
        var powershell = executor.Calls.Single(call => call.Path.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase));
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(powershell.Arguments[3]));
        Assert.Contains(executable, script, StringComparison.Ordinal);
    }

    [Fact]
    public void DisableReportsCommandFailureInsteadOfPretendingItWorked()
    {
        using var executor = new RecordingCommandExecutor
        {
            Handler = (_, _) => new CommandExecutionResult(true, 5, string.Empty, "access denied"),
        };
        var manager = new AutostartManager(executor, () => null, () => @"C:\Windows");

        var result = manager.Disable();

        Assert.False(result.Succeeded);
        Assert.Equal(5, result.ExitCode);
        Assert.Contains("access denied", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void IsAutostartLaunchRequiresExactArgument()
    {
        Assert.True(AutostartManager.IsAutostartLaunch(["--autostart"]));
        Assert.False(AutostartManager.IsAutostartLaunch(["--autostart=true"]));
        Assert.False(AutostartManager.IsAutostartLaunch([]));
    }

    private sealed class RecordingCommandExecutor : IPlatformCommandExecutor, IDisposable
    {
        public List<Call> Calls { get; } = [];
        public Func<string, IReadOnlyList<string>, CommandExecutionResult>? Handler { get; init; }

        public CommandExecutionResult Execute(string executablePath, IReadOnlyList<string> arguments)
        {
            Calls.Add(new Call(executablePath, arguments.ToArray()));
            return Handler?.Invoke(executablePath, arguments) ?? new CommandExecutionResult(true, 0, string.Empty, string.Empty);
        }

        public void Dispose() => Calls.Clear();
    }

    private sealed record Call(string Path, IReadOnlyList<string> Arguments);
}
