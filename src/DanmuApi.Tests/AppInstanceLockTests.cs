using System.Net;
using System.Net.Sockets;
using System.Text;
using DanmuApi.Platform;

namespace DanmuApi.Tests;

public sealed class AppInstanceLockTests
{
    [Fact]
    public void LegacyByteRangeLockBlocksNewHostAndIsReleasedAfterFailure()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path, Path.Combine(directory.Path, "settings"));
        Directory.CreateDirectory(paths.SettingsDirectory);
        using var legacy = new FileStream(Path.Combine(paths.SettingsDirectory, "app.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        legacy.Lock(0, long.MaxValue);
        using var modern = new AppInstanceLock(paths, _ => { });
        Assert.False(modern.TryAcquireDetailed().Succeeded);
        Assert.False(modern.IsAcquired);
        legacy.Unlock(0, long.MaxValue);
        Assert.True(modern.TryAcquire());
        Assert.Throws<IOException>(() => legacy.Lock(0, long.MaxValue));
        modern.Release();
        legacy.Lock(0, long.MaxValue);
        legacy.Unlock(0, long.MaxValue);
    }

    [Fact]
    public void SecondInstanceCannotAcquireTheSameFileLock()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path, Path.Combine(directory.Path, "settings"));
        using var first = new AppInstanceLock(paths, _ => { });
        using var second = new AppInstanceLock(paths, _ => { });

        Assert.True(first.TryAcquire(out var firstResult));
        Assert.True(firstResult.Succeeded);
        Assert.False(second.TryAcquire(out var secondResult));
        Assert.True(secondResult.AlreadyOwned);
        Assert.Contains("已被其他实例持有", secondResult.Diagnostic);
    }

    [Fact]
    public async Task ControlServerAuthenticatesCommandsAndWritesAtomicEndpoint()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path, Path.Combine(directory.Path, "settings"));
        var log = new List<string>();
        var commands = new List<InstanceCommand>();
        using var owner = new AppInstanceLock(paths, log.Add);

        Assert.True(owner.TryAcquire());
        Assert.True(owner.StartControlServer(commands.Add).Succeeded);
        Assert.True(File.Exists(paths.InstanceEndpointFile));
        Assert.False(File.Exists(paths.InstanceEndpointFile + ".tmp"));

        var endpoint = ReadEndpoint(paths.InstanceEndpointFile);
        Assert.InRange(endpoint.Port, 1, 65_535);
        Assert.False(string.IsNullOrWhiteSpace(endpoint.Token));

        Assert.Equal("OK", await SendRawAsync(endpoint.Port, $"{endpoint.Token}\tSHOW_SETTINGS"));
        Assert.Equal([InstanceCommand.SHOW_SETTINGS], commands);

        var invalidTokenResponse = await SendRawAsync(endpoint.Port, $"invalid-token\tSHOW_OVERVIEW");
        Assert.StartsWith("ERROR ", invalidTokenResponse);
        Assert.Contains("令牌", invalidTokenResponse);
        Assert.Equal([InstanceCommand.SHOW_SETTINGS], commands);

        var unknownCommandResponse = await SendRawAsync(endpoint.Port, $"{endpoint.Token}\tUNKNOWN");
        Assert.StartsWith("ERROR ", unknownCommandResponse);
        Assert.Contains("未知", unknownCommandResponse);
        Assert.Equal([InstanceCommand.SHOW_SETTINGS], commands);
        Assert.Contains(log, message => message.Contains("本地唤醒请求失败", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendCommandUsesEndpointAndReleaseOnlyDeletesOwnToken()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path, Path.Combine(directory.Path, "settings"));
        using var owner = new AppInstanceLock(paths, _ => { });
        Assert.True(owner.TryAcquire());
        Assert.True(owner.StartControlServer(_ => { }).Succeeded);

        var sendResult = await owner.SendCommandAsync(InstanceCommand.SHOW_OVERVIEW);
        Assert.True(sendResult.Succeeded, sendResult.Diagnostic);

        var replacement = "port=65534\ntoken=replacement-token\n";
        File.WriteAllText(paths.InstanceEndpointFile, replacement, new UTF8Encoding(false));
        var releaseResult = owner.Release();
        Assert.True(releaseResult.Succeeded, releaseResult.Diagnostic);
        Assert.Equal(replacement, File.ReadAllText(paths.InstanceEndpointFile, new UTF8Encoding(false)));
    }

    [Fact]
    public async Task SendCommandFailsWithDiagnosticWhenEndpointIsMissingWithinBoundedWindow()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path, Path.Combine(directory.Path, "settings"));
        using var client = new AppInstanceLock(paths, _ => { });

        var started = DateTimeOffset.UtcNow;
        var result = await client.SendCommandAsync(InstanceCommand.SHOW_OVERVIEW);
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.False(result.Succeeded);
        Assert.Contains("3000ms", result.Diagnostic);
        Assert.Contains("端点不存在", result.Diagnostic);
        Assert.InRange(elapsed, TimeSpan.FromMilliseconds(2_500), TimeSpan.FromSeconds(4.5));
    }

    private static (int Port, string Token) ReadEndpoint(string path)
    {
        var values = File.ReadAllLines(path, new UTF8Encoding(false))
            .Select(line => line.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        return (int.Parse(values["port"]), values["token"]);
    }

    private static async Task<string> SendRawAsync(int port, string request)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            NewLine = "\n",
            AutoFlush = true,
        };
        await writer.WriteLineAsync(request);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return (await reader.ReadLineAsync()) ?? string.Empty;
    }
}
