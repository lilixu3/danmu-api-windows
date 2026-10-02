using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DanmuApi.App.Services;
using DanmuApi.Core;
using DanmuApi.Platform;

namespace DanmuApi.Tests;

public sealed class OutboundRuntimeBundleTests
{
    private static readonly string[] HostFiles =
    [
        "main.js", "android-server.js", "favorite-scheduler-host.js", "runtime-polyfills.js", "startup-failure.js",
        "worker-proxy.js", "package.json", "package-lock.json", "runtime_asset_layout.txt",
        "app-outbound-bridge.js", "app-outbound-runtime.js", "app-outbound-diagnostics.js", "app-outbound-errors.js"
    ];
    private static readonly string[] HelperFiles =
    [
        "danmu-outbound.exe", "outbound-build.json", "OUTBOUND-LICENSE.txt", "OUTBOUND-UPSTREAM.txt",
        "outbound-source.zip", "OUTBOUND-NOTICES.txt", "OUTBOUND-SHA256SUMS.txt"
    ];

    [Theory]
    [InlineData("danmu-outbound.exe")]
    [InlineData("outbound-build.json")]
    [InlineData("OUTBOUND-SHA256SUMS.txt")]
    [InlineData("outbound-source.zip")]
    public void MissingHelperPayloadFailsBeforeCreatingRuntime(string file)
    {
        using var fixture = new Fixture();
        File.Delete(fixture.Helper(file));
        fixture.Manifest();
        Assert.Throws<IOException>(() => fixture.Prepare());
        Assert.False(Directory.Exists(fixture.Paths.RuntimeDirectory));
    }

    [Theory]
    [InlineData("app-outbound-bridge.js")]
    [InlineData("app-outbound-runtime.js")]
    [InlineData("app-outbound-diagnostics.js")]
    [InlineData("app-outbound-errors.js")]
    public void PartialOutboundHostCannotClaimFeatureAvailability(string file)
    {
        using var fixture = new Fixture();
        File.Delete(fixture.Source("nodejs-project/" + file));
        fixture.Manifest();
        Assert.Throws<IOException>(() => fixture.Prepare());
        Assert.False(Directory.Exists(fixture.Paths.RuntimeDirectory));
    }

