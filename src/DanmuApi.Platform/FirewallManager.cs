using System.Security.Cryptography;
using System.Text;
using DanmuApi.Runtime;

namespace DanmuApi.Platform;

public sealed class FirewallManager : IRuntimeFirewall
{
    private const int MaximumDiagnosticLength = 2_000;
    private const string RuleNamePrefix = "DanmuApi Node Inbound";
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

        var query = QueryRule(netsh, nodeExe);
        if (!query.Succeeded)
        {
            return RuntimeFirewallResult.Failure($"查询 Windows 防火墙规则失败：{DescribeCommand(query)}");
        }

        if (ContainsProgramPath(query.CombinedOutput, nodeExe))
        {
            return RuntimeFirewallResult.Success("Windows 防火墙已存在当前 node.exe 入站规则");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var add = AddRule(netsh, powershell, nodeExe);
        if (!add.Succeeded)
        {
            return RuntimeFirewallResult.Failure(
                $"添加 Windows 防火墙规则失败（可能取消了 UAC 授权）：{DescribeCommand(add)}",
                authorizationAttempted: true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var verify = QueryRule(netsh, nodeExe);
        if (!verify.Succeeded)
        {
            return RuntimeFirewallResult.Failure(
                $"Windows 防火墙规则添加后复查失败：{DescribeCommand(verify)}",
                authorizationAttempted: true);
        }

        return ContainsProgramPath(verify.CombinedOutput, nodeExe)
            ? RuntimeFirewallResult.Success("Windows 防火墙规则已添加并复查通过", authorizationAttempted: true)
            : RuntimeFirewallResult.Failure(
                $"Windows 防火墙命令返回成功，但未找到当前 node.exe 路径规则：{LimitDiagnostic(verify.CombinedOutput)}",
                authorizationAttempted: true);
    }

    private CommandExecutionResult QueryRule(string netsh, string nodeExe) =>
        Execute(
            netsh,
            "advfirewall",
            "firewall",
            "show",
            "rule",
            "name=all",
            "dir=in",
            "verbose");

    private CommandExecutionResult AddRule(string netsh, string powershell, string nodeExe)
    {
        var ruleName = $"{RuleNamePrefix} {ComputePathId(nodeExe)}";
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
            "-NoProfile",
            "-NonInteractive",
            "-EncodedCommand",
            outerEncoded);
    }

    private CommandExecutionResult Execute(string executable, params string[] arguments)
    {
        try
        {
            return _commandExecutor.Execute(executable, arguments);
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

            var path = relativePath.Equals("netsh.exe", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(systemRoot, "System32", relativePath)
                : Path.Combine(systemRoot, "System32", relativePath);
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
