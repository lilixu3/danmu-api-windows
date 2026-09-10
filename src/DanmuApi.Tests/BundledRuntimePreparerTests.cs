using System.Security.Cryptography;
using System.Text.Json;
using DanmuApi.App.Services;
using DanmuApi.Platform;

namespace DanmuApi.Tests;

public sealed class BundledRuntimePreparerTests
{
    [Fact]
    public void PreparesMissingDependenciesWithoutOverwritingConfigurationOrCore()
    {
        using var directory = new TemporaryDirectory();
        var bundle = Path.Combine(directory.Path, "bundle");
        Directory.CreateDirectory(Path.Combine(bundle, "nodejs-project"));
        File.WriteAllText(Path.Combine(bundle, "node.exe"), "node-test");
        File.WriteAllText(Path.Combine(bundle, "nodejs-project", "main.js"), "entry-test");
        WriteManifest(bundle);
        var paths = new AppPaths(Path.Combine(directory.Path, "runtime-root"), Path.Combine(directory.Path, "settings"));
        BundledRuntimePreparer.Prepare(bundle, paths);
        File.WriteAllText(Path.Combine(paths.NodeProjectDirectory, "main.js"), "existing-entry");
        Directory.CreateDirectory(Path.Combine(paths.NodeProjectDirectory, "config"));
        File.WriteAllText(Path.Combine(paths.NodeProjectDirectory, "config", ".env"), "TOKEN=keep");
        Assert.Throws<IOException>(() => BundledRuntimePreparer.Prepare(bundle, paths));
        Assert.Equal("existing-entry", File.ReadAllText(Path.Combine(paths.NodeProjectDirectory, "main.js")));
        Assert.Equal("TOKEN=keep", File.ReadAllText(Path.Combine(paths.NodeProjectDirectory, "config", ".env")));
        Assert.Empty(Directory.EnumerateDirectories(paths.NodeProjectDirectory, "danmu_api*"));
    }

