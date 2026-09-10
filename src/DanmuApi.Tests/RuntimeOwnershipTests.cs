using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class RuntimeOwnershipTests
{
    [Fact]
    public void RequiresIdentityPortAndAllPaths()
    {
        using var directory = new TemporaryDirectory();
        var health = new RuntimeOwnership.Health("desktop-1", 9321, directory.Path, directory.Path, directory.Path);

        Assert.True(RuntimeOwnership.IsOwned("desktop-1", 9321, directory.Path, health));
        Assert.False(RuntimeOwnership.IsOwned("desktop-2", 9321, directory.Path, health));
        Assert.False(RuntimeOwnership.IsOwned("desktop-1", 9322, directory.Path, health));
        Assert.False(RuntimeOwnership.IsOwned("desktop-1", 9321, directory.Path, health with { Cwd = Path.Combine(directory.Path, "other") }));
    }
}
