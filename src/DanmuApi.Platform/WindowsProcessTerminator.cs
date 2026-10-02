using System.Diagnostics;
using System.Text.Json;
using DanmuApi.Runtime;

namespace DanmuApi.Platform;

public sealed class WindowsProcessTerminator : IProcessTerminator, IVerifiedProcessTerminator
{
    public Task<ProcessTerminationResult> TerminateAsync(
        Process process,
        string expectedNodeExe,
        string expectedMainScript,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        TerminateVerifiedAsync(
            process,
            expectedNodeExe,
            expectedMainScript,
            "node.exe",
            timeout,
            cancellationToken,
            expectedArgumentLabel: "入口 main.js");

    public async Task<ProcessTerminationResult> TerminateVerifiedAsync(
        Process process,
        string expectedExecutablePath,
        string expectedArgumentFragment,
        string expectedExecutableLabel,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        string? expectedArgumentLabel = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedExecutableLabel);
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

        if (string.IsNullOrWhiteSpace(expectedExecutablePath) || string.IsNullOrWhiteSpace(expectedArgumentFragment))
        {
            return new(false, $"拒绝终止：缺少预期 {expectedExecutableLabel} 或参数片段");
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
        DateTime startedUtc;
        try
        {
            // 持有句柄并记录创建时间，避免元数据查询期间 PID 被复用后误杀另一个进程。
            _ = process.SafeHandle;
            startedUtc = process.StartTime.ToUniversalTime();
            metadata = await ReadMetadataAsync(process.Id, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, $"拒绝终止：查询 PID={process.Id} 的进程信息超时");
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or JsonException or System.ComponentModel.Win32Exception)
        {
            // 元数据/PowerShell 输出可能含手工进程的认证参数，绝不回显原始异常或输出。
            return new(false, $"拒绝终止：无法验证 PID={process.Id} 的进程信息（{error.GetType().Name}, HResult=0x{error.HResult:X8}）");
        }

        if (Math.Abs((metadata.StartedUtc - startedUtc).Ticks) >= TimeSpan.TicksPerMillisecond)
        {
            return new(false, $"拒绝终止：PID={process.Id} 创建时间与受管进程句柄不一致");
        }

        if (!PathsEqual(metadata.ExecutablePath, expectedExecutablePath))
        {
            return new(false, $"拒绝终止：PID={process.Id} 可执行文件不是预期 {expectedExecutableLabel}");
        }

        var argumentMatches = string.Equals(expectedExecutableLabel, "node.exe", StringComparison.OrdinalIgnoreCase)
            ? WindowsProcessArguments.MatchesNodeEntryPoint(metadata.CommandLine, expectedArgumentFragment)
            : expectedExecutableLabel is "frpc.exe" or "frps.exe"
                && WindowsProcessArguments.MatchesFrpConfig(metadata.CommandLine, expectedArgumentFragment);
        if (!argumentMatches)
        {
            var label = string.Equals(expectedExecutableLabel, "node.exe", StringComparison.OrdinalIgnoreCase)
                ? "位置入口 main.js" : "-c/--config 配置参数";
            return new(false, $"拒绝终止：PID={process.Id} 命令行不匹配预期{label}");
        }

        if (process.HasExited)
        {
            return new(true, "进程在验证后退出", process.ExitCode);
        }

        if (ResolveSystemTool("taskkill.exe") is not { } taskkillPath)
        {
            return new(false, "无法定位原生系统目录里的 taskkill.exe（SystemRoot 未设置或文件缺失），拒绝按 PATH 猜测工具", OwnershipVerified: true);
        }

        using var killer = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = taskkillPath,
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
                return new(false, "taskkill.exe 启动失败", OwnershipVerified: true);
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
                return new(false, $"taskkill 失败，exitCode={killer.ExitCode}; {Truncate(string.Join(" ", output, error))}", killer.ExitCode, OwnershipVerified: true);
            }

            var exitDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            while (!process.HasExited && DateTimeOffset.UtcNow < exitDeadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }

            return process.HasExited
                ? new(true, Truncate(output), process.ExitCode, OwnershipVerified: true)
                : new(false, $"taskkill 已返回但 PID={process.Id} 仍存活", OwnershipVerified: true);
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

            return new(false, $"taskkill 超时，PID={process.Id}", OwnershipVerified: true);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new(false, $"执行 taskkill 异常: {error.Message}", OwnershipVerified: true);
        }
    }

    private static async Task<ProcessMetadata> ReadMetadataAsync(
        int pid,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        // 进程元数据查询要绝对路径的原生 powershell：PATH 里可能是商店别名存根（CreateProcess 直接
        // 拒绝访问），而 32 位构建的进程看到的 System32 会被重定向到 SysWOW64。
        if (ResolveSystemTool("WindowsPowerShell", "v1.0", "powershell.exe") is not { } powershellPath)
        {
            throw new InvalidOperationException(
                "无法定位原生系统目录里的 powershell.exe（SystemRoot 未设置或文件缺失），拒绝按 PATH 猜测工具");
        }

        const string query = "$ErrorActionPreference = 'Stop'; $OutputEncoding = [Text.UTF8Encoding]::new($false); [Console]::OutputEncoding = $OutputEncoding; $p = Get-CimInstance -ClassName Win32_Process -Filter 'ProcessId = PID_PLACEHOLDER' | Select-Object -First 1; if ($null -eq $p) { exit 2 }; [pscustomobject]@{ ExecutablePath = $p.ExecutablePath; CommandLine = $p.CommandLine; StartedUtc = $p.CreationDate.ToUniversalTime().ToString('o') } | ConvertTo-Json -Compress";
        using var queryProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = powershellPath,
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
            throw new InvalidOperationException($"PowerShell 元数据查询失败，exitCode={queryProcess.ExitCode}");
        }

        if (output.Length == 0)
        {
            throw new InvalidOperationException($"PowerShell 元数据查询未返回 PID={pid}");
        }

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("ExecutablePath", out var executable) || executable.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("CommandLine", out var commandLine) || commandLine.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("StartedUtc", out var started) || started.ValueKind != JsonValueKind.String ||
            !DateTime.TryParse(started.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var startedUtc))
        {
            throw new InvalidOperationException("PowerShell 元数据查询返回格式无效");
        }

        return new(executable.GetString(), commandLine.GetString(), startedUtc.ToUniversalTime());
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

    /// <summary>
    /// 原生系统目录里的系统工具（specs/03 §1 的踩坑：PATH 上的 taskkill / powershell 可能是商店别名
    /// 存根，CreateProcess 直接拒绝访问；32 位构建的进程还会把 System32 重定向到 SysWOW64）。
    /// 定位不到就返回 null，由调用方显式报错——不回到 PATH 猜一个。
    /// </summary>
    private static string? ResolveSystemTool(params string[] relativeParts)
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (string.IsNullOrWhiteSpace(systemRoot))
        {
            return null;
        }

        try
        {
            var path = Path.GetFullPath(
                Path.Combine(SystemPaths.NativeSystemDirectory(systemRoot), Path.Combine(relativeParts)));
            return File.Exists(path) ? path : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string Truncate(string value) => value.Length <= 2_000 ? value : value[..2_000] + "…";

    private sealed record ProcessMetadata(string? ExecutablePath, string? CommandLine, DateTime StartedUtc);
}
