using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DanmuApi.Core;

namespace DanmuApi.Tests;

public sealed class CoreDependencyServiceTests
{
    [SkippableFact]
    public async Task MissingMetadataIsFailureAndEmptyExplicitRequirementsAreHealthy()
    {
        using var f = new Fixture(); using var service = f.Service();
        File.Delete(Path.Combine(f.Core, "package.json"));
        await Assert.ThrowsAsync<IOException>(() => service.CheckAsync(ManagedCoreVariant.Stable));
        File.WriteAllText(Path.Combine(f.Core, "package.json"), "{}");
        var error = await Assert.ThrowsAsync<IOException>(() => service.CheckAsync(ManagedCoreVariant.Stable));
        Assert.Contains("requirements are unknown", error.Message);
        f.Require(new());
        Assert.True((await service.CheckAsync(ManagedCoreVariant.Stable)).IsHealthy);
        Assert.Empty(f.Http.Requests);
    }

    /// <summary>
    /// 宿主故意不随包的那批依赖必须被跳过，否则健康的核心会永远显示"缺失"；
    /// 但名单之外的依赖一个都不能漏报。
    /// </summary>
    [SkippableFact]
    public async Task NotBundledNamesAreSkippedWhileEverythingElseIsStillReported()
    {
        using var f = new Fixture();
        // 核心声明了三个"宿主自己承担"的包，加一个真的缺了的包。
        f.Require(new() { ["chokidar"] = "^4.0.3", ["dotenv"] = "^16.4.7", ["esbuild"] = "^0.25.10", ["missing-package"] = "^1.0.0" });

        // 默认策略就是宿主策略：这三个由 Windows 自己承担，不该算进"必需依赖"。
        using var service = f.Service();
        var result = await service.CheckAsync(ManagedCoreVariant.Stable);

        Assert.DoesNotContain(result.Issues, issue => issue.Name == "chokidar");
        Assert.DoesNotContain(result.Issues, issue => issue.Name == "dotenv");
        Assert.DoesNotContain(result.Issues, issue => issue.Name == "esbuild");
        // 关键：名单没有掩盖真正缺失的包。
        Assert.False(result.IsHealthy);
        Assert.Contains(result.Issues, issue => issue.Name == "missing-package");
    }

    /// <summary>配置了 LOCAL_REDIS_URL 时 redis 不再被跳过，摊开失败必须照报缺失。</summary>
    [SkippableFact]
    public async Task RedisIsOnlyExcludedWhileItIsNotExpected()
    {
        using var f = new Fixture();
        f.Require(new() { ["redis"] = "^5.11.0" });

        Assert.DoesNotContain("redis", CoreDependencyService.NotBundledDependencyNames(redisExpected: true));
        Assert.Contains("redis", CoreDependencyService.NotBundledDependencyNames(redisExpected: false));

        using var notExpected = f.Service(CoreDependencyService.NotBundledDependencyNames(redisExpected: false));
        Assert.True((await notExpected.CheckAsync(ManagedCoreVariant.Stable)).IsHealthy);

        using var expected = f.Service(CoreDependencyService.NotBundledDependencyNames(redisExpected: true));
        var result = await expected.CheckAsync(ManagedCoreVariant.Stable);
        Assert.False(result.IsHealthy);
        Assert.Contains(result.Issues, issue => issue.Name == "redis");
    }

    /// <summary>
    /// 带 pre-release 的版本/范围必须能解析：随包依赖里确实存在
    /// `^1.0.0-rc.4-de6c356`（drizzle-orm），原来会把它当成"不支持的范围"误报。
    /// </summary>
    [SkippableFact]
    public async Task PrereleaseRangesAndVersionsResolveInsteadOfBeingReportedUnsupported()
    {
        using var f = new Fixture();
        f.Require(new() { ["prerelease-root"] = "^1.0.0-rc.4-de6c356", ["prerelease-child"] = "^2.0.0-beta.7" });
        f.PublicPackage("prerelease-root", "1.0.0-rc.4-de6c356", new() { ["prerelease-child"] = "^2.0.0-beta.7" });
        f.PublicPackage("prerelease-child", "2.1.0");

        using var service = f.Service();
        var result = await service.CheckAsync(ManagedCoreVariant.Stable);

        Assert.True(result.IsHealthy, string.Join("；", result.Issues.Select(issue => issue.Name + ":" + issue.Diagnostic)));
        Assert.Empty(f.Http.Requests);
    }