    [Theory]
    [InlineData("nodejs-project/outbound/foreign.exe")]
    [InlineData("nodejs-project/outbound/subdir/danmu-outbound.exe")]
    [InlineData("nodejs-project/outbound/../danmu-outbound.exe")]
    [InlineData("nodejs-project/config/outbound/settings.json")]
    [InlineData("nodejs-project/app-outbound-foreign.js")]
    public void OutboundWhitelistDoesNotOpenExecutableOrUserConfigChannel(string path)
    {
        using var fixture = new Fixture();
        File.AppendAllText(fixture.Source("SHA256SUMS.txt"), new string('A', 64) + "  " + path + "\n");
        Assert.Throws<IOException>(() => fixture.Prepare());
        Assert.False(Directory.Exists(fixture.Paths.RuntimeDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourcePeArchitectureIsCheckedBeforeInstallationAndOnRepeat(bool wrongNode)
    {
        using var fixture = new Fixture();
        fixture.Prepare();
        var marker = File.ReadAllText(fixture.Target(".bundled-runtime.json"));
        var file = wrongNode ? fixture.Source("node.exe") : fixture.Helper("danmu-outbound.exe");
        WritePe(file, BundledRuntimePreparer.HostArchitecture() == "x86" ? "x64" : "x86");
        // Leave the manifest unchanged: a same-version Prepare must still inspect the source PE.
        var error = Assert.Throws<IOException>(() => fixture.Prepare());
        Assert.Contains("PE 架构", error.Message);
        Assert.Equal(marker, File.ReadAllText(fixture.Target(".bundled-runtime.json")));
    }

    [Theory]
    [InlineData("schemaVersion", "2")]
    [InlineData("schemaVersion", "\"1\"")]
    [InlineData("protocolVersion", "2")]
    [InlineData("version", "\"0.9.0\"")]
    [InlineData("runtimeIdentifier", "\"win-foreign\"")]
    [InlineData("goVersion", "\"go1.25.0\"")]
    [InlineData("sourceCommit", "\"foreign\"")]
    [InlineData("machine", "0")]
    [InlineData("executableSha256", "\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"")]
    [InlineData("sourceSha256", "\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"")]
    public void MetadataVersionsAndDigestsMustAgreeWithArchitectureAndManifests(string key, string value)
    {
        using var fixture = new Fixture();
        fixture.ChangeMetadata(key, JsonNode.Parse(value));
        fixture.HelperManifest();
        fixture.Manifest();
        Assert.Throws<IOException>(() => fixture.Prepare());
        Assert.False(Directory.Exists(fixture.Paths.RuntimeDirectory));
    }

    [Theory]
    [InlineData("toolchainArchiveUrl", "null")]
    [InlineData("toolchainArchiveUrl", "\"https://untrusted.invalid/go.zip\"")]
    [InlineData("toolchainArchiveSha256", "\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"")]
    [InlineData("toolchainArchiveBytes", "74815265")]
    [InlineData("toolchainArchiveBytes", "\"74815266\"")]
    [InlineData("toolchainInputCount", "0")]
    [InlineData("toolchainInputCount", "\"100\"")]
    [InlineData("toolchainInputSha256", "null")]
    [InlineData("toolchainDriverSha256", "\"bad\"")]
    [InlineData("toolchainCompilerSha256", "null")]
    [InlineData("toolchainLinkerSha256", "null")]
    [InlineData("toolchainStandardLibrarySha256", "null")]
    [InlineData("goEnvironment", "null")]
    [InlineData("goEnvironment", "{\"GOROOT\":\"wrong\",\"GOTOOLDIR\":\"pkg/tool/windows_amd64\",\"GOENV\":\"off\",\"GOTOOLCHAIN\":\"local\"}")]
    [InlineData("goEnvironment", "{\"GOROOT\":\"verified-official-archive-root\",\"GOTOOLDIR\":\"pkg/tool/windows_amd64\",\"GOENV\":\"user\",\"GOTOOLCHAIN\":\"local\"}")]
    public void CompleteOfficialToolchainProvenanceCannotBeMissingOrMalformed(string key, string value)
    {
        using var fixture = new Fixture();
        fixture.ChangeMetadata(key, JsonNode.Parse(value));
        fixture.HelperManifest();
        fixture.Manifest();
        Assert.Throws<IOException>(() => fixture.Prepare());
        Assert.False(Directory.Exists(fixture.Paths.RuntimeDirectory));
        var inputs = fixture.ScriptInputs();
        var destination = Path.Combine(fixture.Directory.Path, "rejected provenance");
        var result = RunBundleScript("-BaseBundle", inputs.Base, "-Destination", destination, "-Arch", fixture.Arch,
            "-OutboundArtifact", inputs.Artifact, "-HostSource", inputs.Host);
        Assert.NotEqual(0, result.Code);
        Assert.False(Directory.Exists(destination));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("traversal")]
    [InlineData("digest")]
    public void NativeHashManifestCannotDisagreeWithRuntimeManifest(string kind)
    {
        using var fixture = new Fixture();
        var file = fixture.Helper("OUTBOUND-SHA256SUMS.txt");
        var lines = File.ReadAllLines(file).ToList();
        if (kind == "duplicate") lines.Add(lines[0]);
        if (kind == "missing") lines.RemoveAt(0);
        if (kind == "traversal") lines[0] = new string('A', 64) + "  ../foreign.exe";
        if (kind == "digest") lines[0] = new string('A', 64) + lines[0][64..];
        File.WriteAllLines(file, lines);
        fixture.Manifest();
        Assert.Throws<IOException>(() => fixture.Prepare());
        Assert.False(Directory.Exists(fixture.Paths.RuntimeDirectory));
    }

    [Fact]
    public void SourceArchiveMustContainTheRecordedSourceBytes()
    {
        using var fixture = new Fixture();
        using (var zip = ZipFile.Open(fixture.Helper("outbound-source.zip"), ZipArchiveMode.Update))
        {
            zip.GetEntry("runtime/outbound/src/main.go")!.Delete();
            var entry = zip.CreateEntry("runtime/outbound/src/main.go");
            using var output = new StreamWriter(entry.Open());
            output.Write("changed source");
        }
        fixture.HelperManifest();
        fixture.Manifest();
        Assert.Throws<IOException>(() => fixture.Prepare());
        Assert.False(Directory.Exists(fixture.Paths.RuntimeDirectory));
    }

    [Theory]
    [InlineData("nodejs-project/app-outbound-bridge.js")]
    [InlineData("nodejs-project/app-outbound-runtime.js")]
    [InlineData("nodejs-project/app-outbound-diagnostics.js")]
    [InlineData("nodejs-project/app-outbound-errors.js")]
    [InlineData("nodejs-project/outbound/danmu-outbound.exe")]
    [InlineData("nodejs-project/outbound/outbound-build.json")]
    [InlineData("nodejs-project/outbound/OUTBOUND-SHA256SUMS.txt")]
    public void StartupHashesEveryOutboundEntryEvenWithRestoredTimestamps(string path)
    {
        using var fixture = new Fixture();
        fixture.Prepare();
        Assert.Equal(BundledRuntimeReadinessStatus.Ready, BundledRuntimePreparer.CheckReadiness(fixture.Bundle, fixture.Paths).Status);
        var target = fixture.Target(path);
        var time = File.GetLastWriteTimeUtc(target);
        var bytes = File.ReadAllBytes(target);
        bytes[^1] ^= 1;
        File.WriteAllBytes(target, bytes);
        File.SetLastWriteTimeUtc(target, time);
        Assert.Equal(BundledRuntimeReadinessStatus.NeedsPreparation, BundledRuntimePreparer.CheckReadiness(fixture.Bundle, fixture.Paths).Status);
    }

    [Theory]
    [InlineData("nodejs-project/app-outbound-bridge.js")]
    [InlineData("nodejs-project/app-outbound-runtime.js")]
    [InlineData("nodejs-project/app-outbound-diagnostics.js")]
    [InlineData("nodejs-project/app-outbound-errors.js")]
    [InlineData("nodejs-project/outbound/danmu-outbound.exe")]
    [InlineData("nodejs-project/outbound/outbound-build.json")]
    [InlineData("nodejs-project/outbound/OUTBOUND-LICENSE.txt")]
    [InlineData("nodejs-project/outbound/OUTBOUND-UPSTREAM.txt")]
    [InlineData("nodejs-project/outbound/outbound-source.zip")]
    [InlineData("nodejs-project/outbound/OUTBOUND-NOTICES.txt")]
    [InlineData("nodejs-project/outbound/OUTBOUND-SHA256SUMS.txt")]
    public void FullInspectorAcceptsCompleteOutboundBundleAndHashesEveryNewManagedPayload(string relative)
    {
        using var fixture = new Fixture();
        fixture.Prepare();
        var count = File.ReadAllLines(fixture.Source("SHA256SUMS.txt")).Length;
        Assert.Equal(new RuntimeDependencyCheckResult(count, 0, 0), RuntimeDependencyInspector.Check(fixture.Bundle, fixture.Paths));
        var marker = fixture.Target(".bundled-runtime.json");
        var originalMarker = File.ReadAllBytes(marker);
        var target = fixture.Target(relative);
        var time = File.GetLastWriteTimeUtc(target);
        var bytes = File.ReadAllBytes(target);
        bytes[^1] ^= 1;
        File.WriteAllBytes(target, bytes);
        File.SetLastWriteTimeUtc(target, time);
        Assert.Equal(new RuntimeDependencyCheckResult(count, 0, 1), RuntimeDependencyInspector.Check(fixture.Bundle, fixture.Paths));
        File.Delete(target);
        Assert.Equal(new RuntimeDependencyCheckResult(count, 1, 0), RuntimeDependencyInspector.Check(fixture.Bundle, fixture.Paths));
        Assert.Equal(originalMarker, File.ReadAllBytes(marker));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void FullInspectorRejectsIncompleteOutboundManifestBeforeCheckingTargets()
    {
        using var fixture = new Fixture();
        File.Delete(fixture.Source("nodejs-project/app-outbound-bridge.js"));
        fixture.Manifest();
        var error = Assert.Throws<IOException>(() => RuntimeDependencyInspector.Check(fixture.Bundle, fixture.Paths));
        Assert.Contains("app-outbound-bridge.js", error.Message);
        Assert.False(Directory.Exists(fixture.Paths.RuntimeDirectory));
    }

    [Fact]
    public void OutboundIdentityCannotDeclareHelperAsMetadataOnly()
    {
        using var fixture = new Fixture();
        var path = fixture.Source("runtime-build.json");
        var state = JsonSerializer.Deserialize<BundledRuntimePreparer.State>(File.ReadAllText(path))!;
        var files = state.Files.Select(e => e.Path.EndsWith("danmu-outbound.exe", StringComparison.Ordinal) ? e with { Mode = "metadata" } : e).ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(state with { Files = files }));
        Assert.Throws<IOException>(() => fixture.Prepare());
        Assert.False(Directory.Exists(fixture.Paths.RuntimeDirectory));
    }

    [Fact]
    public void UpgradeAndRollbackPreserveUserOutboundConfiguration()
    {
        using var fixture = new Fixture();
        fixture.Prepare();
        var config = fixture.Target("nodejs-project/config/outbound/settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "{\"enabled\":true,\"user\":\"keep\"}");
        var before = File.ReadAllBytes(fixture.Target("nodejs-project/outbound/danmu-outbound.exe"));
        var marker = File.ReadAllText(fixture.Target(".bundled-runtime.json"));
        using (var output = new FileStream(fixture.Helper("danmu-outbound.exe"), FileMode.Append)) output.WriteByte(42);
        fixture.CreateMetadata();
        fixture.HelperManifest();
        fixture.Manifest();
        Assert.Throws<InvalidOperationException>(() => fixture.Prepare(progress: p =>
        {
            if (p.Phase == "Replacing" && p.Completed == 2) throw new InvalidOperationException("injected replacement failure");
        }));
        Assert.Equal(before, File.ReadAllBytes(fixture.Target("nodejs-project/outbound/danmu-outbound.exe")));
        Assert.Equal(marker, File.ReadAllText(fixture.Target(".bundled-runtime.json")));
        fixture.Prepare();
        Assert.Equal(File.ReadAllBytes(fixture.Helper("danmu-outbound.exe")), File.ReadAllBytes(fixture.Target("nodejs-project/outbound/danmu-outbound.exe")));
        Assert.Equal("{\"enabled\":true,\"user\":\"keep\"}", File.ReadAllText(config));
        Assert.False(Directory.Exists(fixture.Target(".bundled-runtime-transaction")));
    }

    [Fact]
    public void CorruptSourceUpgradeFailsWithoutTouchingPreviousRuntime()
    {
        using var fixture = new Fixture();
        fixture.Prepare();
        var marker = File.ReadAllText(fixture.Target(".bundled-runtime.json"));
        File.WriteAllText(fixture.Source("nodejs-project/main.js"), "new host version");
        fixture.Manifest();
        using (var output = new FileStream(fixture.Helper("danmu-outbound.exe"), FileMode.Append)) output.WriteByte(42);
        Assert.Throws<IOException>(() => fixture.Prepare());
        Assert.Equal(marker, File.ReadAllText(fixture.Target(".bundled-runtime.json")));
        Assert.Equal("fixture-main.js", File.ReadAllText(fixture.Target("nodejs-project/main.js")));
    }

    [Fact]
    public void LegacyBareFixtureStillPreparesAndDoesNotClaimOutboundReadiness()
    {
        using var temp = new TemporaryDirectory();
        var bundle = Path.Combine(temp.Path, "bare");
        Directory.CreateDirectory(Path.Combine(bundle, "nodejs-project"));
        File.WriteAllText(Path.Combine(bundle, "node.exe"), "old fixture");
        File.WriteAllText(Path.Combine(bundle, "nodejs-project", "main.js"), "old host");
        WriteManifest(bundle);
        var paths = new AppPaths(Path.Combine(temp.Path, "runtime"));
        BundledRuntimePreparer.Prepare(bundle, paths);
        var ready = BundledRuntimePreparer.CheckReadiness(bundle, paths);
        Assert.Equal(BundledRuntimeReadinessStatus.Ready, ready.Status);
        Assert.Contains("未包含出站", ready.Reason);
    }

    [Theory]
    [InlineData("x64")]
    [InlineData("x86")]
    [InlineData("arm64")]
    public void BundleScriptAssemblesFreshOfflineBundleForEachPeArchitecture(string arch)
    {
        using var fixture = new Fixture(arch);
        var inputs = fixture.ScriptInputs();
        var destination = Path.Combine(fixture.Directory.Path, "fresh outbound " + arch);
        var result = RunBundleScript("-BaseBundle", inputs.Base, "-Destination", destination, "-Arch", arch,
            "-OutboundArtifact", inputs.Artifact, "-HostSource", inputs.Host);
        Assert.True(result.Code == 0, result.Output);
        Assert.False(Directory.Exists(Path.Combine(destination, "nodejs-project", "config")));
        Assert.False(Directory.Exists(Path.Combine(destination, "nodejs-project", "danmu_api_stable")));
        Assert.True(File.Exists(Path.Combine(destination, "nodejs-project", "outbound", "danmu-outbound.exe")));
        var identity = JsonSerializer.Deserialize<BundledRuntimePreparer.State>(File.ReadAllText(Path.Combine(destination, "runtime-build.json")))!;
        Assert.Equal(arch, identity.Arch);
        Assert.InRange(identity.Files.Count, 22, 56);
        Assert.All(identity.Files.Where(e => e.Path.Contains("app-outbound-", StringComparison.Ordinal) ||
            e.Path.EndsWith("danmu-outbound.exe", StringComparison.Ordinal) || e.Path.EndsWith("outbound-build.json", StringComparison.Ordinal) ||
            e.Path.EndsWith("OUTBOUND-SHA256SUMS.txt", StringComparison.Ordinal)), e => Assert.Equal("hash", e.Mode));
        Assert.DoesNotContain(File.ReadAllLines(Path.Combine(destination, "SHA256SUMS.txt")), line => line.Contains("/redis/", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(destination, "redis-payload.SHA256SUMS.txt")));
        result = RunBundleScript("-BaseBundle", destination, "-Arch", arch, "-ValidateOnly");
        Assert.True(result.Code == 0, result.Output);
        Assert.Contains("native execution=not performed", result.Output);
    }

    [Fact]
    public void DirectFileInvocationResolvesOmittedHostSourceFromScriptRepository()
    {
        using var fixture = new Fixture();
        var inputs = fixture.ScriptInputs();
        var isolatedRepo = Path.Combine(fixture.Directory.Path, "isolated script repo");
        var build = Path.Combine(isolatedRepo, "build");
        var defaultHost = Path.Combine(isolatedRepo, "runtime", "node-host");
        Directory.CreateDirectory(build);
        Directory.CreateDirectory(defaultHost);
        var originalScript = FindBundleScript();
        var script = Path.Combine(build, "New-OutboundRuntimeBundle.ps1");
        File.Copy(originalScript, script);
        File.Copy(Path.Combine(Path.GetDirectoryName(originalScript)!, "New-RuntimeBuildIdentity.ps1"), Path.Combine(build, "New-RuntimeBuildIdentity.ps1"));
        File.WriteAllText(Path.Combine(isolatedRepo, "Directory.Build.props"),
            $"<Project><PropertyGroup><DanmuBundledNodeVersion>{BundledNodeRuntime.ExpectedVersion}</DanmuBundledNodeVersion></PropertyGroup></Project>");
        foreach (var file in HostFiles) File.Copy(Path.Combine(inputs.Host, file), Path.Combine(defaultHost, file));
        var destination = Path.Combine(fixture.Directory.Path, "omitted HostSource output");
        // Launch a fresh PowerShell 5.1 process through -File, omitting HostSource entirely.
        var result = RunBundleScriptFromPath(script, "-BaseBundle", inputs.Base, "-Destination", destination,
            "-Arch", fixture.Arch, "-OutboundArtifact", inputs.Artifact);
        Assert.True(result.Code == 0, result.Output);
        foreach (var file in HostFiles) Assert.Equal(File.ReadAllBytes(Path.Combine(defaultHost, file)),
            File.ReadAllBytes(Path.Combine(destination, "nodejs-project", file)));
        result = RunBundleScriptFromPath(script, "-BaseBundle", destination, "-Arch", fixture.Arch, "-ValidateOnly");
        Assert.True(result.Code == 0, result.Output);
    }

    [Theory]
    [InlineData("existing")]
    [InlineData("wrongPE")]
    [InlineData("missingBridge")]
    [InlineData("helperHash")]
    [InlineData("baseHash")]
    [InlineData("dependencies")]
    [InlineData("foreignHelper")]
    public void BundleScriptRejectsBadInputsAndCleansOnlyItsOwnDestination(string kind)
    {
        using var fixture = new Fixture();
        var inputs = fixture.ScriptInputs();
        var destination = Path.Combine(fixture.Directory.Path, "failed output");
        if (kind == "existing") { Directory.CreateDirectory(destination); File.WriteAllText(Path.Combine(destination, "sentinel"), "keep"); }
        if (kind == "wrongPE") WritePe(Path.Combine(inputs.Artifact, "danmu-outbound.exe"), fixture.Arch == "x86" ? "x64" : "x86");
        if (kind == "missingBridge") File.Delete(Path.Combine(inputs.Host, "app-outbound-bridge.js"));
        if (kind == "helperHash") File.AppendAllText(Path.Combine(inputs.Artifact, "OUTBOUND-LICENSE.txt"), "tamper");
        if (kind == "baseHash") File.AppendAllText(Path.Combine(inputs.Base, "nodejs-project", "node_modules", "pkg", "index.js"), "tamper");
        if (kind == "dependencies") File.AppendAllText(Path.Combine(inputs.Host, "package.json"), "different dependency graph");
        if (kind == "foreignHelper") File.AppendAllText(Path.Combine(inputs.Artifact, "OUTBOUND-SHA256SUMS.txt"), new string('A', 64) + "  foreign.exe\n");
        var result = RunBundleScript("-BaseBundle", inputs.Base, "-Destination", destination, "-Arch", fixture.Arch,
            "-OutboundArtifact", inputs.Artifact, "-HostSource", inputs.Host);
        Assert.NotEqual(0, result.Code);
        Assert.True(Directory.Exists(inputs.Base));
        Assert.Equal("user-config", File.ReadAllText(Path.Combine(inputs.Base, "nodejs-project", "config", "outbound", "settings.json")));
        if (kind == "existing") Assert.Equal("keep", File.ReadAllText(Path.Combine(destination, "sentinel")));
        else Assert.False(Directory.Exists(destination), result.Output);
    }

    [Theory]
    [InlineData("nodejs-project/config/.env", false)]
    [InlineData("nodejs-project/danmu_api_stable/worker.js", false)]
    [InlineData("nodejs-project/outbound/foreign.exe", false)]
    [InlineData("nodejs-project/outbound/.hidden", true)]
    [InlineData("nodejs-project/node_modules/redis/foreign.js", false)]
    [InlineData("nodejs-project/node_modules/foreign-empty", true)]
    public void ReleaseStructureGateRejectsEveryUnlistedFileAndDirectoryIncludingHidden(string relative, bool hidden)
    {
        using var fixture = new Fixture();
        var inputs = fixture.ScriptInputs();
        var destination = Path.Combine(fixture.Directory.Path, "closed bundle");
        var result = RunBundleScript("-BaseBundle", inputs.Base, "-Destination", destination, "-Arch", fixture.Arch,
            "-OutboundArtifact", inputs.Artifact, "-HostSource", inputs.Host);
        Assert.True(result.Code == 0, result.Output);
        var path = Path.Combine(destination, relative);
        if (relative.EndsWith("foreign-empty", StringComparison.Ordinal)) Directory.CreateDirectory(path);
        else { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "unlisted test fixture"); }
        if (hidden) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        result = RunBundleScript("-BaseBundle", destination, "-Arch", fixture.Arch, "-ValidateOnly");
        Assert.NotEqual(0, result.Code);
        Assert.Contains("Unlisted runtime", result.Output);
    }

    [Fact]
    public async Task ReleaseStructureGateRejectsDirectoryJunctionBeforeFollowingIt()
    {
        using var fixture = new Fixture();
        var inputs = fixture.ScriptInputs();
        var destination = Path.Combine(fixture.Directory.Path, "junction bundle");
        var result = RunBundleScript("-BaseBundle", inputs.Base, "-Destination", destination, "-Arch", fixture.Arch,
            "-OutboundArtifact", inputs.Artifact, "-HostSource", inputs.Host);
        Assert.True(result.Code == 0, result.Output);
        var target = Path.Combine(fixture.Directory.Path, "junction target");
        Directory.CreateDirectory(target);
        var junction = Path.Combine(destination, "nodejs-project", "node_modules", "junction");
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + junction.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' | Out-Null" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await stdout + await stderr);
            result = RunBundleScript("-BaseBundle", destination, "-Arch", fixture.Arch, "-ValidateOnly");
            Assert.NotEqual(0, result.Code);
            Assert.Contains("Reparse point forbidden", result.Output);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await process.WaitForExitAsync(cleanupTimeout.Token);
            }
            // Remove only the junction itself, never recursively follow its target.
            if (Directory.Exists(junction)) Directory.Delete(junction);
        }
    }

    [Theory]
    [InlineData("tamper")]
    [InlineData("missing")]
    [InlineData("overlap")]
    [InlineData("foreign")]
    public void OptionalRedisPayloadIsVerifiedByTheFinalReleaseGate(string kind)
    {
        using var fixture = new Fixture();
        var inputs = fixture.ScriptInputs();
        var destination = Path.Combine(fixture.Directory.Path, "redis bundle");
        var result = RunBundleScript("-BaseBundle", inputs.Base, "-Destination", destination, "-Arch", fixture.Arch,
            "-OutboundArtifact", inputs.Artifact, "-HostSource", inputs.Host);
        Assert.True(result.Code == 0, result.Output);
        var redis = Path.Combine(destination, "nodejs-project", "node_modules", "redis", "index.js");
        var manifest = Path.Combine(destination, "redis-payload.SHA256SUMS.txt");
        if (kind == "tamper") File.AppendAllText(redis, "changed");
        if (kind == "missing") File.Delete(redis);
        if (kind == "overlap") File.WriteAllText(manifest, Digest(File.ReadAllBytes(Path.Combine(destination, "nodejs-project", "node_modules", "pkg", "index.js"))) + "  pkg/index.js\n");
        if (kind == "foreign") File.WriteAllText(manifest, new string('A', 64) + "  foreign/input.js\n");
        result = RunBundleScript("-BaseBundle", destination, "-Arch", fixture.Arch, "-ValidateOnly");
        Assert.NotEqual(0, result.Code);
    }

    [Fact]
    public void ReleaseScriptProjectsManifestFilesAndRevalidatesAfterPublishWithoutRecursiveRuntimeCopy()
    {
        var path = Path.Combine(Path.GetDirectoryName(FindBundleScript())!, "Build-WindowsRelease.ps1");
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("Copy-Item -LiteralPath $RuntimeBundle", text);
        Assert.Contains("foreach($relative in $runtimeFiles)", text);
        Assert.Contains("redis-payload.SHA256SUMS.txt", text);
        Assert.True(text.Split("-ValidateOnly", StringSplitOptions.None).Length >= 5);
    }

    [Fact]
    public void ReleaseStructureGateRejectsLegacyBundleWithoutOutboundFeature()
    {
        using var fixture = new Fixture();
        var inputs = fixture.ScriptInputs();
        var result = RunBundleScript("-BaseBundle", inputs.Base, "-Arch", fixture.Arch, "-ValidateOnly");
        Assert.NotEqual(0, result.Code);
        Assert.Contains("incomplete", result.Output);
    }

    private static string FindBundleScript()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "build", "New-OutboundRuntimeBundle.ps1"))) current = current.Parent;
        Assert.NotNull(current);
        return Path.Combine(current!.FullName, "build", "New-OutboundRuntimeBundle.ps1");
    }

    private static (int Code, string Output) RunBundleScript(params string[] arguments) =>
        RunBundleScriptFromPath(FindBundleScript(), arguments);

    private static (int Code, string Output) RunBundleScriptFromPath(string script, params string[] arguments)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NonInteractive", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            Assert.True(process.WaitForExit(60000), "Bundle script timed out");
            return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        }
        finally { if (!process.HasExited) { process.Kill(true); process.WaitForExit(); } }
    }

    private sealed class Fixture : IDisposable
    {
        public TemporaryDirectory Directory { get; } = new();
        public string Bundle { get; }
        public AppPaths Paths { get; }
        public string Arch { get; }
        public Fixture(string? arch = null)
        {
            Arch = arch ?? BundledRuntimePreparer.HostArchitecture();
            Bundle = Path.Combine(Directory.Path, "bundle");
            Paths = new AppPaths(Path.Combine(Directory.Path, "isolated-runtime"));
            System.IO.Directory.CreateDirectory(Path.Combine(Bundle, "nodejs-project", "outbound"));
            WritePe(Source("node.exe"), Arch);
            File.WriteAllText(Source("NODE-LICENSE.txt"), "node-license");
            foreach (var file in HostFiles) File.WriteAllText(Source("nodejs-project/" + file), "fixture-" + file);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Source("nodejs-project/node_modules/pkg/index.js"))!);
            File.WriteAllText(Source("nodejs-project/node_modules/pkg/index.js"), "locked dependency");
            WritePe(Helper("danmu-outbound.exe"), Arch);
            foreach (var file in new[] { "OUTBOUND-LICENSE.txt", "OUTBOUND-UPSTREAM.txt", "OUTBOUND-NOTICES.txt" }) File.WriteAllText(Helper(file), "fixture-" + file);
            using (var zip = ZipFile.Open(Helper("outbound-source.zip"), ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("runtime/outbound/src/main.go");
                using var output = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                output.Write("package main\n");
            }
            CreateMetadata();
            HelperManifest();
            Manifest();
        }
        public string Source(string path) => Path.Combine(Bundle, path);
        public string Helper(string file) => Source("nodejs-project/outbound/" + file);
        public string Target(string path) => Path.Combine(Paths.RuntimeDirectory, path);
        public void Manifest() => WriteManifest(Bundle, Arch);
        public void HelperManifest() => File.WriteAllLines(Helper("OUTBOUND-SHA256SUMS.txt"),
            HelperFiles.Where(f => f != "OUTBOUND-SHA256SUMS.txt" && File.Exists(Helper(f))).Select(f => Digest(File.ReadAllBytes(Helper(f))).ToUpperInvariant() + "  " + f));
        public void CreateMetadata()
        {
            var sourceHash = Digest(Encoding.UTF8.GetBytes("package main\n"));
            var canonical = sourceHash + "  runtime/outbound/src/main.go\n";
            File.WriteAllText(Helper("outbound-build.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, version = "1.0.1+6004732", protocolVersion = 1, runtimeIdentifier = "win-" + Arch,
                goVersion = "go1.26.0", sourceCommit = "600473250a07d0f78502262d141e0f3faf4a9a36",
                sourceSha256 = Digest(Encoding.UTF8.GetBytes(canonical)), executableSha256 = Digest(File.ReadAllBytes(Helper("danmu-outbound.exe"))),
                machine = Machine(Arch), toolchainArchiveSha256 = "9bbe0fc64236b2b51f6255c05c4232532b8ecc0e6d2e00950bd3021d8a4d07d4",
                toolchainArchiveUrl = "https://go.dev/dl/go1.26.0.windows-amd64.zip", toolchainArchiveBytes = 74815266,
                toolchainInputCount = 100, toolchainInputSha256 = new string('a', 64), toolchainDriverSha256 = new string('b', 64),
                toolchainCompilerSha256 = new string('c', 64), toolchainLinkerSha256 = new string('d', 64), toolchainStandardLibrarySha256 = new string('e', 64),
                goEnvironment = new { GOROOT = "verified-official-archive-root", GOTOOLDIR = "pkg/tool/windows_amd64", GOENV = "off", GOTOOLCHAIN = "local" },
                sourceInputs = new[] { new { path = "runtime/outbound/src/main.go", sha256 = sourceHash } },
                dependencies = Array.Empty<object>(), buildFlags = new[] { "-trimpath" }
            }));
        }
        public void ChangeMetadata(string key, JsonNode? value)
        {
            var metadata = JsonNode.Parse(File.ReadAllText(Helper("outbound-build.json")))!;
            metadata[key] = value;
            File.WriteAllText(Helper("outbound-build.json"), metadata.ToJsonString());
        }
        public BundledRuntimePreparationResult Prepare(Action<BundledRuntimeProgress>? progress = null) =>
            BundledRuntimePreparer.Prepare(Bundle, Paths, () => true, progress: progress);
        public (string Base, string Artifact, string Host) ScriptInputs()
        {
            var baseDir = Path.Combine(Directory.Path, "baseline");
            var artifact = Path.Combine(Directory.Path, "native");
            var host = Path.Combine(Directory.Path, "authoritative-host");
            System.IO.Directory.CreateDirectory(Path.Combine(baseDir, "nodejs-project", "node_modules", "pkg"));
            System.IO.Directory.CreateDirectory(artifact);
            System.IO.Directory.CreateDirectory(host);
            foreach (var file in HostFiles)
            {
                File.Copy(Source("nodejs-project/" + file), Path.Combine(host, file));
                if (!file.StartsWith("app-outbound-", StringComparison.Ordinal)) File.Copy(Source("nodejs-project/" + file), Path.Combine(baseDir, "nodejs-project", file));
            }
            File.Copy(Source("node.exe"), Path.Combine(baseDir, "node.exe"));
            File.Copy(Source("NODE-LICENSE.txt"), Path.Combine(baseDir, "NODE-LICENSE.txt"));
            File.Copy(Source("nodejs-project/node_modules/pkg/index.js"), Path.Combine(baseDir, "nodejs-project", "node_modules", "pkg", "index.js"));
            foreach (var file in HelperFiles) File.Copy(Helper(file), Path.Combine(artifact, file));
            WriteManifest(baseDir, Arch);
            var config = Path.Combine(baseDir, "nodejs-project", "config", "outbound", "settings.json");
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            File.WriteAllText(config, "user-config");
            var core = Path.Combine(baseDir, "nodejs-project", "danmu_api_stable");
            System.IO.Directory.CreateDirectory(core);
            File.WriteAllText(Path.Combine(core, "worker.js"), "user-core");
            var redis = Path.Combine(baseDir, "nodejs-project", "node_modules", "redis", "index.js");
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(redis)!);
            File.WriteAllText(redis, "optional redis");
            File.WriteAllText(Path.Combine(baseDir, "redis-payload.SHA256SUMS.txt"), Digest(File.ReadAllBytes(redis)).ToUpperInvariant() + "  redis/index.js\n");
            return (baseDir, artifact, host);
        }
        public void Dispose() => Directory.Dispose();
    }

    private static int Machine(string arch) => arch switch { "x64" => 0x8664, "x86" => 0x014c, "arm64" => 0xaa64, _ => throw new ArgumentException(arch) };
    private static void WritePe(string file, string arch)
    {
        var bytes = new byte[160];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BitConverter.GetBytes(64).CopyTo(bytes, 0x3c);
        bytes[64] = (byte)'P'; bytes[65] = (byte)'E';
        BitConverter.GetBytes((ushort)Machine(arch)).CopyTo(bytes, 68);
        File.WriteAllBytes(file, bytes);
    }
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void WriteManifest(string bundle, string? arch = null)
    {
        var entries = System.IO.Directory.GetFiles(bundle, "*", SearchOption.AllDirectories)
            .Where(file => Path.GetFileName(file) is not ("SHA256SUMS.txt" or "runtime-build.json"))
            .Select(file => new BundledRuntimePreparer.Entry(Path.GetRelativePath(bundle, file).Replace('\\', '/'), Digest(File.ReadAllBytes(file)).ToUpperInvariant())).ToList();
        File.WriteAllLines(Path.Combine(bundle, "SHA256SUMS.txt"), entries.Select(e => e.Hash + "  " + e.Path));
        var version = Digest(Encoding.UTF8.GetBytes(string.Join('\n', entries.OrderBy(e => e.Path, StringComparer.Ordinal).Select(e => e.Hash + "  " + e.Path)))).ToUpperInvariant();
        var critical = entries.Where(e => !e.Path.Contains("/node_modules/", StringComparison.Ordinal)).Select(e => e with { Mode = "hash" }).ToList();
        File.WriteAllText(Path.Combine(bundle, "runtime-build.json"), JsonSerializer.Serialize(new BundledRuntimePreparer.State(1, version, critical, BundledNodeRuntime.ExpectedVersion, arch ?? BundledRuntimePreparer.HostArchitecture())));
    }
}
