using System.Diagnostics;
using System.Text.Json;
using DanmuApi.Runtime;

namespace DanmuApi.Platform;

public sealed class WindowsProcessTerminator : IProcessTerminator
{
    public async Task<ProcessTerminationResult> TerminateAsync(
        Process process,
        string expectedNodeExe,
        string expectedMainScript,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsWindows())
        {
            return new(false, "进程树终止器仅支持 Windows");
        }

        if (process.Id <= 0)
        {
            return new(false, $"拒绝终止无效 PID={process.Id}");
        }

        if (process.Id == Environment.ProcessId)
        {
            return new(false, "拒绝终止当前 Desktop 进程");
        }

        if (string.IsNullOrWhiteSpace(expectedNodeExe) || string.IsNullOrWhiteSpace(expectedMainScript))
        {
            return new(false, "拒绝终止：缺少预期 node.exe 或 main.js 路径");
        }

        if (timeout <= TimeSpan.Zero)
        {
            return new(false, "拒绝终止：超时时间必须大于零");
        }

        if (process.HasExited)
        {
            return new(true, "进程已经退出", process.ExitCode);
        }

        ProcessMetadata metadata;
        try
        {
            metadata = await ReadMetadataAsync(process.Id, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, $"拒绝终止：查询 PID={process.Id} 的进程信息超时");
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or JsonException)
        {
            return new(false, $"拒绝终止：无法验证 PID={process.Id} 的进程信息: {error.Message}");
        }

        if (!PathsEqual(metadata.ExecutablePath, expectedNodeExe))
        {
            return new(false, $"拒绝终止：PID={process.Id} 可执行文件不是预期 node.exe（实际={metadata.ExecutablePath ?? "未知"}）");
        }

        if (!CommandLineContainsScript(metadata.CommandLine, expectedMainScript))
        {
            return new(false, $"拒绝终止：PID={process.Id} 命令行不包含预期入口 main.js（实际={metadata.CommandLine ?? "未知"}）");
        }

        if (process.HasExited)
        {
            return new(true, "进程在验证后退出", process.ExitCode);
        }

        using var killer = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                Arguments = $"/PID {process.Id} /T /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            },
        };

        try
        {
            if (!killer.Start())
            {
                return new(false, "taskkill.exe 启动失败");
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            await killer.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            var outputTask = killer.StandardOutput.ReadToEndAsync(linked.Token);
            var errorTask = killer.StandardError.ReadToEndAsync(linked.Token);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            var output = outputTask.Result.Trim();
            var error = errorTask.Result.Trim();
            if (killer.ExitCode != 0)
            {
                return new(false, $"taskkill 失败，exitCode={killer.ExitCode}; {Truncate(string.Join(" ", output, error))}", killer.ExitCode);
            }

            var exitDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            while (!process.HasExited && DateTimeOffset.UtcNow < exitDeadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }

            return process.HasExited
                ? new(true, Truncate(output), process.ExitCode)
                : new(false, $"taskkill 已返回但 PID={process.Id} 仍存活");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!killer.HasExited)
                {
                    killer.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The taskkill process exited while the timeout cleanup raced with it.
            }

            return new(false, $"taskkill 超时，PID={process.Id}");
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            return new(false, $"执行 taskkill 异常: {error.Message}");
        }
    }

    private static async Task<ProcessMetadata> ReadMetadataAsync(
        int pid,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        const string query = "$OutputEncoding = [Text.UTF8Encoding]::new($false); [Console]::OutputEncoding = $OutputEncoding; $p = Get-CimInstance -ClassName Win32_Process -Filter 'ProcessId = PID_PLACEHOLDER' | Select-Object -First 1 ExecutablePath, CommandLine; if ($null -eq $p) { exit 2 }; $p | ConvertTo-Json -Compress";
        using var queryProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            },
        };
        queryProcess.StartInfo.ArgumentList.Add("-NoProfile");
        queryProcess.StartInfo.ArgumentList.Add("-NonInteractive");
        queryProcess.StartInfo.ArgumentList.Add("-Command");
        queryProcess.StartInfo.ArgumentList.Add(query.Replace("PID_PLACEHOLDER", pid.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));

        if (!queryProcess.Start())
        {
            throw new InvalidOperationException("powershell.exe 启动失败");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : timeout);
        var outputTask = queryProcess.StandardOutput.ReadToEndAsync(linked.Token);
        var errorTask = queryProcess.StandardError.ReadToEndAsync(linked.Token);
        await queryProcess.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
        var output = outputTask.Result.Trim();
        var error = errorTask.Result.Trim();
        if (queryProcess.ExitCode != 0)
        {
            throw new InvalidOperationException($"PowerShell 元数据查询失败，exitCode={queryProcess.ExitCode}; {Truncate(string.Join(" ", output, error))}");
        }

        if (output.Length == 0)
        {
            throw new InvalidOperationException($"PowerShell 元数据查询未返回 PID={pid}");
        }

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("ExecutablePath", out var executable) || executable.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("CommandLine", out var commandLine) || commandLine.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"PowerShell 元数据查询返回格式无效: {Truncate(output)}");
        }

        return new(executable.GetString(), commandLine.GetString());
    }

    private static bool PathsEqual(string? actual, string expected)
    {
        if (string.IsNullOrWhiteSpace(actual))
        {
            return false;
        }

        try
        {
            return string.Equals(
                RuntimeValidation.CanonicalPath(actual),
                RuntimeValidation.CanonicalPath(expected),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool CommandLineContainsScript(string? commandLine, string expectedMainScript)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        var normalizedCommandLine = commandLine.Replace('/', '\\');
        var normalizedExpected = RuntimeValidation.CanonicalPath(expectedMainScript).Replace('/', '\\');
        return normalizedCommandLine.Contains(normalizedExpected, StringComparison.OrdinalIgnoreCase);
    }

    private static string Truncate(string value) => value.Length <= 2_000 ? value : value[..2_000] + "…";

    private sealed record ProcessMetadata(string? ExecutablePath, string? CommandLine);
}
