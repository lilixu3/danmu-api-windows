using System.Diagnostics;
using System.Text;

namespace DanmuApi.Platform;

public sealed record CommandExecutionResult(
    bool Started,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    string? Diagnostic = null)
{
    public bool Succeeded => Started && ExitCode == 0;

    public string CombinedOutput => string.Join(
        Environment.NewLine,
        new[] { StandardOutput.Trim(), StandardError.Trim() }.Where(value => value.Length > 0));

    public static CommandExecutionResult Failure(string diagnostic) =>
        new(false, null, string.Empty, string.Empty, diagnostic);
}

public interface IPlatformCommandExecutor
{
    CommandExecutionResult Execute(string executablePath, IReadOnlyList<string> arguments);

    /// <summary>
    /// 带超时与取消的执行。外部命令（reg.exe / netsh.exe / 提权后的 powershell）都可能无限期挂住——
    /// UAC 提示不答就是永久 Pending，UI 连取消都不生效，所以这两件事必须由执行器负责。
    /// 默认实现退化为无超时版本，好让测试替身不必跟着改签名。
    /// </summary>
    CommandExecutionResult Execute(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        Execute(executablePath, arguments);

    CommandExecutionResult Execute(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        Execute(executablePath, arguments, timeout, cancellationToken);

    CommandExecutionResult Execute(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string> environment) =>
        throw new NotSupportedException("此命令执行器不支持隔离 Git 环境");
}

public sealed class ProcessCommandExecutor : IPlatformCommandExecutor
{
    /// <summary>没显式给预算时的兜底：够 reg.exe 写完一个键，又不至于把 UI 挂死。</summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public CommandExecutionResult Execute(string executablePath, IReadOnlyList<string> arguments) =>
        Execute(executablePath, arguments, DefaultTimeout, CancellationToken.None);

    public CommandExecutionResult Execute(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        Execute(executablePath, arguments, string.Empty, timeout, cancellationToken);

    public CommandExecutionResult Execute(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        Execute(executablePath, arguments, workingDirectory, timeout, cancellationToken,
            new Dictionary<string, string>());

    public CommandExecutionResult Execute(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string> environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                    ? Environment.CurrentDirectory
                    : Path.GetFullPath(workingDirectory),
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        if (environment.ContainsKey("GIT_CONFIG_NOSYSTEM"))
        {
            foreach (var key in process.StartInfo.Environment.Keys
                .Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase) ||
                              key.StartsWith("GCM_", StringComparison.OrdinalIgnoreCase) ||
                              key.Equals("SSH_ASKPASS", StringComparison.OrdinalIgnoreCase))
                .ToArray())
            {
                process.StartInfo.Environment.Remove(key);
            }
        }
        foreach (var (key, value) in environment)
        {
            process.StartInfo.Environment[key] = value;
        }

        try
        {
            if (!process.Start())
            {
                return CommandExecutionResult.Failure($"启动命令失败: {executablePath}");
            }

            var standardOutputTask = process.StandardOutput.ReadToEndAsync();
            var standardErrorTask = process.StandardError.ReadToEndAsync();
            // 轮询而不是无参 WaitForExit()：后者在子进程等 UAC / netsh 卡住时会永久挂住调用线程，
            // 取消令牌也就永远没有生效的机会。
            var deadline = DateTime.UtcNow + timeout;
            while (!process.WaitForExit(200))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Abort(process, executablePath, "命令已取消");
                }

                if (DateTime.UtcNow >= deadline)
                {
                    Abort(process, executablePath, $"命令超时（{(int)timeout.TotalSeconds} 秒）");
                }
            }

            // 进程已退出，只给读尾流留一小段预算，避免流句柄没关闭时的第二次挂死。
            var drained = false;
            try
            {
                drained = Task.WaitAll([standardOutputTask, standardErrorTask], TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // 读取流本身失败：下面按"没读完"处理，诊断里带上已经拿到的部分。
            }

            var standardOutput = standardOutputTask.IsCompletedSuccessfully ? standardOutputTask.Result : string.Empty;
            var standardError = standardErrorTask.IsCompletedSuccessfully ? standardErrorTask.Result : string.Empty;
            if (!drained)
            {
                standardError = string.IsNullOrWhiteSpace(standardError)
                    ? "命令输出读取超时"
                    : standardError + " | 命令输出读取超时";
            }

            return new CommandExecutionResult(
                true,
                process.ExitCode,
                standardOutput,
                standardError);
        }
        catch (TimeoutException error)
        {
            // 取消/超时走这条：诊断里就是原因本身，不要包成"执行命令异常"。
            return CommandExecutionResult.Failure(error.Message ?? "命令未完成");
        }
        catch (Exception error)
        {
            return CommandExecutionResult.Failure($"执行命令异常: {Describe(error)}");
        }
    }

    /// <summary>
    /// 超时/取消都要留下痕迹：直接返回诊断，不静默吞掉进程——提权窗口里的 netsh 可能仍在跑，
    /// 调用方要靠这句话判断该不该复查规则。
    /// </summary>
    private static void Abort(Process process, string executablePath, string reason)
    {
        var diagnostic = $"{reason}: {Path.GetFileName(executablePath)}";
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch (Exception error)
        {
            diagnostic += $"（终止进程失败：{Describe(error)}）";
        }

        throw new TimeoutException(diagnostic);
    }

    private static string Describe(Exception error) => string.IsNullOrWhiteSpace(error.Message)
        ? error.GetType().Name
        : error.Message.Replace('\r', ' ').Replace('\n', ' ');
}

public sealed record AutostartResult(
    bool Succeeded,
    string Diagnostic,
    bool? Enabled = null,
    bool Supported = true,
    int? ExitCode = null,
    string Output = "",
    AutostartState State = AutostartState.Unknown,
    bool? IsRegistered = null)
{
    public static AutostartResult Success(
        string diagnostic,
        bool? enabled = null,
        int? exitCode = null,
        string output = "") => new(true, diagnostic, enabled, true, exitCode, output);

    public static AutostartResult Unsupported(string diagnostic) => new(false, diagnostic, null, false, State: AutostartState.Unsupported);
}

/// <summary>
/// Manages the HKCU Run value used to launch the packaged desktop executable.
/// </summary>
public sealed class AutostartManager
{
    private const string RunKey = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string PowerShellRunKey = @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DanmuApi";
    private const string AutostartArgument = "--autostart";
    private static readonly string[] LegacyValueNames = ["DanmuApiDesktop"];
    private const int MaximumDiagnosticLength = 2_000;