    [Fact]
    public void CorruptedBundleFailsBeforeWritingAnyDependency()
    {
        using var directory = new TemporaryDirectory();
        var bundle = Path.Combine(directory.Path, "bundle");
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "node.exe"), "original");
        WriteManifest(bundle);
        File.WriteAllText(Path.Combine(bundle, "node.exe"), "corrupted");
        var paths = new AppPaths(Path.Combine(directory.Path, "target"));
        Assert.Throws<IOException>(() => BundledRuntimePreparer.Prepare(bundle, paths));
        Assert.False(Directory.Exists(paths.RuntimeDirectory));
    }

    [Fact]
    public void UpdatesPreviouslyDeployedBundle()
    {
        using var directory = new TemporaryDirectory();
        var bundle = Path.Combine(directory.Path, "bundle");
        Directory.CreateDirectory(Path.Combine(bundle, "nodejs-project"));
        File.WriteAllText(Path.Combine(bundle, "node.exe"), "node-v1");
        File.WriteAllText(Path.Combine(bundle, "nodejs-project", "main.js"), "main-v1");
        WriteManifest(bundle);
        var paths = new AppPaths(Path.Combine(directory.Path, "target"));
        BundledRuntimePreparer.Prepare(bundle, paths);
        File.WriteAllText(Path.Combine(bundle, "node.exe"), "node-v2");
        WriteManifest(bundle);
        BundledRuntimePreparer.Prepare(bundle, paths);
        Assert.Equal("node-v2", File.ReadAllText(Path.Combine(paths.RuntimeDirectory, "node.exe")));
    }

    [Fact]
    public void RepeatSkipsSourceHashesButChecksEveryTargetMetadataAndCriticalHashes()
    {
        using var fixture = new BundleFixture();
        fixture.Prepare();
        // Source payload is not reread on a same-version repeat.
        File.WriteAllText(fixture.Source("nodejs-project/node_modules/pkg/index.js"), "bad source");
        var result = fixture.Prepare();
        Assert.False(result.Changed);
        Assert.Equal(0, result.SourceFilesHashed);
        Assert.Equal(2, result.TargetFilesHashed);
        var dependency = fixture.Target("nodejs-project/node_modules/pkg/index.js");
        File.SetLastWriteTimeUtc(dependency, File.GetLastWriteTimeUtc(dependency).AddMinutes(1));
        result = fixture.Prepare();
        Assert.Equal(3, result.TargetFilesHashed);
        Assert.Equal(0, result.SourceFilesHashed);
        Assert.Equal(2, fixture.Prepare().TargetFilesHashed);
    }

    [Theory]
    [InlineData("node.exe")]
    [InlineData("nodejs-project/main.js")]
    [InlineData("nodejs-project/node_modules/pkg/index.js")]
    public void EveryManagedFileRejectsDamageAndMissingFile(string relative)
    {
        using var fixture = new BundleFixture();
        fixture.Prepare();
        File.WriteAllText(fixture.Target(relative), "corrupt payload");
        Assert.Throws<IOException>(() => fixture.Prepare());
        Assert.Equal("corrupt payload", File.ReadAllText(fixture.Target(relative)));
        fixture.Prepare(force: true);
        File.Delete(fixture.Target(relative));
        Assert.Throws<IOException>(() => fixture.Prepare());
        Assert.False(File.Exists(fixture.Target(relative)));
        fixture.Prepare(force: true);
        Assert.Equal(File.ReadAllText(fixture.Source(relative)), File.ReadAllText(fixture.Target(relative)));
    }

    [Fact]
    public void CriticalSameMetadataDamageStillFails()
    {
        using var fixture = new BundleFixture();
        fixture.Prepare();
        var file = fixture.Target("node.exe");
        var modified = File.GetLastWriteTimeUtc(file);
        File.WriteAllText(file, "NODE-v1");
        File.SetLastWriteTimeUtc(file, modified);
        Assert.Throws<IOException>(() => fixture.Prepare());
    }

    [Fact]
    public void MetadataModePayloadSkipsLaunchHashingButKeepsEveryOtherCheck()
    {
        using var fixture = new BundleFixture("node.exe");
        fixture.Prepare();
        Assert.Equal(BundledRuntimeReadinessStatus.Ready, BundledRuntimePreparer.CheckReadiness(fixture.Bundle, fixture.Paths).Status);
        var receipt = JsonSerializer.Deserialize<BundledRuntimePreparer.StartupReceipt>(
            File.ReadAllText(fixture.Target(".bundled-runtime-receipt.json")))!;
        Assert.Equal(2, receipt.Schema);
        var recorded = receipt.Files.Single(entry => entry.Path == "node.exe");
        Assert.True(recorded.Length > 0 && recorded.LastWrite > 0 && recorded.Created > 0);
        // Same length and restored timestamps: the metadata fast check cannot see this by design.
        var node = fixture.Target("node.exe");
        var modified = File.GetLastWriteTimeUtc(node);
        File.WriteAllText(node, "NODE-v1");
        File.SetLastWriteTimeUtc(node, modified);
        Assert.Equal(BundledRuntimeReadinessStatus.Ready, BundledRuntimePreparer.CheckReadiness(fixture.Bundle, fixture.Paths).Status);
        // Deployment/repair still hashes every critical entry, and the explicit full check reports it.
        Assert.Throws<IOException>(() => fixture.Prepare());
        Assert.Equal(1, RuntimeDependencyInspector.Check(fixture.Bundle, fixture.Paths).Damaged);
        // Real metadata drift still forces re-verification and re-registers the refreshed metadata.
        File.Copy(fixture.Source("node.exe"), node, true);
        File.SetLastWriteTimeUtc(node, File.GetLastWriteTimeUtc(node).AddMinutes(1));
        Assert.Equal(BundledRuntimeReadinessStatus.NeedsPreparation, BundledRuntimePreparer.CheckReadiness(fixture.Bundle, fixture.Paths).Status);
        var result = BundledRuntimePreparer.PrepareForStartup(fixture.Bundle, fixture.Paths);
        Assert.False(result.Changed);
        Assert.Equal(2, result.TargetFilesHashed);
        Assert.Equal(BundledRuntimeReadinessStatus.Ready, BundledRuntimePreparer.CheckReadiness(fixture.Bundle, fixture.Paths).Status);
    }

    [Fact]
    public void LegacyReceiptIsUpgradedWithoutRedeployingPayloads()
    {
        using var fixture = new BundleFixture("node.exe");
        fixture.Prepare();
        var path = fixture.Target(".bundled-runtime-receipt.json");
        var receipt = JsonSerializer.Deserialize<BundledRuntimePreparer.StartupReceipt>(File.ReadAllText(path))!;
        File.WriteAllText(path, JsonSerializer.Serialize(receipt with { Schema = 1 }));
        Assert.Equal(BundledRuntimeReadinessStatus.NeedsPreparation, BundledRuntimePreparer.CheckReadiness(fixture.Bundle, fixture.Paths).Status);
        var phases = new List<string>();
        var result = BundledRuntimePreparer.PrepareForStartup(fixture.Bundle, fixture.Paths, progress: p => phases.Add(p.Phase));
        Assert.False(result.Changed);
        Assert.DoesNotContain("Staging", phases);
        Assert.DoesNotContain("Replacing", phases);
        Assert.Equal(BundledRuntimeReadinessStatus.Ready, BundledRuntimePreparer.CheckReadiness(fixture.Bundle, fixture.Paths).Status);
        Assert.Equal(2, JsonSerializer.Deserialize<BundledRuntimePreparer.StartupReceipt>(File.ReadAllText(path))!.Schema);
    }

    [Fact]
    public void BrandNewSubtreesArePromotedInOneMoveAndRollBackCompletely()
    {
        using var fixture = new BundleFixture();
        var promotedBeforeFirstReplacement = false;
        Assert.Throws<InvalidOperationException>(() => fixture.Prepare(progress: p =>
        {
            if (p.Phase != "Replacing" || p.Completed != 1) return;
            promotedBeforeFirstReplacement = File.Exists(fixture.Target("nodejs-project/main.js"))
                && File.Exists(fixture.Target("nodejs-project/node_modules/pkg/index.js"));
            throw new InvalidOperationException("injected");
        }));
        Assert.True(promotedBeforeFirstReplacement, "首次安装应整目录搬运，而不是逐文件重命名。");
        Assert.False(File.Exists(fixture.Target("node.exe")));
        Assert.False(File.Exists(fixture.Target("nodejs-project/main.js")));
        Assert.False(File.Exists(fixture.Target("nodejs-project/node_modules/pkg/index.js")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.RuntimeDirectory, ".bundled-runtime-transaction")));
        Assert.True(fixture.Prepare().Changed);
        Assert.Equal("node-v1", File.ReadAllText(fixture.Target("node.exe")));
        Assert.Equal("main-v1", File.ReadAllText(fixture.Target("nodejs-project/main.js")));
        Assert.Equal("dependency-v1", File.ReadAllText(fixture.Target("nodejs-project/node_modules/pkg/index.js")));
    }

    [Fact]
    public void VersionChangeRedeploysOnlyChangedPayloadsAndStillRemovesObsoleteFiles()
    {
        using var fixture = new BundleFixture();
        fixture.Prepare();
        File.WriteAllText(fixture.Source("nodejs-project/node_modules/pkg/obsolete.js"), "obsolete-v1");
        WriteManifest(fixture.Bundle);
        fixture.Prepare();
        var unchanged = fixture.Target("nodejs-project/node_modules/pkg/index.js");
        var unchangedTime = File.GetLastWriteTimeUtc(unchanged);
        var unchangedBytes = File.ReadAllBytes(unchanged);
        var obsolete = fixture.Target("nodejs-project/node_modules/pkg/obsolete.js");
        Assert.True(File.Exists(obsolete));
        File.WriteAllText(fixture.Source("node.exe"), "node-v2");
        File.Delete(fixture.Source("nodejs-project/node_modules/pkg/obsolete.js"));
        WriteManifest(fixture.Bundle);
        var events = new List<BundledRuntimeProgress>();
        var result = fixture.Prepare(progress: events.Add);
        Assert.True(result.Changed);
        Assert.Equal("node-v2", File.ReadAllText(fixture.Target("node.exe")));
        Assert.False(File.Exists(obsolete));
        Assert.Equal(unchangedTime, File.GetLastWriteTimeUtc(unchanged));
        Assert.Equal(unchangedBytes, File.ReadAllBytes(unchanged));
        Assert.Equal(1, events.Last(p => p.Phase == "Staging").Total);
        Assert.Equal(2, events.Last(p => p.Phase == "Replacing").Total);
        Assert.Equal(2, events.Last(p => p.Phase == "BackingUp").Total);
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.RuntimeDirectory, ".bundled-runtime-transaction")));
        Assert.False(fixture.Prepare().Changed);
    }

    [Fact]
    public void LegacyUnmarkedConflictRequiresExplicitForce()
    {
        using var fixture = new BundleFixture();
        Directory.CreateDirectory(fixture.Paths.RuntimeDirectory);
        File.WriteAllText(fixture.Target("node.exe"), "legacy-custom-node");
        Assert.Throws<BundledRuntimeNeedsConfirmationException>(() => fixture.Prepare());
        Assert.Equal("legacy-custom-node", File.ReadAllText(fixture.Target("node.exe")));
        fixture.Prepare(force: true);
        Assert.Equal("node-v1", File.ReadAllText(fixture.Target("node.exe")));
        File.Delete(Path.Combine(fixture.Paths.RuntimeDirectory, ".bundled-runtime.json"));
        Assert.True(fixture.Prepare().Changed); // Matching unmarked files can be adopted.
    }

    [Fact]
    public void UpgradeRemovesObsoleteOwnedFilesAndPreservesAllUserData()
    {
        using var fixture = new BundleFixture();
        fixture.Prepare();
        var users = new[] { "nodejs-project/config/.env", "nodejs-project/logs/node.log",
            "nodejs-project/danmu_api_stable/worker.js", "nodejs-project/danmu_api_dev/configs/envs.js",
            "nodejs-project/danmu_api_custom/worker.js", "nodejs-project/.cache/value", "nodejs-project/tmp/value",
            "nodejs-project/node_modules/unmanaged.txt" };
        foreach (var relative in users)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.Target(relative))!);
            File.WriteAllText(fixture.Target(relative), relative);
        }
        File.Delete(fixture.Source("nodejs-project/node_modules/pkg/index.js"));
        fixture.Update();
        fixture.Prepare();
        Assert.False(File.Exists(fixture.Target("nodejs-project/node_modules/pkg/index.js")));
        foreach (var relative in users) Assert.Equal(relative, File.ReadAllText(fixture.Target(relative)));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("node.exe:ads")]
    [InlineData("nodejs-project/config/.env")]
    [InlineData("nodejs-project/logs/output")]
    [InlineData("nodejs-project/danmu_api_stable/worker.js")]
    [InlineData("nodejs-project/node_modules/pkg/../escape")]
    [InlineData("nodejs-project/node_modules/CON.txt")]
    [InlineData("nodejs-project/node_modules/name.")]
    [InlineData("nodejs-project/node_modules/name ")]
    [InlineData("nodejs-project\\node_modules\\evil")]
    public void RejectsManifestBoundaryViolationsBeforeWriting(string relative)
    {
        using var fixture = new BundleFixture();
        File.AppendAllText(Path.Combine(fixture.Bundle, "SHA256SUMS.txt"), new string('A', 64) + "  " + relative + "\n");
        Assert.Throws<IOException>(() => fixture.Prepare());
        Assert.False(Directory.Exists(fixture.Paths.RuntimeDirectory));
    }

    [Fact]
    public void RejectsDuplicatePathsAndInvalidPersistedState()
    {
        using var fixture = new BundleFixture();
        var manifest = Path.Combine(fixture.Bundle, "SHA256SUMS.txt");
        File.AppendAllText(manifest, File.ReadLines(manifest).First() + "\n");
        Assert.Throws<IOException>(() => fixture.Prepare());
        WriteManifest(fixture.Bundle);
        fixture.Prepare();
        File.WriteAllText(Path.Combine(fixture.Paths.RuntimeDirectory, ".bundled-runtime.json"), "{}");
        Assert.Throws<IOException>(() => fixture.Prepare());
    }

    [Fact]
    public void SafetyGateBlocksUpgradeAndPreservesOldVersion()
    {
        using var fixture = new BundleFixture();
        fixture.Prepare();
        fixture.Update();
        Assert.Throws<IOException>(() => fixture.Prepare(safe: () => false));
        Assert.Equal("node-v1", File.ReadAllText(fixture.Target("node.exe")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.RuntimeDirectory, ".bundled-runtime-transaction")));
    }

    [Fact]
    public void CallbackFailureRollsBackAllReplacementsAndMarker()
    {
        using var fixture = new BundleFixture();
        fixture.Prepare();
        var marker = File.ReadAllText(Path.Combine(fixture.Paths.RuntimeDirectory, ".bundled-runtime.json"));
        fixture.Update();
        Assert.Throws<InvalidOperationException>(() => fixture.Prepare(progress: p =>
        {
            if (p.Phase == "Replacing" && p.Completed == 2) throw new InvalidOperationException("injected fault");
        }));
        Assert.Equal("node-v1", File.ReadAllText(fixture.Target("node.exe")));
        Assert.Equal("main-v1", File.ReadAllText(fixture.Target("nodejs-project/main.js")));
        Assert.Equal(marker, File.ReadAllText(Path.Combine(fixture.Paths.RuntimeDirectory, ".bundled-runtime.json")));
        Assert.True(fixture.Prepare().Changed);
    }

    [Fact]
    public void InterruptedReplacementRecoversOnNextInvocationAndReports()
    {
        using var fixture = new BundleFixture();
        fixture.Prepare();
        fixture.Update();
        var safe = true;
        Assert.Throws<AggregateException>(() => fixture.Prepare(safe: () => safe, progress: p =>
        {
            if (p.Phase == "Replacing" && p.Completed == 1)
            {
                safe = false; // Makes immediate recovery unsafe; persistent journal must survive.
                throw new IOException("interruption");
            }
        }));
        Assert.True(File.Exists(Path.Combine(fixture.Paths.RuntimeDirectory, ".bundled-runtime-transaction", "journal.json")));
        var error = Assert.Throws<IOException>(() => fixture.Prepare());
        Assert.Contains("已恢复", error.Message);
        Assert.Equal("node-v1", File.ReadAllText(fixture.Target("node.exe")));
        Assert.Equal("main-v1", File.ReadAllText(fixture.Target("nodejs-project/main.js")));
        Assert.True(fixture.Prepare().Changed);
    }

    [Fact]
    public void CancellationDuringReplacementRollsBackAndRethrows()
    {
        using var fixture = new BundleFixture();
        fixture.Prepare();
        fixture.Update();
        using var cancel = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => fixture.Prepare(progress: p =>
        {
            if (p.Phase == "Replacing") cancel.Cancel();
        }, cancellationToken: cancel.Token));
        Assert.Equal("node-v1", File.ReadAllText(fixture.Target("node.exe")));
        Assert.True(fixture.Prepare().Changed);
    }

    [Fact]
    public void ReparsePointCannotRedirectManagedDependencyWrites()
    {
        using var fixture = new BundleFixture();
        fixture.Prepare();
        var modules = fixture.Target("nodejs-project/node_modules");
        Directory.Delete(modules, true);
        var outside = Path.Combine(fixture.Directory.Path, "outside");
        Directory.CreateDirectory(outside);
        // Junctions do not require Windows developer mode or symlink privilege.
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{modules}\" \"{outside}\"",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output);
        try { Assert.Throws<IOException>(() => fixture.Prepare(force: true)); Assert.Empty(Directory.GetFiles(outside)); }
        finally { Directory.Delete(modules); }
    }

    [Fact]
    public void UpgradeValidatesAllSourcesBeforeReplacingAnything()
    {
        using var fixture = new BundleFixture();
        fixture.Prepare();
        fixture.Update();
        File.WriteAllText(fixture.Source("nodejs-project/node_modules/pkg/index.js"), "corrupt new source");
        Assert.Throws<IOException>(() => fixture.Prepare());
        Assert.Equal("node-v1", File.ReadAllText(fixture.Target("node.exe")));
        Assert.Equal("main-v1", File.ReadAllText(fixture.Target("nodejs-project/main.js")));
    }

    [Fact]
    public void SupportsSpacesChineseAndLongRuntimePaths()
    {
        using var fixture = new BundleFixture();
        var root = Path.Combine(fixture.Directory.Path, "中文 空格", new string('a', 90), new string('b', 90), new string('c', 90));
        var paths = new AppPaths(root);
        var first = BundledRuntimePreparer.Prepare(fixture.Bundle, paths, () => true);
        Assert.True(first.Changed);
        Assert.False(BundledRuntimePreparer.Prepare(fixture.Bundle, paths, () => true).Changed);
        Assert.Equal("node-v1", File.ReadAllText(Path.Combine(paths.RuntimeDirectory, "node.exe")));
    }

    [Fact]
    public void StagingBatchCancellationWaitsForCopiesAndKeepsCallbacksOnCallerThread()
    {
        using var fixture = new BundleFixture();
        for (var i = 0; i < 100; i++) File.WriteAllText(fixture.Source($"nodejs-project/node_modules/pkg/file-{i}.js"), $"payload-{i}");
        WriteManifest(fixture.Bundle);
        var callerThread = Environment.CurrentManagedThreadId;
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => fixture.Prepare(progress: p =>
        {
            Assert.Equal(callerThread, Environment.CurrentManagedThreadId);
            if (p.Phase == "Staging") cancellation.Cancel();
        }, cancellationToken: cancellation.Token));
        Assert.False(File.Exists(fixture.Target("node.exe")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.RuntimeDirectory, ".bundled-runtime-transaction")));
        Assert.True(fixture.Prepare().Changed);
        Assert.Equal(103, Directory.GetFiles(fixture.Paths.RuntimeDirectory, "*", SearchOption.AllDirectories)
            .Count(file => !Path.GetFileName(file).StartsWith(".bundled-runtime")));
    }

    [Fact]
    public void SourceChangedAfterVerificationFailsInStagingBeforeReplacement()
    {
        using var fixture = new BundleFixture();
        fixture.Prepare();
        fixture.Update();
        var error = Assert.Throws<IOException>(() => fixture.Prepare(progress: p =>
        {
            if (p.Phase == "VerifyingSource" && p.Completed == p.Total)
                File.WriteAllText(fixture.Source("nodejs-project/main.js"), "changed after source verification");
        }));
        Assert.Contains("暂存校验失败", error.Message);
        Assert.Equal("main-v1", File.ReadAllText(fixture.Target("nodejs-project/main.js")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.RuntimeDirectory, ".bundled-runtime-transaction")));
    }

    [Fact]
    public void ConcurrentPrepareCannotAcquireTransactionLock()
    {
        using var fixture = new BundleFixture();
        fixture.Prepare();
        using var held = new FileStream(Path.Combine(fixture.Paths.RuntimeDirectory, ".bundled-runtime.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => fixture.Prepare());
    }

    [Fact]
    public void CorruptBackupStopsRecoveryWithoutTouchingOtherTargets()
    {
        using var fixture = new BundleFixture();
        fixture.Prepare();
        fixture.Update();
        var safe = true;
        Assert.Throws<AggregateException>(() => fixture.Prepare(safe: () => safe, progress: p =>
        {
            if (p.Phase == "Replacing") { safe = false; throw new IOException("interrupted"); }
        }));
        var transaction = Path.Combine(fixture.Paths.RuntimeDirectory, ".bundled-runtime-transaction");
        File.WriteAllText(Path.Combine(transaction, "backup", "node.exe"), "corrupt backup");
        var before = File.ReadAllText(fixture.Target("node.exe"));
        Assert.Throws<IOException>(() => fixture.Prepare());
        Assert.Equal(before, File.ReadAllText(fixture.Target("node.exe")));
        Assert.True(File.Exists(Path.Combine(transaction, "journal.json")));
    }

    [SkippableFact]
    public void ActualTargetNodeProcessBlocksReplacementWithoutKillingIt()
    {
        var bundle = Environment.GetEnvironmentVariable("DANMU_TEST_PREPARE_BUNDLE");
        Skip.If(string.IsNullOrWhiteSpace(bundle), "Opt in with DANMU_TEST_PREPARE_BUNDLE for isolated Node process test.");
        using var fixture = new BundleFixture();
        File.Copy(Path.Combine(bundle!, "node.exe"), fixture.Source("node.exe"), true);
        WriteManifest(fixture.Bundle);
        fixture.Prepare();
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fixture.Target("node.exe"))
        {
            Arguments = "-e \"console.log('ready');setInterval(()=>{},1000)\"", UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        try
        {
            Assert.True(process.StandardOutput.ReadLineAsync().Wait(TimeSpan.FromSeconds(15)), "Node readiness timed out");
            File.WriteAllText(fixture.Source("nodejs-project/main.js"), "changed-host");
            WriteManifest(fixture.Bundle);
            var error = Assert.Throws<IOException>(() => fixture.Prepare());
            Assert.Contains("仍在运行", error.Message);
            Assert.False(process.HasExited);
            Assert.Equal("main-v1", File.ReadAllText(fixture.Target("nodejs-project/main.js")));
        }
        finally { if (!process.HasExited) process.Kill(true); process.WaitForExit(); }
    }

    [SkippableFact]
    public async Task UnrelatedNodeCreationAndExitDoNotBlockPreparation()
    {
        var bundle = Environment.GetEnvironmentVariable("DANMU_TEST_PREPARE_BUNDLE");
        Skip.If(string.IsNullOrWhiteSpace(bundle), "Opt in for isolated Node creation/exit race test.");
        using var fixture = new BundleFixture();
        var unrelated = Path.Combine(fixture.Directory.Path, "unrelated", "node.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(unrelated)!);
        File.Copy(Path.Combine(bundle!, "node.exe"), unrelated);
        using var started = new CountdownEvent(4);
        using var release = new ManualResetEventSlim();
        var churn = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            started.Signal();
            release.Wait();
            for (var i = 0; i < 40; i++)
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(unrelated)
                {
                    Arguments = "-e \"process.exit(0)\"", UseShellExecute = false,
                    CreateNoWindow = true, RedirectStandardError = true
                })!;
                try
                {
                    Assert.True(process.WaitForExit(15000), "Isolated Node did not exit");
                    Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
                }
                finally { if (!process.HasExited) { process.Kill(true); process.WaitForExit(); } }
            }
        })).ToArray();
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(15)));
            release.Set();
            for (var i = 0; i < 60; i++) fixture.Prepare(force: true);
        }
        finally { release.Set(); await Task.WhenAll(churn); }
    }

    [SkippableFact]
    public void RealBundleIsolatedTimingsAndAllFilesVerified()
    {
        var bundle = Environment.GetEnvironmentVariable("DANMU_TEST_PREPARE_BUNDLE");
        Skip.If(string.IsNullOrWhiteSpace(bundle), "Opt in with DANMU_TEST_PREPARE_BUNDLE; uses isolated artifacts directory.");
        var parent = Environment.GetEnvironmentVariable("DANMU_TEST_PREPARE_ARTIFACTS");
        Assert.False(string.IsNullOrWhiteSpace(parent));
        var directory = Path.Combine(parent!, "prepare-timing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var legacyPaths = new AppPaths(Path.Combine(directory, "legacy"));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            LegacyPrepare(bundle!, legacyPaths);
            var legacyFirst = clock.Elapsed;
            clock.Restart();
            LegacyPrepare(bundle!, legacyPaths);
            var legacyRepeat = clock.Elapsed;
            var paths = new AppPaths(Path.Combine(directory, "current"));
            var phaseTimes = new List<string>();
            var preparationClock = System.Diagnostics.Stopwatch.StartNew();
            var first = BundledRuntimePreparer.Prepare(bundle!, paths, () => true, progress: p =>
            {
                if (p.Completed == p.Total) phaseTimes.Add($"{p.Phase}@{preparationClock.ElapsedMilliseconds}ms");
            });
            var repeat = BundledRuntimePreparer.Prepare(bundle!, paths, () => true);
            var adoption = BundledRuntimePreparer.Prepare(bundle!, legacyPaths, () => true);
            long bytes = 0;
            var count = 0;
            foreach (var line in File.ReadLines(Path.Combine(bundle!, "SHA256SUMS.txt")))
            {
                var relative = line[66..];
                var target = Path.Combine(paths.RuntimeDirectory, relative);
                Assert.Equal(line[..64].ToUpperInvariant(), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))));
                bytes += new FileInfo(target).Length;
                count++;
            }
            Assert.Equal(count, first.SourceFilesHashed);
            Assert.Equal(0, repeat.SourceFilesHashed);
            Assert.False(repeat.Changed);
            var summary = $"files={count}; bytes={bytes}; legacy first={legacyFirst.TotalMilliseconds:F0}ms; legacy repeat={legacyRepeat.TotalMilliseconds:F0}ms; current first={first.Elapsed.TotalMilliseconds:F0}ms; current repeat={repeat.Elapsed.TotalMilliseconds:F0}ms; repeat source hashes={repeat.SourceFilesHashed}; repeat target hashes={repeat.TargetFilesHashed}; legacy adoption={adoption.Elapsed.TotalMilliseconds:F0}ms; phases={string.Join(',', phaseTimes)}";
            Console.WriteLine(summary);
            File.WriteAllText(Path.Combine(parent!, "prepare-timing-result.txt"), summary);
        }
        finally { Directory.Delete(directory, true); }
    }

    [SkippableFact]
    public void RealBundleReadinessStaysCheapForMetadataModePayloads()
    {
        var bundle = Environment.GetEnvironmentVariable("DANMU_TEST_PREPARE_BUNDLE");
        Skip.If(string.IsNullOrWhiteSpace(bundle), "Opt in with DANMU_TEST_PREPARE_BUNDLE for isolated readiness timing.");
        var parent = Environment.GetEnvironmentVariable("DANMU_TEST_PREPARE_ARTIFACTS");
        Assert.False(string.IsNullOrWhiteSpace(parent));
        var directory = Path.Combine(parent!, "readiness-timing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var paths = new AppPaths(Path.Combine(directory, "target"));
            BundledRuntimePreparer.Prepare(bundle!, paths, () => true);
            var samples = new List<double>();
            BundledRuntimeReadiness ready = default!;
            for (var i = 0; i < 3; i++)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                ready = BundledRuntimePreparer.CheckReadiness(bundle!, paths);
                samples.Add(clock.Elapsed.TotalMilliseconds);
                Assert.Equal(BundledRuntimeReadinessStatus.Ready, ready.Status);
            }
            var build = JsonSerializer.Deserialize<BundledRuntimePreparer.State>(
                File.ReadAllText(Path.Combine(bundle!, "runtime-build.json")))!;
            var metadataEntries = build.Files.Count(entry => entry.Mode == "metadata");
            Assert.True(metadataEntries > 0, "Real bundle must declare the large payload as metadata mode.");
            var summary = $"readinessMs={string.Join('/', samples.Select(sample => sample.ToString("F1")))} critical={ready.CriticalFilesChecked} metadataMode={metadataEntries} receipt={ready.ReceiptBytesRead}";
            var inspectorClock = System.Diagnostics.Stopwatch.StartNew();
            var inspector = RuntimeDependencyInspector.Check(bundle!, paths);
            summary += $" fullCheckMs={inspectorClock.Elapsed.TotalMilliseconds:F0} files={inspector.Total}";
            Console.WriteLine(summary);
            File.WriteAllText(Path.Combine(parent!, "readiness-timing-result.txt"), summary);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void MatchingUnmarkedRuntimeRegistersWithoutReplacingAnyPayload()    {
        using var fixture = new BundleFixture();
        LegacyPrepare(fixture.Bundle, fixture.Paths);
        var files = Directory.GetFiles(fixture.Paths.RuntimeDirectory, "*", SearchOption.AllDirectories);
        var timestamps = files.ToDictionary(f => f, File.GetLastWriteTimeUtc);
        // Deny deletion/replacement while allowing hashes. Registration must not stage or back up payloads.
        var handles = files.Select(f => new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read)).ToList();
        try
        {
            var phases = new List<string>();
            var result = fixture.Prepare(progress: p => phases.Add(p.Phase));
            Assert.True(result.Changed);
            Assert.InRange(result.TargetFilesHashed, files.Length, files.Length + 657);
            Assert.DoesNotContain("Staging", phases);
            Assert.DoesNotContain("Replacing", phases);
            Assert.False(Directory.Exists(Path.Combine(fixture.Paths.RuntimeDirectory, ".bundled-runtime-transaction")));
            foreach (var file in files) Assert.Equal(timestamps[file], File.GetLastWriteTimeUtc(file));
            Assert.False(fixture.Prepare().Changed);
        }
        finally { foreach (var handle in handles) handle.Dispose(); }
    }

    [Fact]
    public void ForceRepairReportsRealPhaseCountsThroughCleanupAndCompletion()
    {
        using var fixture = new BundleFixture();
        fixture.Prepare();
        var events = new List<BundledRuntimeProgress>();
        var caller = Environment.CurrentManagedThreadId;
        fixture.Prepare(force: true, progress: p =>
        {
            Assert.Equal(caller, Environment.CurrentManagedThreadId);
            events.Add(p);
            if (p.Phase == "CleaningUp" && p.Completed == 0)
            {
                var transaction = Path.Combine(fixture.Paths.RuntimeDirectory, ".bundled-runtime-transaction");
                Assert.Equal(Directory.GetFileSystemEntries(transaction, "*", SearchOption.AllDirectories).Length + 1, p.Total);
            }
            if (p.Phase == "Completed")
                Assert.False(Directory.Exists(Path.Combine(fixture.Paths.RuntimeDirectory, ".bundled-runtime-transaction")));
        });
        var phases = events.Select(p => p.Phase).Distinct().ToArray();
        Assert.Equal(new[] { "ReadingManifest", "ReadingState", "CheckingTargetMetadata", "VerifyingTarget",
            "VerifyingSource", "CheckingConflicts", "BackingUp", "Staging", "WritingJournal", "Replacing",
            "Snapshotting", "Committing", "InspectingCleanup", "CleaningUp", "Completed" }, phases);
        foreach (var group in events.GroupBy(p => p.Phase).Where(g => g.Key is not ("InspectingCleanup" or "Completed")))
        {
            Assert.Equal(0, group.First().Completed);
            Assert.Equal(group.Last().Total, group.Last().Completed);
            Assert.All(group, p => Assert.InRange(p.Completed, 0, p.Total));
            Assert.Equal(group.Select(p => p.Completed).Order(), group.Select(p => p.Completed));
            Assert.Single(group.Select(p => p.Total).Distinct());
        }
        Assert.Equal(2, events.Last(p => p.Phase == "VerifyingTarget").Total);
        foreach (var phase in new[] { "CheckingTargetMetadata", "VerifyingSource", "CheckingConflicts", "BackingUp", "Staging", "Replacing", "Snapshotting" })
            Assert.Equal(3, events.Last(p => p.Phase == phase).Total);
    }

    [Theory]
    [InlineData("CheckingTargetMetadata")]
    [InlineData("VerifyingTarget")]
    [InlineData("VerifyingSource")]
    [InlineData("CheckingConflicts")]
    [InlineData("BackingUp")]
    [InlineData("Staging")]
    [InlineData("WritingJournal")]
    [InlineData("Replacing")]
    [InlineData("Snapshotting")]
    [InlineData("Committing")]
    public void CancellationBeforeCommitPreservesOldFilesAndMarker(string phase)
    {
        using var fixture = new BundleFixture();
        fixture.Prepare();
        fixture.Update();
        var markerPath = Path.Combine(fixture.Paths.RuntimeDirectory, ".bundled-runtime.json");
        var marker = File.ReadAllText(markerPath);
        using var cancellation = new CancellationTokenSource();
        var events = new List<BundledRuntimeProgress>();
        Assert.Throws<OperationCanceledException>(() => fixture.Prepare(progress: p =>
        {
            events.Add(p);
            if (p.Phase == phase && (p.Completed > 0 || phase == "Committing")) cancellation.Cancel();
        }, cancellationToken: cancellation.Token));
        Assert.DoesNotContain(events, p => p.Phase == "Completed");
        Assert.Equal("node-v1", File.ReadAllText(fixture.Target("node.exe")));
        Assert.Equal("main-v1", File.ReadAllText(fixture.Target("nodejs-project/main.js")));
        Assert.Equal(marker, File.ReadAllText(markerPath));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.RuntimeDirectory, ".bundled-runtime-transaction")));
    }

    [Theory]
    [InlineData("Committing")]
    [InlineData("InspectingCleanup")]
    [InlineData("CleaningUp")]
    public void CancellationAfterCommitBoundaryCompletesCleanupWithoutClaimingRollback(string phase)
    {
        using var fixture = new BundleFixture();
        fixture.Prepare();
        fixture.Update();
        using var cancellation = new CancellationTokenSource();
        Assert.True(fixture.Prepare(progress: p =>
        {
            if (p.Phase == phase && (p.Completed > 0 || phase != "Committing")) cancellation.Cancel();
        }, cancellationToken: cancellation.Token).Changed);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal("node-v2", File.ReadAllText(fixture.Target("node.exe")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.RuntimeDirectory, ".bundled-runtime-transaction")));
        Assert.False(fixture.Prepare().Changed);
    }

    [Fact]
    public void ParallelBackupFailureWaitsForWorkersAndPreservesOriginalFiles()
    {
        using var fixture = new BundleFixture();
        fixture.Prepare();
        fixture.Update();
        FileStream? locked = null;
        try
        {
            Assert.ThrowsAny<IOException>(() => fixture.Prepare(progress: p =>
            {
                if (p.Phase == "BackingUp" && p.Completed == 0)
                    locked = new FileStream(fixture.Target("node.exe"), FileMode.Open, FileAccess.Read, FileShare.None);
            }));
        }
        finally { locked?.Dispose(); }
        Assert.Equal("node-v1", File.ReadAllText(fixture.Target("node.exe")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.RuntimeDirectory, ".bundled-runtime-transaction")));
        Assert.True(fixture.Prepare().Changed);
    }

    [Fact]
    public void CleanupFailureIsExplicitAndCommittedJournalAllowsCleanupOnNextInvocation()
    {
        using var fixture = new BundleFixture();
        fixture.Prepare();
        fixture.Update();
        var transaction = Path.Combine(fixture.Paths.RuntimeDirectory, ".bundled-runtime-transaction");
        FileStream? locked = null;
        var completed = false;
        try
        {
            var error = Assert.Throws<AggregateException>(() => fixture.Prepare(progress: p =>
            {
                if (p.Phase == "Completed") completed = true;
                if (p.Phase == "CleaningUp" && p.Completed == 0)
                    locked = new FileStream(Path.Combine(transaction, "backup", "node.exe"), FileMode.Open, FileAccess.Read, FileShare.Read);
            }));
            Assert.Contains(error.Flatten().InnerExceptions, e => e is IOException);
            Assert.False(completed);
            Assert.True(File.Exists(Path.Combine(transaction, "journal.json")));
        }
        finally { locked?.Dispose(); }
        Assert.Equal("node-v2", File.ReadAllText(fixture.Target("node.exe")));
        Assert.Contains("已恢复或完成清理", Assert.Throws<IOException>(() => fixture.Prepare()).Message);
        Assert.False(Directory.Exists(transaction));
        Assert.False(fixture.Prepare().Changed);
    }

    [Fact]
    public void CleanupChecksWholeTreeBeforeDeletingAndRejectsReparsePoints()
    {
        using var fixture = new BundleFixture();
        fixture.Prepare();
        fixture.Update();
        var transaction = Path.Combine(fixture.Paths.RuntimeDirectory, ".bundled-runtime-transaction");
        var link = Path.Combine(transaction, "redirect");
        var outside = Path.Combine(fixture.Directory.Path, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "sentinel"), "keep");
        try
        {
            var error = Assert.Throws<AggregateException>(() => fixture.Prepare(progress: p =>
            {
                if (p.Phase != "InspectingCleanup" || p.Completed != 0) return;
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
                {
                    Arguments = $"/c mklink /J \"{link}\" \"{outside}\"", UseShellExecute = false,
                    CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                })!;
                var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit();
                Assert.True(process.ExitCode == 0, output);
            }));
            Assert.Contains(error.Flatten().InnerExceptions, e => e.Message.Contains("reparse point", StringComparison.Ordinal));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "sentinel")));
            Assert.True(File.Exists(Path.Combine(transaction, "backup", "node.exe")));
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); }
        Assert.Throws<IOException>(() => fixture.Prepare());
        Assert.False(Directory.Exists(transaction));
        Assert.False(fixture.Prepare().Changed);
    }

    [Theory]
    [InlineData("BackingUp")]
    [InlineData("Snapshotting")]
    [InlineData("Committing")]
    public void NewPhaseCallbackFailureIsExplicitAndRollsBackBeforeCommit(string phase)
    {
        using var fixture = new BundleFixture();
        fixture.Prepare();
        fixture.Update();
        Assert.Throws<InvalidOperationException>(() => fixture.Prepare(progress: p =>
        {
            if (p.Phase == phase && p.Completed == 0) throw new InvalidOperationException("progress sink failed");
        }));
        Assert.Equal("node-v1", File.ReadAllText(fixture.Target("node.exe")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.RuntimeDirectory, ".bundled-runtime-transaction")));
    }

    [Fact]
    public void ReadinessIsBoundedAndDoesNotReadFullStateManifestOrDependencyMetadata()
    {
        using var fixture = new BundleFixture();
        for (var i = 0; i < 6466; i++) File.WriteAllText(fixture.Source($"nodejs-project/node_modules/pkg/extra-{i}.js"), "x");
        WriteManifest(fixture.Bundle);
        fixture.Prepare();
        using var marker = new FileStream(fixture.Target(".bundled-runtime.json"), FileMode.Open, FileAccess.Read, FileShare.None);
        using var manifest = new FileStream(fixture.Source("SHA256SUMS.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
        // The fast path must not inspect noncritical metadata (a full Prepare would reject this).
        File.Delete(fixture.Target("nodejs-project/node_modules/pkg/index.js"));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var ready = BundledRuntimePreparer.CheckReadiness(fixture.Bundle, fixture.Paths);
        Assert.Equal(BundledRuntimeReadinessStatus.Ready, ready.Status);
        Assert.InRange(ready.ReceiptBytesRead, 1, 16384);
        Assert.Equal(2, ready.CriticalFilesChecked);
        var phases = new List<string>();
        var result = BundledRuntimePreparer.PrepareForStartup(fixture.Bundle, fixture.Paths, progress: p => phases.Add(p.Phase));
        Assert.False(result.Changed);
        Assert.Empty(phases);
        Console.WriteLine($"6469 entries readiness+startup={clock.Elapsed.TotalMilliseconds:F2}ms; receipt={ready.ReceiptBytesRead}; critical={ready.CriticalFilesChecked}");
    }

    [Fact]
    public void MissingReceiptFullyVerifiesAndRegistersWithoutReplacement()
    {
        using var fixture = new BundleFixture();
        fixture.Prepare();
        File.Delete(fixture.Target(".bundled-runtime-receipt.json"));
        Assert.Equal(BundledRuntimeReadinessStatus.NeedsPreparation, BundledRuntimePreparer.CheckReadiness(fixture.Bundle, fixture.Paths).Status);
        var result = BundledRuntimePreparer.PrepareForStartup(fixture.Bundle, fixture.Paths);
        Assert.Equal(3, result.TargetFilesHashed);
        Assert.False(result.Changed);
        Assert.Equal(BundledRuntimeReadinessStatus.Ready, BundledRuntimePreparer.CheckReadiness(fixture.Bundle, fixture.Paths).Status);
    }

    [Theory]
    [InlineData("damage")]
    [InlineData("missing")]
    [InlineData("oversize")]
    [InlineData("transaction")]
    [InlineData("root")]
    public void FastCheckRejectsInvalidEvidence(string kind)
    {
        using var fixture = new BundleFixture();
        fixture.Prepare();
        if (kind == "damage")
        {
            var path = fixture.Target("nodejs-project/main.js");
            var time = File.GetLastWriteTimeUtc(path);
            File.WriteAllText(path, "MAIN-v1");
            File.SetLastWriteTimeUtc(path, time);
        }
        if (kind == "missing") File.Delete(fixture.Target("node.exe"));
        if (kind == "oversize") File.WriteAllText(fixture.Target(".bundled-runtime-receipt.json"), new string('x', 16385));
        if (kind == "transaction") Directory.CreateDirectory(fixture.Target(".bundled-runtime-transaction"));
        if (kind == "root")
        {
            var path = fixture.Target(".bundled-runtime-receipt.json");
            var receipt = System.Text.Json.JsonSerializer.Deserialize<BundledRuntimePreparer.StartupReceipt>(File.ReadAllText(path))!;
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(receipt with { Root = fixture.Bundle }));
        }
        var ready = BundledRuntimePreparer.CheckReadiness(fixture.Bundle, fixture.Paths);
        Assert.Equal(BundledRuntimeReadinessStatus.NeedsPreparation, ready.Status);
        Assert.InRange(ready.ReceiptBytesRead, 0, 16384);
        Assert.NotEmpty(ready.Reason);
    }

    [SkippableFact]
    public void PublishedKotlinMigratesOnlyAfterCompleteTrustedHashesAndPreservesUserData()
    {
        var archive = Environment.GetEnvironmentVariable("DANMU_TEST_KOTLIN_ZIP");
        Skip.If(string.IsNullOrWhiteSpace(archive), "Requires original hash-verified Kotlin release ZIP.");
        using var fixture = new BundleFixture();
        using (var input = File.OpenRead(archive!))
            Assert.Equal("F9B5C73194067F30B59839D4C85F7A51740CB049D592D58A9E6DA70F06CBF20D", Convert.ToHexString(SHA256.HashData(input)));
        using var zip = System.IO.Compression.ZipFile.OpenRead(archive!);
        var jarEntry = Assert.Single(zip.Entries, e => System.Text.RegularExpressions.Regex.IsMatch(e.FullName, @"/app/desktop-0\.1\.0-.*\.jar$"));
        using var memory = new MemoryStream();
        using (var stream = jarEntry.Open()) stream.CopyTo(memory);
        memory.Position = 0;
        using var jar = new System.IO.Compression.ZipArchive(memory);
        foreach (var entry in jar.Entries.Where(e => e.FullName.StartsWith("runtime/", StringComparison.Ordinal) && !e.FullName.EndsWith('/')))
        {
            var target = fixture.Target(entry.FullName[8..]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = entry.Open();
            using var destination = File.Create(target);
            source.CopyTo(destination);
        }
        var sentinels = new[] { "nodejs-project/config/.env", "nodejs-project/danmu_api_stable/worker.js", "nodejs-project/danmu_api_dev/worker.js", "nodejs-project/danmu_api_custom/worker.js", "nodejs-project/logs/a", "nodejs-project/.cache/a", "nodejs-project/tmp/a", "nodejs-project/compile-cache/a", "nodejs-project/node_modules/user-file" };
        foreach (var relative in sentinels) { Directory.CreateDirectory(Path.GetDirectoryName(fixture.Target(relative))!); File.WriteAllText(fixture.Target(relative), "preserve-" + relative); }
        var last = jar.Entries.Last(e => e.FullName.StartsWith("runtime/nodejs-project/node_modules/", StringComparison.Ordinal) && !e.FullName.EndsWith('/'));
        var oldBytes = File.ReadAllBytes(fixture.Target(last.FullName[8..]));
        File.WriteAllText(fixture.Target(last.FullName[8..]), "unknown legacy edit");
        var error = Assert.Throws<BundledRuntimeNeedsConfirmationException>(() => fixture.Prepare());
        Assert.DoesNotContain("forceRepair", error.Message);
        Assert.Equal("unknown legacy edit", File.ReadAllText(fixture.Target(last.FullName[8..])));
        File.WriteAllBytes(fixture.Target(last.FullName[8..]), oldBytes);
        Assert.Throws<InvalidOperationException>(() => fixture.Prepare(progress: p => { if (p.Phase == "Replacing" && p.Completed == 2) throw new InvalidOperationException("rollback probe"); }));
        Assert.Equal(oldBytes, File.ReadAllBytes(fixture.Target(last.FullName[8..])));
        var result = fixture.Prepare();
        Assert.True(result.Changed);
        Assert.True(result.TargetFilesHashed >= 657);
        foreach (var relative in sentinels) Assert.Equal("preserve-" + relative, File.ReadAllText(fixture.Target(relative)));
        Assert.Equal(BundledRuntimeReadinessStatus.Ready, BundledRuntimePreparer.CheckReadiness(fixture.Bundle, fixture.Paths).Status);
    }

    // Frozen old algorithm for reproducible, opt-in timing on isolated runtime directories only.
    private static void LegacyPrepare(string bundleDirectory, AppPaths paths)
    {
        var entries = new List<(string Source, string Target, string Hash)>();
        foreach (var line in File.ReadAllLines(Path.Combine(bundleDirectory, "SHA256SUMS.txt")))
        {
            if (line.Length < 67 || line.Substring(64, 2) != "  ") throw new IOException("invalid manifest");
            var relative = line[66..].Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Any(part => part is ".." or ".")) throw new IOException("invalid path");
            if (relative.Contains("config" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || relative.Contains("danmu_api_", StringComparison.OrdinalIgnoreCase)) throw new IOException("user data");
            var source = Path.Combine(bundleDirectory, relative);
            using var stream = File.OpenRead(source);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(line[..64], StringComparison.OrdinalIgnoreCase)) throw new IOException("invalid hash");
            entries.Add((source, Path.Combine(paths.RuntimeDirectory, relative), line[..64]));
        }
        if (!entries.Any(entry => entry.Target == Path.Combine(paths.RuntimeDirectory, "node.exe")) ||
            !entries.Any(entry => entry.Target == Path.Combine(paths.NodeProjectDirectory, "main.js"))) throw new IOException("missing entry");
        foreach (var entry in entries)
        {
            if (File.Exists(entry.Target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(entry.Target)!);
            var temporary = entry.Target + ".prepare-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(entry.Source, temporary);
                using (var stream = File.OpenRead(temporary))
                    if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(entry.Hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("copy hash");
                File.Move(temporary, entry.Target);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private sealed class BundleFixture : IDisposable
    {
        private readonly string? _metadataModePath;
        public TemporaryDirectory Directory { get; } = new();
        public string Bundle { get; }
        public AppPaths Paths { get; }
        public BundleFixture(string? metadataModePath = null)
        {
            _metadataModePath = metadataModePath;
            Bundle = Path.Combine(Directory.Path, "bundle");
            Paths = new AppPaths(Path.Combine(Directory.Path, "target"));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Source("nodejs-project/node_modules/pkg/index.js"))!);
            File.WriteAllText(Source("node.exe"), "node-v1");
            File.WriteAllText(Source("nodejs-project/main.js"), "main-v1");
            File.WriteAllText(Source("nodejs-project/node_modules/pkg/index.js"), "dependency-v1");
            WriteManifest(Bundle, metadataModePath);
        }
        public string Source(string relative) => Path.Combine(Bundle, relative);
        public string Target(string relative) => Path.Combine(Paths.RuntimeDirectory, relative);
        public void Update()
        {
            File.WriteAllText(Source("node.exe"), "node-v2");
            File.WriteAllText(Source("nodejs-project/main.js"), "main-v2");
            WriteManifest(Bundle, _metadataModePath);
        }
        public BundledRuntimePreparationResult Prepare(bool force = false, Func<bool>? safe = null,
            Action<BundledRuntimeProgress>? progress = null, CancellationToken cancellationToken = default) =>
            BundledRuntimePreparer.Prepare(Bundle, Paths, safe, force, progress, cancellationToken);
        public void Dispose() => Directory.Dispose();
    }

    private static void WriteManifest(string bundle, string? metadataModePath = null)
    {
        var entries = Directory.GetFiles(bundle, "*", SearchOption.AllDirectories)
            .Where(file => Path.GetFileName(file) is not ("SHA256SUMS.txt" or "runtime-build.json"))
            .Select(file => new BundledRuntimePreparer.Entry(Path.GetRelativePath(bundle, file).Replace('\\', '/'),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))))).ToList();
        File.WriteAllLines(Path.Combine(bundle, "SHA256SUMS.txt"), entries.Select(e => e.Hash + "  " + e.Path));
        var version = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n',
            entries.OrderBy(e => e.Path, StringComparer.Ordinal).Select(e => e.Hash + "  " + e.Path)))));
        var critical = entries.Where(e => !e.Path.Contains("/node_modules/", StringComparison.Ordinal))
            .Select(e => e.Path == metadataModePath ? e with { Mode = "metadata" } : e).ToList();
        File.WriteAllText(Path.Combine(bundle, "runtime-build.json"), JsonSerializer.Serialize(
            new BundledRuntimePreparer.State(1, version, critical)));
    }
}
