using DanmuApi.App.Services;

namespace DanmuApi.Tests;

public sealed class ApplicationInstallerLaunchTests
{
    [Fact]
    public void InstallerManifestOwnsElevationAndOriginalUserContext()
    {
        var job = new ApplicationUpdateJob(1234, 5678, @"C:\Program Files\DanmuApi", "setup.exe", "installer", "0.5.24");
        var start = ApplicationUpdateHelper.CreateInstallerStartInfo(@"C:\updates\setup.exe", job, @"C:\updates\task");

        Assert.True(start.UseShellExecute);
        Assert.Empty(start.Verb);
        Assert.Equal(@"C:\updates\setup.exe", start.FileName);
        Assert.Contains("/UPDATEPARENT=1234", start.Arguments);
        Assert.Contains("/UPDATEREADY=\"C:\\updates\\task\\installer.ready\"", start.Arguments);
        Assert.Contains("/DIR=\"C:\\Program Files\\DanmuApi\"", start.Arguments);
        Assert.Contains("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART", start.Arguments);
    }

    [Theory]
    [InlineData(System.Runtime.InteropServices.Architecture.X86, Microsoft.Win32.RegistryView.Registry32)]
    [InlineData(System.Runtime.InteropServices.Architecture.X64, Microsoft.Win32.RegistryView.Registry64)]
    [InlineData(System.Runtime.InteropServices.Architecture.Arm64, Microsoft.Win32.RegistryView.Registry64)]
    public void InstallationLookupMatchesTheInstallerRegistryView(System.Runtime.InteropServices.Architecture architecture, Microsoft.Win32.RegistryView expected) =>
        Assert.Equal(expected, ApplicationUpdateHelper.RegistryViewForArchitecture(architecture));

    [Fact]
    public void LegacyUpdateParentUsesCapturedUserLeaseInsteadOfAnAdministratorProfileProbe()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "installer", "DanmuApi.iss"))) root = root.Parent;
        Assert.NotNull(root);
        var script = File.ReadAllText(Path.Combine(root!.FullName, "installer", "DanmuApi.iss"));

        Assert.Contains("Result := BeginUpdateLease(ParentPid, ReadyPath)", script);
        Assert.Contains("--installer-update-lease", script);
        Assert.DoesNotContain("SaveStringToFile(ReadyPath, 'ready'", script);
        Assert.Contains("BeforeInstall: EnsureUpdateLease", script);
        Assert.Contains("if CurStep = ssDone then FinishUpdateLease()", script);
        Assert.Contains("if ParentPid <= 0 then begin", script);
        Assert.Contains("ExecAsOriginalUser", script);
    }
}
