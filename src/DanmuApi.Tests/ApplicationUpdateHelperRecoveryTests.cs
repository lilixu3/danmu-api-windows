using System.Text.Json;
using DanmuApi.App.Services;

namespace DanmuApi.Tests;

public sealed class ApplicationUpdateHelperRecoveryTests
{
    [Fact]
    public void ExistingJobWithoutResumeServiceRemainsCompatible()
    {
        const string json = """{"ParentPid":123,"ParentStartTicks":456,"TargetDirectory":"test","AssetName":"app.zip","Kind":"portable","ExpectedVersion":"1.2.3"}""";
        var job = JsonSerializer.Deserialize<ApplicationUpdateJob>(json)!;
        Assert.False(job.ResumeService);
        var resumed = JsonSerializer.Deserialize<ApplicationUpdateJob>(JsonSerializer.Serialize(job with { ResumeService = true }))!;
        Assert.True(resumed.ResumeService);
    }

    [Fact]
    public void NormalStartupDoesNotResumeUpdateService()
    {
        Assert.False(ApplicationUpdateHelper.ShouldResumeService([]));
        Assert.False(ApplicationUpdateHelper.ShouldResumeService(["--app-update-recover", "unused"]));
        ApplicationUpdateHelper.AcknowledgeStartup([]);
    }

    [Fact]
    public void LockedInstallerCleanupRecordsWarningWithoutReportingUpdateFailureOrSensitivePath()
    {
        using var directory = new TemporaryDirectory();
        var package = Path.Combine(directory.Path, "TOKEN=do-not-log.exe");
        File.WriteAllText(package, "fake-installer");
        File.WriteAllText(Path.Combine(directory.Path, "result.txt"), "更新完成，新版本已确认启动。");
        using (var locked = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            ApplicationUpdateHelper.CleanupInstallerPackage(directory.Path, package);
            var warning = File.ReadAllText(Path.Combine(directory.Path, "cleanup-warning.txt"));
            Assert.Contains("cleanup-warning", warning);
            Assert.DoesNotContain("do-not-log", warning);
            Assert.False(File.Exists(Path.Combine(directory.Path, "error.txt")));
            Assert.Contains("更新完成", File.ReadAllText(Path.Combine(directory.Path, "result.txt")));
        }
        ApplicationUpdateHelper.CleanupInstallerPackage(directory.Path, package);
        Assert.False(File.Exists(package));
    }
}