    private readonly IPlatformCommandExecutor _commandExecutor;
    private readonly Func<string?> _executablePathResolver;
    private readonly Func<string?> _systemRootResolver;
    private readonly IAutostartRegistryReader? _registryReader;

    public AutostartManager(
        IPlatformCommandExecutor? commandExecutor = null,
        Func<string?>? executablePathResolver = null,
        Func<string?>? systemRootResolver = null,
        IAutostartRegistryReader? registryReader = null)
    {
        // Explicit executor-only injection retains the old test adapter. Production always reads Registry directly.
        _registryReader = registryReader ?? (commandExecutor is null or ProcessCommandExecutor ? new WindowsAutostartRegistryReader() : null);
        _commandExecutor = commandExecutor ?? new ProcessCommandExecutor();
        _executablePathResolver = executablePathResolver ?? ResolveCurrentExecutablePath;
        _systemRootResolver = systemRootResolver ?? (() => Environment.GetEnvironmentVariable("SystemRoot"));
    }

    public string? ResolveExecutablePath() => ResolveExecutablePathCore().Path;

    public bool IsSupported() => ResolveExecutablePathCore().Path is not null;

    public AutostartResult IsEnabled()
    {
        if (_registryReader is not null) return QueryExact();
        CommandExecutionResult query;
        try
        {
            query = ExecuteReg("query", RunKey, "/v", ValueName);
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Failure($"查询开机自启异常: {Describe(error)}", error: error);
        }

        if (!query.Started)
        {
            return Failure($"查询开机自启失败: {DescribeCommand(query)}", query);
        }

        if (query.ExitCode is not (0 or 1))
        {
            return Failure($"查询注册表自启值失败: {DescribeCommand(query)}", query);
        }

        var enabled = query.ExitCode == 0;
        var state = enabled ? "开机自启已启用" : "开机自启未启用";
        var diagnostic = AppendCommandDiagnostic(state, query);
        return AutostartResult.Success(diagnostic, enabled, query.ExitCode, query.CombinedOutput);
    }

    private AutostartRegistryEntry ReadEntry(string name)
    {
        try { return _registryReader!.Read(name); }
        catch (Exception error)
        {
            return new(false, Succeeded: false,
                Diagnostic: $"读取自启配置异常（{error.GetType().Name}, HRESULT=0x{error.HResult:X8}）");
        }
    }

