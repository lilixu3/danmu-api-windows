namespace DanmuApi.Platform;

/// <summary>可选 Redis 载荷的铺开结果。Configured=false 表示未配置 LOCAL_REDIS_URL，无需处理。</summary>
public sealed record OptionalRedisStageResult(bool Configured, bool Staged, int Files, string? Diagnostic);

/// <summary>
/// Redis 是核心声明了、随包主依赖清单却不提供的一批包：上游只在配置了 LOCAL_REDIS_URL 时才 import 它。
/// 这里照移动端（NodeProjectManager.ensureOptionalRedisDependency）的做法处理——载荷始终随包，
/// 但只有 <c>config/.env</c> 明确配置了 LOCAL_REDIS_URL 时才铺进运行环境的 node_modules，
/// 避免给不用 Redis 的用户多铺 590 个文件。
/// 载荷不在随包受管清单里（不参与 BundledRuntimePreparer 的事务替换），因此这里自己做完整性校验：
/// 逐个文件回读 SHA256，已存在即校验而不是覆盖，避免静默接受一份损坏或半途中断的副本。
/// </summary>
public static class OptionalRedisStager
{
    public const string EnvironmentKey = "LOCAL_REDIS_URL";
    /// <summary>载荷清单相对 bundle 根的位置；列出的路径以 nodejs-project/node_modules 为根。</summary>
    public const string PayloadManifest = "redis-payload.SHA256SUMS.txt";
    private const string NodeModules = "nodejs-project/node_modules";
    private const int MaxFiles = 4096;
    private const long MaxBytes = 32L * 1024 * 1024;

    public static OptionalRedisStageResult Stage(string bundleDirectory, AppPaths paths,
        CancellationToken cancellationToken = default)
    {
        var configured = ReadConfigured(paths);
        if (configured is null)
        {
            return new(false, false, 0, null);
        }

        var bundle = Path.GetFullPath(bundleDirectory);
        var root = Path.GetFullPath(paths.RuntimeDirectory);
        if (Within(bundle, root) || Within(root, bundle))
        {
            return new(true, false, 0, "依赖源与目标目录不得重叠，拒绝铺开 Redis 依赖。");
        }

        var manifestPath = Path.Combine(bundle, PayloadManifest);
        if (!File.Exists(manifestPath))
        {
            return new(true, false, 0, $"随包运行环境缺少 Redis 载荷清单 {PayloadManifest}，无法为已配置的 {EnvironmentKey} 提供 Redis 支持；请用完整安装包重新准备运行环境。");
        }

        // 载荷铺在 node_modules 根下：redis 需要同级解析 @redis/* 与 cluster-key-slot。
        var sourceRoot = Path.Combine(bundle, NodeModules.Replace('/', Path.DirectorySeparatorChar));
        var targetRoot = Path.Combine(root, NodeModules.Replace('/', Path.DirectorySeparatorChar));
        var entries = ParseManifest(manifestPath, out var formatError);
        if (entries is null)
        {
            return new(true, false, 0, $"随包 Redis 载荷清单无效：{formatError}");
        }

        var copied = 0;
        var verified = 0;
        var inspected = 0;
        long bytes = 0;
        foreach (var (relative, hash) in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.Combine(sourceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            var target = Path.Combine(targetRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            EnsureNoLinks(source);
            EnsureNoLinks(target);
            if (!File.Exists(source))
            {
                return new(true, false, copied, $"随包 Redis 载荷缺少文件：{NodeModules}/{relative}");
            }

            if (++inspected > MaxFiles || (bytes = checked(bytes + new FileInfo(source).Length)) > MaxBytes)
            {
                return new(true, false, copied, "Redis 载荷超过随包配额，拒绝铺开运行环境。");
            }

            if (File.Exists(target))
            {
                if (!Hash(target).Equals(hash, StringComparison.OrdinalIgnoreCase))
                {
                    return new(true, false, copied, $"运行环境里已存在的 Redis 依赖文件与随包载荷不一致：{NodeModules}/{relative}；请先清理该文件后重新启动以重新铺开。");
                }

                verified++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: false);
            if (!Hash(target).Equals(hash, StringComparison.OrdinalIgnoreCase))
            {
                return new(true, false, copied, $"铺开 Redis 依赖后校验失败：{NodeModules}/{relative}");
            }

            copied++;
        }

        return new(true, true, copied, null);
    }

    private static string? ReadConfigured(AppPaths paths)
    {
        var env = Path.Combine(paths.NodeProjectDirectory, "config", ".env");
        if (!File.Exists(env))
        {
            return null;
        }

        try
        {
            var value = DanmuApi.Runtime.DotEnvFile.ReadValue(env, EnvironmentKey);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
        {
            // .env 读不了就不能判定"未配置"，必须显式失败而不是当作不需要 Redis。
            throw new IOException($"读取 {EnvironmentKey} 判断是否需要 Redis 依赖失败：{error.Message}", error);
        }
    }

    private static Dictionary<string, string>? ParseManifest(string path, out string error)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length < 67 || line.Substring(64, 2) != "  " ||
                !System.Text.RegularExpressions.Regex.IsMatch(line[..64], "\\A[0-9a-fA-F]{64}\\z"))
            {
                error = "清单行格式无效";
                return null;
            }

            var relative = line[66..];
            if (relative.Contains('\\') || relative.Contains(':') || relative.StartsWith('/') ||
                relative.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')) ||
                !(relative.StartsWith("redis/", StringComparison.OrdinalIgnoreCase) ||
                  relative.StartsWith("@redis/", StringComparison.OrdinalIgnoreCase) ||
                  relative.StartsWith("cluster-key-slot/", StringComparison.OrdinalIgnoreCase)))
            {
                error = $"载荷路径越界：{relative}";
                return null;
            }

            if (!result.TryAdd(relative, line[..64].ToUpperInvariant()))
            {
                error = $"载荷路径重复：{relative}";
                return null;
            }
        }

        if (result.Count == 0)
        {
            error = "清单为空";
            return null;
        }

        if (!result.ContainsKey("redis/package.json"))
        {
            error = "清单缺少 redis/package.json";
            return null;
        }

        error = string.Empty;
        return result;
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private static bool Within(string candidate, string root) =>
        candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); ; current = Path.GetDirectoryName(current)!)
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"Redis 依赖路径禁止链接或重解析点：{current}");
            }

            if (Path.GetDirectoryName(current) is null)
            {
                break;
            }
        }
    }
}
