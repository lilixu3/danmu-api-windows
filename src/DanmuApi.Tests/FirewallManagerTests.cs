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
        // 先按本应用的 ASCII 规则名探测（未命中），再退回全量规则里的路径匹配。
        Assert.Equal(2, executor.Calls.Count(call => call.Path.EndsWith("netsh.exe", StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(executor.Calls, call => call.Path.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RuleCreatedByNameIsFoundWithoutDecodingLocalizedOutput()
    {
        // netsh 的输出跟随系统码页（中文系统是 CP936），拿安装路径去子串匹配会因解码失败而恒假，
        // 于是每次启动都重弹 UAC 并堆积重复规则。规则名是本应用自己写的 ASCII + 路径哈希，
        // 按名字探测就不依赖解码：这里让 netsh 只回显名字、完全不给路径行。
        var queries = new List<string>();
        using var executor = new RecordingExecutor
        {
            Handler = (_, arguments) =>
            {
                var nameArgument = arguments.FirstOrDefault(argument =>
                    argument.StartsWith("name=", StringComparison.OrdinalIgnoreCase));
                if (nameArgument is null)
                {
                    return new CommandExecutionResult(true, 0, string.Empty, string.Empty);
                }

                queries.Add(nameArgument);
                return new CommandExecutionResult(
                    true,
                    0,
                    $"规则名称: {nameArgument["name=".Length..]} 已启用: 是",
                    string.Empty);
            },
        };
        var manager = new FirewallManager(executor, () => @"C:\Windows");

        var result = await manager.EnsureInboundRuleAsync(@"C:\Tools\node.exe");

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.False(result.AuthorizationAttempted);
        var probe = Assert.Single(queries);
        Assert.StartsWith("name=DanmuApi Node Inbound ", probe, StringComparison.Ordinal);
        // 规则名里只有路径哈希，不含路径本身：换一份 node.exe 就是另一个名字，
        // 不会把别的程序（或旧路径）的规则误判成「已放行」。
        Assert.DoesNotContain("Tools", probe, StringComparison.Ordinal);
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
                // 按名探测/复查时回显查询的名字（模拟 netsh 的「规则名称:」行），
                // 全量查询只在添加之后的一次里带上我们的路径。
                var nameArgument = arguments.FirstOrDefault(argument =>
                    argument.StartsWith("name=", StringComparison.OrdinalIgnoreCase));
                if (nameArgument is not null && !nameArgument.Equals("name=all", StringComparison.OrdinalIgnoreCase))
                {
                    return new CommandExecutionResult(
                        true,
                        0,
                        queryCount == 1
                            ? "没有与指定标准相匹配的规则。"
                            : $"规则名称: {nameArgument["name=".Length..]}",
                        string.Empty);
                }

                return new CommandExecutionResult(true, 0, "Rule Name: other\r\nProgram: C:\\Other\\node.exe", string.Empty);
            }

            return new CommandExecutionResult(true, 0, "added", string.Empty);
        };
        var manager = new FirewallManager(executor, () => @"C:\Windows");

        var result = await manager.EnsureInboundRuleAsync(@"C:\Tools\node.exe");

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.True(result.AuthorizationAttempted);
        // 名称探测 → 全量查询 → 提权添加 → 名称复查
        Assert.Equal(3, executor.Calls.Count(call => call.Path.EndsWith("netsh.exe", StringComparison.OrdinalIgnoreCase)));
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
