using System.Security.Cryptography;
using System.Text;
using DanmuApi.Platform;

namespace DanmuApi.Tests;

public sealed class OptionalRedisStagerTests
{
    [Fact]
    public void UnconfiguredLocalRedisUrlStagesNothing()
    {
        using var f = new Fixture();
        var result = OptionalRedisStager.Stage(f.Bundle, f.Paths);

        Assert.False(result.Configured);
        Assert.False(result.Staged);
        Assert.Null(result.Diagnostic);
        Assert.False(File.Exists(f.Target("redis/package.json")));
        Assert.False(Directory.Exists(Path.Combine(f.Paths.RuntimeDirectory, "nodejs-project", "node_modules")));
    }

    [Fact]
    public void ConfiguredLocalRedisUrlStagesWholePayloadAndIsIdempotent()
    {
        using var f = new Fixture();
        f.Configure("redis://127.0.0.1:6379");

        var first = OptionalRedisStager.Stage(f.Bundle, f.Paths);
        Assert.True(first.Configured);
        Assert.True(first.Staged);
        Assert.Null(first.Diagnostic);
        Assert.Equal(4, first.Files);
        foreach (var relative in f.PayloadPaths)
        {
            Assert.True(File.Exists(f.Target(relative)), relative);
        }

        // 重复启动必须放行：已存在的文件只校验、不覆盖、不计数。
        var second = OptionalRedisStager.Stage(f.Bundle, f.Paths);
        Assert.True(second.Staged);
        Assert.Equal(0, second.Files);
        Assert.Null(second.Diagnostic);
    }

    [Fact]
    public void EmptyOrBlankValueCountsAsUnconfigured()
    {
        using var f = new Fixture();
        f.Configure("   ");
        var result = OptionalRedisStager.Stage(f.Bundle, f.Paths);

        Assert.False(result.Configured);
        Assert.False(File.Exists(f.Target("redis/package.json")));
    }

    [Theory]
    [InlineData("redis/package.json")]
    [InlineData("cluster-key-slot/index.js")]
    [InlineData("@redis/client/index.js")]
    public void PayloadManifestEntriesOutsideTheAllowedPrefixesAreRejected(string relative)
    {
        using var f = new Fixture();
        f.Configure("redis://127.0.0.1:6379");
        File.AppendAllText(Path.Combine(f.Bundle, OptionalRedisStager.PayloadManifest),
            new string('A', 64) + "  " + relative + "-escape/注入.js " + "\n");

        var result = OptionalRedisStager.Stage(f.Bundle, f.Paths);

        Assert.True(result.Configured);
        Assert.False(result.Staged);
        Assert.NotNull(result.Diagnostic);
        Assert.Contains("载荷路径", result.Diagnostic);
        Assert.False(File.Exists(f.Target("redis/package.json")));
    }

    [Fact]
    public void MissingPayloadManifestFailsExplicitlyInsteadOfSkippingRedis()
    {
        using var f = new Fixture();
        f.Configure("redis://127.0.0.1:6379");
        File.Delete(Path.Combine(f.Bundle, OptionalRedisStager.PayloadManifest));

        var result = OptionalRedisStager.Stage(f.Bundle, f.Paths);

        Assert.True(result.Configured);
        Assert.False(result.Staged);
        Assert.NotNull(result.Diagnostic);
        Assert.Contains(OptionalRedisStager.PayloadManifest, result.Diagnostic);
    }

    [Fact]
    public void MissingPayloadFileFailsExplicitly()
    {
        using var f = new Fixture();
        f.Configure("redis://127.0.0.1:6379");
        File.Delete(f.Source("cluster-key-slot/index.js"));

        var result = OptionalRedisStager.Stage(f.Bundle, f.Paths);

        Assert.False(result.Staged);
        Assert.NotNull(result.Diagnostic);
        Assert.Contains("缺少文件", result.Diagnostic);
    }

    [Fact]
    public void ExistingConflictingTargetIsNeverOverwrittenSilently()
    {
        using var f = new Fixture();
        f.Configure("redis://127.0.0.1:6379");
        var target = f.Target("redis/index.js");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "被改坏的内容");

        var result = OptionalRedisStager.Stage(f.Bundle, f.Paths);

