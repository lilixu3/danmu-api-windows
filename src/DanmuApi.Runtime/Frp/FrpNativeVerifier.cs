using System.Diagnostics;
using System.Text;

namespace DanmuApi.Runtime.Frp;

public sealed record FrpNativeVerificationResult(bool Succeeded, string Diagnostic);

public interface IFrpNativeVerifier
{
    Task<FrpNativeVerificationResult> VerifyAsync(FrpRunPlan plan, CancellationToken cancellationToken = default);
}

/// <summary>Local official `verify`: no network, bounded output/time, and no configuration snippets in diagnostics.</summary>
public sealed class FrpNativeVerifier : IFrpNativeVerifier
{
    public const int MaxOutputCharacters = 16 * 1024;
    private readonly TimeSpan _timeout;

    public FrpNativeVerifier(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public Task<FrpNativeVerificationResult> VerifyAsync(FrpRunPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        // Process.Start, file checks and process cleanup must never block the UI thread.
        return Task.Run(() => VerifyCoreAsync(plan, cancellationToken), cancellationToken);
    }

    private async Task<FrpNativeVerificationResult> VerifyCoreAsync(FrpRunPlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(plan.ExecutablePath)
        {
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "verify", "-c", plan.ConfigPath }) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return new(false, $"{plan.ExecutableLabel} 原生校验未能创建进程");
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Win32 exceptions can include a command line; type/HResult are sufficient and do not contain raw text.
            return new(false, $"{plan.ExecutableLabel} 原生校验无法启动：{error.GetType().Name}, HResult=0x{error.HResult:X8}");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        var stdout = ReadBoundedAsync(process.StandardOutput, deadline.Token);
        var stderr = ReadBoundedAsync(process.StandardError, deadline.Token);
        try
        {
            // A pump failure (including oversize output) must interrupt a stuck verifier, not wait for its timeout.
            var exit = process.WaitForExitAsync(deadline.Token);
            var pending = new List<Task> { exit, stdout, stderr };
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending).ConfigureAwait(false);
                await finished.ConfigureAwait(false);
                pending.Remove(finished);
            }
            var output = await stdout.ConfigureAwait(false);
            var errors = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                // Native parser errors sometimes include literal configuration values. Never publish their text.
                var category = ErrorCategory(RuntimeManagementClient.Redact(output + "\n" + errors,
                    plan.Secrets.Append(plan.AdminPassword).ToArray()));
                return new(false, $"{plan.ExecutableLabel} 原生校验失败，exitCode={process.ExitCode}；{category}（原始输出含配置片段，未回显）");
            }
            return new(true, $"{plan.ExecutableLabel} 原生校验通过（exitCode=0）");
        }
        catch (Exception error)
        {
            string? cleanupFailure = null;
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                // HasExited/WaitForExitAsync can observe an exit code before Windows signals the process handle
                // and releases its working directory. Cleanup requires the native wait, not that earlier signal.
                if (!process.WaitForExit(5000)) throw new TimeoutException("校验进程在终止后 5 秒内未完成清理");
            }
            catch (Exception cleanup)
            {
                cleanupFailure = $"校验进程清理失败：{cleanup.GetType().Name}, HResult=0x{cleanup.HResult:X8}";
            }
            deadline.Cancel();
            var outputFailure = await ObservePumpAsync(stdout).ConfigureAwait(false);
            var errorFailure = await ObservePumpAsync(stderr).ConfigureAwait(false);
            var pumpDiagnostic = string.Join("；", new[] { outputFailure, errorFailure }.Where(note => note is not null));
            var suffix = pumpDiagnostic.Length == 0 ? string.Empty : $"；{pumpDiagnostic}";
            if (cleanupFailure is not null)
                return new(false, $"原生校验未完成（{error.GetType().Name}）；{cleanupFailure}{suffix}");
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException($"frp 原生校验已取消，校验进程已清理{suffix}", error, cancellationToken);
            return new(false, (error switch
            {
                OperationCanceledException => $"{plan.ExecutableLabel} 原生校验超时（{_timeout.TotalSeconds:0.##} 秒），校验进程已清理",
                InvalidDataException => $"{plan.ExecutableLabel} 原生校验输出超过 {MaxOutputCharacters} 字符，已拒绝并清理校验进程",
                _ => $"{plan.ExecutableLabel} 原生校验执行失败：{error.GetType().Name}, HResult=0x{error.HResult:X8}；校验进程已清理",
            }) + suffix);
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[1024];
        var content = new StringBuilder();
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
            {
                if (content.Length + count > MaxOutputCharacters) throw new InvalidDataException("原生校验输出过大");
                content.Append(buffer, 0, count);
            }
            return content.ToString();
        }
        finally { Array.Clear(buffer); }
    }

    private static async Task<string?> ObservePumpAsync(Task task)
    {
        try { await task.ConfigureAwait(false); return null; }
        catch (Exception error)
        {
            // Report both pump outcomes without exception text (a parser may quote source/credentials).
            return $"校验输出读取终止：{error.GetType().Name}, HResult=0x{error.HResult:X8}";
        }
    }

    private static string ErrorCategory(string output)
    {
        // Extract names/positions, never quote a native source line or an offending value.
        const string field = @"[A-Za-z_][A-Za-z0-9_.\[\]-]{0,95}";
        var knownFields = new[] { "serverAddr", "serverPort", "bindPort", "localPort", "remotePort", "webServer.port", "auth.method", "customDomains", "name", "type", "localIP" };
        var details = new List<string>();
        var unknown = System.Text.RegularExpressions.Regex.Match(output,
            "unknown field [\\\"'](?<field>" + field + ")[\\\"']", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (unknown.Success) details.Add("配置包含当前 frp 版本不支持的字段（原文字段名未回显）");
        var required = System.Text.RegularExpressions.Regex.Match(output,
            "(?:field [\\\"']?)?(?<field>" + field + ")[\\\"']? (?:is )?(?:required|missing)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (required.Success)
        {
            var known = knownFields.FirstOrDefault(name => name.Equals(required.Groups["field"].Value, StringComparison.OrdinalIgnoreCase));
            details.Add(known is null ? "缺少原生 frp 所需字段（原文字段名未回显）" : $"缺少原生 frp 所需字段：{known}");
        }
        var line = System.Text.RegularExpressions.Regex.Match(output, @"\bline\s*:?\s*(\d{1,7})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var column = System.Text.RegularExpressions.Regex.Match(output, @"\b(?:column|col)\s*:?\s*(\d{1,7})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (line.Success) details.Add($"原生报错行 {line.Groups[1].Value}" + (column.Success ? $"，列 {column.Groups[1].Value}" : string.Empty));
        if (details.Count == 0)
        {
            var mentioned = knownFields.Where(label => System.Text.RegularExpressions.Regex.IsMatch(output,
                @"(?<![A-Za-z0-9_])" + System.Text.RegularExpressions.Regex.Escape(label) + @"(?![A-Za-z0-9_])", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToArray();
            var reason = output.Contains("required", StringComparison.OrdinalIgnoreCase) || output.Contains("empty", StringComparison.OrdinalIgnoreCase)
                ? "缺少必填项" : output.Contains("invalid", StringComparison.OrdinalIgnoreCase) ? "字段值/格式无效" : "字段约束或原生语法校验未通过";
            details.Add(mentioned.Length == 0 ? reason : reason + "，涉及字段：" + string.Join("、", mentioned));
        }
        return string.Join("；", details);
    }
}
