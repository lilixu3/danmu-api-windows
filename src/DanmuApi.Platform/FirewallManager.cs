using System.Security.Cryptography;
using System.Text;
using DanmuApi.Runtime;

namespace DanmuApi.Platform;

public sealed class FirewallManager : IRuntimeFirewall
{
    private const int MaximumDiagnosticLength = 2_000;
    private const string RuleNamePrefix = "DanmuApi Node Inbound";

    /// <summary>netsh 查询的预算：本地查询正常是秒级，30 秒还回不来就是卡住了。</summary>
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 提权那条命令的预算要留够：外层 powershell 会一直等 UAC 里的人做决定，
    /// 但也不能无限等——那样防火墙任务会永久 Pending，UI 的取消按钮也不生效。
    /// </summary>
    private static readonly TimeSpan ElevationTimeout = TimeSpan.FromMinutes(5);
    private readonly IPlatformCommandExecutor _commandExecutor;
    private readonly Func<string?> _systemRootResolver;

    public FirewallManager(
        IPlatformCommandExecutor? commandExecutor = null,
        Func<string?>? systemRootResolver = null)
    {
        _commandExecutor = commandExecutor ?? new ProcessCommandExecutor();
        _systemRootResolver = systemRootResolver ?? (() => Environment.GetEnvironmentVariable("SystemRoot"));
    }

