using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DanmuApi.Core.ApplicationUpdates;
using DanmuApi.Platform;
using Microsoft.Win32;

namespace DanmuApi.App.Services;

internal sealed record ApplicationUpdateJob(int ParentPid, long ParentStartTicks, string TargetDirectory, string AssetName, string Kind, string ExpectedVersion, bool ResumeService = false);

internal static class ApplicationUpdateHelper
{
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBox(IntPtr owner, string text, string title, uint type);
    public static string JobRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DanmuApi", "app-updates");

    internal static RegistryView InstallationRegistryView => RegistryViewForArchitecture(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);

    internal static RegistryView RegistryViewForArchitecture(System.Runtime.InteropServices.Architecture architecture) => architecture switch
    {
        System.Runtime.InteropServices.Architecture.X86 => RegistryView.Registry32,
        System.Runtime.InteropServices.Architecture.X64 or System.Runtime.InteropServices.Architecture.Arm64 => RegistryView.Registry64,
        _ => throw new PlatformNotSupportedException("应用更新不支持当前进程架构")
    };

    public static bool IsInstalled(string executable)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, InstallationRegistryView);
        using var key = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{D6F1E4DB-9C73-4DE2-B985-9DAF96B8E9B4}_is1");
        return key?.GetValue("InstallLocation") is string directory &&
            string.Equals(Path.GetFullPath(Path.Combine(directory, "DanmuApi.App.exe")), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);
    }

    public static string ValidateJobDirectory(string directory)
    {
        var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(JobRoot), StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(full), "N", out _)) throw new IOException("应用更新任务路径无效");
        EnsurePlainPath(full);
        if (!Directory.Exists(full)) throw new IOException("更新任务目录不存在");
        foreach (var name in new[] { "job.json", "update-manifest.json", "update-manifest.json.sig", "startup.ok", "startup.ok.tmp", "helper.ready", "cancel", "result.txt", "error.txt", "cleanup-warning.txt" })
            EnsurePlainPath(Path.Combine(full, name));
        var jobFile = new FileInfo(Path.Combine(full, "job.json"));
        if (jobFile.Exists && jobFile.Length > 64 * 1024) throw new IOException("更新任务大小超出上限");
        var receiptFile = new FileInfo(Path.Combine(full, "startup.ok"));
        if (receiptFile.Exists && receiptFile.Length > 256) throw new IOException("更新回执大小超出上限");
        return full;
    }

    private static void EnsurePlainPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("更新路径不能包含链接");
    }

    public static int Run(string directory)
    {
        directory = ValidateJobDirectory(directory);
        Process? installer = null;
        var startupConfirmed = false;
        try
        {
            if (File.Exists(Path.Combine(directory, "startup.ok")) || File.Exists(PortableApplicationUpdate.JournalPath(Path.Combine(directory, "backup"))))
                throw new IOException("更新事务已存在；请执行显式恢复入口，不能重复启动替换");
            var job = JsonSerializer.Deserialize<ApplicationUpdateJob>(File.ReadAllText(Path.Combine(directory, "job.json"))) ?? throw new IOException("更新任务缺失");
            using var remote = new ApplicationUpdateService(AppUpdateTrust.PublicKey());
            var manifest = remote.VerifyManifest(File.ReadAllBytes(Path.Combine(directory, "update-manifest.json")), File.ReadAllBytes(Path.Combine(directory, "update-manifest.json.sig")));
            var asset = manifest.Assets.Single(item => item.Name == job.AssetName && item.Kind == job.Kind);
            if (manifest.Version != job.ExpectedVersion) throw new IOException("更新任务版本与签名清单不符");
            var package = Path.Combine(directory, asset.Name);
            EnsurePlainPath(package);
            EnsurePlainPath(job.TargetDirectory);
            using (var stream = File.OpenRead(package))
                if (stream.Length != asset.Size || !Convert.ToHexString(SHA256.HashData(stream)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("更新包校验失败");
            var targetExe = Path.Combine(Path.GetFullPath(job.TargetDirectory), "DanmuApi.App.exe");
            using var parent = Process.GetProcessById(job.ParentPid);
            if (parent.StartTime.ToUniversalTime().Ticks != job.ParentStartTicks ||
                !string.Equals(parent.MainModule?.FileName, targetExe, StringComparison.OrdinalIgnoreCase)) throw new IOException("更新目标进程身份不符");
            AppUpdateTrust.VerifyExecutable(targetExe);
            var current = SemanticVersion.Parse(FileVersionInfo.GetVersionInfo(targetExe).ProductVersion!);
            if (SemanticVersion.Parse(manifest.Version).CompareTo(current) <= 0) throw new IOException("更新版本必须高于当前版本");
            IReadOnlyList<string>? files = null;
            var stage = Path.Combine(directory, "stage");
            if (job.Kind == "portable")
            {
                if (IsInstalled(targetExe)) throw new IOException("安装版不能使用便携替换");
                files = PortableApplicationUpdate.Extract(package, stage);
                AppUpdateTrust.VerifyExecutable(Path.Combine(stage, "DanmuApi.App.exe"));
                if (!VersionsMatch(FileVersionInfo.GetVersionInfo(Path.Combine(stage,"DanmuApi.App.exe")).ProductVersion!, manifest.Version))
                    throw new IOException("新程序实际版本与签名清单不符");
            }
            else
            {
                if (!IsInstalled(targetExe)) throw new IOException("便携副本不能使用安装器更新");
                AppUpdateTrust.VerifyExecutable(package);
                installer = Process.Start(CreateInstallerStartInfo(package, job, directory))
                    ?? throw new IOException("安装器未启动");
                var watch = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(directory, "installer.ready")))
                {
                    if (installer.HasExited) throw CreateInstallerFailure(directory, installer.ExitCode, initializing: true);
                    if (File.Exists(Path.Combine(directory, "cancel")) || watch.Elapsed > TimeSpan.FromMinutes(2)) throw new IOException("安装器准备超时或已取消");
                    Thread.Sleep(100);
                }
            }
            if (File.Exists(Path.Combine(directory, "cancel"))) throw new OperationCanceledException("主程序取消更新准备");
            File.WriteAllText(Path.Combine(directory, "helper.ready"), "ready");
            var waiting = Stopwatch.StartNew();
            while (!parent.WaitForExit(100))
            {
                if (File.Exists(Path.Combine(directory, "cancel"))) throw new OperationCanceledException("主程序取消更新");
                if (waiting.Elapsed > TimeSpan.FromMinutes(2)) throw new TimeoutException("原程序没有退出，未替换文件");
            }
            if (File.Exists(Path.Combine(directory, "cancel"))) throw new OperationCanceledException("更新已取消");
            var backup = Path.Combine(directory, "backup");
            if (installer is not null)
            {
                if (!installer.WaitForExit(10 * 60 * 1000)) throw new TimeoutException("安装器尚未结束，请检查安装日志");
                if (installer.ExitCode != 0) throw CreateInstallerFailure(directory, installer.ExitCode, initializing: false);
            }
            else PortableApplicationUpdate.Replace(job.TargetDirectory, stage, backup, files!);
            try
            {
                var launch = new ProcessStartInfo(targetExe) { UseShellExecute = false, WorkingDirectory = job.TargetDirectory };
                launch.ArgumentList.Add("--app-update-receipt"); launch.ArgumentList.Add(directory);
                using var child = Process.Start(launch) ?? throw new IOException("新版本未启动");
                var watch = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(directory, "startup.ok")))
                {
                    if (child.HasExited) throw new IOException($"新版本启动失败：exit={child.ExitCode}");
                    if (watch.Elapsed > TimeSpan.FromSeconds(90)) throw new IOException("新版本未确认启动，备份保留；请退出新版本后恢复");
                    Thread.Sleep(100);
                }
                if (File.ReadAllText(Path.Combine(directory, "startup.ok")) != job.ExpectedVersion)
                    throw new IOException("新版本启动回执内容不符");
            }
            catch (Exception launchError)
            {
                if (File.Exists(Path.Combine(directory, "startup.ok")) && File.ReadAllText(Path.Combine(directory, "startup.ok")) == job.ExpectedVersion)
                {
                    startupConfirmed = true;
                }
                else if (installer is null)
                {
                    if (IsTargetRunning(targetExe))
                        throw new IOException("新版本尚在运行，未回滚；请退出应用后执行 --app-update-recover 恢复任务。", launchError);
                    var action = PortableApplicationUpdate.Recover(job.TargetDirectory, backup);
                    throw new IOException($"新版本启动失败；恢复动作：{action}。", launchError);
                }
                else throw;
            }
            startupConfirmed = true;
            // Startup acknowledgement is a separate durable commit. Cleanup can never enter the launch rollback branch.
            if (installer is null) PortableApplicationUpdate.MarkStartupConfirmed(job.TargetDirectory, backup);
            File.WriteAllText(Path.Combine(directory, "result.txt"), "更新完成，新版本已确认启动。");
            if (installer is null) PortableApplicationUpdate.CleanupConfirmed(job.TargetDirectory, backup, package, stage);
            else CleanupInstallerPackage(directory, package);
            return 0;
        }
        catch (Exception error)
        {
            if (startupConfirmed)
            {
                RecordCleanupWarning(directory, error);
                return 0;
            }
            var message = $"更新失败：{SafeFailure(error)}\n恢复材料已保留；便携版请退出应用后运行 \"{Path.Combine(directory, "DanmuApi.Updater.exe")}\" --app-update-recover \"{directory}\"。安装版请检查 installer.log 并重新运行已签名安装器。";
            File.WriteAllText(Path.Combine(directory, "error.txt"), message);
            if (File.Exists(Path.Combine(directory, "helper.ready"))) MessageBox(IntPtr.Zero, message, "弹幕API · 更新失败", 0x10);
            return 1;
        }
        finally { installer?.Dispose(); }
    }

    internal static ProcessStartInfo CreateInstallerStartInfo(string package, ApplicationUpdateJob job, string directory)
    {
        // Setup's manifest performs elevation while preserving Inno's original-user context.
        var start = new ProcessStartInfo(package) { UseShellExecute = true };
        start.Arguments = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATEPARENT={job.ParentPid} /UPDATEREADY=\"{Path.Combine(directory, "installer.ready")}\" /DIR=\"{job.TargetDirectory}\" /LOG=\"{Path.Combine(directory, "installer.log")}\"";
        return start;
    }

    internal const int InstallerDiagnosticTailBytes = 64 * 1024;
    internal const int InstallerDiagnosticLimit = 8;

    private static readonly HashSet<string> InstallerDiagnosticPhases = new(StringComparer.Ordinal)
    {
        "arguments", "verify", "probe", "ready", "parent-wait", "acquire", "prepared", "finish-wait",
        "release", "released", "cleanup", "owner-monitor", "finish-monitor", "delay-monitor", "diagnostic", "marker-cleanup"
    };
    private static readonly HashSet<string> InstallerDiagnosticReasons = new(StringComparer.Ordinal)
    {
        "operationfailed", "argumentsinvalid", "platformunsupported", "parentidentity", "parentdead", "installidentity",
        "installertrust", "targetinvalid", "tokenmissing", "tokenidentity", "knownfolderinvalid", "pathinvalid",
        "leaseinvalid", "readyinvalid", "jobinvalid", "joboversize", "probefailed", "instanceheld", "acquirefailed",
        "parenttimeout", "installdead", "cancelled", "finishtimeout", "lockreleasefail", "cleanupfailed",
        "diagnosticwritefail", "markercleanupfailed"
    };
    private static readonly HashSet<string> InstallerDiagnosticTypes = new(StringComparer.Ordinal)
    {
        "LeaseException", "Exception", "IOException", "FileNotFoundException", "DirectoryNotFoundException",
        "EndOfStreamException", "InvalidDataException", "PathTooLongException", "UnauthorizedAccessException",
        "Win32Exception", "SecurityException", "ArgumentException", "ArgumentNullException", "ArgumentOutOfRangeException",
        "InvalidOperationException", "ObjectDisposedException", "TimeoutException", "OperationCanceledException",
        "NotSupportedException", "PlatformNotSupportedException", "JsonException", "CryptographicException",
        "FormatException", "OverflowException", "AggregateException"
    };
    private static readonly System.Text.RegularExpressions.Regex InstallerDiagnosticLine = new(
        @"\A(?:(?:[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3} +)?Update lease failure: )?" +
        @"(?<code>installer-update-lease phase=(?<phase>[a-z-]+) reason=(?<reason>[a-z]+) " +
        @"type=(?<type>[A-Za-z0-9]+) HRESULT=0x(?<hr>[0-9A-F]{8})); (?<explanation>[\x20-\x7E]+)\z",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking);

    // Never attach the raw read exception or log text: both can contain credentials and user paths.
    internal static IOException CreateInstallerFailure(string directory, int exitCode, bool initializing)
    {
        var failure = initializing
            ? $"安装器初始化失败：exit={exitCode}；stage=初始化"
            : $"安装器失败：exit={exitCode}；stage=安装，应用未自动重启";
        const string logHint = "请检查更新任务目录中的 installer.log";
        string detail;
        try
        {
            var (codes, limited) = ReadInstallerDiagnostics(Path.Combine(directory, "installer.log"));
            detail = codes.Count == 0
                ? $"日志尾部未找到已验证的结构化诊断；{logHint}"
                : "安装器结构化诊断：" + string.Join(" | ", codes) +
                  (limited ? $"；诊断超过上限，仅显示最近 {InstallerDiagnosticLimit} 条；{logHint}" : "");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            var status = error is FileNotFoundException or DirectoryNotFoundException ? "尚不存在" : "读取失败";
            detail = $"installer.log {status}：type={error.GetType().Name} HRESULT=0x{error.HResult:X8}；未获得结构化诊断；{logHint}";
        }
        return new IOException($"{failure}；{detail}");
    }

    private static (List<string> Codes, bool Limited) ReadInstallerDiagnostics(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var start = Math.Max(0, length - InstallerDiagnosticTailBytes);
        stream.Seek(start, SeekOrigin.Begin);
        var tail = new byte[(int)(length - start)];
        stream.ReadExactly(tail); // A concurrent truncation is an explicit read failure, not an empty diagnostic.
        // One-to-one byte decoding: only the ASCII protocol is recognized, never replacement-decoded log text.
        var text = System.Text.Encoding.Latin1.GetString(tail);
        if (start > 0)
        {
            var boundary = text.IndexOf('\n');
            if (boundary < 0) return ([], false);
            text = text[(boundary + 1)..]; // Do not interpret a fragment of a line cut by the byte bound.
        }
        else if (tail.Length >= 3 && tail[0] == 0xEF && tail[1] == 0xBB && tail[2] == 0xBF) text = text[3..];

        var lines = text.Split('\n');
        var codes = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var limited = false;
        for (var index = lines.Length - 1; index >= 0; index--)
        {
            var line = lines[index].TrimEnd('\r');
            // Bound input before matching; whitelists and the producer's fixed text bound accepted fields.
            // Avoid bounded variable repeats that expand the NonBacktracking automaton at type initialization.
            if (line.Length > 1024) continue;
            var match = InstallerDiagnosticLine.Match(line);
            if (!match.Success) continue;
            var phase = match.Groups["phase"].Value;
            var reason = match.Groups["reason"].Value;
            var type = match.Groups["type"].Value;
            if (!InstallerDiagnosticPhases.Contains(phase) || !InstallerDiagnosticReasons.Contains(reason) || !InstallerDiagnosticTypes.Contains(type)) continue;
            var hresult = unchecked((int)uint.Parse(match.Groups["hr"].Value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture));
            var code = match.Groups["code"].Value;
            // Compare the fixed explanation against the producer, but expose only validated codes to the UI.
            if (!string.Equals(code + "; " + match.Groups["explanation"].Value,
                new InstallerUpdateLease.Diagnostic(phase, reason, type, hresult).Format(), StringComparison.Ordinal) || !seen.Add(code)) continue;
            if (codes.Count == InstallerDiagnosticLimit) { limited = true; break; }
            codes.Add(code);
        }
        codes.Reverse();
        return (codes, limited);
    }

    private static string SafeFailure(Exception error) => System.Text.RegularExpressions.Regex.Replace(
        error.Message, @"(?i)((?:[A-Z0-9_]*(?:TOKEN|COOKIE|API_KEY|PASSWORD|SECRET)[A-Z0-9_]*)\s*[:=]\s*)([^\r\n;]+)", "$1[REDACTED]");

    private static void RecordCleanupWarning(string directory, Exception error)
    {
        var warning = $"cleanup-warning: 新版本已确认启动；清理未完成，可稍后重试 ({error.GetType().Name}, 0x{error.HResult:X8})";
        try { File.WriteAllText(Path.Combine(directory, "cleanup-warning.txt"), warning); }
        catch (Exception diagnostic) when (diagnostic is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"{warning}; warning文件写入失败 ({diagnostic.GetType().Name}, 0x{diagnostic.HResult:X8})");
        }
    }

    internal static void CleanupInstallerPackage(string directory, string package)
    {
        try { File.Delete(package); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { RecordCleanupWarning(directory, error); }
    }

    /// <summary>Explicit recovery entry for Program's --app-update-recover dispatch. Never starts or kills processes.</summary>
    public static int Recover(string directory)
    {
        directory = ValidateJobDirectory(directory);
        var confirmed = false;
        try
        {
            var job = JsonSerializer.Deserialize<ApplicationUpdateJob>(File.ReadAllText(Path.Combine(directory, "job.json"))) ?? throw new IOException("恢复任务缺失");
            if (job.Kind != "portable") throw new IOException("安装版由已签名安装器恢复，不能执行便携回滚");
            using var remote = new ApplicationUpdateService(AppUpdateTrust.PublicKey());
            var manifest = remote.VerifyManifest(File.ReadAllBytes(Path.Combine(directory, "update-manifest.json")), File.ReadAllBytes(Path.Combine(directory, "update-manifest.json.sig")));
            var asset = manifest.Assets.Single(item => item.Name == job.AssetName && item.Kind == job.Kind);
            if (manifest.Version != job.ExpectedVersion) throw new IOException("恢复任务版本与签名清单不符");
            var targetExe = Path.Combine(Path.GetFullPath(job.TargetDirectory), "DanmuApi.App.exe");
            if (IsInstalled(targetExe)) throw new IOException("安装版不能执行便携恢复");
            var backup = Path.Combine(directory, "backup");
            var receipt = Path.Combine(directory, "startup.ok");
            if (File.Exists(receipt))
            {
                if (File.ReadAllText(receipt) != job.ExpectedVersion) throw new IOException("恢复任务启动回执不符");
                AppUpdateTrust.VerifyExecutable(targetExe);
                if (!VersionsMatch(FileVersionInfo.GetVersionInfo(targetExe).ProductVersion!, job.ExpectedVersion))
                    throw new IOException("恢复目标版本与启动回执不符");
                confirmed = true;
                PortableApplicationUpdate.MarkStartupConfirmed(job.TargetDirectory, backup);
                PortableApplicationUpdate.CleanupConfirmed(job.TargetDirectory, backup, Path.Combine(directory, asset.Name), Path.Combine(directory, "stage"));
                File.WriteAllText(Path.Combine(directory, "result.txt"), "新版本已确认启动；恢复动作：仅清理，无回滚。");
            }
            else
            {
                if (IsTargetRunning(targetExe)) throw new IOException("应用仍在运行，未更改文件；请退出应用后重试恢复");
                // Validate original executable trust before allowing a journal to restore it.
                var originalExe = Path.Combine(backup, "DanmuApi.App.exe");
                if (File.Exists(originalExe)) AppUpdateTrust.VerifyExecutable(originalExe);
                var action = PortableApplicationUpdate.Recover(job.TargetDirectory, backup);
                File.WriteAllText(Path.Combine(directory, "result.txt"), "恢复动作：" + action);
            }
            return 0;
        }
        catch (Exception error)
        {
            if (confirmed) { RecordCleanupWarning(directory, error); return 0; }
            File.WriteAllText(Path.Combine(directory, "error.txt"), $"恢复失败：{SafeFailure(error)}；恢复材料保留，请排除错误后重试 --app-update-recover。");
            return 1;
        }
    }

    private static bool IsTargetRunning(string path)
    {
        foreach (var process in Process.GetProcessesByName("DanmuApi.App"))
        {
            using (process)
            {
                try { if (string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase)) return true; }
                catch (System.ComponentModel.Win32Exception) { return true; }
            }
        }
        return false;
    }

    internal static bool VersionsMatch(string productVersion, string expectedVersion)
    {
        return ParseVersion(productVersion, "程序").CompareTo(ParseVersion(expectedVersion, "更新任务")) == 0;

        static SemanticVersion ParseVersion(string value, string source)
        {
            try { return SemanticVersion.Parse(value); }
            catch (FormatException error)
            {
                throw new FormatException($"应用更新{source}版本格式无效（{error.GetType().Name}，0x{error.HResult:X8}）");
            }
        }
    }

    public static bool ShouldResumeService(string[] args)
    {
        if (args.Length != 2 || args[0] != "--app-update-receipt") return false;
        return ShouldResumeService(args, AppContext.BaseDirectory, Environment.ProcessPath!);
    }

    internal static bool ShouldResumeService(string[] args, string baseDirectory, string executablePath)
    {
        if (args.Length != 2 || args[0] != "--app-update-receipt") return false;
        var directory = ValidateJobDirectory(args[1]);
        var job = ReadStartupJob(directory, baseDirectory, executablePath);
        return job.ResumeService;
    }

    private static ApplicationUpdateJob ReadStartupJob(string directory, string baseDirectory, string executablePath)
    {
        var job = JsonSerializer.Deserialize<ApplicationUpdateJob>(File.ReadAllText(Path.Combine(directory, "job.json"))) ?? throw new IOException("更新回执任务缺失");
        if (!string.Equals(Path.GetFullPath(job.TargetDirectory).TrimEnd(Path.DirectorySeparatorChar), baseDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            !VersionsMatch(FileVersionInfo.GetVersionInfo(executablePath).ProductVersion!, job.ExpectedVersion))
            throw new IOException("更新回执版本或路径不符");
        return job;
    }

    public static void AcknowledgeStartup(string[] args)
    {
        if (args.Length != 2 || args[0] != "--app-update-receipt") return;
        AcknowledgeStartup(args, AppContext.BaseDirectory, Environment.ProcessPath!);
    }

    internal static void AcknowledgeStartup(string[] args, string baseDirectory, string executablePath)
    {
        if (args.Length != 2 || args[0] != "--app-update-receipt") return;
        var directory = ValidateJobDirectory(args[1]);
        var job = ReadStartupJob(directory, baseDirectory, executablePath);
        var receipt = Path.Combine(directory, "startup.ok");
        using (var stream = new FileStream(receipt + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(job.ExpectedVersion);
            stream.Write(bytes); stream.Flush(true);
        }
        File.Move(receipt + ".tmp", receipt, true);
    }
}
