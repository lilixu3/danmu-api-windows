using System.IO.Compression;
using DanmuApi.Platform;

namespace DanmuApi.Tests;

public sealed class PortableApplicationUpdateTests
{
    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("C:/escape.exe")]
    [InlineData("runtime-bundle/../escape.exe")]
    [InlineData("runtime-bundle/test:stream")]
    public void RejectsUnsafeArchiveNames(string name)
    {
        using var directory = new TemporaryDirectory();
        var zip = Path.Combine(directory.Path, "update.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write("bad");
        Assert.Throws<IOException>(() => PortableApplicationUpdate.Extract(zip, Path.Combine(directory.Path, "stage")));
    }

    [Fact]
    public void ExtractIncludesBundledGitAndRejectsPackageWithoutIt()
    {
        using var directory = new TemporaryDirectory();
        var zip = Path.Combine(directory.Path, "update.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "DanmuApi.App.exe", "runtime-bundle/SHA256SUMS.txt", "git/cmd/git.exe", "git/mingw64/bin/git-remote-https.exe" })
            using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write(name);
        }
        var files = PortableApplicationUpdate.Extract(zip, Path.Combine(directory.Path, "stage"));
        Assert.Contains(Path.Combine("git", "cmd", "git.exe"), files);
        Assert.True(File.Exists(Path.Combine(directory.Path, "stage", "git", "mingw64", "bin", "git-remote-https.exe")));

        using (var archive = ZipFile.Open(Path.Combine(directory.Path, "missing.zip"), ZipArchiveMode.Create))
        {
            archive.CreateEntry("DanmuApi.App.exe");
            archive.CreateEntry("runtime-bundle/SHA256SUMS.txt");
        }
        var error = Assert.Throws<IOException>(() => PortableApplicationUpdate.Extract(
            Path.Combine(directory.Path, "missing.zip"), Path.Combine(directory.Path, "missing-stage")));
        Assert.Contains("随包 Git", error.Message);
    }

    [Fact]
    public void ReplacementAndRecoveryRestoreBundledGit()
    {
        using var directory = new TemporaryDirectory();
        var target = Path.Combine(directory.Path, "app"); var stage = Path.Combine(directory.Path, "stage");
        var backup = Path.Combine(directory.Path, "backup");
        var git = Path.Combine("git", "cmd", "git.exe");
        Directory.CreateDirectory(Path.Combine(target, "git", "cmd"));
        Directory.CreateDirectory(Path.Combine(stage, "git", "cmd"));
        File.WriteAllText(Path.Combine(target, git), "old-git");
        File.WriteAllText(Path.Combine(stage, git), "new-git");
        PortableApplicationUpdate.Replace(target, stage, backup, [git]);
        Assert.Equal("new-git", File.ReadAllText(Path.Combine(target, git)));
        PortableApplicationUpdate.Recover(target, backup);
        Assert.Equal("old-git", File.ReadAllText(Path.Combine(target, git)));
    }

    [Fact]
    public void MidReplacementFailureRestoresOldFilesAndPreservesUnmanagedFiles()
    {
        using var directory = new TemporaryDirectory();
        var target = Path.Combine(directory.Path, "app");
        var stage = Path.Combine(directory.Path, "stage");
        Directory.CreateDirectory(target); Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(target, "DanmuApi.App.exe"), "old");
        File.WriteAllText(Path.Combine(target, "notes.txt"), "mine");
        File.WriteAllText(Path.Combine(stage, "DanmuApi.App.exe"), "new");
        Assert.Throws<FileNotFoundException>(() => PortableApplicationUpdate.Replace(target, stage,
            Path.Combine(directory.Path, "backup"), ["DanmuApi.App.exe", "libSkiaSharp.dll"]));
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "DanmuApi.App.exe")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(target, "notes.txt")));
    }

    [Fact]
    public void CompleteReplacementCanBeRestoredWithoutDeletingUserFiles()
    {
        using var directory = new TemporaryDirectory();
        var target = Path.Combine(directory.Path, "app"); var stage = Path.Combine(directory.Path, "stage"); var backup = Path.Combine(directory.Path, "backup");
        Directory.CreateDirectory(target); Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(target, "DanmuApi.App.exe"), "old");
        File.WriteAllText(Path.Combine(stage, "DanmuApi.App.exe"), "new");
        File.WriteAllText(Path.Combine(stage, "libSkiaSharp.dll"), "newdll");
        PortableApplicationUpdate.Replace(target, stage, backup, ["DanmuApi.App.exe", "libSkiaSharp.dll"]);
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "DanmuApi.App.exe")));
        PortableApplicationUpdate.Restore(target, backup, File.ReadAllLines(Path.Combine(backup,"replaced-files.txt")), File.ReadAllLines(Path.Combine(backup,"original-files.txt")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "DanmuApi.App.exe")));
        Assert.False(File.Exists(Path.Combine(target,"libSkiaSharp.dll")));
    }
}