    public Task<RuntimeFirewallResult> EnsureInboundRuleAsync(
        string nodeExe,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeExe);
        var normalized = NormalizePath(nodeExe);
        return Task.Run(() => EnsureCore(normalized, cancellationToken), cancellationToken);
    }

    private RuntimeFirewallResult EnsureCore(string nodeExe, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return RuntimeFirewallResult.Failure("Windows 防火墙管理仅支持 Windows");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var netsh = ResolveSystemTool("netsh.exe");
        if (netsh is null)
        {
            return RuntimeFirewallResult.Failure("SystemRoot 未设置，无法定位 netsh.exe");
        }

        var powershell = ResolveSystemTool(Path.Combine("WindowsPowerShell", "v1.0", "powershell.exe"));
        if (powershell is null)
        {
            return RuntimeFirewallResult.Failure("SystemRoot 未设置，无法定位绝对 powershell.exe");
        }

        var ruleName = BuildRuleName(nodeExe);

        // 先按「本应用自己写的 ASCII 规则名」探测：netsh 的输出跟随系统码页（中文系统是 CP936），
        // 拿安装路径去子串匹配在非 UTF-8 下会恒假 —— 安装路径含中文时每次启动都重弹 UAC、
        // 并堆积重复入站规则。规则名里带路径哈希，换了 node.exe 就是另一个名字，不会误判成已存在。
        var mine = QueryRuleByName(netsh, ruleName, cancellationToken);
        if (mine.Succeeded && RuleNamePresent(mine.CombinedOutput, ruleName))
        {
            return RuntimeFirewallResult.Success("Windows 防火墙已存在本应用为当前 node.exe 建立的入站规则");
        }

        var query = QueryAllRules(netsh, cancellationToken);
        if (!query.Succeeded)
        {
            return RuntimeFirewallResult.Failure($"查询 Windows 防火墙规则失败：{DescribeCommand(query)}");
        }

        if (ContainsProgramPath(query.CombinedOutput, nodeExe))
        {
            return RuntimeFirewallResult.Success("Windows 防火墙已存在当前 node.exe 入站规则");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var add = AddRule(netsh, powershell, nodeExe, ruleName, cancellationToken);
        if (!add.Succeeded)
        {
            return RuntimeFirewallResult.Failure(
                $"添加 Windows 防火墙规则失败（可能取消了 UAC 授权）：{DescribeCommand(add)}",
                authorizationAttempted: true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var verify = QueryRuleByName(netsh, ruleName, cancellationToken);
        if (!verify.Succeeded)
        {
            return RuntimeFirewallResult.Failure(
                $"Windows 防火墙规则添加后复查失败：{DescribeCommand(verify)}",
                authorizationAttempted: true);
        }

        return RuleNamePresent(verify.CombinedOutput, ruleName)
            ? RuntimeFirewallResult.Success("Windows 防火墙规则已添加并复查通过", authorizationAttempted: true)
            : RuntimeFirewallResult.Failure(
                $"Windows 防火墙命令返回成功，但复查没有找到规则「{ruleName}」：{LimitDiagnostic(verify.CombinedOutput)}",
                authorizationAttempted: true);
    }

    /// <summary>本应用为某个 node.exe 生成的规则名：ASCII + 路径哈希，可安全放进 netsh 的 name= 参数。</summary>
    private static string BuildRuleName(string nodeExe) => $"{RuleNamePrefix} {ComputePathId(nodeExe)}";

    /// <summary>
    /// 按名字精确查询一条规则。name= 的值必须作为**一个**参数、且不带内层引号传给 netsh：
    /// 加引号会被 netsh 判成「指定的值无效」，实测确认。
    /// </summary>
    private CommandExecutionResult QueryRuleByName(string netsh, string ruleName, CancellationToken cancellationToken) =>
        Execute(
            netsh,
            QueryTimeout,
            cancellationToken,
            "advfirewall", "firewall", "show", "rule", $"name={ruleName}");

    private CommandExecutionResult QueryAllRules(string netsh, CancellationToken cancellationToken) =>
        Execute(
            netsh,
            QueryTimeout,
            cancellationToken,
            "advfirewall",
            "firewall",
            "show",
            "rule",
            "name=all",
            "dir=in",
            "verbose");

    private static bool RuleNamePresent(string output, string ruleName) =>
        output.Contains(ruleName, StringComparison.OrdinalIgnoreCase);

    private CommandExecutionResult AddRule(
        string netsh,
        string powershell,
        string nodeExe,
        string ruleName,
        CancellationToken cancellationToken)
    {
        var innerScript =
            $"& '{EscapePowerShellLiteral(netsh)}' advfirewall firewall add rule " +
            $"name='{EscapePowerShellLiteral(ruleName)}' dir=in action=allow " +
            $"program='{EscapePowerShellLiteral(nodeExe)}' enable=yes profile=any protocol=TCP";
        var innerEncoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(innerScript));
        var outerScript =
            "$arguments = @('-NoProfile','-NonInteractive','-EncodedCommand','" +
            innerEncoded +
            "'); " +
            "$process = Start-Process -FilePath '" +
            EscapePowerShellLiteral(powershell) +
            "' -Verb RunAs -Wait -PassThru -ArgumentList $arguments; " +
            "if ($null -eq $process) { exit 1 }; " +
            "exit $process.ExitCode";
        var outerEncoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(outerScript));
        return Execute(
            powershell,
            ElevationTimeout,
            cancellationToken,
            "-NoProfile",
            "-NonInteractive",
            "-EncodedCommand",
            outerEncoded);
    }

    private CommandExecutionResult Execute(
        string executable,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        try
        {
            return _commandExecutor.Execute(executable, arguments, timeout, cancellationToken);
        }
        catch (Exception error)
        {
            return CommandExecutionResult.Failure($"命令执行器异常：{Describe(error)}");
        }
    }

    private string? ResolveSystemTool(string relativePath)
    {
        try
        {
            var systemRoot = _systemRootResolver();
            if (string.IsNullOrWhiteSpace(systemRoot))
            {
                return null;
            }

            var path = Path.Combine(SystemPaths.NativeSystemDirectory(systemRoot), relativePath);
            return Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool ContainsProgramPath(string output, string nodeExe)
    {
        var normalizedOutput = output.Replace('/', '\\');
        return normalizedOutput.Contains(nodeExe, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path.Trim());
        if (!Path.IsPathFullyQualified(fullPath))
        {
            throw new ArgumentException("node.exe 路径必须是绝对路径", nameof(path));
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string ComputePathId(string path)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
        return Convert.ToHexString(bytes)[..12];
    }

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string DescribeCommand(CommandExecutionResult command)
    {
        var detail = string.IsNullOrWhiteSpace(command.Diagnostic)
            ? $"exitCode={command.ExitCode?.ToString() ?? "未启动"}"
            : command.Diagnostic;
        var output = command.CombinedOutput;
        return output.Length == 0
            ? LimitDiagnostic(detail)
            : LimitDiagnostic($"{detail}; output={output}");
    }

    private static string Describe(Exception error) =>
        string.IsNullOrWhiteSpace(error.Message)
            ? error.GetType().Name
            : error.Message.Replace('\r', ' ').Replace('\n', ' ');

    private static string LimitDiagnostic(string value) => value.Length <= MaximumDiagnosticLength
        ? value
        : value[..MaximumDiagnosticLength] + "…";
}
