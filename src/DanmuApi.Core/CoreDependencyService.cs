using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DanmuApi.Tests")]

namespace DanmuApi.Core;

public sealed record CoreDependencyIssue(string Name, string Range, string ParentDirectory,
    string? InstalledDirectory, string? PublicRootName, string? PublicRootDirectory, string Diagnostic);
public sealed record CoreDependencyCheckResult(int Total, IReadOnlyList<CoreDependencyIssue> Issues)
{
    public bool IsHealthy => Issues.Count == 0;
}
public sealed record CoreDependencyRepairProgress(string Stage, string Detail, long? CompletedBytes = null, long? TotalBytes = null);
public interface ICoreDependencyService
{
    Task<CoreDependencyCheckResult> CheckAsync(ManagedCoreVariant variant, CancellationToken cancellationToken = default);
    Task<CoreDependencyCheckResult> RepairAsync(ManagedCoreVariant variant,
        IProgress<CoreDependencyRepairProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>Repairs only the selected installed core's required dependency closure. Public node_modules is read-only.
/// Both guards must be shared with the runtime/scheduler and the actual CoreInstaller instance respectively.</summary>
public sealed class CoreDependencyService : ICoreDependencyService, IDisposable
{
    private readonly string _project, _node;
    private readonly Func<CancellationToken, ValueTask<IAsyncDisposable>> _runtimeLease, _coreLease;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _publicKey;
    private readonly Func<IReadOnlyCollection<string>> _notBundled;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private const string JournalName = ".dependency-repair.json";
    private const string BackupName = ".dependency-old-node_modules";
    internal Action<string>? TransactionHook { get; set; }

    /// <summary>
    /// Windows 宿主故意不随包、且核心在 Windows 下也不会执行到的依赖（与移动端
    /// <c>coreDependenciesManagedOutsideBaseRuntime</c> 同一套判断）：宿主用 worker.js 加载核心，
    /// server.js 里的 dotenv/chokidar 链路从不执行；esbuild 只是仓库根的构建期工具。
    /// 把它们算进"必需依赖"只会让健康的核心永远显示缺失。
    /// </summary>
    internal static readonly string[] NotBundledByHost = ["chokidar", "dotenv", "esbuild"];

    /// <summary>
    /// 探测必需依赖时要跳过的名字。<paramref name="redisExpected"/> 为 true 表示用户配置了
    /// LOCAL_REDIS_URL（此时 redis 必须可见，铺开失败就要照报缺失），否则 redis 属于按需可选。
    /// </summary>
    public static IReadOnlyList<string> NotBundledDependencyNames(bool redisExpected) =>
        redisExpected ? NotBundledByHost : [.. NotBundledByHost, "redis"];

    public CoreDependencyService(string nodeProjectDirectory, string nodeExecutable,
        Func<CancellationToken, ValueTask<IAsyncDisposable>> acquireStoppedMaintenanceLease,
        Func<CancellationToken, ValueTask<IAsyncDisposable>> acquireCoreMutationLease, HttpClient? httpClient = null,
        Func<IReadOnlyCollection<string>>? notBundledDependencies = null)
        : this(nodeProjectDirectory, nodeExecutable, acquireStoppedMaintenanceLease, acquireCoreMutationLease,
            httpClient, SignedCoreDependencyPack.TrustedPublicKey, notBundledDependencies) { }

    internal CoreDependencyService(string nodeProjectDirectory, string nodeExecutable,
        Func<CancellationToken, ValueTask<IAsyncDisposable>> acquireStoppedMaintenanceLease,
        Func<CancellationToken, ValueTask<IAsyncDisposable>> acquireCoreMutationLease, HttpClient? httpClient, string publicKey,
        Func<IReadOnlyCollection<string>>? notBundledDependencies = null)
    {
        _project = Path.GetFullPath(nodeProjectDirectory); _node = Path.GetFullPath(nodeExecutable);
        _runtimeLease = acquireStoppedMaintenanceLease ?? throw new ArgumentNullException(nameof(acquireStoppedMaintenanceLease));
        _coreLease = acquireCoreMutationLease ?? throw new ArgumentNullException(nameof(acquireCoreMutationLease));
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false });
        if (_http.DefaultRequestHeaders.Authorization is not null) throw new ArgumentException("依赖签名通道禁止携带 Authorization", nameof(httpClient));
        _publicKey = publicKey;
        _notBundled = notBundledDependencies ?? (() => NotBundledByHost);
    }

