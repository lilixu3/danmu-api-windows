using DanmuApi.Platform;

namespace DanmuApi.Tests;

public sealed class AppPathsTests
{
    [Fact]
    public void PlatformFilesUseTheSpecifiedSettingsAndHostLogDirectories()
    {
        using var directory = new TemporaryDirectory();
        var root = Path.Combine(directory.Path, "local-data");
        var appData = Path.Combine(directory.Path, "roaming-data");
        var paths = new AppPaths(root, appData);

        Assert.Equal(Path.Combine(appData, "instance.lock"), paths.InstanceLockFile);
        Assert.Equal(Path.Combine(appData, "instance.endpoint"), paths.InstanceEndpointFile);
        Assert.Equal(Path.Combine(root, "logs", "tray.log"), paths.TrayLogFile);
        Assert.Equal(Path.Combine(root, "logs", "lifecycle.log"), paths.LifecycleLogFile);
        Assert.StartsWith(Path.GetFullPath(appData), paths.InstanceLockFile, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(appData), paths.InstanceEndpointFile, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(Path.Combine(root, "logs")), paths.TrayLogFile, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(Path.Combine(root, "logs")), paths.LifecycleLogFile, StringComparison.OrdinalIgnoreCase);
    }
}