    private AutostartResult QueryExact(string name = ValueName)
    {
        var executable = ResolveExecutablePathCore();
        if (executable.Path is null) return AutostartResult.Unsupported(executable.Diagnostic);
        var entry = ReadEntry(name);
        if (!entry.Succeeded) return Failure(entry.Diagnostic);
        if (!entry.Exists) return new(true, "开机自启未登记", false, State: AutostartState.NotRegistered, IsRegistered: false);
        if (entry.SystemEnabled is null)
            return new(false, "自启已登记，但无法识别 Windows 启动审批状态", State: AutostartState.Unknown, IsRegistered: true);
        if (entry.SystemEnabled == false)
            return new(true, "自启已登记，但被 Windows 禁用；请在系统启动应用设置中启用", false,
                State: AutostartState.SystemDisabled, IsRegistered: true);
        var expected = $"\"{executable.Path}\" {AutostartArgument}";
        if (!string.Equals(entry.Command, expected, StringComparison.OrdinalIgnoreCase))
            return new(true, "自启已登记，但路径或启动参数与当前应用不匹配", false,
                State: AutostartState.InvalidPath, IsRegistered: true);
        return new(true, "开机自启已登记且系统允许启动", true, State: AutostartState.Registered, IsRegistered: true);
    }

    private AutostartResult DeleteExact(string name)
    {
        var before = ReadEntry(name);
        if (!before.Succeeded) return Failure(before.Diagnostic);
        if (!before.Exists) return new(true, "自启登记不存在", false, State: AutostartState.NotRegistered, IsRegistered: false);
        var deleted = ExecuteReg("delete", RunKey, "/v", name, "/f");
        if (!deleted.Succeeded) return ExactCommandFailure("删除自启登记失败", deleted);
        var after = ReadEntry(name);
        if (!after.Succeeded) return Failure(after.Diagnostic);
        if (after.Exists) return Failure("删除后回读发现自启登记仍存在");
        return new(true, "自启登记已删除", false, State: AutostartState.NotRegistered, IsRegistered: false);
    }

    private static AutostartResult ExactCommandFailure(string action, CommandExecutionResult command) =>
        new(false, $"{action}（started={command.Started}, exitCode={command.ExitCode?.ToString() ?? "无"}；命令输出已隐去）",
            ExitCode: command.ExitCode);

    public AutostartResult Enable()
    {
        var executable = ResolveExecutablePathCore();
        if (executable.Path is null)
        {
            return AutostartResult.Unsupported(executable.Diagnostic);
        }

        var executablePath = executable.Path;

        var value = $"\"{executablePath}\" {AutostartArgument}";
        var write = WriteRunValue(value);
        if (!write.Started || write.ExitCode != 0)
        {
            return _registryReader is not null ? ExactCommandFailure("写入自启登记失败", write)
                : Failure($"写入注册表失败: {DescribeCommand(write)}", write);
        }

        if (_registryReader is not null)
        {
            var registered = ReadEntry(ValueName);
            if (!registered.Succeeded) return Failure(registered.Diagnostic);
            if (!registered.Exists || !string.Equals(registered.Command, value, StringComparison.Ordinal))
                return Failure("写入后回读校验失败；保留旧版自启登记");
        }
        var cleanup = DeleteLegacyValues();
        if (!cleanup.Succeeded) return cleanup;
        if (_registryReader is not null) return QueryExact();
        var diagnostic = cleanup.Diagnostic.Length == 0
            ? "开机自启已启用"
            : $"开机自启已启用；{cleanup.Diagnostic}";
        return AutostartResult.Success(diagnostic, enabled: true, write.ExitCode, write.CombinedOutput);
    }

