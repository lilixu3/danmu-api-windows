using System.Diagnostics;
using DanmuApi.Platform;

namespace DanmuApi.Tests;

public sealed class WindowsProcessTerminatorTests
{
    [Fact]
    public async Task RejectsCurrentDesktopProcessBeforeTaskkill()
    {
        using var process = Process.GetCurrentProcess();
        var terminator = new WindowsProcessTerminator();

        var result = await terminator.TerminateAsync(
            process,
            "C:\\expected\\node.exe",
            "C:\\expected\\main.js",
            TimeSpan.FromSeconds(1));

        Assert.False(result.Succeeded);
        Assert.Contains("当前 Desktop", result.Diagnostic, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task RejectsWrongExecutableAndThenTerminatesVerifiedNode()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "进程元数据验证测试仅在 Windows 执行");
        var nodePath = Environment.GetEnvironmentVariable("DANMU_TEST_NODE_EXE");
        Skip.If(string.IsNullOrWhiteSpace(nodePath), "DANMU_TEST_NODE_EXE 未设置；跳过真实进程元数据测试");
        Skip.IfNot(File.Exists(nodePath), $"DANMU_TEST_NODE_EXE 指向不存在的文件: {nodePath}");

        using var directory = new TemporaryDirectory();
        var script = Path.Combine(directory.Path, "main.js");
        File.WriteAllText(script, "setTimeout(() => {}, 60000);", new System.Text.UTF8Encoding(false));
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.GetFullPath(nodePath!),
            Arguments = $"\"{script}\"",
            WorkingDirectory = directory.Path,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("无法启动测试 node.exe");

        try
        {
            await Task.Delay(300);
            Assert.False(process.HasExited);
            var terminator = new WindowsProcessTerminator();
            var rejected = await terminator.TerminateAsync(
                process,
                Path.Combine(directory.Path, "other-node.exe"),
                script,
                TimeSpan.FromSeconds(5));

            Assert.False(rejected.Succeeded);
            Assert.Contains("可执行文件不是预期", rejected.Diagnostic, StringComparison.Ordinal);
            Assert.False(process.HasExited);

            var terminated = await terminator.TerminateAsync(
                process,
                nodePath!,
                script,
                TimeSpan.FromSeconds(10));

            Assert.True(terminated.Succeeded, terminated.Diagnostic);
            Assert.True(process.HasExited);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
