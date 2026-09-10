using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DanmuApi.Platform;

public sealed record BundledRuntimeProgress(string Phase, int Completed, int Total);
public sealed record BundledRuntimePreparationResult(string Version, bool Changed, TimeSpan Elapsed,
    int SourceFilesHashed, int TargetFilesHashed);

/// <summary>
/// Owns only manifest-listed host/dependency files. The caller must serialize Prepare with Node startup.
/// The optional safety gate returns true only while replacement is safe; process inspection is also mandatory.
/// Metadata is a startup optimization, not cryptographic tamper protection: same-size writes with restored
/// timestamps, a modified marker, concurrent writers and privileged filesystem changes are outside its guarantee.
/// Journal/marker writes are flushed; payloads are closed and hashed. Recovery covers process interruption,
/// not sudden power loss or storage-controller write-cache loss. Runtime storage must be writable by this user.
/// </summary>
public static partial class BundledRuntimePreparer
{
    private const string Marker = ".bundled-runtime.json";
    private const string Transaction = ".bundled-runtime-transaction";
    private const string HashMode = "hash";
    private const string MetadataMode = "metadata";
    private const int MaxCriticalEntries = 64;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "node.exe", "NODE-LICENSE.txt", "nodejs-project/main.js", "nodejs-project/android-server.js",
        "nodejs-project/favorite-scheduler-host.js", "nodejs-project/package-lock.json", "nodejs-project/package.json",
        "nodejs-project/runtime-polyfills.js", "nodejs-project/runtime_asset_layout.txt",
        "nodejs-project/startup-failure.js", "nodejs-project/worker-proxy.js"
    };

    /// <summary>Mode selects the startup fast-check rule for a critical entry. "hash" (the default)
    /// verifies content on every launch; "metadata" compares size and timestamps and only hashes when
    /// they differ, which keeps a 92 MB node.exe from dominating startup. Deployment, explicit repair
    /// and the full dependency check always verify content regardless of mode.</summary>
    public sealed record Entry(string Path, string Hash, long Length = 0, long LastWrite = 0, long Created = 0, int Attributes = 0, string? Mode = null);
    public sealed record State(int Schema, string Version, List<Entry> Files);
    public sealed record Operation(string Path, bool Existed, string? BackupHash);
    public sealed record Journal(int Schema, bool Committed, State? Previous, List<Operation> Operations);

    public static void Prepare(string bundleDirectory, AppPaths paths) => Prepare(bundleDirectory, paths, null);

    /// <summary>Run via Task.Run for UI use. Cancellation before Committing rolls back; after the final
    /// cancellation check at Committing 0, commit/cleanup must finish and cancellation is no longer observed.
    /// forceRepair explicitly authorizes replacing conflicting managed files. Recovery always reports an
    /// IOException, so the caller must acknowledge/retry. Progress is phase-local (Total=0 means unknown),
    /// never overall percent. Callbacks run on this thread; failures propagate, with recovery before commit
    /// and cleanup after commit. Completed is emitted only after all work succeeds.</summary>
    public static BundledRuntimePreparationResult Prepare(string bundleDirectory, AppPaths paths,
        Func<bool>? isSafeToReplace, bool forceRepair = false, Action<BundledRuntimeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        var sourceHashes = 0;
        var targetHashes = 0;
        cancellationToken.ThrowIfCancellationRequested();
        Report("ReadingManifest", 0, 1);
        var bundle = Path.GetFullPath(bundleDirectory);
        var root = Path.GetFullPath(paths.RuntimeDirectory);
        if (Within(bundle, root) || Within(root, bundle)) throw new IOException("依赖源与目标目录不得重叠。");
        CheckPath(bundle);
        CheckPath(root);
        var manifestPath = Path.Combine(bundle, "SHA256SUMS.txt");
        CheckPath(manifestPath);
        var manifest = File.ReadAllText(manifestPath);
        var entries = ParseManifest(manifest);
        var version = Version(entries);
        if (File.Exists(Path.Combine(bundle, BuildIdentityName)))
        {
            var build = ReadSmall<State>(Path.Combine(bundle, BuildIdentityName), out _);
            ValidateBuild(build);
            if (build.Version != version || build.Files.Any(e => !entries.Any(s => s.Path == e.Path && s.Hash == e.Hash)) ||
                entries.Where(e => Critical(e.Path)).Any(e => !build.Files.Any(s => s.Path == e.Path && s.Hash == e.Hash)))
                throw new IOException("安装包版本记录与完整清单不一致。");
        }
        Report("ReadingManifest", 1, 1);
        cancellationToken.ThrowIfCancellationRequested();
        // Source validation precedes creating a fresh runtime directory.
        if (!Directory.Exists(root)) VerifySources();
        Directory.CreateDirectory(root);
        var lockPath = Path.Combine(root, ".bundled-runtime.lock");
        CheckPath(lockPath);
        using var prepareLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var markerPath = Path.Combine(root, Marker);
        var transaction = Path.Combine(root, Transaction);
        CheckPath(markerPath);
        CheckPath(transaction);
        if (Directory.Exists(transaction))
        {
            EnsureSafe();
            Report("Recovering", 0, 0);
            Recover(root, transaction);
            throw new IOException("检测到上次运行环境准备中断，已恢复或完成清理；请确认后重新准备。");
        }
        Report("ReadingState", 0, 1);
        var old = File.Exists(markerPath) ? Read<State>(markerPath) : null;
        if (old is not null) ValidateState(old);
        Report("ReadingState", 1, 1);
        var changed = old?.Version != version || forceRepair;
        HashSet<string>? verifiedTargets = null;
        HashSet<string>? driftedPaths = null;
        if (old is not null)
        {
            Report("CheckingTargetMetadata", 0, old.Files.Count);
            CheckPaths(old.Files.Select(entry => Path.GetDirectoryName(Target(root, entry.Path))!));
            var receiptState = ReadReceiptState(root);
            // A missing or unreadable receipt invalidates every shortcut: verify every recorded payload.
            var verifyEverything = receiptState == ReceiptState.Missing;
            // Do not short-circuit: every target must be checked, including reparse points after the first mismatch.
            var drifted = new List<Entry>();
            driftedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < old.Files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = old.Files[i];
                if (!MetadataMatches(Target(root, entry.Path), entry))
                {
                    drifted.Add(entry);
                    driftedPaths.Add(entry.Path);
                }
                Report("CheckingTargetMetadata", i + 1, old.Files.Count);
            }
            var metadataChanged = verifyEverything || receiptState == ReceiptState.Legacy || drifted.Count > 0;
            // Hash critical entries plus anything whose metadata drifted; a full verification is only
            // required when the receipt is missing, which is the state we cannot reason about at all.
            var targetsToVerify = verifyEverything
                ? old.Files
                : old.Files.Where(entry => Critical(entry.Path)).Concat(drifted)
                    .DistinctBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).ToList();
            verifiedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Report("VerifyingTarget", 0, targetsToVerify.Count);
            for (var i = 0; i < targetsToVerify.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = targetsToVerify[i];
                var target = Target(root, entry.Path);
                if (!File.Exists(target))
                {
                    if (!forceRepair) throw new IOException($"运行依赖丢失：{entry.Path}；请确认后修复运行环境。");
                }
                else
                {
                    targetHashes++;
                    var matches = Hash(target) == entry.Hash;
                    if (!matches && !forceRepair)
                        throw new IOException($"运行依赖损坏或发生冲突：{entry.Path}；请确认后修复运行环境。");
                    if (matches) verifiedTargets.Add(entry.Path);
                }
                // Count checked candidates, including explicitly authorized missing files, not hashes.
                Report("VerifyingTarget", i + 1, targetsToVerify.Count);
            }
            if (!changed)
            {
                // Persist verified metadata to avoid repeatedly hashing harmless timestamp changes.
                if (metadataChanged) CommitMarker(CreateSnapshot());
                else cancellationToken.ThrowIfCancellationRequested();
                return Result(false);
            }
        }
        if (sourceHashes == 0) VerifySources();
        var trustedLegacy = old is null && !forceRepair
            ? MatchTrustedLegacy(root, cancellationToken.ThrowIfCancellationRequested, () => targetHashes++) : null;
        var previous = (old?.Files ?? trustedLegacy)?.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        Report("CheckingConflicts", 0, entries.Count);
        CheckPaths(entries.Select(entry => Target(root, entry.Path)));
        var adoptMatching = old is null && trustedLegacy is null;
        for (var i = 0; i < entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[i];
            var target = Target(root, entry.Path);
            if (Directory.Exists(target)) throw new IOException($"依赖目标是目录：{entry.Path}");
            if (!File.Exists(target)) adoptMatching = false;
            else if (!previous.ContainsKey(entry.Path))
            {
                targetHashes++;
                if (Hash(target, validatePath: false) != entry.Hash)
                {
                    adoptMatching = false;
                    if (!forceRepair)
                        throw new BundledRuntimeNeedsConfirmationException($"未受管理的旧依赖与安装包冲突：{entry.Path}；请确认后修复运行环境。");
                }
            }
            Report("CheckingConflicts", i + 1, entries.Count);
        }
        // Payloads that already match the new manifest stay untouched: the checks above either hashed
        // them or confirmed their recorded metadata, so re-copying 6469 files on every bundle change
        // only wastes I/O. Anything whose recorded hash changed, is missing or drifted is redeployed.
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (old is not null && !forceRepair && verifiedTargets is not null && driftedPaths is not null)
        {
            foreach (var entry in entries)
            {
                if (!previous.TryGetValue(entry.Path, out var recorded) || recorded.Hash != entry.Hash) continue;
                if (verifiedTargets.Contains(entry.Path) || !driftedPaths.Contains(entry.Path)) keep.Add(entry.Path);
            }
        }
        var nextPaths = new HashSet<string>(entries.Select(entry => entry.Path), StringComparer.OrdinalIgnoreCase);
        var obsolete = previous.Keys.Where(path => !nextPaths.Contains(path)).ToList();
        var deploy = entries.Where(entry => !keep.Contains(entry.Path)).ToList();
        EnsureSafe();
        if (adoptMatching)
        {
            CommitMarker(CreateSnapshot());
            return Result(true);
        }
        if (deploy.Count == 0 && obsolete.Count == 0)
        {
            // The bundle version changed but every managed payload already matches it: re-register only.
            CommitMarker(CreateSnapshot());
            return Result(true);
        }
        Directory.CreateDirectory(transaction);
        var operations = new List<Operation>();
        var journalPath = Path.Combine(transaction, "journal.json");
        try
        {
            var allPaths = deploy.Select(e => e.Path).Concat(obsolete).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Report("BackingUp", 0, allPaths.Count);
            var backedUp = new Operation[allPaths.Count];
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken };
            // Only private backup payloads are written concurrently; managed targets remain read-only.
            // Preserve both hashes and all path checks. Join every batch before callbacks or recovery.
            for (var start = 0; start < allPaths.Count; start += 64)
            {
                var end = Math.Min(start + 64, allPaths.Count);
                RunBatch(start, end, parallelOptions, i =>
                {
                    var relative = allPaths[i];
                    var target = Target(root, relative);
                    var exists = File.Exists(target);
                    string? backupHash = null;
                    if (exists)
                    {
                        var backup = Target(Path.Combine(transaction, "backup"), relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                        backupHash = CopyAndHash(target, backup);
                        if (Hash(target) != backupHash) throw new IOException($"备份过程中目标发生变化：{relative}");
                    }
                    backedUp[i] = new(relative, exists, backupHash);
                });
                cancellationToken.ThrowIfCancellationRequested();
                Report("BackingUp", end, allPaths.Count);
            }
            operations.AddRange(backedUp);
            Report("Staging", 0, deploy.Count);
            CheckPaths(deploy.Select(entry => Target(bundle, entry.Path)));
            var stagedFiles = deploy.Select(entry => Target(Path.Combine(transaction, "stage"), entry.Path)).ToArray();
            var stageDirectories = stagedFiles.Select(file => Path.GetDirectoryName(file)!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            CheckPaths(stageDirectories);
            foreach (var directory in stageDirectories) Directory.CreateDirectory(directory);
            CheckPaths(stageDirectories);
            // Only private, not-yet-deployed payloads are copied concurrently. Bound I/O and memory;
            // wait for the whole batch before callbacks, journaling, cancellation rollback or cleanup.
            for (var start = 0; start < deploy.Count; start += 64)
            {
                var end = Math.Min(start + 64, deploy.Count);
                RunBatch(start, end, parallelOptions, i =>
                {
                    var entry = deploy[i];
                    // Hash the bytes as they are written: same verification, one less full read.
                    if (CopyAndHash(Target(bundle, entry.Path), stagedFiles[i]) != entry.Hash)
                        throw new IOException($"依赖暂存校验失败：{entry.Path}");
                });
                cancellationToken.ThrowIfCancellationRequested();
                Report("Staging", end, deploy.Count);
            }
            Report("WritingJournal", 0, 1);
            cancellationToken.ThrowIfCancellationRequested();
            var journal = new Journal(1, false, old, operations);
            Save(journalPath, journal); // Write-ahead, flushed before the first replacement.
            Report("WritingJournal", 1, 1);
            EnsureSafe();
            Report("Replacing", 0, operations.Count);
            var next = entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
            // A managed subtree whose target directory is absent is entirely new: a kept file would
            // have required that directory to exist. Promoting it is one rename instead of thousands,
            // which dominates first install on machines where each rename is intercepted by AV.
            var promoted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var segment in deploy.Select(entry => entry.Path.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (next.ContainsKey(segment)) continue;
                var promotedTarget = Target(root, segment);
                if (File.Exists(promotedTarget) || Directory.Exists(promotedTarget)) continue;
                var stagedRoot = Target(Path.Combine(transaction, "stage"), segment);
                if (!Directory.Exists(stagedRoot)) continue;
                cancellationToken.ThrowIfCancellationRequested();
                if (isSafeToReplace is not null && !isSafeToReplace()) throw new IOException("运行环境替换安全门已关闭。");
                CheckPath(promotedTarget, ancestors: false);
                Directory.Move(stagedRoot, promotedTarget);
                promoted.Add(segment);
            }
            var targetDirectories = operations.Select(o => Path.GetDirectoryName(Target(root, o.Path))!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            CheckPaths(targetDirectories);
            foreach (var directory in targetDirectories) Directory.CreateDirectory(directory);
            CheckPaths(targetDirectories);
            for (var i = 0; i < operations.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (isSafeToReplace is not null && !isSafeToReplace()) throw new IOException("运行环境替换安全门已关闭。");
                var operation = operations[i];
                var target = Target(root, operation.Path);
                CheckPath(target, ancestors: false);
                if (next.ContainsKey(operation.Path))
                {
                    if (promoted.Contains(operation.Path.Split('/')[0])) { Report("Replacing", i + 1, operations.Count); continue; }
                    File.Move(Target(Path.Combine(transaction, "stage"), operation.Path), target, true);
                }
                else File.Delete(target);
                Report("Replacing", i + 1, operations.Count);
            }
            // Each staged file was closed and hashed before its same-volume atomic move.
            // A third full payload read adds no process-interruption guarantee.
            var snapshot = CreateSnapshot();
            Report("Committing", 0, 2);
            cancellationToken.ThrowIfCancellationRequested();
            WriteReceipt(bundle, root, snapshot);
            Save(markerPath, snapshot);
            Report("Committing", 1, 2);
            Save(journalPath, journal with { Committed = true });
            Report("Committing", 2, 2);
            DeleteTransaction(transaction, progress);
            return Result(true);
        }
        catch (Exception original)
        {
            try
            {
                // Never restore while the caller reports Node running. Keep journal for the next invocation.
                EnsureSafe();
                Report("Recovering", 0, 0);
                if (Directory.Exists(transaction)) Recover(root, transaction);
            }
            catch (Exception recovery)
            {
                throw new AggregateException("准备失败且恢复未完成；保留交易记录，禁止启动 Node。", original, recovery);
            }
            throw;
        }

        void Report(string phase, int completed, int total) => progress?.Invoke(new(phase, completed, total));
        BundledRuntimePreparationResult Result(bool didChange)
        {
            Report("Completed", 1, 1);
            return new(version, didChange, clock.Elapsed, sourceHashes, targetHashes);
        }
        State CreateSnapshot()
        {
            Report("Snapshotting", 0, entries.Count);
            var files = new List<Entry>(entries.Count);
            for (var i = 0; i < entries.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = entries[i];
                files.Add(Stat(Target(root, entry.Path), entry));
                Report("Snapshotting", i + 1, entries.Count);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(1, version, files);
        }
        void CommitMarker(State snapshot)
        {
            Report("Committing", 0, 1);
            cancellationToken.ThrowIfCancellationRequested();
            WriteReceipt(bundle, root, snapshot);
            Save(markerPath, snapshot);
            Report("Committing", 1, 1);
        }
        void VerifySources()
        {
            Report("VerifyingSource", 0, entries.Count);
            CheckPaths(entries.Select(entry => Target(bundle, entry.Path)));
            // Source hashing is read-only and independent per entry, so batches overlap I/O; the
            // caller thread still owns every progress callback and the cancellation check.
            var options = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken };
            for (var start = 0; start < entries.Count; start += 64)
            {
                var end = Math.Min(start + 64, entries.Count);
                RunBatch(start, end, options, i =>
                {
                    var entry = entries[i];
                    if (Hash(Target(bundle, entry.Path), validatePath: false) != entry.Hash)
                        throw new IOException($"内置运行环境校验失败：{entry.Path}");
                });
                sourceHashes += end - start;
                cancellationToken.ThrowIfCancellationRequested();
                Report("VerifyingSource", end, entries.Count);
            }
        }
        void EnsureSafe()
        {
            if (isSafeToReplace is not null && !isSafeToReplace()) throw new IOException("Node 运行中或无法确认停止，禁止替换运行环境。");
            EnsureNodeStopped(root);
        }
    }

    private static List<Entry> ParseManifest(string text)
    {
        var entries = new List<Entry>();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length < 67 || line.Substring(64, 2) != "  " || !Regex.IsMatch(line[..64], "\\A[0-9a-fA-F]{64}\\z"))
                throw new IOException("运行环境清单格式无效。");
            ValidateRelative(line[66..]);
            entries.Add(new(line[66..], line[..64].ToUpperInvariant()));
        }
        ValidateEntries(entries);
        return entries;
    }

    private static void ValidateEntries(List<Entry> entries)
    {
        if (entries.Count == 0 || entries.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
            throw new IOException("运行依赖清单为空或包含重复路径。");
        var names = entries.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            ValidateRelative(entry.Path);
            if (!Regex.IsMatch(entry.Hash, "\\A[0-9A-F]{64}\\z")) throw new IOException("依赖 hash 无效。");
            var segments = entry.Path.Split('/');
            for (var i = 1; i < segments.Length; i++)
                if (names.Contains(string.Join('/', segments.Take(i)))) throw new IOException("依赖路径发生文件/目录冲突。");
        }
        if (!names.Contains("node.exe") || !names.Contains("nodejs-project/main.js")) throw new IOException("运行环境清单缺少 Node 或启动入口。");
    }

    private static void ValidateState(State state)
    {
        if (state.Schema != 1 || state.Files is null) throw new IOException("运行依赖状态版本无效。");
        ValidateEntries(state.Files);
        if (state.Version != Version(state.Files) || state.Files.Any(e => e.Length < 0 || e.LastWrite <= 0 || e.Created <= 0))
            throw new IOException("运行依赖状态校验失败。");
    }

    private static string Version(IEnumerable<Entry> entries) => Convert.ToHexString(SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(string.Join('\n', entries.OrderBy(e => e.Path, StringComparer.Ordinal)
            .Select(e => e.Hash + "  " + e.Path)))));

    private static void ValidateRelative(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.Contains(':') || path.StartsWith('/') ||
            path.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Any(c => c < 32 || "<>\"|?*".Contains(c)) ||
                Regex.IsMatch(part, "\\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|$)", RegexOptions.IgnoreCase)))
            throw new IOException($"运行依赖路径非法：{path}");
        if (!Hosts.Contains(path) && !path.StartsWith("nodejs-project/node_modules/", StringComparison.OrdinalIgnoreCase))
            throw new IOException($"运行依赖清单越过程序自有文件边界：{path}");
    }

    private static string Target(string root, string relative)
    {
        // Callers use only entries validated when reading the manifest, marker or journal.
        return Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private static bool Within(string candidate, string root) => candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void CheckPath(string path, bool ancestors = true) => CheckPaths([path], ancestors);

    // Sharing one cache is safe inside a single read-only pass: the ancestor set is checked once.
    private static void CheckPath(string path, HashSet<string> checkedPaths) => CheckPaths([path], true, checkedPaths);

    private static void CheckPaths(IEnumerable<string> paths, bool ancestors = true) =>
        CheckPaths(paths, ancestors, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private static void CheckPaths(IEnumerable<string> paths, bool ancestors, HashSet<string> checkedPaths)
    {
        // Cache only checks within one read-only pass, never across mutations.
        foreach (var path in paths)
        for (string? current = Path.GetFullPath(path); current is not null && checkedPaths.Add(current); current = ancestors ? Path.GetDirectoryName(current) : null)
        {
            var nativePath = current.StartsWith(@"\\?\", StringComparison.Ordinal) ? current :
                current.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + current[2..] : @"\\?\" + current;
            var attributes = GetFileAttributes(nativePath);
            if (attributes == uint.MaxValue)
            {
                var error = Marshal.GetLastWin32Error();
                if (error is 2 or 3) continue; // Explicitly classified missing path, not an access-error fallback.
                throw new IOException($"无法检查运行依赖路径：{current}", new System.ComponentModel.Win32Exception(error));
            }
            if ((attributes & (uint)FileAttributes.ReparsePoint) != 0)
                throw new IOException($"运行依赖路径包含 reparse point：{current}");
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileAttributesW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributes(string path);

    private static string Hash(string path, bool validatePath = true)
    {
        if (validatePath) CheckPath(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static Entry Stat(string path, Entry entry)
    {
        var info = new FileInfo(path);
        return entry with { Length = info.Length, LastWrite = info.LastWriteTimeUtc.Ticks,
            Created = info.CreationTimeUtc.Ticks, Attributes = (int)info.Attributes };
    }
    private static bool MetadataMatches(string path, Entry entry)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            // FileInfo.Exists also returns false for access errors; explicitly classify those.
            try { _ = File.GetAttributes(path); }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }
        if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException($"运行依赖目标不是普通文件：{path}");
        return info.Length == entry.Length && info.LastWriteTimeUtc.Ticks == entry.LastWrite &&
            info.CreationTimeUtc.Ticks == entry.Created && (int)info.Attributes == entry.Attributes;
    }
    private static bool Critical(string path) => !path.StartsWith("nodejs-project/node_modules/", StringComparison.OrdinalIgnoreCase);
    private static void RunBatch(int start, int end, ParallelOptions options, Action<int> action)
    {
        try { Parallel.For(start, end, options, action); }
        catch (AggregateException error) when (error.Flatten().InnerExceptions.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.Flatten().InnerExceptions[0]).Throw();
            throw; // Preserve all failures; never retry or accept a partial batch.
        }
    }

    private static T Read<T>(string path)
    {
        CheckPath(path);
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new IOException($"运行依赖记录为空：{path}"); }
        catch (JsonException error) { throw new IOException($"运行依赖记录损坏：{path}", error); }
    }
    private static void Save<T>(string path, T value)
    {
        CheckPath(path);
        var temporary = path + ".writing";
        CheckPath(temporary);
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, value, Json);
            file.Flush(true);
        }
        File.Move(temporary, path, true);
    }
    private static void CopyPayload(string source, string destination, bool validatePaths = true)
    {
        if (validatePaths) { CheckPath(source); CheckPath(destination); }
        File.Copy(source, destination, overwrite: false); // Closed before the caller verifies its hash.
    }

    /// <summary>Copies and hashes in one pass. The caller compares the returned digest with the
    /// manifest, so a payload changed between verification and copy still fails before deployment.</summary>
    private static string CopyAndHash(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var sha = SHA256.Create();
        var buffer = new byte[128 * 1024];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            sha.TransformBlock(buffer, 0, count, null, 0);
            output.Write(buffer, 0, count);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    private static void Recover(string root, string transaction)
    {
        CheckTree(transaction);
        var receiptPath = Path.Combine(root, ReceiptName);
        CheckPath(receiptPath);
        File.Delete(receiptPath); // Recovery invalidates startup evidence; the next full verification re-registers it.
        var journalPath = Path.Combine(transaction, "journal.json");
        if (!File.Exists(journalPath)) { DeleteTransaction(transaction); return; }
        var journal = Read<Journal>(journalPath);
        if (journal.Schema != 1 || journal.Operations is null || journal.Operations.Count == 0 ||
            journal.Operations.Select(o => o.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Operations.Count)
            throw new IOException("交易 journal 格式无效。");
        if (journal.Previous is not null) ValidateState(journal.Previous);
        foreach (var operation in journal.Operations)
        {
            ValidateRelative(operation.Path);
            if (operation.Existed ? operation.BackupHash is null || !Regex.IsMatch(operation.BackupHash, "\\A[0-9A-F]{64}\\z") : operation.BackupHash is not null)
                throw new IOException("交易备份记录无效。");
            CheckPath(Target(root, operation.Path));
            if (!journal.Committed && operation.Existed &&
                Hash(Target(Path.Combine(transaction, "backup"), operation.Path)) != operation.BackupHash)
                throw new IOException($"交易备份校验失败：{operation.Path}");
        }
        if (!journal.Committed)
        {
            foreach (var operation in journal.Operations)
            {
                var target = Target(root, operation.Path);
                if (operation.Existed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    // Backups remain intact until the whole rollback completes, so recovery is repeatable.
                    var restore = Target(Path.Combine(transaction, "restore"), operation.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(restore)!);
                    CheckPath(restore);
                    if (File.Exists(restore)) File.Delete(restore);
                    CopyPayload(Target(Path.Combine(transaction, "backup"), operation.Path), restore);
                    File.Move(restore, target, true);
                }
                else File.Delete(target);
            }
            var markerPath = Path.Combine(root, Marker);
            CheckPath(markerPath);
            if (journal.Previous is null) File.Delete(markerPath);
            else Save(markerPath, journal.Previous);
            // Make cleanup crash-safe after rollback too.
            Save(journalPath, journal with { Committed = true });
        }
        DeleteTransaction(transaction);
    }

    private static void CheckTree(string directory)
    {
        CheckPath(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            CheckPath(entry);
            if (Directory.Exists(entry)) CheckTree(entry);
        }
    }
    private static void DeleteTransaction(string directory, Action<BundledRuntimeProgress>? progress = null)
    {
        // Inspect the complete tree before any deletion, preserving the reparse-point safety barrier.
        // Postorder traversal gives a real count of files AND directories; journal and root are last.
        var payloads = new List<(string Path, bool Directory)>();
        var checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inspected = 0;
        var journal = Path.Combine(directory, "journal.json");
        progress?.Invoke(new("InspectingCleanup", 0, 0));
        Inspect(directory);
        var hasJournal = File.Exists(journal);
        var total = payloads.Count + (hasJournal ? 1 : 0) + 1;
        var completed = 0;
        progress?.Invoke(new("CleaningUp", 0, total));
        foreach (var entry in payloads)
        {
            if (entry.Directory) Directory.Delete(entry.Path); else File.Delete(entry.Path);
            progress?.Invoke(new("CleaningUp", ++completed, total));
        }
        if (hasJournal)
        {
            File.Delete(journal);
            progress?.Invoke(new("CleaningUp", ++completed, total));
        }
        Directory.Delete(directory);
        progress?.Invoke(new("CleaningUp", ++completed, total));

        void Inspect(string current)
        {
            CheckPaths([current], true, checkedPaths);
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                CheckPaths([entry], true, checkedPaths);
                var isDirectory = Directory.Exists(entry);
                if (isDirectory) Inspect(entry);
                inspected++;
                progress?.Invoke(new("InspectingCleanup", inspected, 0));
                if (!entry.Equals(journal, StringComparison.OrdinalIgnoreCase)) payloads.Add((entry, isDirectory));
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(Microsoft.Win32.SafeHandles.SafeProcessHandle process,
        uint flags, System.Text.StringBuilder name, ref int size);

    private static void EnsureNodeStopped(string root)
    {
        var target = Path.Combine(root, "node.exe");
        var processes = Process.GetProcessesByName("node");
        try
        {
            foreach (var process in processes)
            {
                string executable;
                try
                {
                    // Query the kernel-maintained executable path, not the loader's mutable module list.
                    // PROCESS_QUERY_LIMITED_INFORMATION requires neither VM_READ nor full process access.
                    using var handle = OpenProcess(0x1000, false, process.Id);
                    if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    var name = new System.Text.StringBuilder(32768);
                    var size = name.Capacity;
                    if (!QueryFullProcessImageName(handle, 0, name, ref size))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    if (size == 0) throw new IOException($"Node 进程 {process.Id} 的可执行路径为空，禁止替换。");
                    executable = name.ToString();
                }
                catch (System.ComponentModel.Win32Exception error)
                {
                    bool exited;
                    try { exited = process.HasExited; }
                    catch (Exception stateError) when (stateError is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        throw new IOException($"无法读取 Node 进程 {process.Id} 归属且无法确认退出，禁止替换。",
                            new AggregateException(error, stateError));
                    }
                    if (exited) continue; // Confirmed exited; never infer exit from an access error or retry silently.
                    throw new IOException($"无法读取 Node 进程 {process.Id} 归属，禁止替换。", error);
                }
                if (Path.GetFullPath(executable).Equals(target, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"目标 Node 进程 {process.Id} 仍在运行，禁止替换运行环境。");
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
