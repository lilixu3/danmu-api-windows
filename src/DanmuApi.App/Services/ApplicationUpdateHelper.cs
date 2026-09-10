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

    public static bool IsInstalled(string executable)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
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
                if (SemanticVersion.Parse(FileVersionInfo.GetVersionInfo(Path.Combine(stage,"DanmuApi.App.exe")).ProductVersion!).Value != manifest.Version)
                    throw new IOException("新程序实际版本与签名清单不符");
            }
            else
            {
                if (!IsInstalled(targetExe)) throw new IOException("便携副本不能使用安装器更新");
                AppUpdateTrust.VerifyExecutable(package);
                var start = new ProcessStartInfo(package) { UseShellExecute = true, Verb = "runas" };
                start.Arguments = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATEPARENT={job.ParentPid} /UPDATEREADY=\"{Path.Combine(directory, "installer.ready")}\" /DIR=\"{job.TargetDirectory}\" /LOG=\"{Path.Combine(directory,"installer.log")}\"";
                installer = Process.Start(start) ?? throw new IOException("安装器未启动");
                var watch = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(directory, "installer.ready")))
                {
                    if (installer.HasExited) throw new IOException($"安装器初始化失败：exit={installer.ExitCode}");
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
                if (installer.ExitCode != 0) throw new IOException($"安装器失败：exit={installer.ExitCode}，应用未自动重启");
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
                if (SemanticVersion.Parse(FileVersionInfo.GetVersionInfo(targetExe).ProductVersion!).Value != job.ExpectedVersion)
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

    public static bool ShouldResumeService(string[] args)
    {
        if (args.Length != 2 || args[0] != "--app-update-receipt") return false;
        var directory = ValidateJobDirectory(args[1]);
        var job = ReadStartupJob(directory);
        return job.ResumeService;
    }

    private static ApplicationUpdateJob ReadStartupJob(string directory)
    {
        var job = JsonSerializer.Deserialize<ApplicationUpdateJob>(File.ReadAllText(Path.Combine(directory, "job.json"))) ?? throw new IOException("更新回执任务缺失");
        if (!string.Equals(Path.GetFullPath(job.TargetDirectory).TrimEnd(Path.DirectorySeparatorChar), AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            SemanticVersion.Parse(FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).ProductVersion!).Value != job.ExpectedVersion)
            throw new IOException("更新回执版本或路径不符");
        return job;
    }

    public static void AcknowledgeStartup(string[] args)
    {
        if (args.Length != 2 || args[0] != "--app-update-receipt") return;
        var directory = ValidateJobDirectory(args[1]);
        var job = ReadStartupJob(directory);
        var receipt = Path.Combine(directory, "startup.ok");
        using (var stream = new FileStream(receipt + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(job.ExpectedVersion);
            stream.Write(bytes); stream.Flush(true);
        }
        File.Move(receipt + ".tmp", receipt, true);
    }
}