        Assert.False(result.Staged);
        Assert.NotNull(result.Diagnostic);
        Assert.Contains("不一致", result.Diagnostic);
        Assert.Equal("被改坏的内容", File.ReadAllText(target));
        // 冲突前的文件照常铺好，冲突本身不静默通过。
        Assert.True(File.Exists(f.Target("redis/package.json")));
    }

    [Fact]
    public void UnreadableEnvFilePropagatesInsteadOfAssumingRedisIsUnused()
    {
        using var f = new Fixture();
        var env = Path.Combine(f.Paths.NodeProjectDirectory, "config", ".env");
        Directory.CreateDirectory(Path.GetDirectoryName(env)!);
        // .env 不是合法 UTF-8：读取失败必须向上抛，绝不能当作"未配置 LOCAL_REDIS_URL"跳过 Redis。
        File.WriteAllBytes(env, [0x4C, 0x4F, 0x43, 0x41, 0x4C, 0x5F, 0x52, 0x45, 0x44, 0x49, 0x53, 0x5F, 0x55, 0x52, 0x4C, 0x3D, 0xFF, 0xFE, 0x0A]);

        Assert.ThrowsAny<Exception>(() => OptionalRedisStager.Stage(f.Bundle, f.Paths));
        Assert.False(File.Exists(f.Target("redis/package.json")));
    }

    [Fact]
    public void DuplicateKeysFollowTheSharedLenientEnvParserInsteadOfInventingNewRules()
    {
        using var f = new Fixture();
        var env = Path.Combine(f.Paths.NodeProjectDirectory, "config", ".env");
        Directory.CreateDirectory(Path.GetDirectoryName(env)!);
        // 与核心一致：重复键按后出现的值生效，stager 不额外发明更严的解析规则。
        File.WriteAllText(env, $"{OptionalRedisStager.EnvironmentKey}=\n{OptionalRedisStager.EnvironmentKey}=redis://127.0.0.1:6379\n", new UTF8Encoding(false));

        var result = OptionalRedisStager.Stage(f.Bundle, f.Paths);

        Assert.True(result.Configured);
        Assert.True(result.Staged);
    }

    [SkippableFact]
    public void RealBundlePayloadStagesAndPackagedNodeResolvesIt()
    {
        var bundle = Environment.GetEnvironmentVariable("DANMU_TEST_RUNTIME_BUNDLE");
        Skip.If(string.IsNullOrWhiteSpace(bundle), "Opt in with DANMU_TEST_RUNTIME_BUNDLE pointing at a built runtime-bundle.");
        var bundlePath = Path.GetFullPath(bundle!);
        Skip.If(!File.Exists(Path.Combine(bundlePath, OptionalRedisStager.PayloadManifest)),
            "The runtime bundle has no redis payload manifest; build it with -OptionalRedisNodeModules.");

        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(Path.Combine(directory.Path, "runtime"));
        var env = Path.Combine(paths.NodeProjectDirectory, "config", ".env");
        Directory.CreateDirectory(Path.GetDirectoryName(env)!);
        File.WriteAllText(env, $"{OptionalRedisStager.EnvironmentKey}=redis://127.0.0.1:6379\n", new UTF8Encoding(false));

        var result = OptionalRedisStager.Stage(bundlePath, paths);

        Assert.True(result.Configured);
        Assert.True(result.Staged, result.Diagnostic);
        Assert.Null(result.Diagnostic);
        Assert.True(result.Files > 0);
        // 真实性：清单里每个文件都必须落到运行环境且哈希一致。
        var manifest = Path.Combine(bundlePath, OptionalRedisStager.PayloadManifest);
        var expected = File.ReadLines(manifest).Select(line => line[66..]).ToArray();
        var staged = Path.Combine(paths.NodeProjectDirectory, "node_modules");
        foreach (var relative in expected)
        {
            var file = Path.Combine(staged, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(file), relative);
        }

        // 只读清单里的哈希也要与落盘内容一致（Stage 自身已校验，这里独立复核一遍）。
        foreach (var line in File.ReadLines(manifest))
        {
            var file = Path.Combine(staged, line[66..].Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal(line[..64], Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        }

        // 决定性证据：用随包 node.exe 在铺开后的运行环境里按核心的方式 import redis。
        var node = Path.Combine(bundlePath, "node.exe");
        Skip.If(!File.Exists(node), "The runtime bundle has no node.exe.");
        var probe = Path.Combine(paths.NodeProjectDirectory, "__redis_probe.mjs");
        File.WriteAllText(probe, """
            console.log(await import.meta.resolve('redis'));
            console.log(await import.meta.resolve('@redis/client'));
            console.log(await import.meta.resolve('cluster-key-slot'));
            const mod = await import('redis');
            console.log('createClient=' + (typeof mod.createClient === 'function'));
            """, new UTF8Encoding(false));
        var start = new System.Diagnostics.ProcessStartInfo(node)
        {
            WorkingDirectory = paths.NodeProjectDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--experimental-import-meta-resolve");
        start.ArgumentList.Add(probe);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(120_000), "redis 解析探针超时");
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("createClient=true", stdout);
        // 解析结果必须落在运行环境里，证明铺开的正是被解析的那份。
        Assert.Contains(staged.Replace('\\', '/'), stdout.Replace('\\', '/'));
        Assert.DoesNotContain(bundlePath.Replace('\\', '/') + "/nodejs-project", stdout.Replace('\\', '/'));
        Assert.Empty(stderr);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        public Fixture()
        {
            Bundle = Path.Combine(_directory.Path, "bundle");
            Paths = new AppPaths(Path.Combine(_directory.Path, "runtime"));
            foreach (var relative in PayloadPaths)
            {
                var path = Source(relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "payload:" + relative, new UTF8Encoding(false));
            }

            var lines = PayloadPaths.Select(relative =>
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Source(relative)))) + "  " + relative);
            File.WriteAllLines(Path.Combine(Bundle, OptionalRedisStager.PayloadManifest), lines, Encoding.ASCII);
            Directory.CreateDirectory(Paths.NodeProjectDirectory);
        }

        public string Bundle { get; }
        public AppPaths Paths { get; }
        public string[] PayloadPaths { get; } =
        [
            "redis/package.json",
            "redis/index.js",
            "cluster-key-slot/index.js",
            "@redis/client/index.js",
        ];

        public string Source(string relative) => Path.Combine(Bundle, "nodejs-project", "node_modules", relative.Replace('/', Path.DirectorySeparatorChar));
        public string Target(string relative) => Path.Combine(Paths.NodeProjectDirectory, "node_modules", relative.Replace('/', Path.DirectorySeparatorChar));

        public void Configure(string value)
        {
            var env = Path.Combine(Paths.NodeProjectDirectory, "config", ".env");
            Directory.CreateDirectory(Path.GetDirectoryName(env)!);
            File.WriteAllText(env, $"{OptionalRedisStager.EnvironmentKey}={value}\n", new UTF8Encoding(false));
        }

        public void Dispose() => _directory.Dispose();
    }
}
