using System.Text.Json.Nodes;
using DanmuApi.Platform;

namespace DanmuApi.Tests;

public sealed class PortableApplicationUpdateJournalTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory directory = new();
        public string Target => Path.Combine(directory.Path, "app");
        public string Stage => Path.Combine(directory.Path, "stage");
        public string Backup => Path.Combine(directory.Path, "backup");
        public readonly string[] Files = ["DanmuApi.App.exe", "libSkiaSharp.dll", Path.Combine("runtime-bundle", "main.js")];
        public Fixture()
        {
            Directory.CreateDirectory(Target); Directory.CreateDirectory(Stage);
            Directory.CreateDirectory(Path.Combine(Stage, "runtime-bundle"));
            Directory.CreateDirectory(Path.Combine(Target, "config"));
            File.WriteAllText(Path.Combine(Target, "DanmuApi.App.exe"), "old-exe");
            File.WriteAllText(Path.Combine(Target, "config", ".env"), "user-settings");
            File.WriteAllText(Path.Combine(Target, "notes.txt"), "user-notes");
            foreach (var file in Files) File.WriteAllText(Path.Combine(Stage, file), "new-" + file);
        }
        public void AssertRestored()
        {
            Assert.Equal("old-exe", File.ReadAllText(Path.Combine(Target, "DanmuApi.App.exe")));
            Assert.False(File.Exists(Path.Combine(Target, "libSkiaSharp.dll")));
            Assert.False(File.Exists(Path.Combine(Target, "runtime-bundle", "main.js")));
            Assert.Equal("user-settings", File.ReadAllText(Path.Combine(Target, "config", ".env")));
            Assert.Equal("user-notes", File.ReadAllText(Path.Combine(Target, "notes.txt")));
        }
        public void Dispose() => directory.Dispose();
    }

    [Theory]
    [InlineData("journal-created")]
    [InlineData("backed-up:DanmuApi.App.exe")]
    [InlineData("replace-intent:DanmuApi.App.exe")]
    [InlineData("file-replaced:DanmuApi.App.exe")]
    [InlineData("installed:DanmuApi.App.exe")]
    [InlineData("file-replaced:libSkiaSharp.dll")]
    [InlineData("files-committed")]
    public void ProcessInterruptionAtDurableBoundaryRecoversRepeatedly(string boundary)
    {
        using var fixture = new Fixture();
        using var snapshot = new TemporaryDirectory();
        var captured = false;
        PortableApplicationUpdate.Replace(fixture.Target, fixture.Stage, fixture.Backup, fixture.Files, step =>
        {
            if (step != boundary) return;
            CopyTree(fixture.Target, Path.Combine(snapshot.Path, "app"));
            CopyTree(fixture.Backup, Path.Combine(snapshot.Path, "backup"));
            File.Copy(PortableApplicationUpdate.JournalPath(fixture.Backup), Path.Combine(snapshot.Path, "journal.json"));
            captured = true;
        });
        Assert.True(captured);
        // Recreate exactly the durable filesystem image at interruption; in-memory rollback state is absent.
        Directory.Delete(fixture.Target, true); Directory.Delete(fixture.Backup, true);
        CopyTree(Path.Combine(snapshot.Path, "app"), fixture.Target);
        CopyTree(Path.Combine(snapshot.Path, "backup"), fixture.Backup);
        File.Copy(Path.Combine(snapshot.Path, "journal.json"), PortableApplicationUpdate.JournalPath(fixture.Backup), true);
        PortableApplicationUpdate.Recover(fixture.Target, fixture.Backup);
        PortableApplicationUpdate.Recover(fixture.Target, fixture.Backup);
        fixture.AssertRestored();
    }

    [Theory]
    [InlineData("file-replaced:DanmuApi.App.exe")]
    [InlineData("file-replaced:libSkiaSharp.dll")]
    [InlineData("files-committed")]
    public void OrdinaryExceptionAutomaticallyRestoresAndStillFails(string boundary)
    {
        using var fixture = new Fixture();
        var error = Assert.Throws<IOException>(() => PortableApplicationUpdate.Replace(fixture.Target, fixture.Stage, fixture.Backup, fixture.Files,
            step => { if (step == boundary) throw new IOException("injected IO failure"); }));
        Assert.Contains("原版本已自动恢复", error.Message);
        Assert.Equal("injected IO failure", error.InnerException!.Message);
        fixture.AssertRestored();
        PortableApplicationUpdate.Recover(fixture.Target, fixture.Backup);
        fixture.AssertRestored();
    }

    [Theory]
    [InlineData("restore-intent:DanmuApi.App.exe")]
    [InlineData("file-restored:DanmuApi.App.exe")]
    [InlineData("file-restored:libSkiaSharp.dll")]
    public void InterruptedRecoveryCanBeRepeated(string boundary)
    {
        using var fixture = new Fixture();
        PortableApplicationUpdate.Replace(fixture.Target, fixture.Stage, fixture.Backup, fixture.Files);
        Assert.Throws<IOException>(() => PortableApplicationUpdate.Recover(fixture.Target, fixture.Backup,
            step => { if (step == boundary) throw new IOException("recovery interrupted"); }));
        PortableApplicationUpdate.Recover(fixture.Target, fixture.Backup);
        PortableApplicationUpdate.Recover(fixture.Target, fixture.Backup);
        fixture.AssertRestored();
    }

    [Fact]
    public void CleanupFailureAfterStartupIsWarningAndCanNeverRollback()
    {
        using var fixture = new Fixture();
        PortableApplicationUpdate.Replace(fixture.Target, fixture.Stage, fixture.Backup, fixture.Files);
        Assert.Throws<IOException>(() => PortableApplicationUpdate.CleanupConfirmed(fixture.Target, fixture.Backup));
        PortableApplicationUpdate.MarkStartupConfirmed(fixture.Target, fixture.Backup);
        using (var lockedBackup = new FileStream(Path.Combine(fixture.Backup, "DanmuApi.App.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var warning = PortableApplicationUpdate.CleanupConfirmed(fixture.Target, fixture.Backup);
            Assert.Contains("cleanup-warning", warning);
            Assert.Contains("不回滚", PortableApplicationUpdate.Recover(fixture.Target, fixture.Backup));
            Assert.Equal("new-DanmuApi.App.exe", File.ReadAllText(Path.Combine(fixture.Target, "DanmuApi.App.exe")));
        }
        Assert.Null(PortableApplicationUpdate.CleanupConfirmed(fixture.Target, fixture.Backup, fixture.Stage));
        Assert.Null(PortableApplicationUpdate.CleanupConfirmed(fixture.Target, fixture.Backup, fixture.Stage));
        Assert.Contains("不回滚", PortableApplicationUpdate.Recover(fixture.Target, fixture.Backup));
        Assert.Equal("user-notes", File.ReadAllText(Path.Combine(fixture.Target, "notes.txt")));
    }

    [Fact]
    public void CorruptBackupFailsBeforeAnyTargetChanges()
    {
        using var fixture = new Fixture();
        PortableApplicationUpdate.Replace(fixture.Target, fixture.Stage, fixture.Backup, fixture.Files);
        File.WriteAllText(Path.Combine(fixture.Backup, "DanmuApi.App.exe"), "corrupt");
        Assert.Throws<IOException>(() => PortableApplicationUpdate.Recover(fixture.Target, fixture.Backup));
        Assert.Equal("new-DanmuApi.App.exe", File.ReadAllText(Path.Combine(fixture.Target, "DanmuApi.App.exe")));
        Assert.True(File.Exists(Path.Combine(fixture.Target, "libSkiaSharp.dll")));
    }

    [Theory]
    [InlineData("../notes.txt")]
    [InlineData("notes.txt")]
    [InlineData("runtime-bundle/../../notes.txt")]
    [InlineData("runtime-bundle/NUL.txt")]
    [InlineData("runtime-bundle/a:stream")]
    public void InvalidJournalPathCannotDeleteUserFiles(string path)
    {
        using var fixture = new Fixture();
        PortableApplicationUpdate.Replace(fixture.Target, fixture.Stage, fixture.Backup, fixture.Files);
        var journalPath = PortableApplicationUpdate.JournalPath(fixture.Backup);
        var json = JsonNode.Parse(File.ReadAllText(journalPath))!;
        json["Files"]![1]!["Path"] = path;
        File.WriteAllText(journalPath, json.ToJsonString());
        Assert.Throws<IOException>(() => PortableApplicationUpdate.Recover(fixture.Target, fixture.Backup));
        Assert.Equal("user-notes", File.ReadAllText(Path.Combine(fixture.Target, "notes.txt")));
        Assert.Equal("new-DanmuApi.App.exe", File.ReadAllText(Path.Combine(fixture.Target, "DanmuApi.App.exe")));
    }

    [Fact]
    public void OversizedAndWrongTargetJournalFailExplicitly()
    {
        using var fixture = new Fixture();
        PortableApplicationUpdate.Replace(fixture.Target, fixture.Stage, fixture.Backup, fixture.Files);
        Assert.Throws<IOException>(() => PortableApplicationUpdate.Recover(fixture.Stage, fixture.Backup));
        using (var stream = File.OpenWrite(PortableApplicationUpdate.JournalPath(fixture.Backup))) stream.SetLength(16 * 1024 * 1024 + 1);
        Assert.Throws<IOException>(() => PortableApplicationUpdate.Recover(fixture.Target, fixture.Backup));
    }

    [Fact]
    public void ReparsePointInRecoveryTargetIsRejected()
    {
        using var fixture = new Fixture();
        using var external = new TemporaryDirectory();
        PortableApplicationUpdate.Replace(fixture.Target, fixture.Stage, fixture.Backup, fixture.Files);
        Directory.Delete(Path.Combine(fixture.Target, "runtime-bundle"), true);
        var link = Path.Combine(fixture.Target, "runtime-bundle");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{link}\" \"{external.Path}\"",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output);
        try
        {
            Assert.Throws<IOException>(() => PortableApplicationUpdate.Recover(fixture.Target, fixture.Backup));
            Assert.Equal("new-DanmuApi.App.exe", File.ReadAllText(Path.Combine(fixture.Target, "DanmuApi.App.exe")));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void ConcurrentRecoveryCannotRaceAnActiveReplacement()
    {
        using var fixture = new Fixture();
        var checkedLock = false;
        PortableApplicationUpdate.Replace(fixture.Target, fixture.Stage, fixture.Backup, fixture.Files, step =>
        {
            if (step != "file-replaced:DanmuApi.App.exe") return;
            Assert.Throws<IOException>(() => PortableApplicationUpdate.Recover(fixture.Target, fixture.Backup));
            checkedLock = true;
        });
        Assert.True(checkedLock);
        PortableApplicationUpdate.Recover(fixture.Target, fixture.Backup);
        fixture.AssertRestored();
    }

    [Fact]
    public void UncommittedJournalTemporaryFileDoesNotOverrideDurableJournal()
    {
        using var fixture = new Fixture();
        PortableApplicationUpdate.Replace(fixture.Target, fixture.Stage, fixture.Backup, fixture.Files);
        File.WriteAllText(PortableApplicationUpdate.JournalPath(fixture.Backup) + ".tmp", "{incomplete write");
        PortableApplicationUpdate.Recover(fixture.Target, fixture.Backup);
        fixture.AssertRestored();
    }

    [Fact]
    public void FileCountAndDuplicateNamesFailBeforeMutation()
    {
        using var fixture = new Fixture();
        Assert.Throws<IOException>(() => PortableApplicationUpdate.Replace(fixture.Target, fixture.Stage, fixture.Backup,
            Enumerable.Repeat("DanmuApi.App.exe", 30001).ToArray()));
        Assert.Throws<IOException>(() => PortableApplicationUpdate.Replace(fixture.Target, fixture.Stage, fixture.Backup,
            ["DanmuApi.App.exe", "DANMUAPI.APP.EXE"]));
        fixture.AssertRestored();
        Assert.False(Directory.Exists(fixture.Backup));
    }

    [Fact]
    public void CleanupCannotRemoveUserOwnedFiles()
    {
        using var fixture = new Fixture();
        PortableApplicationUpdate.Replace(fixture.Target, fixture.Stage, fixture.Backup, fixture.Files);
        PortableApplicationUpdate.MarkStartupConfirmed(fixture.Target, fixture.Backup);
        var warning = PortableApplicationUpdate.CleanupConfirmed(fixture.Target, fixture.Backup, Path.Combine(fixture.Target, "notes.txt"));
        Assert.Contains("cleanup-warning", warning);
        Assert.Equal("user-notes", File.ReadAllText(Path.Combine(fixture.Target, "notes.txt")));
        Assert.Equal("new-DanmuApi.App.exe", File.ReadAllText(Path.Combine(fixture.Target, "DanmuApi.App.exe")));
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
    }
}
