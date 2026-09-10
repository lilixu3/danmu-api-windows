using System.IO.Compression;
using System.Text;
using DanmuApi.Core;

namespace DanmuApi.Tests;

public sealed class CoreInstallerTests
{
    [Fact]
    public async Task InstallsNestedCoreAndPreservesExternalUserData()
    {
        using var directory = new TemporaryDirectory();
        var project = Path.Combine(directory.Path, "runtime", "nodejs-project");
        var cache = Path.Combine(directory.Path, "core-cache");
        Directory.CreateDirectory(Path.Combine(project, "config"));
        Directory.CreateDirectory(Path.Combine(project, "logs"));
        var env = Path.Combine(project, "config", ".env");
        var log = Path.Combine(project, "logs", "sentinel.log");
        File.WriteAllText(env, "TOKEN=keep-me");
        File.WriteAllText(log, "keep-log");
        var archive = CreateCoreArchive(directory.Path, "1.2.3", "new-core");
        var installer = new CoreInstaller(project, cache, new CopyingDownloader(archive));

        var result = await installer.InstallAsync(Request("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));

        Assert.True(result.IsValid);
        Assert.Equal("1.2.3", result.Version);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", result.Manifest!.CommitSha);
        Assert.Equal("TOKEN=keep-me", File.ReadAllText(env));
        Assert.Equal("keep-log", File.ReadAllText(log));
        Assert.Equal("new-core", File.ReadAllText(Path.Combine(result.Directory, "payload.txt")));
        Assert.Contains("\"type\": \"module\"", File.ReadAllText(Path.Combine(result.Directory, "package.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateArchivesPreviousCoreAndHistoryCanBeRestored()
    {
        using var directory = new TemporaryDirectory();
        var project = Path.Combine(directory.Path, "project");
        var cache = Path.Combine(directory.Path, "cache");
        var firstArchive = CreateCoreArchive(directory.Path, "1.0.0", "first");
        var downloader = new SwitchingDownloader(firstArchive);
        var installer = new CoreInstaller(project, cache, downloader);
        await installer.InstallAsync(Request("1111111111111111111111111111111111111111"));
        downloader.ArchivePath = CreateCoreArchive(directory.Path, "2.0.0", "second");

        var updated = await installer.InstallAsync(Request("2222222222222222222222222222222222222222"));
        var history = installer.GetHistory(ManagedCoreVariant.Stable);

        Assert.Equal("second", File.ReadAllText(Path.Combine(updated.Directory, "payload.txt")));
        var previous = Assert.Single(history);
        Assert.Equal("1111111111111111111111111111111111111111", previous.Manifest.CommitSha);

        var restored = await installer.RestoreHistoryAsync(ManagedCoreVariant.Stable, previous.Id);

        Assert.Equal(CoreInstallKind.Rollback, restored.Manifest!.InstallKind);
        Assert.Equal("1111111111111111111111111111111111111111", restored.Manifest.CommitSha);
        Assert.Equal("first", File.ReadAllText(Path.Combine(restored.Directory, "payload.txt")));
    }

    [Fact]
    public async Task ZipSlipIsRejectedWithoutCreatingEscapedFile()
    {
        using var directory = new TemporaryDirectory();
        var archive = Path.Combine(directory.Path, "malicious.zip");
        using (var file = File.Create(archive))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "owner-repo/root/../../escaped.txt", new string('x', 2048));
        }
        var project = Path.Combine(directory.Path, "project");
        var installer = new CoreInstaller(project, Path.Combine(directory.Path, "cache"), new CopyingDownloader(archive));

        var error = await Assert.ThrowsAsync<IOException>(() =>
            installer.InstallAsync(Request("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")));

        Assert.Contains("非法路径", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(project, "escaped.txt")));
        Assert.False(installer.Inspect(ManagedCoreVariant.Stable).IsInstalled);
    }

    [Fact]
    public async Task MissingRequiredFileIsRejectedBeforeReplacingOldCore()
    {
        using var directory = new TemporaryDirectory();
        var project = Path.Combine(directory.Path, "project");
        var cache = Path.Combine(directory.Path, "cache");
        var first = CreateCoreArchive(directory.Path, "1.0.0", "old");
        var downloader = new SwitchingDownloader(first);
        var installer = new CoreInstaller(project, cache, downloader);
        await installer.InstallAsync(Request("1111111111111111111111111111111111111111"));
        downloader.ArchivePath = CreateCoreArchive(directory.Path, "2.0.0", "bad", includeEnvs: false);

        var error = await Assert.ThrowsAsync<IOException>(() =>
            installer.InstallAsync(Request("2222222222222222222222222222222222222222")));

        Assert.Contains("envs.js", error.Message, StringComparison.Ordinal);
        Assert.Equal("old", File.ReadAllText(Path.Combine(project, "danmu_api_stable", "payload.txt")));
        Assert.Equal("1111111111111111111111111111111111111111", installer.Inspect(ManagedCoreVariant.Stable).Manifest!.CommitSha);
    }

    [Fact]
    public async Task FailureAtReplacingStageLeavesOldCoreUntouched()
    {
        using var directory = new TemporaryDirectory();
        var project = Path.Combine(directory.Path, "project");
        var cache = Path.Combine(directory.Path, "cache");
        var first = CreateCoreArchive(directory.Path, "1.0.0", "old");
        var downloader = new SwitchingDownloader(first);
        var installer = new CoreInstaller(project, cache, downloader);
        await installer.InstallAsync(Request("1111111111111111111111111111111111111111"));
        downloader.ArchivePath = CreateCoreArchive(directory.Path, "2.0.0", "new");
        var failing = new CoreInstaller(project, cache, downloader, stage =>
        {
            if (stage == CoreInstallStage.Replacing)
            {
                throw new IOException("injected replacement failure");
            }
        });

        var error = await Assert.ThrowsAsync<IOException>(() =>
            failing.InstallAsync(Request("2222222222222222222222222222222222222222")));

        Assert.Contains("injected replacement failure", error.Message, StringComparison.Ordinal);
        Assert.Equal("old", File.ReadAllText(Path.Combine(project, "danmu_api_stable", "payload.txt")));
    }

    [Fact]
    public async Task DeleteRemovesOnlyManagedCoreDirectory()
    {
        using var directory = new TemporaryDirectory();
        var project = Path.Combine(directory.Path, "project");
        var config = Path.Combine(project, "config");
        Directory.CreateDirectory(config);
        var env = Path.Combine(config, ".env");
        File.WriteAllText(env, "TOKEN=preserved");
        var firstArchive = CreateCoreArchive(directory.Path, "1.0.0", "core-one");
        var downloader = new SwitchingDownloader(firstArchive);
        var installer = new CoreInstaller(
            project,
            Path.Combine(directory.Path, "cache"),
            downloader);
        await installer.InstallAsync(Request("1111111111111111111111111111111111111111"));
        downloader.ArchivePath = CreateCoreArchive(directory.Path, "2.0.0", "core-two");
        await installer.InstallAsync(Request("2222222222222222222222222222222222222222"));
        Assert.NotEmpty(installer.GetHistory(ManagedCoreVariant.Stable));

        installer.Delete(ManagedCoreVariant.Stable);

        Assert.False(Directory.Exists(Path.Combine(project, "danmu_api_stable")));
        Assert.Equal("TOKEN=preserved", File.ReadAllText(env));
        Assert.NotEmpty(installer.GetHistory(ManagedCoreVariant.Stable));
    }

    [Fact]
    public void UnknownDownloadRouteIsRejected()
    {
        Assert.Throws<ArgumentException>(() => GithubProxyCatalog.GetById("missing-route"));
    }

    private static CoreInstallRequest Request(string sha) => new(
        ManagedCoreVariant.Stable,
        GithubRepositoryReference.Official("main"),
        "main",
        sha,
        "稳定核心",
        CoreInstallKind.Branch,
        GithubProxyCatalog.OriginalId);

    private static string CreateCoreArchive(
        string root,
        string version,
        string payload,
        bool includeEnvs = true)
    {
        var path = Path.Combine(root, $"core-{Guid.NewGuid():N}.zip");
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        WriteEntry(zip, "owner-repo/package.json", "{\"name\":\"fixture\",\"padding\":\"" + new string('p', 2048) + "\"}");
        WriteEntry(zip, "owner-repo/danmu_api/worker.js", "export default {};\n");
        if (includeEnvs)
        {
            WriteEntry(zip, "owner-repo/danmu_api/configs/envs.js", "export const envVarConfig = {};\n");
        }
        WriteEntry(zip, "owner-repo/danmu_api/configs/globals.js", $"export const VERSION = '{version}';\n");
        WriteEntry(zip, "owner-repo/danmu_api/payload.txt", payload);
        return path;
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private sealed class CopyingDownloader(string archivePath) : IGithubFileDownloader
    {
        public Task DownloadAsync(
            Uri originalUri,
            string proxyId,
            string targetPath,
            IProgress<GithubDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(archivePath, targetPath);
            progress?.Report(new GithubDownloadProgress(
                new FileInfo(targetPath).Length,
                new FileInfo(targetPath).Length,
                "test",
                originalUri));
            return Task.CompletedTask;
        }
    }

    private sealed class SwitchingDownloader(string archivePath) : IGithubFileDownloader
    {
        public string ArchivePath { get; set; } = archivePath;

        public Task DownloadAsync(
            Uri originalUri,
            string proxyId,
            string targetPath,
            IProgress<GithubDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(ArchivePath, targetPath);
            return Task.CompletedTask;
        }
    }
}