    [SkippableFact]
    public async Task RepairsMissingClosurePreservesUnrelatedLocalAndPublicAndHoldsBothGuards()
    {
        using var f = new Fixture(); f.Require(new() { ["root"] = "^1.0.0" });
        f.LocalPackage("user-package", "9.0.0");
        f.PublicPackage("unchanged", "8.0.0");
        var publicBefore = f.Snapshot(Path.Combine(f.Project, "node_modules"));
        f.Pack([new("root", "1.2.0", new() { ["child"] = "~2.1.0" }), new("child", "2.1.3"), new("unused", "1.0.0")]);
        using var service = f.Service();
        Assert.False((await service.CheckAsync(ManagedCoreVariant.Stable)).IsHealthy);
        Assert.True((await service.RepairAsync(ManagedCoreVariant.Stable)).IsHealthy);
        Assert.Equal(publicBefore, f.Snapshot(Path.Combine(f.Project, "node_modules")));
        Assert.True(File.Exists(Path.Combine(f.Core, "node_modules", "user-package", "index.js")));
        Assert.False(Directory.Exists(Path.Combine(f.Core, "node_modules", "unused")));
        Assert.Equal(3, f.Http.Requests.Count);
        Assert.All(f.Http.GuardStates, state => Assert.True(state));
        Assert.False(f.RuntimeHeld); Assert.False(f.CoreHeld);
    }

    [SkippableFact]
    public async Task PublicParentWithMissingChildIsRelocatedPrivately()
    {
        using var f = new Fixture(); f.Require(new() { ["root"] = "1.0.0" });
        f.PublicPackage("root", "1.0.0", new() { ["child"] = "1.0.0" });
        var original = f.Snapshot(Path.Combine(f.Project, "node_modules"));
        f.Pack([new("child", "1.0.0")]);
        using var service = f.Service();
        Assert.True((await service.RepairAsync(ManagedCoreVariant.Stable)).IsHealthy);
        Assert.Equal(original, f.Snapshot(Path.Combine(f.Project, "node_modules")));
        Assert.True(File.Exists(Path.Combine(f.Core, "node_modules", "root", "node_modules", "child", "index.js")));
    }

    [SkippableTheory]
    [InlineData("not-covered", "1.0.0", "unused", "1.0.0", false, "未覆盖")]
    [InlineData("root", "2.0.0", "root", "1.0.0", false, "必需版本或入口")]
    [InlineData("root", "1.0.0", "root", "1.0.0", true, "必需版本或入口")]
    public async Task UncoveredVersionOrTrimmedEntryNeverReplacesOldModules(string needed, string range, string supplied, string version, bool missingEntry, string diagnostic)
    {
        using var f = new Fixture(); f.Require(new() { [needed] = range }); f.LocalPackage("user-package", "9.0.0");
        var before = f.Snapshot(Path.Combine(f.Core, "node_modules"));
        f.Pack([new(supplied, version, MissingEntry: missingEntry)]);
        using var service = f.Service();
        var error = await Assert.ThrowsAsync<IOException>(() => service.RepairAsync(ManagedCoreVariant.Stable));
        Assert.Contains(diagnostic, error.Message);
        Assert.Equal(before, f.Snapshot(Path.Combine(f.Core, "node_modules")));
        Assert.False((await service.CheckAsync(ManagedCoreVariant.Stable)).IsHealthy);
    }

