using System.Text;
using System.Text.Json;
using DanmuApi.Core;
using DanmuApi.Runtime;

namespace DanmuApi.Platform;

/// <summary>Host must hold its start/stop operation gate for the lease and reject any live/starting/stopping core.
/// A snapshot check alone is NOT an implementation of this contract.</summary>
public interface IBackupRestoreGuard
{
    ValueTask<IAsyncDisposable> AcquireStoppedLeaseAsync(CancellationToken cancellationToken);
}

public sealed record BackupRestorePreview(Guid Id, int SchemaVersion, DateTimeOffset CreatedAt,
    IReadOnlyList<string> Keys, IReadOnlyList<string> OmittedSections, int ExcludedKeys, string Target,
    int? FavoriteCount, IReadOnlyList<string> DesktopPreferenceKeys);

public sealed class BackupLocalService(string envPath, string appVersion, IBackupRestoreGuard guard, string? settingsPath = null)
{
    private readonly string path = Path.GetFullPath(envPath);
    private readonly string favoritesPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(envPath)))!, ".cache", "favoritesCache");
    private readonly string? preferencesPath = settingsPath is null ? null : Path.GetFullPath(settingsPath);
    private readonly SemaphoreSlim gate = new(1, 1);
    private (Guid Id, Dictionary<string, DotEnvFileFingerprint> Fingerprints, byte[] Bytes)? pending;

    public byte[] Create()
    {
        var targets = Targets().ToArray();
        foreach (var target in targets) CheckPath(target);
        var before = targets.ToDictionary(x => x, DotEnvFile.GetFingerprint);
        var values = DotEnvFile.ReadValuesStrict(path);
        var favorites = File.Exists(favoritesPath) ? ReadSmallFile(favoritesPath) : "{}";
        favorites = BackupBundle.NormalizeFavorites(favorites);
        var preferences = preferencesPath is null ? null : new SettingsStore(preferencesPath).Read()
            .Where(x => BackupDesktopPreferencePolicy.AllowedKeys.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value);
        foreach (var target in targets)
            if (before[target] != DotEnvFile.GetFingerprint(target)) throw new IOException("数据在备份期间改变，请重新备份");
        return BackupBundle.Encode(values, appVersion, favorites, preferences);
    }

    public async Task ExportAsync(string destination, bool sensitiveDestinationAuthorized, CancellationToken ct = default)
    {
        if (!sensitiveDestinationAuthorized) throw new InvalidOperationException("需要授权敏感备份的保存位置");
        var target = Path.GetFullPath(destination);
        if (target.StartsWith(@"\\", StringComparison.Ordinal) || Targets().Contains(target, StringComparer.OrdinalIgnoreCase))
            throw new IOException("请选择独立本地备份文件");
        CheckPath(target);
        var bytes = Create();
        var temporary = target + ".backup-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, ct);
            if (!(await File.ReadAllBytesAsync(temporary, ct)).SequenceEqual(bytes)) throw new IOException("备份回读不一致");
            File.Move(temporary, target, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<BackupRestorePreview> PreviewFileAsync(string source, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(source);
        return await PreviewAsync(await BackupBundle.ReadBoundedAsync(stream, ct), ct);
    }

    public async Task<BackupRestorePreview> PreviewAsync(byte[] bytes, CancellationToken ct = default)
    {
        bytes = bytes.ToArray();
        var document = BackupBundle.Decode(bytes);
        if (document.DesktopPreferences is not null && preferencesPath is null)
            throw new InvalidOperationException("宿主未配置桌面偏好恢复路径");
        await gate.WaitAsync(ct);
        try
        {
            foreach (var target in Targets()) CheckPath(target);
            var id = Guid.NewGuid();
            pending = (id, Targets().ToDictionary(x => x, DotEnvFile.GetFingerprint), bytes);
            int? favoriteCount = null;
            if (document.Favorites is not null)
            {
                using var favorites = JsonDocument.Parse(document.Favorites);
                favoriteCount = favorites.RootElement.EnumerateObject().Count();
            }
            return new(id, document.SchemaVersion, DateTimeOffset.FromUnixTimeMilliseconds(document.CreatedAtMs),
                document.Environment.Keys.Order().ToArray(), document.OmittedSections, document.ExcludedKeys, path,
                favoriteCount, document.DesktopPreferences?.Keys.Order().ToArray() ?? []);
        }
        finally { gate.Release(); }
    }

    public async Task RestoreAsync(Guid previewId, bool overwriteConfirmed, CancellationToken ct = default)
    {
        if (!overwriteConfirmed) throw new InvalidOperationException("必须预览并明确确认覆盖");
        await gate.WaitAsync(ct);
        try
        {
            if (pending is not { } preview || preview.Id != previewId) throw new InvalidOperationException("预览已失效，请重新读取备份");
            await using var lease = await guard.AcquireStoppedLeaseAsync(ct);
            var document = BackupBundle.Decode(preview.Bytes);
            foreach (var target in Targets())
            {
                CheckPath(target);
                var actual = DotEnvFile.GetFingerprint(target);
                if (actual != preview.Fingerprints[target]) throw new DotEnvConflictException(target, preview.Fingerprints[target], actual);
            }
            var originals = Targets().ToDictionary(x => x, x => File.Exists(x) ? File.ReadAllBytes(x) : null);
            var touched = new List<string>();
            ct.ThrowIfCancellationRequested();
            try
            {
                if (document.Environment.Count != 0)
                {
                    touched.Add(path);
                    DotEnvFile.ApplyMutations(path, preview.Fingerprints[path], document.Environment.Select(x => DotEnvMutation.Set(x.Key, x.Value)).ToArray());
                }
                if (document.Favorites is not null)
                {
                    touched.Add(favoritesPath);
                    AtomicWrite(favoritesPath, Encoding.UTF8.GetBytes(document.Favorites));
                }
                if (document.DesktopPreferences is not null)
                {
                    touched.Add(preferencesPath!);
                    new SettingsStore(preferencesPath!).Write(document.DesktopPreferences.ToDictionary(x => x.Key, x => (string?)x.Value));
                }
            }
            catch (Exception failure)
            {
                var errors = new List<Exception> { failure };
                foreach (var target in touched.AsEnumerable().Reverse())
                {
                    try
                    {
                        var original = originals[target];
                        if (original is null) { if (File.Exists(target)) File.Delete(target); }
                        else if (!File.Exists(target) || !File.ReadAllBytes(target).SequenceEqual(original)) AtomicWrite(target, original);
                    }
                    catch (Exception rollback) { errors.Add(rollback); }
                }
                if (errors.Count > 1) throw new AggregateException("恢复失败且回滚不完整，需要人工检查；全部诊断已保留", errors);
                throw new IOException("恢复失败，所有已更改文件已回滚", failure);
            }
            pending = null;
        }
        finally { gate.Release(); }
    }

    private IEnumerable<string> Targets()
    {
        yield return path;
        yield return favoritesPath;
        if (preferencesPath is not null) yield return preferencesPath;
    }
    private static string ReadSmallFile(string file)
    {
        if (new FileInfo(file).Length > BackupBundle.MaximumBytes) throw new InvalidDataException("数据文件超过备份大小限制");
        return File.ReadAllText(file, new UTF8Encoding(false, true));
    }
    private static void AtomicWrite(string target, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".backup-write-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target);
            if (!File.ReadAllBytes(target).SequenceEqual(bytes)) throw new IOException("恢复文件回读校验失败");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void CheckPath(string target)
    {
        for (var current = target; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("备份操作不允许符号链接或重解析目录");
    }
}