    public AutostartResult Disable()
    {
        if (_registryReader is not null)
        {
            if (!IsSupported()) return AutostartResult.Unsupported("当前运行方式不支持开机自启");
            // Delete legacy first: even partial failure must not leave a legacy entry to resurrect.
            var legacyCleanup = DeleteLegacyValues();
            if (!legacyCleanup.Succeeded) return legacyCleanup;
            return DeleteExact(ValueName);
        }
        CommandExecutionResult delete;
        try
        {
            delete = ExecuteReg("delete", RunKey, "/v", ValueName, "/f");
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Failure($"删除开机自启异常: {Describe(error)}", error: error);
        }

        if (!delete.Started)
        {
            return Failure($"删除注册表自启值失败: {DescribeCommand(delete)}", delete);
        }

        if (delete.ExitCode is not (0 or 1))
        {
            return Failure($"删除注册表自启值失败: {DescribeCommand(delete)}", delete);
        }

        var cleanup = DeleteLegacyValues();
        if (!cleanup.Succeeded) return cleanup;
        var diagnostic = delete.ExitCode == 1
            ? "开机自启本来就未启用（注册表值不存在）"
            : "开机自启已禁用";
        return AutostartResult.Success(diagnostic, enabled: false, delete.ExitCode, delete.CombinedOutput);
    }

    public AutostartResult RefreshIfEnabled()
    {
        if (_registryReader is not null)
        {
            var exact = QueryExact();
            if (!exact.Succeeded || exact.State == AutostartState.SystemDisabled) return exact;
            if (exact.IsRegistered == true) return Enable();
            foreach (var name in LegacyValueNames)
            {
                var legacy = QueryExact(name);
                if (!legacy.Succeeded || legacy.State == AutostartState.SystemDisabled) return legacy;
                if (legacy.IsRegistered == true) return Enable();
            }
            return exact;
        }
        var state = IsEnabled();
        if (!state.Succeeded)
        {
            return state;
        }

        if (state.Enabled != true)
        {
            foreach (var legacyName in LegacyValueNames)
            {
                var legacy = ExecuteReg("query", RunKey, "/v", legacyName);
                if (!legacy.Started || legacy.ExitCode is not (0 or 1))
                    return Failure($"读取旧版自启配置失败: {DescribeCommand(legacy)}", legacy);
                if (legacy.ExitCode == 0)
                    return Enable();
            }
            return state;
        }

        var executable = ResolveExecutablePathCore();
        if (executable.Path is null)
        {
            return AutostartResult.Unsupported($"开机自启已启用，但无法刷新当前路径: {executable.Diagnostic}");
        }

        var executablePath = executable.Path;

        var write = WriteRunValue($"\"{executablePath}\" {AutostartArgument}");
        if (!write.Started || write.ExitCode != 0)
        {
            return Failure($"刷新注册表自启值失败: {DescribeCommand(write)}", write);
        }

        var cleanup = DeleteLegacyValues();
        if (!cleanup.Succeeded) return cleanup;
        var diagnostic = cleanup.Diagnostic.Length == 0
            ? "开机自启路径已刷新"
            : $"开机自启路径已刷新；{cleanup.Diagnostic}";
        return AutostartResult.Success(diagnostic, enabled: true, write.ExitCode, write.CombinedOutput);
    }

    public static bool IsAutostartLaunch(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Any(argument => string.Equals(argument, AutostartArgument, StringComparison.Ordinal));
    }

    public CommandExecutionResult WriteRunValue(string value, string name = ValueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (value.Contains('\0') || value.Contains('\r') || value.Contains('\n'))
        {
            return CommandExecutionResult.Failure("注册表自启值包含非法控制字符");
        }

        string powershellPath;
        try
        {
            var systemRoot = _systemRootResolver();
            if (string.IsNullOrWhiteSpace(systemRoot))
            {
                return CommandExecutionResult.Failure("SystemRoot 未设置，无法定位绝对 powershell.exe");
            }

            powershellPath = Path.GetFullPath(Path.Combine(SystemPaths.NativeSystemDirectory(systemRoot), "WindowsPowerShell", "v1.0", "powershell.exe"));
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
        {
            return CommandExecutionResult.Failure($"无法定位绝对 powershell.exe: {Describe(error)}");
        }

        var script = $"$ErrorActionPreference = 'Stop'; $key = '{EscapePowerShellLiteral(PowerShellRunKey)}'; " +
                     "New-Item -Path $key -Force | Out-Null; " +
                     $"Set-ItemProperty -Path $key -Name '{EscapePowerShellLiteral(name)}' -Value '{EscapePowerShellLiteral(value)}'";
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return ExecuteCommand(
            powershellPath,
            "-NoProfile",
            "-NonInteractive",
            "-EncodedCommand",
            encodedCommand);
    }

    private AutostartResult DeleteLegacyValues(string? prefixDiagnostic = null)
    {
        var diagnostics = new List<string>();
        if (!string.IsNullOrWhiteSpace(prefixDiagnostic))
        {
            diagnostics.Add(prefixDiagnostic);
        }

        foreach (var legacyName in LegacyValueNames)
        {
            if (_registryReader is not null)
            {
                var result = DeleteExact(legacyName);
                if (!result.Succeeded) return result;
                diagnostics.Add(result.Diagnostic);
                continue;
            }
            CommandExecutionResult delete;
            try
            {
                delete = ExecuteReg("delete", RunKey, "/v", legacyName, "/f");
            }
            catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                return Failure($"清理历史自启值 {legacyName} 异常: {Describe(error)}", error: error);
            }

            if (!delete.Started)
            {
                return Failure($"清理历史自启值 {legacyName} 失败: {DescribeCommand(delete)}", delete);
            }

            if (delete.ExitCode != 0)
            {
                // reg.exe uses exit code 1 for a missing value. Other failures, such as
                // access denied, must remain visible to the caller.
                if (delete.ExitCode != 1)
                {
                    return Failure($"清理历史自启值 {legacyName} 失败: {DescribeCommand(delete)}", delete);
                }

                diagnostics.Add($"历史键 {legacyName} 不存在（exitCode=1）");
            }
        }

        return AutostartResult.Success(string.Join("；", diagnostics));
    }

