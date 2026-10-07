using System.IO.Compression;
using DanmuApi.App.Services;
using DanmuApi.Platform;

namespace DanmuApi.Tests;

public sealed class PortableApplicationUpdateTests
{
    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(content);
    }

    private static void WriteInstalledFile(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void ExtractChangedSkipsFilesAlreadyIdenticalToTheInstalledCopy()
    {
        using var directory = new TemporaryDirectory();
        var target = Path.Combine(directory.Path, "app");
        var stage = Path.Combine(directory.Path, "stage");
        var zip = Path.Combine(directory.Path, "update.zip");
        var package = new (string Name, string Content)[]
        {
            ("DanmuApi.App.exe", "new-exe"),
            ("libSkiaSharp.dll", "unchanged-dll"),
            ("git/cmd/git.exe", "unchanged-git"),
            ("runtime-bundle/SHA256SUMS.txt", "unchanged-sums"),
        };
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            foreach (var (name, content) in package) WriteEntry(archive, name, content);
        // 已安装副本与包内逐字节相同，只有主程序不同。
        Directory.CreateDirectory(target);
        WriteInstalledFile(target, "DanmuApi.App.exe", "old-exe");
        WriteInstalledFile(target, "libSkiaSharp.dll", "unchanged-dll");
        WriteInstalledFile(target, Path.Combine("git", "cmd", "git.exe"), "unchanged-git");
        WriteInstalledFile(target, Path.Combine("runtime-bundle", "SHA256SUMS.txt"), "unchanged-sums");

        var result = PortableApplicationUpdate.ExtractChanged(zip, stage, target);

        Assert.Equal(["DanmuApi.App.exe"], result.Changed);
        Assert.Equal(
            [Path.Combine("libSkiaSharp.dll"), Path.Combine("git", "cmd", "git.exe"), Path.Combine("runtime-bundle", "SHA256SUMS.txt")],
            result.Unchanged);
        Assert.True(File.Exists(Path.Combine(stage, "DanmuApi.App.exe")));
        Assert.False(File.Exists(Path.Combine(stage, "libSkiaSharp.dll")));
        Assert.False(File.Exists(Path.Combine(stage, "git", "cmd", "git.exe")));
        Assert.False(File.Exists(Path.Combine(stage, "runtime-bundle", "SHA256SUMS.txt")));
    }

    [Fact]
    public void ExtractChangedRepairsInstalledFileWithSameLengthButDifferentContent()
    {
        using var directory = new TemporaryDirectory();
        var target = Path.Combine(directory.Path, "app");
        var stage = Path.Combine(directory.Path, "stage");
        var zip = Path.Combine(directory.Path, "update.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "DanmuApi.App.exe", "BBBB");
            WriteEntry(archive, "git/cmd/git.exe", "unchanged-git");
            WriteEntry(archive, "runtime-bundle/SHA256SUMS.txt", "unchanged-sums");
        }
        Directory.CreateDirectory(target);
        WriteInstalledFile(target, "DanmuApi.App.exe", "AAAA");
        WriteInstalledFile(target, Path.Combine("git", "cmd", "git.exe"), "unchanged-git");
        WriteInstalledFile(target, Path.Combine("runtime-bundle", "SHA256SUMS.txt"), "unchanged-sums");

        var result = PortableApplicationUpdate.ExtractChanged(zip, stage, target);

        Assert.Equal(["DanmuApi.App.exe"], result.Changed);
        Assert.Equal("BBBB", File.ReadAllText(Path.Combine(stage, "DanmuApi.App.exe")));
    }

    [Fact]
    public void ExtractWithoutInstalledDirectoryStillReturnsEveryFile()
    {
        using var directory = new TemporaryDirectory();
        var zip = Path.Combine(directory.Path, "update.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "DanmuApi.App.exe", "exe");
            WriteEntry(archive, "git/cmd/git.exe", "git");
            WriteEntry(archive, "runtime-bundle/SHA256SUMS.txt", "sums");
        }
        var result = PortableApplicationUpdate.ExtractChanged(zip, Path.Combine(directory.Path, "stage"), null);
        Assert.Equal(3, result.Changed.Count);
        Assert.Empty(result.Unchanged);
    }

    [Fact]
    public void ExtractChangedRejectsAJunctionInTheInstalledTreeEvenWhenContentMatches()
    {
        using var directory = new TemporaryDirectory();
        using var external = new TemporaryDirectory();
        var target = Path.Combine(directory.Path, "app");
        var stage = Path.Combine(directory.Path, "stage");
        var zip = Path.Combine(directory.Path, "update.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "DanmuApi.App.exe", "exe");
            WriteEntry(archive, "git/cmd/git.exe", "git");
            WriteEntry(archive, "runtime-bundle/SHA256SUMS.txt", "sums");
            WriteEntry(archive, "runtime-bundle/nodejs-project/main.js", "main");
        }
        Directory.CreateDirectory(target);
        WriteInstalledFile(target, "DanmuApi.App.exe", "exe");
        WriteInstalledFile(target, Path.Combine("git", "cmd", "git.exe"), "git");
        WriteInstalledFile(target, Path.Combine("runtime-bundle", "SHA256SUMS.txt"), "sums");
        // 链接目标里的内容与更新包一致：只有重解析点检查能挡住它，内容比对挡不住。
        WriteInstalledFile(external.Path, "main.js", "main");
        var link = Path.Combine(target, "runtime-bundle", "nodejs-project");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{link}\" \"{external.Path}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output);
        try
        {
            var error = Assert.Throws<IOException>(() => PortableApplicationUpdate.ExtractChanged(zip, stage, target));
            Assert.Contains("链接", error.Message);
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void PackageWithoutANewMainExecutableIsRejectedInsteadOfBecomingAnEmptyUpdate()
    {
        var error = Assert.Throws<IOException>(() => ApplicationUpdateHelper.SelectPortableReplacementFiles(
            new PortableApplicationUpdate.ExtractResult([Path.Combine("runtime-bundle", "nodejs-project", "main.js")], ["DanmuApi.App.exe"])));
        Assert.Contains("主程序", error.Message);
        var files = ApplicationUpdateHelper.SelectPortableReplacementFiles(
            new PortableApplicationUpdate.ExtractResult(["DanmuApi.App.exe", "libSkiaSharp.dll"], []));
        Assert.Equal(["DanmuApi.App.exe", "libSkiaSharp.dll"], files);
    }

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
