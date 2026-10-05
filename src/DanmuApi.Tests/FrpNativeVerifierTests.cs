using System.Diagnostics;
using DanmuApi.Core.Frp;
using DanmuApi.Runtime.Frp;

namespace DanmuApi.Tests;

public sealed class FrpNativeVerifierTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutAndBoundedOutputFailExplicitlyAndCleanTheOwnedVerifier(bool oversized)
    {
        var node = Environment.GetEnvironmentVariable("DANMU_TEST_NODE_EXE");
        Skip.If(string.IsNullOrWhiteSpace(node) || !File.Exists(node), "需要真实 DANMU_TEST_NODE_EXE；跳过不算通过");
        using var directory = new TemporaryDirectory();
        // The verifier owns its Process handle/PID; no basename scan is involved. Use the installed fixture
        // directly instead of placing a mapped executable in a directory whose Dispose deletes every file.
        var executable = node!;
        File.WriteAllText(Path.Combine(directory.Path, "verify"),
            "require('node:fs').writeFileSync('pid', String(process.pid));" +
            (oversized ? "process.stdout.write('private-content'.repeat(3000));" : "") + "setInterval(()=>{},1000);");
        var plan = new FrpRunPlan(FrpRole.Client, executable, Path.Combine(directory.Path, "source.json"),
            directory.Path, directory.Path, 7400, "admin", "admin-secret", "api", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        var verifier = new FrpNativeVerifier(TimeSpan.FromMilliseconds(oversized ? 5000 : 1000));
        var result = await verifier.VerifyAsync(plan);
        Assert.False(result.Succeeded);
        Assert.Contains(oversized ? "输出超过" : "超时", result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private-content", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("清理", result.Diagnostic, StringComparison.Ordinal);
        AssertOwnedVerifierExited(directory.Path);
    }

    [SkippableFact]
    public async Task CancellationDoesNotLeaveAVerifierProcess()
    {
        var node = Environment.GetEnvironmentVariable("DANMU_TEST_NODE_EXE");
        Skip.If(string.IsNullOrWhiteSpace(node) || !File.Exists(node), "需要真实 DANMU_TEST_NODE_EXE；跳过不算通过");
        using var directory = new TemporaryDirectory();
        // The verifier owns its Process handle/PID; no basename scan is involved. Use the installed fixture
        // directly instead of placing a mapped executable in a directory whose Dispose deletes every file.
        var executable = node!;
        File.WriteAllText(Path.Combine(directory.Path, "verify"),
            "require('node:fs').writeFileSync('pid', String(process.pid));setInterval(()=>{},1000);");
        var plan = new FrpRunPlan(FrpRole.Client, executable, Path.Combine(directory.Path, "source.json"),
            directory.Path, directory.Path, 7400, "admin", "admin-secret", "api", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        using var cancel = new CancellationTokenSource();
        var pending = new FrpNativeVerifier().VerifyAsync(plan, cancel.Token);
        var pidFile = Path.Combine(directory.Path, "pid");
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!File.Exists(pidFile) && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(File.Exists(pidFile));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        AssertOwnedVerifierExited(directory.Path);
    }

    private static void AssertOwnedVerifierExited(string directory)
    {
        var pid = int.Parse(File.ReadAllText(Path.Combine(directory, "pid")), System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.True(process.HasExited, "Owned verifier is still alive");
            Assert.True(process.WaitForExit(0), "Owned verifier has not signaled native exit or released its working directory");
        }
        catch (ArgumentException)
        {
            // GetProcessById explicitly confirms that this tracked fixture PID no longer exists.
        }
    }
}
