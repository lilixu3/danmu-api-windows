using DanmuApi.Platform;

namespace DanmuApi.Tests;

/// <summary>
/// 外部命令执行器的两条硬约束：不许无限期等（UAC 提示不答、netsh 卡住都会让防火墙任务永久
/// Pending、UI 连取消都不生效），以及取消要真的能中断正在等的命令。
/// </summary>
public sealed class ProcessCommandExecutorTests
{
    private static string PingPath
    {
        get
        {
            Assert.True(OperatingSystem.IsWindows(), "命令执行器只在 Windows 上有意义");
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "ping.exe");
            Assert.True(File.Exists(path), $"测试需要系统自带的 ping.exe，找不到：{path}");
            return path;
        }
    }

    [Fact]
    public void TimedOutCommandIsTerminatedAndReported()
    {
        var executor = new ProcessCommandExecutor();
        var started = DateTimeOffset.UtcNow;

        // ping 31 次约 30 秒；预算 1 秒必须真的在几秒内带原因返回。
        var result = executor.Execute(
            PingPath,
            ["-n", "31", "127.0.0.1"],
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.False(result.Succeeded);
        Assert.Contains("超时", result.Diagnostic, StringComparison.Ordinal);
        Assert.True(elapsed < TimeSpan.FromSeconds(15), $"超时后没有终止进程树，白等了 {elapsed}");
    }

    [Fact]
    public void CancelledCommandStopsWaitingAndSaysSo()
    {
        var executor = new ProcessCommandExecutor();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var result = executor.Execute(
            PingPath,
            ["-n", "31", "127.0.0.1"],
            TimeSpan.FromSeconds(30),
            cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Contains("取消", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortCommandsStillReturnExitCodeAndOutput()
    {
        var executor = new ProcessCommandExecutor();

        var result = executor.Execute(
            PingPath,
            ["-n", "1", "-w", "100", "127.0.0.1"],
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.NotEmpty(result.CombinedOutput);
    }
}
