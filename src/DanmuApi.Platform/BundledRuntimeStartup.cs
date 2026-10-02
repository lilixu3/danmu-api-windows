using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DanmuApi.Core;

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
            if (HasOutbound(build.Files)) ValidateOutbound(root, build.Files, build.Arch!);
            return new(BundledRuntimeReadinessStatus.Ready,
                HasOutbound(build.Files) ? "运行环境及出站辅助程序已就绪。" : "运行环境已就绪（旧宿主未包含出站功能）。", bytes, checkedFiles);
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
        if (build.Files.Count > (HasOutbound(build.Files) ? 56 : MaxCriticalEntries)) throw new IOException("安装包关键入口数量超过限制。");
        if (build.Files.Any(entry => entry.Mode is not (null or HashMode or MetadataMode)))
            throw new IOException("安装包关键入口校验模式无效。");
        if (HasOutbound(build.Files) && build.Files.Any(e =>
                (e.Path.StartsWith("nodejs-project/app-outbound-", StringComparison.OrdinalIgnoreCase) ||
                 e.Path == OutboundPrefix + "danmu-outbound.exe" || e.Path == OutboundPrefix + "outbound-build.json" ||
                 e.Path == OutboundPrefix + "OUTBOUND-SHA256SUMS.txt") && e.Mode != HashMode))
            throw new IOException("出站关键入口必须在每次启动时校验 SHA256。");
        ValidateRuntimeIdentity(build);
    }

    private static void ValidateOutbound(string root, IReadOnlyCollection<Entry> entries, string arch)
    {
        var machine = arch switch
        {
            "x64" => 0x8664, "x86" => 0x014c, "arm64" => 0xAA64,
            _ => throw new IOException("出站辅助程序架构无效：" + arch)
        };
        ValidatePeMachine(Target(root, "node.exe"), machine);
        ValidatePeMachine(Target(root, OutboundPrefix + "danmu-outbound.exe"), machine);
        var expected = entries.ToDictionary(e => e.Path, e => e.Hash, StringComparer.OrdinalIgnoreCase);
        var metadata = ReadSmall<JsonElement>(Target(root, OutboundPrefix + "outbound-build.json"), out _);
        if (metadata.ValueKind != JsonValueKind.Object ||
            metadata.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != metadata.EnumerateObject().Count())
            throw new IOException("出站构建记录必须是没有重复字段的对象。");
        RequireNumber("schemaVersion", 1);
        RequireNumber("protocolVersion", 1);
        RequireNumber("machine", machine);
        RequireText("version", OutboundVersion);
        RequireText("runtimeIdentifier", "win-" + arch);
        RequireText("goVersion", "go1.26.0");
        RequireText("sourceCommit", OutboundSourceCommit);
        var executableHash = Text("executableSha256");
        if (!ValidDigest(executableHash) || !string.Equals(executableHash,
                expected[OutboundPrefix + "danmu-outbound.exe"], StringComparison.OrdinalIgnoreCase))
            throw new IOException("出站辅助程序构建记录与运行环境 SHA256 不一致。");
        if (!ValidDigest(Text("sourceSha256")))
            throw new IOException("出站构建记录源码 SHA256 无效。");
        RequireText("toolchainArchiveUrl", "https://go.dev/dl/go1.26.0.windows-amd64.zip");
        RequireText("toolchainArchiveSha256", "9bbe0fc64236b2b51f6255c05c4232532b8ecc0e6d2e00950bd3021d8a4d07d4");
        RequireNumber("toolchainArchiveBytes", 74815266);
        if (!metadata.TryGetProperty("toolchainInputCount", out var inputCount) ||
            inputCount.ValueKind != JsonValueKind.Number || !inputCount.TryGetInt32(out var count) || count <= 0)
            throw new IOException("出站构建记录工具链输入数量无效。");
        foreach (var key in new[] { "toolchainInputSha256", "toolchainDriverSha256", "toolchainCompilerSha256", "toolchainLinkerSha256", "toolchainStandardLibrarySha256" })
            if (!ValidDigest(Text(key))) throw new IOException("出站构建记录工具链输入 SHA256 无效：" + key);
        if (!metadata.TryGetProperty("goEnvironment", out var environment) || environment.ValueKind != JsonValueKind.Object ||
            environment.EnumerateObject().Count() != 4 ||
            environment.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != 4)
            throw new IOException("出站构建记录 Go 环境无效。");
        foreach (var pair in new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GOROOT"] = "verified-official-archive-root", ["GOTOOLDIR"] = "pkg/tool/windows_amd64",
            ["GOENV"] = "off", ["GOTOOLCHAIN"] = "local"
        })
            if (!environment.TryGetProperty(pair.Key, out var value) || value.ValueKind != JsonValueKind.String || value.GetString() != pair.Value)
                throw new IOException("出站构建记录未使用隔离的官方 Go 工具链：" + pair.Key);
        _ = OutboundSourceInputs(metadata);
        if (!metadata.TryGetProperty("dependencies", out var dependencies) || dependencies.ValueKind != JsonValueKind.Array ||
            !metadata.TryGetProperty("buildFlags", out var flags) || flags.ValueKind != JsonValueKind.Array ||
            flags.EnumerateArray().Any(f => f.ValueKind != JsonValueKind.String))
            throw new IOException("出站构建记录依赖或构建参数类型无效。");
        foreach (var dependency in dependencies.EnumerateArray())
            if (dependency.ValueKind != JsonValueKind.Object || new[] { "path", "version", "sum", "goModSum" }.Any(key =>
                    !dependency.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())))
                throw new IOException("出站构建记录依赖字段无效。");
        var hashFile = Target(root, OutboundPrefix + "OUTBOUND-SHA256SUMS.txt");
        CheckPath(hashFile);
        if (new FileInfo(hashFile).Length > SmallRecordLimit) throw new IOException("出站 SHA256 清单超过 16 KiB。");
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(hashFile))
        {
            if (line.Length < 67 || line.Substring(64, 2) != "  " || !ValidDigest(line[..64]))
                throw new IOException("出站 SHA256 清单格式无效。");
            var name = line[66..];
            if (name == "OUTBOUND-SHA256SUMS.txt" || !OutboundFiles.Contains(name, StringComparer.Ordinal) || !observed.Add(name))
                throw new IOException("出站 SHA256 清单包含重复或非程序自有文件：" + name);
            if (!string.Equals(line[..64], expected[OutboundPrefix + name], StringComparison.OrdinalIgnoreCase))
                throw new IOException("出站与运行环境 SHA256 清单不一致：" + name);
        }
        if (observed.Count != OutboundFiles.Length - 1) throw new IOException("出站 SHA256 清单不完整。");

        string Text(string key)
        {
            if (!metadata.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw new IOException("出站构建记录缺少有效字符串：" + key);
            return value.GetString()!;
        }
        void RequireText(string key, string value)
        {
            if (Text(key) != value) throw new IOException("出站构建记录版本或身份不匹配：" + key);
        }
        void RequireNumber(string key, int number)
        {
            if (!metadata.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var actual) || actual != number)
                throw new IOException("出站构建记录整数或版本不匹配：" + key);
        }
    }

    private static Dictionary<string, string> OutboundSourceInputs(JsonElement metadata)
    {
        if (!metadata.TryGetProperty("sourceInputs", out var inputs) || inputs.ValueKind != JsonValueKind.Array || inputs.GetArrayLength() == 0)
            throw new IOException("出站构建记录缺少源码输入。");
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var input in inputs.EnumerateArray())
        {
            if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String ||
                !input.TryGetProperty("sha256", out var hash) || hash.ValueKind != JsonValueKind.String || !ValidDigest(hash.GetString()!))
                throw new IOException("出站源码输入格式无效。");
            var name = path.GetString()!;
            if (!SafeArchivePath(name) || !(name.StartsWith("runtime/outbound/", StringComparison.Ordinal) ||
                    name is "build/Build-OutboundHelper.ps1" or "build/Get-GoToolchain.ps1") || !expected.TryAdd(name, hash.GetString()!.ToLowerInvariant()))
                throw new IOException("出站源码输入路径非法或重复：" + name);
        }
        var canonical = string.Concat(expected.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => e.Value + "  " + e.Key + "\n"));
        if (!metadata.TryGetProperty("sourceSha256", out var sourceHash) || sourceHash.ValueKind != JsonValueKind.String ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))), sourceHash.GetString(), StringComparison.OrdinalIgnoreCase))
            throw new IOException("出站源码输入聚合 SHA256 不一致。");
        return expected;
    }

    private static bool SafeArchivePath(string name) => !string.IsNullOrWhiteSpace(name) && !name.Contains('\\') && !name.Contains(':') &&
        !name.StartsWith('/') && name.Split('/').All(p => p.Length > 0 && p is not ("." or "..") && !p.EndsWith('.') && !p.EndsWith(' ') &&
            !p.Any(c => c < 32 || "<>\"|?*".Contains(c)));

    private static void ValidateOutboundSourceArchive(string root)
    {
        var metadata = ReadSmall<JsonElement>(Target(root, OutboundPrefix + "outbound-build.json"), out _);
        var inputs = OutboundSourceInputs(metadata);
        var archivePath = Target(root, OutboundPrefix + "outbound-source.zip");
        CheckPath(archivePath);
        using var archive = System.IO.Compression.ZipFile.OpenRead(archivePath);
        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (!SafeArchivePath(entry.FullName) || !observed.Add(entry.FullName)) throw new IOException("出站源码归档包含非法或重复路径。");
            if (!inputs.TryGetValue(entry.FullName, out var digest))
            {
                if (!entry.FullName.StartsWith("licenses/", StringComparison.Ordinal)) throw new IOException("出站源码归档包含未声明输入：" + entry.FullName);
                continue;
            }
            using var content = entry.Open();
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(content)), digest, StringComparison.OrdinalIgnoreCase))
                throw new IOException("出站源码归档 SHA256 不一致：" + entry.FullName);
        }
        if (inputs.Keys.Any(name => !observed.Contains(name))) throw new IOException("出站源码归档缺少声明输入。");
    }

    private static bool ValidDigest(string value) => System.Text.RegularExpressions.Regex.IsMatch(value, "\\A[0-9a-fA-F]{64}\\z");

    private static void ValidatePeMachine(string path, int expected)
    {
        CheckPath(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(file);
        if (file.Length < 64 || reader.ReadUInt16() != 0x5A4D) throw new IOException("出站运行环境可执行文件缺少 MZ 头：" + path);
        file.Position = 0x3C;
        var offset = reader.ReadInt32();
        if (offset < 64 || offset > file.Length - 24) throw new IOException("出站运行环境 PE 头偏移无效：" + path);
        file.Position = offset;
        if (reader.ReadUInt32() != 0x00004550) throw new IOException("出站运行环境可执行文件缺少 PE 头：" + path);
        var actual = reader.ReadUInt16();
        if (actual != expected) throw new IOException($"出站运行环境 PE 架构不匹配：{Path.GetFileName(path)} 为 0x{actual:X4}，要求 0x{expected:X4}。");
    }

    /// <summary>The bundle states which Node runtime it carries. A missing or mismatched record is a
    /// packaging error rather than a repairable state, so it fails loudly and is never "fixed" by
    /// re-preparing the same wrong payload.</summary>
    private static void ValidateRuntimeIdentity(State build)
    {
        if (string.IsNullOrWhiteSpace(build.NodeVersion) || string.IsNullOrWhiteSpace(build.Arch))
            throw new InvalidOperationException("安装包运行环境记录缺少 Node 版本或架构，无法确认内置运行时。");
        var host = HostArchitecture();
        if (!string.Equals(build.Arch, host, StringComparison.Ordinal))
            throw new InvalidOperationException($"安装包内置的 Node 运行环境为 {build.Arch}，与当前进程架构 {host} 不匹配。");
        if (BundledNodeRuntime.ParseMajor(build.NodeVersion) != BundledNodeRuntime.ExpectedMajor)
            throw new InvalidOperationException(
                $"安装包内置 Node {build.NodeVersion} 与本程序要求的 Node {BundledNodeRuntime.ExpectedVersion} 不匹配。");
    }

    /// <summary>Runtime-identifier style architecture name of the running process.</summary>
    public static string HostArchitecture() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        Architecture.Arm64 => "arm64",
        var other => throw new InvalidOperationException("不支持的进程架构：" + other),
    };

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
