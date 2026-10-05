using DanmuApi.Platform;

namespace DanmuApi.Tests;

public sealed class AppInstanceProcessBindingTests
{
    [SkippableFact]
    public async Task VerifiedProcessReceivesOneAuthenticatedExitRequest()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path, Path.Combine(directory.Path, "settings"));
        var received = new List<InstanceCommand>();
        using var owner = new AppInstanceLock(paths, _ => { });
        Assert.True(owner.TryAcquire());
        Assert.True(owner.StartControlServer(received.Add).Succeeded);
        using var sender = new AppInstanceLock(paths, _ => { });
        var result = await sender.SendCommandAsync(InstanceCommand.REQUEST_EXIT, expectedProcessId: Environment.ProcessId);
        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal([InstanceCommand.REQUEST_EXIT], received);
    }

    [SkippableFact]
    public async Task OtherProcessIdentityIsRejectedBeforeDispatchEvenWithTheCorrectEndpointToken()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path, Path.Combine(directory.Path, "settings"));
        var received = new List<InstanceCommand>();
        using var owner = new AppInstanceLock(paths, _ => { });
        Assert.True(owner.TryAcquire());
        Assert.True(owner.StartControlServer(received.Add).Succeeded);
        using var sender = new AppInstanceLock(paths, _ => { });
        var result = await sender.SendCommandAsync(InstanceCommand.REQUEST_EXIT, expectedProcessId: int.MaxValue);
        Assert.False(result.Succeeded);
        Assert.Equal("endpointowner", result.FailureCode);
        Assert.Empty(received);
        Assert.DoesNotContain(File.ReadAllText(paths.InstanceEndpointFile).Split('\n').Single(line => line.StartsWith("token=")).Substring(6), result.Diagnostic);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidExpectedProcessDoesNotReadTheEndpoint(int pid)
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path, Path.Combine(directory.Path, "settings"));
        using var sender = new AppInstanceLock(paths, _ => { });
        var result = await sender.SendCommandAsync(InstanceCommand.REQUEST_EXIT, expectedProcessId: pid);
        Assert.False(result.Succeeded);
        Assert.Equal("endpointowner", result.FailureCode);
        Assert.False(File.Exists(paths.InstanceEndpointFile));
    }

    [SkippableFact]
    public async Task AVerifiedServerMayRefuseExitWithoutDispatchBeingRepeated()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path, Path.Combine(directory.Path, "settings"));
        var requests = 0;
        using var owner = new AppInstanceLock(paths, _ => { });
        Assert.True(owner.TryAcquire());
        Assert.True(owner.StartControlServer(_ => { requests++; throw new InvalidOperationException("unsupported secret=not-for-installer"); }).Succeeded);
        using var sender = new AppInstanceLock(paths, _ => { });
        var result = await sender.SendCommandAsync(InstanceCommand.REQUEST_EXIT, expectedProcessId: Environment.ProcessId);
        Assert.False(result.Succeeded);
        Assert.Equal("commandrejected", result.FailureCode);
        Assert.DoesNotContain("not-for-installer", result.Diagnostic);
        Assert.Equal(1, requests);
        Assert.True(owner.IsAcquired);
    }

    [Fact]
    public async Task EndpointReadFailureRetainsItsExceptionTypeAndHResult()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path, Path.Combine(directory.Path, "settings"));
        Directory.CreateDirectory(paths.SettingsDirectory);
        using var sender = new AppInstanceLock(paths, _ => { });
        var result = await sender.SendCommandAsync(InstanceCommand.REQUEST_EXIT, expectedProcessId: Environment.ProcessId);
        Assert.False(result.Succeeded);
        Assert.Equal(nameof(FileNotFoundException), result.FailureType);
        Assert.Equal(unchecked((int)0x80070002), result.FailureHResult);
    }
}
