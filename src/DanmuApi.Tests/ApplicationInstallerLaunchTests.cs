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
    public void LegacyUpdateParentRetainsReadyParentWaitCancelAndLockOrder()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "installer", "DanmuApi.iss"))) root = root.Parent;
        Assert.NotNull(root);
        var script = File.ReadAllText(Path.Combine(root!.FullName, "installer", "DanmuApi.iss"));

        var prepare = script.IndexOf("function PrepareToInstall(", StringComparison.Ordinal);
        var ready = script.IndexOf("SaveStringToFile(ReadyPath, 'ready'", prepare, StringComparison.Ordinal);
        var wait = script.IndexOf("WaitForSingleObject(ParentHandle, 120000)", prepare, StringComparison.Ordinal);
        var cancel = script.IndexOf("FileExists(ExtractFilePath(ReadyPath) + 'cancel')", prepare, StringComparison.Ordinal);
        var modernLock = script.IndexOf("{userappdata}\\DanmuApi\\instance.lock", prepare, StringComparison.Ordinal);
        var legacyLock = script.IndexOf("{userappdata}\\DanmuApi\\app.lock", prepare, StringComparison.Ordinal);
        var manual = script.IndexOf("Result := StopRunningTarget()", prepare, StringComparison.Ordinal);
        Assert.True(prepare >= 0 && ready > prepare && wait > ready && cancel > wait &&
                    modernLock > cancel && legacyLock > modernLock && manual > legacyLock);
        Assert.DoesNotContain("BeginUpdateLease", script);
        Assert.DoesNotContain("--installer-update-lease", script);
        Assert.DoesNotContain("ExecAsOriginalUser", script);
    }
}