    [SkippableTheory]
    [InlineData("OldMoved")]
    [InlineData("NewMoved")]
    [InlineData("Validated")]
    public async Task TransactionFailureRestoresExactOldDirectory(string stage)
    {
        using var f = new Fixture(); f.Require(new() { ["root"] = "1.0.0" }); f.LocalPackage("keep", "8.0.0");
        var before = f.Snapshot(Path.Combine(f.Core, "node_modules"));
        f.Pack([new("root", "1.0.0")]);
        using var service = f.Service(); service.TransactionHook = value => { if (value == stage) throw new IOException("injected " + stage); };
        var error = await Assert.ThrowsAsync<IOException>(() => service.RepairAsync(ManagedCoreVariant.Stable));
        Assert.Contains("injected", error.Message);
        Assert.Equal(before, f.Snapshot(Path.Combine(f.Core, "node_modules")));
        Assert.False(File.Exists(Path.Combine(f.Core, ".dependency-repair.json")));
    }

    [SkippableFact]
    public async Task StoppedGuardRefusalMakesNoNetworkOrFilesystemChanges()
    {
        using var f = new Fixture(); f.Require(new() { ["root"] = "1.0.0" }); f.RefuseRuntime = true;
        var before = f.Snapshot(f.Project); using var service = f.Service();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RepairAsync(ManagedCoreVariant.Stable));
        Assert.Equal(before, f.Snapshot(f.Project)); Assert.Empty(f.Http.Requests);
    }

    [SkippableFact]
    public async Task CustomCoreCannotUseOnlineRepair()
    {
        using var f = new Fixture();
        Directory.Move(f.Core, Path.Combine(f.Project, "danmu_api_custom"));
        using var service = f.Service();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RepairAsync(ManagedCoreVariant.Custom));
        Assert.Empty(f.Http.Requests);
    }

    [SkippableTheory]
    [InlineData("signature")]
    [InlineData("hash")]
    [InlineData("size")]
    [InlineData("serial")]
    [InlineData("schema")]
    [InlineData("nodeMajor")]
    [InlineData("runtimeProtocol")]
    [InlineData("artifactUrl")]
    public async Task InvalidSignedPackCannotReplaceDependencies(string fault)
    {
        using var f = new Fixture(); f.Require(new() { ["root"] = "1.0.0" }); f.LocalPackage("keep", "8.0.0");
        var before = f.Snapshot(Path.Combine(f.Core, "node_modules"));
        f.Pack([new("root", "1.0.0")], fault: fault);
        using var service = f.Service();
        await Assert.ThrowsAnyAsync<Exception>(() => service.RepairAsync(ManagedCoreVariant.Stable));
        Assert.Equal(before, f.Snapshot(Path.Combine(f.Core, "node_modules")));
    }

    [Theory]
    [InlineData("node_modules/root/../escaped.js")]
    [InlineData("node_modules/root/file.js:stream")]
    [InlineData("node_modules/root/CON.txt")]
    [InlineData("node_modules/root/LPT1")]
    [InlineData("node_modules/root/file. ")]
    [InlineData("node_modules/root/file.")]
    [InlineData("node_modules\\root\\file.js")]
    [InlineData("C:/node_modules/root/file.js")]
    public void WindowsInvalidZipPathsFail(string path) => Assert.Throws<InvalidDataException>(() => SignedCoreDependencyPack.ValidateArchivePath(path, false));

    [SkippableTheory]
    [InlineData("node_modules/root/Index.js", 0)]
    [InlineData("node_modules/root/link.js", 0xA000)]
    [InlineData("node_modules/root/native.node", 0)]
    public async Task ArchiveCaseCollisionLinksAndNativeArtifactsAreRejected(string extra, int unixMode)
    {
        using var f = new Fixture(); f.Require(new() { ["root"] = "1.0.0" });
        f.Pack([new("root", "1.0.0")], extra: extra, mode: unixMode);
        using var service = f.Service();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RepairAsync(ManagedCoreVariant.Stable));
        Assert.False(Directory.Exists(Path.Combine(f.Core, "node_modules")));
    }

    [SkippableFact]
    public async Task ActualResearchPackSignatureSchemaAndWindowsExtractionValidateWithoutNetwork()
    {
        using var f = new Fixture();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "artifacts", "runtime-pack-research-20260908"))) root = root.Parent;
        Skip.If(root is null, "Research fixture is not present");
        var assets = Path.Combine(root!.FullName, "artifacts", "runtime-pack-research-20260908");
        f.Http.Manifest = File.ReadAllBytes(Path.Combine(assets, "manifest.json"));
        f.Http.Signature = File.ReadAllBytes(Path.Combine(assets, "manifest.sig"));
        f.Http.Archive = File.ReadAllBytes(Path.Combine(assets, "node_modules.zip"));
        f.Http.RequireGuards = false;
        using var client = new HttpClient(f.Http);
        var pack = new SignedCoreDependencyPack(client, SignedCoreDependencyPack.TrustedPublicKey);
        var signed = await pack.FetchManifestAsync(26, default);
        Assert.Equal(26, signed.Manifest.Serial); Assert.Equal(990, signed.Manifest.ArtifactFileCount);
        await pack.ExtractAsync(signed.Manifest, f.Core, null, default);
        f.Require(new() { ["brotli"] = "1.3.3", ["opencc-js"] = "1.4.1" });
        using var service = f.Service();
        var check = await service.CheckAsync(ManagedCoreVariant.Stable);
        Assert.False(check.IsHealthy);
        Assert.Equal(new[] { "brotli", "opencc-js" }, check.Issues.Select(i => i.Name).Order().ToArray());
    }

    [SkippableFact]
    public async Task RequiredPeersAreCheckedButOptionalAndOverriddenDependenciesAreExcluded()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Core, "package.json"), """
            {"type":"module","dependencies":{"optional":"1.0.0"},"optionalDependencies":{"optional":"2.0.0"},
             "peerDependencies":{"required-peer":"1.0.0","optional-peer":"1.0.0"},
             "peerDependenciesMeta":{"optional-peer":{"optional":true}}}
            """);
        f.Pack([new("required-peer", "1.0.0")]);
        using var service = f.Service();
        var issue = Assert.Single((await service.CheckAsync(ManagedCoreVariant.Stable)).Issues);
        Assert.Equal("required-peer", issue.Name);
        Assert.True((await service.RepairAsync(ManagedCoreVariant.Stable)).IsHealthy);
        Assert.False(Directory.Exists(Path.Combine(f.Core, "node_modules", "optional")));
        Assert.False(Directory.Exists(Path.Combine(f.Core, "node_modules", "optional-peer")));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeCommitRestoresOldButCancellationAfterCommitReturnsSuccess(bool afterCommit)
    {
        using var f = new Fixture(); f.Require(new() { ["root"] = "1.0.0" }); f.LocalPackage("keep", "1.0.0");
        var before = f.Snapshot(Path.Combine(f.Core, "node_modules"));
        f.Pack([new("root", "1.0.0")]);
        using var cancellation = new CancellationTokenSource(); using var service = f.Service();
        if (!afterCommit) service.TransactionHook = stage => { if (stage == "Validated") cancellation.Cancel(); };
        var progress = new ImmediateProgress(value => { if (afterCommit && value.Stage == "Completed") cancellation.Cancel(); });
        if (afterCommit)
            Assert.True((await service.RepairAsync(ManagedCoreVariant.Stable, progress, cancellation.Token)).IsHealthy);
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RepairAsync(ManagedCoreVariant.Stable, progress, cancellation.Token));
            Assert.Equal(before, f.Snapshot(Path.Combine(f.Core, "node_modules")));
        }
    }

    [SkippableFact]
    public async Task PersistedHigherSerialRejectsReplayEvenWhenSignatureIsValid()
    {
        using var f = new Fixture(); f.Require(new() { ["root"] = "1.0.0" }); f.Pack([new("root", "1.0.0")]);
        File.WriteAllText(Path.Combine(f.Project, ".core-dependency-pack-serial"), "27");
        using var service = f.Service();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.RepairAsync(ManagedCoreVariant.Stable));
        Assert.Contains("serial", error.Message);
        Assert.Equal(2, f.Http.Requests.Count);
    }

    [SkippableFact]
    public async Task InterruptedSwapIsExplicitOnCheckAndRestoresOldBeforeRepair()
    {
        using var f = new Fixture(); f.Require(new() { ["root"] = "1.0.0" }); f.LocalPackage("keep", "1.0.0");
        var before = f.Snapshot(Path.Combine(f.Core, "node_modules"));
        Directory.Move(Path.Combine(f.Core, "node_modules"), Path.Combine(f.Core, ".dependency-old-node_modules"));
        f.LocalPackage("partially-applied", "2.0.0");
        File.WriteAllText(Path.Combine(f.Core, ".dependency-repair.json"), "{\"Schema\":1,\"HadOld\":true}");
        f.Pack([new("uncovered", "1.0.0")]);
        using var service = f.Service();
        await Assert.ThrowsAsync<IOException>(() => service.CheckAsync(ManagedCoreVariant.Stable));
        await Assert.ThrowsAsync<IOException>(() => service.RepairAsync(ManagedCoreVariant.Stable));
        Assert.Equal(before, f.Snapshot(Path.Combine(f.Core, "node_modules")));
        Assert.False(File.Exists(Path.Combine(f.Core, ".dependency-repair.json")));
    }

    [SkippableFact]
    public async Task CoreLocalBrokenPackageShadowsHealthyPublicPackage()
    {
        using var f = new Fixture(); f.Require(new() { ["root"] = "1.0.0" });
        f.PublicPackage("root", "1.0.0"); f.LocalPackage("root", "1.0.0");
        File.Delete(Path.Combine(f.Core, "node_modules", "root", "index.js"));
        using var service = f.Service();
        Assert.False((await service.CheckAsync(ManagedCoreVariant.Stable)).IsHealthy);
    }

    [SkippableTheory]
    [InlineData("install")]
    [InlineData("os")]
    [InlineData("cpu")]
    [InlineData("magic")]
    [InlineData("quota")]
    public async Task PlatformInstallScriptsDisguisedNativeAndQuotasFailClosed(string fault)
    {
        using var f = new Fixture(); f.Require(new() { ["root"] = "1.0.0" }); f.Pack([new("root", "1.0.0")], fault: fault);
        using var service = f.Service();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RepairAsync(ManagedCoreVariant.Stable));
        Assert.False(Directory.Exists(Path.Combine(f.Core, "node_modules")));
    }

    [SkippableFact]
    public async Task CleanupFailureClearlyReportsCommittedRepairAndPreservesBackup()
    {
        using var f = new Fixture(); f.Require(new() { ["root"] = "1.0.0" }); f.LocalPackage("keep", "1.0.0");
        f.Pack([new("root", "1.0.0")]); using var service = f.Service();
        var oldFile = Path.Combine(f.Core, ".dependency-old-node_modules", "keep", "index.js");
        var progress = new ImmediateProgress(p => { if (p.Stage == "Completed") File.SetAttributes(oldFile, FileAttributes.ReadOnly); });
        try
        {
            var error = await Assert.ThrowsAsync<IOException>(() => service.RepairAsync(ManagedCoreVariant.Stable, progress));
            Assert.Contains("已修复并生效", error.Message);
            Assert.IsType<AggregateException>(error.InnerException);
            Assert.True((await service.CheckAsync(ManagedCoreVariant.Stable)).IsHealthy);
            Assert.True(File.Exists(oldFile));
        }
        finally { if (File.Exists(oldFile)) File.SetAttributes(oldFile, FileAttributes.Normal); }
    }

    [Fact]
    public async Task InstallerMaintenanceLeaseSerializesActualDeleteAndSupportsCancellation()
    {
        using var dir = new TemporaryDirectory(); using var http = new HttpClient();
        var core = Path.Combine(dir.Path, "danmu_api_stable"); Directory.CreateDirectory(core);
        var installer = new CoreInstaller(dir.Path, Path.Combine(dir.Path, "cache"), new GithubFileDownloader(http));
        var lease = await installer.AcquireMaintenanceLeaseAsync();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await installer.AcquireMaintenanceLeaseAsync(cancel.Token));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deletion = Task.Run(() => { entered.SetResult(); installer.Delete(ManagedCoreVariant.Stable); });
        await entered.Task;
        Assert.NotSame(deletion, await Task.WhenAny(deletion, Task.Delay(100)));
        Assert.True(Directory.Exists(core));
        await lease.DisposeAsync(); await deletion.WaitAsync(TimeSpan.FromSeconds(5));
        await lease.DisposeAsync();
        Assert.False(Directory.Exists(core));
    }

    private sealed class ImmediateProgress(Action<CoreDependencyRepairProgress> report) : IProgress<CoreDependencyRepairProgress>
    {
        public void Report(CoreDependencyRepairProgress value) => report(value);
    }

    private sealed record Package(string Name, string Version, Dictionary<string, string>? Dependencies = null, bool MissingEntry = false);
    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        private readonly RSA _key = RSA.Create(2048);
        public string Project { get; }
        public string Core => Path.Combine(Project, "danmu_api_stable");
        public string Node { get; }
        public FakeHttp Http { get; }
        public bool RuntimeHeld, CoreHeld, RefuseRuntime;
        public Fixture()
        {
            Node = Environment.GetEnvironmentVariable("DANMU_TEST_NODE_EXE") ?? "";
            Skip.If(!File.Exists(Node), "DANMU_TEST_NODE_EXE is required");
            Project = Path.Combine(_directory.Path, "中文 space", "project"); Directory.CreateDirectory(Core);
            File.WriteAllText(Path.Combine(Core, "worker.js"), "export {};"); Require(new());
            CoreManifestStore.Write(Core, new(1, ManagedCoreVariant.Stable, "huangxd-/danmu_api", "main", new string('a', 40), "1.0.0", "fixture", CoreInstallKind.Branch, null, DateTimeOffset.UtcNow));
            Http = new FakeHttp(this);
        }
        public void Require(Dictionary<string, string> dependencies) => File.WriteAllBytes(Path.Combine(Core, "package.json"), JsonSerializer.SerializeToUtf8Bytes(new { type = "module", dependencies }));
        public void LocalPackage(string name, string version, Dictionary<string, string>? deps = null) => WritePackage(Path.Combine(Core, "node_modules"), new(name, version, deps));
        public void PublicPackage(string name, string version, Dictionary<string, string>? deps = null) => WritePackage(Path.Combine(Project, "node_modules"), new(name, version, deps));
        private static void WritePackage(string root, Package p)
        {
            var dir = Path.Combine(root, p.Name); Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "package.json"), PackageJson(p));
            File.WriteAllText(Path.Combine(dir, "index.js"), "module.exports = 42;");
        }
        private static byte[] PackageJson(Package p) => JsonSerializer.SerializeToUtf8Bytes(new { name = p.Name, version = p.Version, main = "index.js", dependencies = p.Dependencies ?? new() });
        public CoreDependencyService Service(IReadOnlyCollection<string>? notBundled = null) => new(Project, Node,
            ct => { if (RefuseRuntime) throw new InvalidOperationException("服务未停止"); RuntimeHeld = true; return ValueTask.FromResult<IAsyncDisposable>(new Lease(() => RuntimeHeld = false)); },
            ct => { CoreHeld = true; return ValueTask.FromResult<IAsyncDisposable>(new Lease(() => CoreHeld = false)); },
            new HttpClient(Http), _key.ExportSubjectPublicKeyInfoPem(),
            notBundled is null ? null : () => notBundled);
        public void Pack(Package[] packages, string? fault = null, string? extra = null, int mode = 0)
        {
            using var output = new MemoryStream(); var count = 0; long size = 0;
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                void Entry(string name, byte[] bytes, int entryMode = 0)
                {
                    var entry = zip.CreateEntry(name); entry.ExternalAttributes = entryMode << 16;
                    using var stream = entry.Open(); stream.Write(bytes); count++; size += bytes.Length;
                }
                foreach (var package in packages)
                {
                    var json = JsonSerializer.Deserialize<Dictionary<string, object>>(PackageJson(package))!;
                    if (fault == "install") json["scripts"] = new { install = "node install.js" };
                    if (fault == "os") json["os"] = new[] { "linux" };
                    if (fault == "cpu") json["cpu"] = new[] { "arm64" };
                    Entry("node_modules/" + package.Name + "/package.json", JsonSerializer.SerializeToUtf8Bytes(json));
                    if (!package.MissingEntry) Entry("node_modules/" + package.Name + "/index.js", Encoding.UTF8.GetBytes(fault == "magic" ? "MZ disguised native" : "module.exports=42;"));
                }
                if (extra is not null) Entry(extra, Encoding.UTF8.GetBytes("bad"), mode);
            }
            Http.Archive = output.ToArray();
            var fields = new Dictionary<string, object?>
            {
                ["schema"] = 3, ["serial"] = 26, ["runtimeProtocol"] = 2, ["nodeMajor"] = 24,
                ["artifactUrl"] = "https://github.com/lilixu3/danmu-api-runtime-packs/releases/download/test/node_modules.zip",
                ["artifactSha256"] = SignedCoreDependencyPack.Hash(Http.Archive), ["artifactSize"] = Http.Archive.Length,
                ["artifactExtractedSize"] = size, ["artifactFileCount"] = count, ["runtimeLockSha256"] = new string('a',64),
                ["dependencyFingerprint"] = SignedCoreDependencyPack.Hash(Encoding.UTF8.GetBytes("{}")), ["dependencies"] = new Dictionary<string,string>(),
                ["packages"] = packages.Select(p => new { name=p.Name, version=p.Version, path="node_modules/"+p.Name }).ToArray()
            };
            switch (fault)
            {
                case "quota": fields["artifactExtractedSize"] = SignedCoreDependencyPack.MaxExtractedBytes + 1; break;
                case "hash": fields["artifactSha256"] = new string('b',64); break;
                case "size": fields["artifactSize"] = Http.Archive.Length+1; break;
                case "serial": fields["serial"] = 25; break;
                case "schema": fields["schema"] = 2; break;
                case "nodeMajor": fields["nodeMajor"] = 22; break;
                case "runtimeProtocol": fields["runtimeProtocol"] = 1; break;
                case "artifactUrl": fields["artifactUrl"] = "https://evil.invalid/node_modules.zip"; break;
            }
            Http.Manifest = JsonSerializer.SerializeToUtf8Bytes(fields);
            Http.Signature = Encoding.UTF8.GetBytes(Convert.ToBase64String(_key.SignData(Http.Manifest, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));
            if (fault == "signature") Http.Manifest[0] = (byte)' ';
        }
        public string Snapshot(string root) => !Directory.Exists(root) ? "<absent>" : string.Join("\n", Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(p => Path.GetRelativePath(root,p) + ":" + SignedCoreDependencyPack.Hash(File.ReadAllBytes(p))));
        public void Dispose() { _key.Dispose(); _directory.Dispose(); }
    }
    private sealed class Lease(Action release) : IAsyncDisposable { public ValueTask DisposeAsync() { release(); return ValueTask.CompletedTask; } }
    private sealed class FakeHttp(Fixture owner) : HttpMessageHandler
    {
        public byte[] Manifest = [], Signature = [], Archive = [];
        public bool RequireGuards = true;
        public List<string> Requests { get; } = [];
        public List<bool> GuardStates { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Null(request.Headers.Authorization);
            var guarded = owner.RuntimeHeld && owner.CoreHeld;
            GuardStates.Add(guarded); if (RequireGuards) Assert.True(guarded);
            var url = request.RequestUri!.AbsoluteUri; Requests.Add(url);
            var bytes = url == SignedCoreDependencyPack.ManifestUrl ? Manifest : url == SignedCoreDependencyPack.SignatureUrl ? Signature :
                url.StartsWith("https://github.com/lilixu3/danmu-api-runtime-packs/releases/download/", StringComparison.Ordinal) ? Archive : throw new Exception("Unexpected GET " + url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