    private CommandExecutionResult ExecuteReg(params string[] arguments) => ExecuteCommand("reg.exe", arguments);

    private CommandExecutionResult ExecuteCommand(string executablePath, params string[] arguments)
    {
        try
        {
            return _commandExecutor.Execute(executablePath, arguments);
        }
            catch (Exception error)
            {
                return CommandExecutionResult.Failure($"命令执行器异常: {Describe(error)}");
            }

    }

    private (string? Path, string Diagnostic) ResolveExecutablePathCore()
    {
        string? command;
        try
        {
            command = _executablePathResolver();
        }
        catch (Exception error)
        {
            return (null, $"解析当前可执行文件失败: {Describe(error)}");
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            return (null, "当前进程没有可用的可执行文件路径");
        }

        string fullPath;
        try
        {
            fullPath = System.IO.Path.GetFullPath(command);
        }
        catch (Exception error)
        {
            return (null, $"当前可执行文件路径无效: {Describe(error)}");
        }

        var fileName = System.IO.Path.GetFileName(fullPath);
        if (fileName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("java.exe", StringComparison.OrdinalIgnoreCase))
        {
            return (null, $"开发运行不支持开机自启: {fileName}");
        }

        if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return (null, $"当前可执行文件不是 Windows exe: {fullPath}");
        }

        if (!File.Exists(fullPath))
        {
            return (null, $"当前可执行文件不存在: {fullPath}");
        }

        return (fullPath, "当前可执行文件可用于开机自启");
    }

    private static string? ResolveCurrentExecutablePath() => Environment.ProcessPath;

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static AutostartResult Failure(string diagnostic, CommandExecutionResult? command = null, Exception? error = null)
    {
        var details = LimitDiagnostic(diagnostic);
        if (error is not null)
        {
            details = LimitDiagnostic($"{details}; exception={error.GetType().Name}: {error.Message}");
        }

        return new AutostartResult(false, details, null, true, command?.ExitCode, command?.CombinedOutput ?? string.Empty);
    }

    private static string DescribeCommand(CommandExecutionResult command)
    {
        var detail = command.Diagnostic;
        if (string.IsNullOrWhiteSpace(detail))
        {
            detail = $"exitCode={command.ExitCode?.ToString() ?? "未启动"}";
        }

        var output = command.CombinedOutput;
        return output.Length == 0 ? LimitDiagnostic(detail) : LimitDiagnostic($"{detail}; output={output}");
    }

    private static string AppendCommandDiagnostic(string prefix, CommandExecutionResult command)
    {
        var output = command.CombinedOutput;
        return output.Length == 0
            ? $"{prefix}（exitCode={command.ExitCode}）"
            : LimitDiagnostic($"{prefix}（exitCode={command.ExitCode}；output={output}）");
    }

    private static string Describe(Exception error) => string.IsNullOrWhiteSpace(error.Message)
        ? error.GetType().Name
        : LimitDiagnostic(error.Message);

    private static string LimitDiagnostic(string value)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ');
        return normalized.Length <= MaximumDiagnosticLength
            ? normalized
            : normalized[..MaximumDiagnosticLength] + "...";
    }
}
