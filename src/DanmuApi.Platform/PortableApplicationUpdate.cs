using System.IO.Compression;
using System.Text.Json;
using System.Security.Cryptography;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DanmuApi.Tests")]

namespace DanmuApi.Platform;

public static class PortableApplicationUpdate
{
    public static IReadOnlyList<string> Extract(string archivePath, string stagingDirectory)
    {
        stagingDirectory = Root(stagingDirectory);
        if (Directory.Exists(stagingDirectory)) throw new IOException("更新暂存目录已存在");
        Directory.CreateDirectory(stagingDirectory);
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 30000) throw new IOException("更新包文件数量超出上限");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var relative = ValidateRelativePath(entry.FullName);
            if (!names.Add(relative)) throw new IOException("更新包包含重复路径");
            if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new IOException("更新包不允许符号链接或重解析点");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            if (!IsManagedFile(relative)) throw new IOException($"更新包包含非应用文件：{relative}");
            total = checked(total + entry.Length);
            if (total > 2L * 1024 * 1024 * 1024) throw new IOException("更新解压大小超过2GiB限制");
            EnsureNoReparsePoints(stagingDirectory, relative);
            var target = Path.Combine(stagingDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
            files.Add(relative);
        }
        if (!files.Contains("DanmuApi.App.exe", StringComparer.OrdinalIgnoreCase) ||
            !files.Contains(Path.Combine("runtime-bundle", "SHA256SUMS.txt"), StringComparer.OrdinalIgnoreCase))
            throw new IOException("更新包缺少主程序或运行环境清单");
        return files;
    }

    public static bool IsManagedFile(string relative) =>
        relative.Equals("DanmuApi.App.exe", StringComparison.OrdinalIgnoreCase) ||
        relative.Equals("av_libglesv2.dll", StringComparison.OrdinalIgnoreCase) ||
        relative.Equals("libHarfBuzzSharp.dll", StringComparison.OrdinalIgnoreCase) ||
        relative.Equals("libSkiaSharp.dll", StringComparison.OrdinalIgnoreCase) ||
        relative.StartsWith("runtime-bundle" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static string ValidateRelativePath(string name)
    {
        var relative = name.Replace('\\', '/').TrimEnd('/');
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 4096 || relative.StartsWith('/') || relative.Contains(':') ||
            relative.Any(c => c < 32 || "<>\"|?*".Contains(c)) || relative.Split('/').Length > 64 ||
            relative.Split('/').Any(part => part is ".." or "." or "" || part.EndsWith(' ') || part.EndsWith('.') ||
                System.Text.RegularExpressions.Regex.IsMatch(part.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            throw new IOException("更新包路径非法");
        return relative.Replace('/', Path.DirectorySeparatorChar);
    }

    private const long MaxBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxFiles = 30000;
    private sealed record Entry(string Path, bool Original, long Length, string? Hash, string State);
    private sealed record Journal(int Version, string Target, string Backup, string State, List<Entry> Files);
    public static string JournalPath(string backupDirectory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(backupDirectory)) + ".journal.json";

    public static void Replace(string targetDirectory, string stagingDirectory, string backupDirectory, IReadOnlyList<string> files) =>
        Replace(targetDirectory, stagingDirectory, backupDirectory, files, null);

    // The observer lets tests snapshot each durable boundary without changing production recovery behavior.
    internal static void Replace(string targetDirectory, string stagingDirectory, string backupDirectory, IReadOnlyList<string> files, Action<string>? boundary)
    {
        var target = Root(targetDirectory); var stage = Root(stagingDirectory); var backup = Root(backupDirectory);
        Separate(target, stage); Separate(target, backup); Separate(stage, backup);
        using var transactionLock = Lock(backup);
        if (Directory.Exists(backup) || File.Exists(JournalPath(backup))) throw new IOException("更新事务已存在，请先恢复或清理");
        if (files.Count is 0 or > MaxFiles) throw new IOException("更新文件数量超出上限");
        var entries = new List<Entry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0, originalTotal = 0, pathBytes = 0;
        foreach (var file in files)
        {
            var relative = ValidateRelativePath(file);
            if (!IsManagedFile(relative) || !names.Add(relative)) throw new IOException("更新文件清单包含非法或重复路径");
            pathBytes += relative.Length * 6L; // Worst-case JSON escaping, leaving room for metadata.
            if (pathBytes > 8 * 1024 * 1024) throw new IOException("更新文件路径总大小超出事务上限");
            EnsureNoReparsePoints(target, relative); EnsureNoReparsePoints(stage, relative);
            var source = new FileInfo(Path.Combine(stage, relative));
            if (!source.Exists) throw new FileNotFoundException("更新暂存文件缺失", source.FullName);
            total = checked(total + source.Length);
            var old = new FileInfo(Path.Combine(target, relative));
            originalTotal = checked(originalTotal + (old.Exists ? old.Length : 0));
            if (total > MaxBytes || originalTotal > MaxBytes) throw new IOException("更新文件大小超出上限");
            entries.Add(new Entry(relative, old.Exists, old.Exists ? old.Length : 0, old.Exists ? Hash(old.FullName) : null, "Pending"));
        }
        Directory.CreateDirectory(backup);
        var journal = new Journal(1, target, backup, "Preparing", entries);
        Save(journal);
        try
        {
            boundary?.Invoke("journal-created");
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.Original)
                {
                    EnsureNoReparsePoints(target, entry.Path); EnsureNoReparsePoints(backup, entry.Path);
                    DurableCopy(Path.Combine(target, entry.Path), Path.Combine(backup, entry.Path));
                    VerifyBackup(backup, entry);
                }
                entries[i] = entry with { State = "BackedUp" };
                Save(journal); boundary?.Invoke("backed-up:" + entry.Path);
            }
            journal = journal with { State = "Replacing" }; Save(journal);
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                entries[i] = entry with { State = "Replacing" };
                Save(journal); boundary?.Invoke("replace-intent:" + entry.Path);
                EnsureNoReparsePoints(target, entry.Path); EnsureNoReparsePoints(stage, entry.Path);
                var destination = Path.Combine(target, entry.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(Path.Combine(stage, entry.Path), destination, true);
                boundary?.Invoke("file-replaced:" + entry.Path);
                entries[i] = entry with { State = "Installed" };
                Save(journal); boundary?.Invoke("installed:" + entry.Path);
            }
            journal = journal with { State = "FilesCommitted" }; Save(journal);
            boundary?.Invoke("files-committed");
            // Retained for callers of the legacy Restore signature; recovery itself only trusts the journal.
            File.WriteAllLines(Path.Combine(backup, "replaced-files.txt"), entries.Select(e => e.Path));
            File.WriteAllLines(Path.Combine(backup, "original-files.txt"), entries.Where(e => e.Original).Select(e => e.Path));
        }
        catch (Exception original)
        {
            try { RecoverCore(target, backup, null); }
            catch (Exception recovery) { throw new IOException($"应用替换失败且自动恢复失败；保留事务，请退出应用后执行恢复。替换错误：{original.GetType().Name}/0x{original.HResult:X8}；恢复错误：{recovery.GetType().Name}/0x{recovery.HResult:X8}", new AggregateException(original, recovery)); }
            throw new IOException($"应用替换失败，原版本已自动恢复；错误：{original.GetType().Name}/0x{original.HResult:X8}", original);
        }
    }

    public static void Restore(string targetDirectory, string backupDirectory, IReadOnlyList<string> installed, IReadOnlyList<string> originals) =>
        Recover(targetDirectory, backupDirectory);

    public static string Recover(string targetDirectory, string backupDirectory) => Recover(targetDirectory, backupDirectory, null);

    internal static string Recover(string targetDirectory, string backupDirectory, Action<string>? boundary)
    {
        using var transactionLock = Lock(backupDirectory);
        return RecoverCore(targetDirectory, backupDirectory, boundary);
    }

    private static string RecoverCore(string targetDirectory, string backupDirectory, Action<string>? boundary)
    {
        var journal = Load(targetDirectory, backupDirectory);
        if (journal.State is "StartupConfirmed" or "Cleaned") return "新版本已确认启动，仅允许清理，不回滚";
        if (journal.State == "Restored") return "原版本已恢复";
        // Validate every needed backup before modifying any target file.
        foreach (var entry in journal.Files.Where(e => e.Original && e.State is "Replacing" or "Installed" or "Restoring"))
            VerifyBackup(journal.Backup, entry);
        journal = journal with { State = "Restoring" }; Save(journal);
        for (var i = journal.Files.Count - 1; i >= 0; i--)
        {
            var entry = journal.Files[i];
            if (entry.State is not ("Replacing" or "Installed" or "Restoring")) continue;
            journal.Files[i] = entry with { State = "Restoring" }; Save(journal);
            boundary?.Invoke("restore-intent:" + entry.Path);
            EnsureNoReparsePoints(journal.Target, entry.Path);
            var target = Path.Combine(journal.Target, entry.Path);
            if (entry.Original) DurableCopy(Path.Combine(journal.Backup, entry.Path), target, true);
            else File.Delete(target);
            boundary?.Invoke("file-restored:" + entry.Path);
            journal.Files[i] = entry with { State = "Restored" }; Save(journal);
        }
        Save(journal with { State = "Restored" });
        return "原版本已恢复；可重新启动应用，恢复材料已保留";
    }

    public static void MarkStartupConfirmed(string targetDirectory, string backupDirectory)
    {
        using var transactionLock = Lock(backupDirectory);
        var journal = Load(targetDirectory, backupDirectory);
        if (journal.State is "StartupConfirmed" or "Cleaned") return;
        if (journal.State != "FilesCommitted") throw new IOException("文件替换尚未提交，不能确认启动");
        Save(journal with { State = "StartupConfirmed" });
    }

    // Returns a warning rather than rolling back an application which already acknowledged startup.
    public static string? CleanupConfirmed(string targetDirectory, string backupDirectory, params string[] paths)
    {
        using var transactionLock = Lock(backupDirectory);
        var journal = Load(targetDirectory, backupDirectory);
        if (journal.State == "Cleaned") return null;
        if (journal.State != "StartupConfirmed") throw new IOException("尚未收到新版本启动确认，不能清理备份");
        try
        {
            var job = Path.GetDirectoryName(journal.Backup)!;
            foreach (var path in paths.Prepend(journal.Backup))
            {
                var full = Root(path);
                Separate(journal.Target, full);
                if (!full.Equals(journal.Backup, StringComparison.OrdinalIgnoreCase) &&
                    !(Path.GetFileName(full).Equals("stage", StringComparison.OrdinalIgnoreCase) && !File.Exists(full)) &&
                    !(Path.GetExtension(full).Equals(".zip", StringComparison.OrdinalIgnoreCase) && !Directory.Exists(full)))
                    throw new IOException("清理仅允许备份、暂存目录和ZIP更新包");
                if (!string.Equals(Path.GetDirectoryName(full), job, StringComparison.OrdinalIgnoreCase) ||
                    full.Equals(JournalPath(journal.Backup), StringComparison.OrdinalIgnoreCase)) throw new IOException("清理路径不属于更新任务");
                ValidateTree(full);
                if (Directory.Exists(full)) Directory.Delete(full, true);
                else File.Delete(full);
            }
            Save(journal with { State = "Cleaned" });
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Only type/HResult are recorded: filesystem exception text can contain user supplied values.
            var warning = $"cleanup-warning: 新版本已启动；清理未完成，可稍后重试 ({error.GetType().Name}, 0x{error.HResult:X8})";
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(journal.Backup)!, "cleanup-warning.txt"), warning);
            return warning;
        }
    }

    private static Journal Load(string targetDirectory, string backupDirectory)
    {
        var target = Root(targetDirectory); var backup = Root(backupDirectory); Separate(target, backup);
        var path = JournalPath(backup); EnsureNoReparsePoints(Path.GetDirectoryName(path)!, Path.GetFileName(path));
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new IOException("恢复事务超出大小上限");
        Journal journal;
        try { journal = JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(path)) ?? throw new IOException("恢复事务为空"); }
        catch (JsonException error) { throw new IOException("恢复事务格式错误", error); }
        if (journal.Version != 1 || !string.Equals(journal.Target, target, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(journal.Backup, backup, StringComparison.OrdinalIgnoreCase) || journal.Files is null || journal.Files.Count is 0 or > MaxFiles ||
            journal.State is not ("Preparing" or "Replacing" or "FilesCommitted" or "Restoring" or "Restored" or "StartupConfirmed" or "Cleaned"))
            throw new IOException("恢复事务身份、状态或数量无效");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var entry in journal.Files)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Path)) throw new IOException("恢复文件项无效");
            var valid = ValidateRelativePath(entry.Path);
            if (!IsManagedFile(valid) || !names.Add(valid) || entry.Length < 0 || entry.Length > MaxBytes ||
                entry.State is not ("Pending" or "BackedUp" or "Replacing" or "Installed" or "Restoring" or "Restored") ||
                (entry.Original && (entry.Hash is null || entry.Hash.Length != 64 || !entry.Hash.All(Uri.IsHexDigit))))
                throw new IOException("恢复文件路径、状态或备份摘要无效");
            total = checked(total + entry.Length); if (total > MaxBytes) throw new IOException("恢复备份大小超出上限");
            EnsureNoReparsePoints(target, valid); EnsureNoReparsePoints(backup, valid);
        }
        return journal;
    }

    private static FileStream Lock(string backupDirectory)
    {
        var path = JournalPath(backupDirectory) + ".lock";
        EnsureNoReparsePoints(Path.GetDirectoryName(path)!, Path.GetFileName(path));
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static void Save(Journal journal)
    {
        var path = JournalPath(journal.Backup); var temporary = path + ".tmp";
        EnsureNoReparsePoints(Path.GetDirectoryName(path)!, Path.GetFileName(path));
        EnsureNoReparsePoints(Path.GetDirectoryName(path)!, Path.GetFileName(temporary));
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, journal); stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void VerifyBackup(string backup, Entry entry)
    {
        EnsureNoReparsePoints(backup, entry.Path);
        var path = Path.Combine(backup, entry.Path);
        if (!File.Exists(path) || new FileInfo(path).Length != entry.Length || Hash(path) != entry.Hash)
            throw new IOException("恢复备份缺失或摘要不符；未删除恢复材料");
    }

    private static void DurableCopy(string source, string destination, bool overwrite = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input = File.OpenRead(source);
        using var output = new FileStream(destination, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output); output.Flush(true);
    }

    private static string Root(string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        EnsureNoReparsePoints(root, "");
        return root;
    }

    private static void Separate(string first, string second)
    {
        if (first.Equals(second, StringComparison.OrdinalIgnoreCase) ||
            first.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            second.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("更新目标、暂存和备份目录必须互不包含");
    }

    private static void ValidateTree(string path)
    {
        Root(path);
        if (!Directory.Exists(path)) return;
        var pending = new Stack<string>(); pending.Push(path); var count = 0;
        while (pending.Count > 0)
            foreach (var child in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                if (++count > MaxFiles * 2) throw new IOException("清理目录数量超出上限");
                Root(child);
                if (Directory.Exists(child)) pending.Push(child);
            }
    }

    private static void EnsureNoReparsePoints(string root, string relative)
    {
        var current = Path.GetFullPath(root);
        for (var ancestor = current; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            if ((File.Exists(ancestor) || Directory.Exists(ancestor)) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("应用更新目录不能是链接");
        foreach (var component in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, component);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("应用更新路径包含链接");
        }
    }
}
