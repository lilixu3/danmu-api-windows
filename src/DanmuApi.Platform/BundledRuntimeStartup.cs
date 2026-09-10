using System.Security.Cryptography;
using System.Text.Json;

namespace DanmuApi.Platform;

public enum BundledRuntimeReadinessStatus { Ready, NeedsPreparation }
public sealed record BundledRuntimeReadiness(BundledRuntimeReadinessStatus Status, string Reason,
    int ReceiptBytesRead, int CriticalFilesChecked);
public sealed class BundledRuntimeNeedsConfirmationException(string message) : IOException(message);

public static partial class BundledRuntimePreparer
{
    private const string ReceiptName = ".bundled-runtime-receipt.json";
    private const string BuildIdentityName = "runtime-build.json";
    private const int SmallRecordLimit = 16 * 1024;
    private const int CurrentReceiptSchema = 2;
    public sealed record StartupReceipt(int Schema, string Version, string Root, List<Entry> Files);

    /// <summary>Read-only bounded startup check. Does not open the full manifest/state or enumerate dependencies.
    /// This detects critical payload damage, not arbitrary noncritical dependency tampering.
    /// Entries declared "metadata" are compared by size and timestamps and only hashed when those differ,
    /// so a large payload such as node.exe no longer dominates every launch; content is still verified by
    /// deployment, explicit repair and the full dependency check.</summary>
    public static BundledRuntimeReadiness CheckReadiness(string bundleDirectory, AppPaths paths)
    {
        var bytes = 0;
        var checkedFiles = 0;
        BundledRuntimeReadiness NotReady(string reason) => new(BundledRuntimeReadinessStatus.NeedsPreparation, reason, bytes, checkedFiles);
        try
        {
            var bundle = Path.GetFullPath(bundleDirectory);
            var root = Path.GetFullPath(paths.RuntimeDirectory);
            if (Within(bundle, root) || Within(root, bundle)) return NotReady("依赖源与目标目录不得重叠。");
            CheckPath(root);
            var transaction = Path.Combine(root, Transaction);
            CheckPath(transaction);
            if (Path.Exists(transaction)) return NotReady("存在未完成的运行环境事务，需要恢复。");
            var receiptPath = Path.Combine(root, ReceiptName);
            CheckPath(receiptPath);
            if (!File.Exists(receiptPath)) return NotReady("缺少运行环境启动凭据，需要完整校验。");
            var receipt = ReadSmall<StartupReceipt>(receiptPath, out bytes);
            var build = ReadSmall<State>(Path.Combine(bundle, BuildIdentityName), out _);
            ValidateBuild(build);
            if (receipt.Schema != CurrentReceiptSchema) return NotReady("运行环境启动凭据版本过旧，需要重新校验。");
            if (receipt.Version != build.Version || !string.Equals(receipt.Root, root, StringComparison.OrdinalIgnoreCase) ||
                receipt.Files is null || receipt.Files.Count != build.Files.Count)
                return NotReady("运行环境启动凭据与安装包不匹配，需要完整校验。");
            var checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < build.Files.Count; i++)
            {
                var expected = build.Files[i];
                var recorded = receipt.Files[i];
                if (recorded.Path != expected.Path || recorded.Hash != expected.Hash)
                    return NotReady("运行环境关键文件凭据不匹配。");
                var target = Target(root, expected.Path);
                CheckPath(target, checkedPaths);
                checkedFiles++;
                if (!File.Exists(target)) return NotReady($"运行环境关键入口缺失或损坏：{expected.Path}");
                if (expected.Mode == MetadataMode && HasMetadata(recorded))
                {
                    if (!MetadataMatches(target, recorded))
                        return NotReady($"运行环境关键入口元数据不符，需要重新校验：{expected.Path}");
                    continue;
                }
                if (Hash(target, validatePath: false) != expected.Hash)
                    return NotReady($"运行环境关键入口缺失或损坏：{expected.Path}");
            }
            return new(BundledRuntimeReadinessStatus.Ready, "运行环境已就绪。", bytes, checkedFiles);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return NotReady($"运行环境快检失败：{error.Message}");
        }
    }

    private enum ReceiptState { Missing, Legacy, Current }

    /// <summary>Classifies the startup receipt without trusting its contents. A missing or unreadable
    /// receipt means the caller must verify every recorded payload; a legacy schema means metadata is
    /// unavailable and the receipt must be rewritten after a successful check.</summary>
    private static ReceiptState ReadReceiptState(string root)
    {
        var path = Path.Combine(root, ReceiptName);
        CheckPath(path);
        if (!File.Exists(path)) return ReceiptState.Missing;
        try
        {
            return ReadSmall<StartupReceipt>(path, out _).Schema == CurrentReceiptSchema
                ? ReceiptState.Current : ReceiptState.Legacy;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return ReceiptState.Missing;
        }
    }

    private static bool HasMetadata(Entry entry) => entry.Length > 0 && entry.LastWrite > 0 && entry.Created > 0;

    public static BundledRuntimePreparationResult PrepareForStartup(string bundleDirectory, AppPaths paths,
        Func<bool>? isSafeToReplace = null, bool forceRepair = false, Action<BundledRuntimeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The caller owns serialization with startup. Prepare owns the cross-process mutation lock.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (!forceRepair && CheckReadiness(bundleDirectory, paths).Status == BundledRuntimeReadinessStatus.Ready)
        {
            var build = ReadSmall<State>(Path.Combine(bundleDirectory, BuildIdentityName), out _);
            return new(build.Version, false, clock.Elapsed, 0, build.Files.Count);
        }
        return Prepare(bundleDirectory, paths, isSafeToReplace, forceRepair, progress, cancellationToken);
    }

    private static T ReadSmall<T>(string path, out int bytes)
    {
        CheckPath(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        bytes = 0;
        if (file.Length > SmallRecordLimit) throw new IOException($"运行环境小记录超过 16 KiB：{Path.GetFileName(path)}");
        var buffer = new byte[(int)file.Length];
        file.ReadExactly(buffer);
        bytes = buffer.Length;
        return JsonSerializer.Deserialize<T>(buffer, Json) ?? throw new IOException("运行环境小记录为空。");
    }

    private static void ValidateBuild(State build)
    {
        if (build.Schema != 1 || build.Version is null ||
            !System.Text.RegularExpressions.Regex.IsMatch(build.Version, "\\A[0-9A-F]{64}\\z") || build.Files is null)
            throw new IOException("安装包运行环境版本记录无效。");
        ValidateEntries(build.Files);
        if (build.Files.Count > MaxCriticalEntries) throw new IOException("安装包关键入口数量超过限制。");
        if (build.Files.Any(entry => entry.Mode is not (null or HashMode or MetadataMode)))
            throw new IOException("安装包关键入口校验模式无效。");
    }

    private static void WriteReceipt(string bundle, string root, State snapshot)
    {
        var descriptor = Path.Combine(bundle, BuildIdentityName);
        if (!File.Exists(descriptor)) return; // Older bundles support full Prepare only; CheckReadiness reports the missing identity.
        var build = ReadSmall<State>(descriptor, out _);
        ValidateBuild(build);
        var observed = snapshot.Files.ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
        if (build.Version != snapshot.Version ||
            build.Files.Any(e => !observed.TryGetValue(e.Path, out var stat) || stat.Hash != e.Hash))
            throw new IOException("安装包版本记录与完整清单不一致。");
        // Record the deployed target's own metadata so the next launch can compare size/timestamps
        // instead of re-hashing every critical payload.
        var files = build.Files.Select(entry =>
        {
            var stat = observed[entry.Path];
            return entry with { Length = stat.Length, LastWrite = stat.LastWrite, Created = stat.Created, Attributes = stat.Attributes };
        }).ToList();
        var receipt = new StartupReceipt(CurrentReceiptSchema, snapshot.Version, root, files);
        if (JsonSerializer.SerializeToUtf8Bytes(receipt, Json).Length > SmallRecordLimit)
            throw new IOException("运行环境启动凭据超过 16 KiB。");
        Save(Path.Combine(root, ReceiptName), receipt);
    }

    private static List<Entry>? MatchTrustedLegacy(string root, Action checkCancellation, Action hashed)
    {
        var assembly = typeof(BundledRuntimePreparer).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sha256", StringComparison.Ordinal)))
        {
            using var input = assembly.GetManifestResourceStream(resource) ?? throw new IOException("内嵌旧版清单缺失。");
            using var reader = new StreamReader(input);
            var entries = ParseManifest(reader.ReadToEnd());
            var matches = true;
            foreach (var entry in entries)
            {
                checkCancellation();
                var target = Target(root, entry.Path);
                CheckPath(target);
                if (!File.Exists(target)) { matches = false; break; }
                hashed();
                if (Hash(target) != entry.Hash) { matches = false; break; }
            }
            if (matches) return entries;
        }
        return null;
    }
}