    public async Task<CoreDependencyCheckResult> CheckAsync(ManagedCoreVariant variant, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var coreLease = await _coreLease(cancellationToken).ConfigureAwait(false);
            var core = CorePath(variant);
            ValidateCore(core);
            if (File.Exists(Path.Combine(core, JournalName))) throw new IOException("核心依赖上次事务未完成，请停止服务后执行修复以恢复旧依赖");
            return await ProbeAsync(core, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<CoreDependencyCheckResult> RepairAsync(ManagedCoreVariant variant,
        IProgress<CoreDependencyRepairProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // This order matches CoreManagement: runtime stop finishes before installer mutation;
            // installer releases its gate before any runtime restart.
            await using var runtimeLease = await _runtimeLease(cancellationToken).ConfigureAwait(false);
            await using var coreLease = await _coreLease(cancellationToken).ConfigureAwait(false);
            var core = CorePath(variant);
            ValidateCore(core);
            Recover(core);
            var source = CoreManifestStore.Read(core) ?? throw new IOException("核心缺少来源 manifest，拒绝在线修复");
            if (variant == ManagedCoreVariant.Custom || source.Variant != variant ||
                source.Repository is not ("huangxd-/danmu_api" or "lilixu3/danmu_api"))
                throw new InvalidOperationException("自定义或非受支持仓库核心禁止使用在线签名依赖包");
            progress?.Report(new("Checking", "正在检查选中核心必需依赖闭包"));
            var before = await ProbeAsync(core, cancellationToken).ConfigureAwait(false);
            if (before.IsHealthy) return before;
            var packageBytes = File.ReadAllBytes(Path.Combine(core, "package.json"));
            var sourceSnapshot = source;
            var serialFile = Path.Combine(_project, ".core-dependency-pack-serial");
            EnsureNoLinks(serialFile);
            var serial = File.Exists(serialFile) ? long.Parse(File.ReadAllText(serialFile), CultureInfo.InvariantCulture) : 26L;
            progress?.Report(new("Manifest", "正在验证固定仓库签名清单"));
            var pack = new SignedCoreDependencyPack(_http, _publicKey);
            var signed = await pack.FetchManifestAsync(serial, cancellationToken).ConfigureAwait(false);
            AtomicWrite(serialFile, System.Text.Encoding.UTF8.GetBytes(signed.Manifest.Serial.ToString(CultureInfo.InvariantCulture)));
            var operation = Guid.NewGuid().ToString("N");
            var staging = Path.Combine(_project, ".core-dependency-staging-" + operation);
            var packDirectory = Path.Combine(_project, ".core-dependency-pack-" + operation);
            Exception? operationError = null;
            var committed = false;
            try
            {
                Directory.CreateDirectory(staging);
                File.WriteAllBytes(Path.Combine(staging, "package.json"), packageBytes);
                var stagedModules = Path.Combine(staging, "node_modules");
                if (Directory.Exists(Path.Combine(core, "node_modules"))) CopyTree(Path.Combine(core, "node_modules"), stagedModules, cancellationToken);
                else Directory.CreateDirectory(stagedModules);
                progress?.Report(new("Extracting", "正在校验签名 ZIP 并安全解压到私有暂存目录"));
                await pack.ExtractAsync(signed.Manifest, packDirectory, progress, cancellationToken).ConfigureAwait(false);
                var replaced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var pass = 0; pass < 500; pass++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var check = await ProbeAsync(staging, cancellationToken).ConfigureAwait(false);
                    if (check.IsHealthy) break;
                    var issue = check.Issues[0];
                    if (issue.PublicRootDirectory is not null && issue.PublicRootName is not null)
                    {
                        // A package resolved in public space cannot see core-local children. Copy its
                        // required ancestor into private space before repairing its child dependency.
                        var target = Path.Combine(stagedModules, issue.PublicRootName.Replace('/', Path.DirectorySeparatorChar));
                        if (Directory.Exists(target)) throw new IOException("公共依赖闭包无法安全重定位：" + Describe(issue));
                        CopyTree(issue.PublicRootDirectory, target, cancellationToken);
                        continue;
                    }
                    if (!string.Equals(staging, issue.ParentDirectory, StringComparison.OrdinalIgnoreCase) && !IsInside(staging, issue.ParentDirectory)) throw new IOException("依赖修复目标越过核心暂存目录");
                    SignedCoreDependencyPack.ValidatePackageName(issue.Name);
                    var destination = issue.InstalledDirectory is { } installed && IsInside(staging, installed)
                        ? installed : Path.Combine(issue.ParentDirectory, "node_modules", issue.Name.Replace('/', Path.DirectorySeparatorChar));
                    if (!replaced.Add(destination)) throw new IOException("签名依赖包不能满足必需版本或入口：" + Describe(issue));
                    var candidate = signed.Manifest.Packages.SingleOrDefault(p => p.Path == "node_modules/" + issue.Name);
                    if (candidate is null) throw new IOException("签名依赖包未覆盖核心必需依赖：" + Describe(issue));
                    progress?.Report(new("Merging", "正在补齐核心本地依赖 " + issue.Name));
                    DeleteTree(destination);
                    CopyTree(Path.Combine(packDirectory, candidate.Path.Replace('/', Path.DirectorySeparatorChar)), destination, cancellationToken);
                    if (pass == 499) throw new IOException("核心依赖修复超出闭包操作配额");
                }
                var validated = await ProbeAsync(staging, cancellationToken).ConfigureAwait(false);
                RequireHealthy(validated);
                // Detect changes made outside the installer guard before applying any transaction.
                ValidateCore(core);
                if (!packageBytes.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(core, "package.json"))) || CoreManifestStore.Read(core) != sourceSnapshot)
                    throw new IOException("修复期间选中核心发生变化，拒绝应用暂存依赖");
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new("Replacing", "正在事务替换核心本地依赖，保留旧目录用于恢复"));
                var result = await ReplaceAsync(core, stagedModules, cancellationToken).ConfigureAwait(false);
                committed = true;
                progress?.Report(new("Completed", "核心必需依赖闭包和声明根入口 Node resolve 验证通过"));
                return result;
            }
            catch (Exception error)
            {
                operationError = error;
                if (committed) throw new IOException("核心依赖已修复并生效，但完成通知失败", error);
                throw;
            }
            finally
            {
                var cleanupErrors = new List<Exception>();
                foreach (var path in new[] { staging, packDirectory }.Concat(committed ? new[] { Path.Combine(core, BackupName) } : Array.Empty<string>()))
                {
                    try { DeleteTree(path); }
                    catch (Exception error) { cleanupErrors.Add(error); }
                }
                if (cleanupErrors.Count > 0)
                {
                    if (operationError is not null) cleanupErrors.Insert(0, operationError);
                    throw new IOException(committed ? "核心依赖已修复并生效，但暂存/旧备份清理失败" : "核心依赖修复失败，且暂存清理也失败",
                        new AggregateException(cleanupErrors));
                }
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<CoreDependencyCheckResult> ReplaceAsync(string core, string staging, CancellationToken ct)
    {
        var target = Path.Combine(core, "node_modules"); var backup = Path.Combine(core, BackupName); var journal = Path.Combine(core, JournalName);
        EnsureNoLinks(target); EnsureNoLinks(backup); EnsureNoLinks(journal);
        if (Directory.Exists(backup) || File.Exists(backup)) throw new IOException("发现未清理的旧依赖备份，拒绝覆盖：" + backup);
        var hadOld = Directory.Exists(target);
        AtomicWrite(journal, JsonSerializer.SerializeToUtf8Bytes(new DependencyTransaction(1, hadOld)));
        try
        {
            if (hadOld) Directory.Move(target, backup);
            TransactionHook?.Invoke("OldMoved");
            Directory.Move(staging, target);
            TransactionHook?.Invoke("NewMoved");
            var result = await ProbeAsync(core, ct).ConfigureAwait(false);
            RequireHealthy(result);
            TransactionHook?.Invoke("Validated");
            ct.ThrowIfCancellationRequested();
            File.Delete(journal); // Commit point; old copy survives every failure before this point.
            return result;
        }
        catch (Exception error)
        {
            try { Recover(core); }
            catch (Exception recoveryError) { throw new IOException("核心依赖替换失败且恢复失败；备份与事务记录保留", new AggregateException(error, recoveryError)); }
            throw;
        }
    }

    private sealed record DependencyTransaction(int Schema, bool HadOld);
    private static void Recover(string core)
    {
        var journal = Path.Combine(core, JournalName); var backup = Path.Combine(core, BackupName); var target = Path.Combine(core, "node_modules");
        EnsureNoLinks(journal); EnsureNoLinks(backup); EnsureNoLinks(target);
        if (!File.Exists(journal)) return;
        using var parsed = JsonDocument.Parse(File.ReadAllBytes(journal));
        var root = parsed.RootElement;
        if (root.GetProperty("Schema").GetInt32() != 1) throw new IOException("未知核心依赖事务 schema，拒绝自动恢复");
        var hadOld = root.GetProperty("HadOld").GetBoolean();
        if (Directory.Exists(backup)) { DeleteTree(target); Directory.Move(backup, target); }
        else if (!hadOld) DeleteTree(target);
        else if (!Directory.Exists(target)) throw new IOException("核心依赖事务丢失旧目录，需人工恢复");
        File.Delete(journal);
    }

    private async Task<CoreDependencyCheckResult> ProbeAsync(string core, CancellationToken ct)
    {
        EnsureNoLinks(core); EnsureNoLinks(Path.Combine(_project, "node_modules"));
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DanmuApi.Core.CoreDependencyProbe.mjs")
            ?? throw new IOException("缺少内置核心依赖探针");
        using var reader = new StreamReader(stream);
        var script = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        var start = new ProcessStartInfo(_node) { WorkingDirectory = core, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false), StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8 };
        start.ArgumentList.Add("--experimental-import-meta-resolve"); start.ArgumentList.Add("--input-type=module");
        start.ArgumentList.Add("--eval"); start.ArgumentList.Add(script);
        foreach (var name in new[] { "NODE_OPTIONS", "NODE_PATH", "NODE_COMPILE_CACHE" }) start.Environment.Remove(name);
        start.Environment["NODE_DISABLE_COMPILE_CACHE"] = "1";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = Process.Start(start) ?? throw new IOException("无法启动核心依赖检查 Node 进程");
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new
            {
                core,
                shared = Path.Combine(_project, "node_modules"),
                excluded = _notBundled().ToArray(),
            })).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var stdout = await output.ConfigureAwait(false); var stderr = await error.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException($"核心依赖检查失败（Node exit {process.ExitCode}）：{stderr}");
            var result = JsonSerializer.Deserialize<CoreDependencyCheckResult>(stdout, SignedCoreDependencyPack.JsonOptions)
                ?? throw new IOException("Node 依赖检查返回空结果");
            if (result.Issues is null || result.Total < 0) throw new IOException("Node 依赖检查返回无效结果");
            return result;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
        }
    }

    private string CorePath(ManagedCoreVariant variant) => Path.Combine(_project, variant.ToDirectoryName());
    private static void ValidateCore(string core)
    {
        EnsureNoLinks(core); EnsureNoLinks(Path.Combine(core, "package.json"));
        if (!Directory.Exists(core) || !File.Exists(Path.Combine(core, "worker.js"))) throw new IOException("选中核心尚未安装或缺少 worker.js");
        if (!File.Exists(Path.Combine(core, "package.json"))) throw new IOException("选中核心缺少 package.json，无法确定必需依赖");
    }
    private static string Describe(CoreDependencyIssue issue) => $"{issue.Name}@{issue.Range}：{issue.Diagnostic}";
    private static void RequireHealthy(CoreDependencyCheckResult check)
    {
        if (!check.IsHealthy) throw new IOException("必需依赖校验失败：" + string.Join("；", check.Issues.Select(Describe)));
    }
    internal static bool IsInside(string root, string path) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); ; current = Path.GetDirectoryName(current)!)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("核心依赖路径禁止链接或重解析点：" + current);
            if (Path.GetDirectoryName(current) is null) break;
        }
    }
    private static void CopyTree(string source, string destination, CancellationToken ct)
    {
        long bytes = 0; var files = 0;
        Copy(source, destination);
        void Copy(string from, string to)
        {
            ct.ThrowIfCancellationRequested(); EnsureNoLinks(from); EnsureNoLinks(to);
            Directory.CreateDirectory(to);
            foreach (var item in Directory.EnumerateFileSystemEntries(from))
            {
                ct.ThrowIfCancellationRequested(); EnsureNoLinks(item);
                var target = Path.Combine(to, Path.GetFileName(item));
                if (Directory.Exists(item)) Copy(item, target);
                else
                {
                    bytes = checked(bytes + new FileInfo(item).Length);
                    if (++files > 50_000 || bytes > 512L * 1024 * 1024) throw new IOException("核心本地依赖复制超过配额");
                    File.Copy(item, target, overwrite: false);
                }
            }
        }
    }
    private static void DeleteTree(string path)
    {
        EnsureNoLinks(path);
        if (!Directory.Exists(path)) return;
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            EnsureNoLinks(child);
            if (Directory.Exists(child)) DeleteTree(child); else File.Delete(child);
        }
        Directory.Delete(path);
    }
    private static void AtomicWrite(string path, byte[] bytes)
    {
        EnsureNoLinks(path);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Dispose() { if (_ownsHttp) _http.Dispose(); _gate.Dispose(); }
}
