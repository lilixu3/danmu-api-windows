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
