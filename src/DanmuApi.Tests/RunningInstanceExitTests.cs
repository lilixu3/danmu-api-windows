using System.Net;
using System.Net.Sockets;
using System.Text;
using DanmuApi.Platform;

namespace DanmuApi.Tests;

/// <summary>
/// 安装器里的「弹幕 API 正在运行，请退出后再安装」以前只有一个死胡同：用户必须自己去托盘退出。
/// 安装器每条入口先以原用户权限运行随包可信中继。只有探测到真实锁被持有才询问，
/// 用户同意后由 <c>--installer-request-exit</c> 请求退出，并等锁真正释放。
///
/// 这些用例钉住协议两端：① 唤醒通道认 <see cref="InstanceCommand.REQUEST_EXIT"/>；
/// ② endpoint 可执行文件字段只是旧信息，中继和安装器均不执行它；
/// ③ elevated token 在任何 profile 探测之前拒绝；④ 以锁释放而不是 OK 为完成判据。
/// </summary>
public sealed class RunningInstanceExitTests
{
    [Fact]
    public async Task RequestExitIsDeliveredThroughTheAuthenticatedChannel()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path, Path.Combine(directory.Path, "settings"));
        var commands = new List<InstanceCommand>();
        using var owner = new AppInstanceLock(paths, _ => { });

        Assert.True(owner.TryAcquire());
        Assert.True(owner.StartControlServer(commands.Add).Succeeded);
        var endpoint = ReadEndpoint(paths.InstanceEndpointFile);

        var response = await SendRawAsync(endpoint.Port, $"{endpoint.Token}\tREQUEST_EXIT");

        Assert.Equal("OK", response);
        Assert.Equal([InstanceCommand.REQUEST_EXIT], commands);
    }

    [Fact]
    public async Task RequestExitStillRequiresTheToken()
    {
        // 与其它命令同一道门禁：没有正确令牌就不能让别人的实例退出。
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path, Path.Combine(directory.Path, "settings"));
        var commands = new List<InstanceCommand>();
        using var owner = new AppInstanceLock(paths, _ => { });

        Assert.True(owner.TryAcquire());
        Assert.True(owner.StartControlServer(commands.Add).Succeeded);
        var endpoint = ReadEndpoint(paths.InstanceEndpointFile);

        var response = await SendRawAsync(endpoint.Port, $"wrong-token\tREQUEST_EXIT");

        Assert.StartsWith("ERROR ", response);
        Assert.Empty(commands);
    }

    [Fact]
    public void EndpointRecordsTheRunningExecutableForTheInstaller()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path, Path.Combine(directory.Path, "settings"));
        using var owner = new AppInstanceLock(paths, _ => { });

        Assert.True(owner.TryAcquire());
        Assert.True(owner.StartControlServer(_ => { }).Succeeded);

        // 安装器靠这条记录找到「该请谁退出」：便携副本可能在任意目录，不能假设是 {app}。
        Assert.True(AppInstanceLock.TryReadRunningExecutable(paths.InstanceEndpointFile, out var executable));
        Assert.False(string.IsNullOrWhiteSpace(executable));
        Assert.True(Path.IsPathFullyQualified(executable!));
    }

    [Fact]
    public void ExecutableLookupFailsClosedInsteadOfGuessing()
    {
        using var directory = new TemporaryDirectory();
        var endpoint = Path.Combine(directory.Path, "instance.endpoint");

        // 文件不存在。
        Assert.False(AppInstanceLock.TryReadRunningExecutable(endpoint, out var missing));
        Assert.Null(missing);

        // 旧版本写的端点没有 exe 字段：必须返回 false，安装器会退回 {app} 或让用户手动退出，
        // 绝不猜一个路径去执行。
        File.WriteAllText(endpoint, "port=1234\ntoken=abc\n");
        Assert.False(AppInstanceLock.TryReadRunningExecutable(endpoint, out var legacy));
        Assert.Null(legacy);

        // 内容损坏同样不猜。
        File.WriteAllText(endpoint, "exe=\n");
        Assert.False(AppInstanceLock.TryReadRunningExecutable(endpoint, out var blank));
        Assert.Null(blank);
    }

    [Fact]
    public async Task ExitRequestCompletesOnlyAfterTheLockIsReleased()
    {
        // 安装器的完成判据必须是「锁空了」。这里模拟完整握手：实例收到请求后在**服务器线程之外**
        // 释放锁，请求方才应看到可获取。
        // 注意不能在回调里直接 Release()：回调正是在唤醒通道的服务循环里执行的，
        // 而 Release 要等待那条服务循环结束 —— 会自我死锁。生产代码同样不在这里退出，
        // 它把动作 Post 到 UI 线程（见 App.axaml.cs 的 REQUEST_EXIT 分支）。
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path, Path.Combine(directory.Path, "settings"));
        using var owner = new AppInstanceLock(paths, _ => { });
        using var observer = new AppInstanceLock(paths, _ => { });

        Assert.True(owner.TryAcquire());
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(owner.StartControlServer(command =>
        {
            if (command != InstanceCommand.REQUEST_EXIT) return;
            _ = Task.Run(() =>
            {
                owner.Release();
                released.TrySetResult();
            });
        }).Succeeded);

        var endpoint = ReadEndpoint(paths.InstanceEndpointFile);
        var response = await SendRawAsync(endpoint.Port, $"{endpoint.Token}\tREQUEST_EXIT");
        Assert.Equal("OK", response);
        await released.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 锁已释放：安装器这时才允许替换文件。
        Assert.True(observer.TryAcquire(out var acquire));
        Assert.True(acquire.Succeeded);
    }

    /// <summary>
    /// 安装器真正调用的入口。重点：只有在**锁被释放**后才返回 0；
    /// 实例仍在运行（例如旧版本不认这条命令，或退出卡住）时必须返回非 0，
    /// 否则安装器会去覆盖正在运行的程序文件。
    /// </summary>
    [Fact]
    public async Task RequesterReportsSuccessOnlyWhenTheInstanceActuallyLeft()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path, Path.Combine(directory.Path, "settings"));

        // 无人运行：等同于"已退出"，安装器可继续。
        Assert.Equal(0, DanmuApi.App.Services.RunningInstanceExitRequester.Run(TimeSpan.FromSeconds(3), paths));

        // 实例在运行但不响应（旧版本实例的真实表现：它不认这条命令，只回 ERROR 且继续运行）。
        using var stubborn = new AppInstanceLock(paths, _ => { });
        Assert.True(stubborn.TryAcquire());
        var result = await Task.Run(() =>
            DanmuApi.App.Services.RunningInstanceExitRequester.Run(TimeSpan.FromSeconds(3), paths));
        Assert.Equal(2, result);
    }

    [Fact]
    public async Task RequesterReturnsZeroAfterTheInstanceReleasesTheLock()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path, Path.Combine(directory.Path, "settings"));
        using var instance = new AppInstanceLock(paths, _ => { });
        Assert.True(instance.TryAcquire());
        Assert.True(instance.StartControlServer(command =>
        {
            if (command == InstanceCommand.REQUEST_EXIT)
            {
                // 与生产一致：退出动作不在服务线程上做。
                _ = Task.Run(() => instance.Release());
            }
        }).Succeeded);

        var result = await Task.Run(() =>
            DanmuApi.App.Services.RunningInstanceExitRequester.Run(TimeSpan.FromSeconds(15), paths));

        Assert.Equal(0, result);
    }

    [Fact]
    public void InstallerRelayRefusesElevatedTokenBeforeLookingAtAnyProfile()
    {
        var touchedProfile = false;
        var result = DanmuApi.App.Services.RunningInstanceExitRequester.RunInstaller(true, TimeSpan.Zero,
            () => true, () => { touchedProfile = true; throw new InvalidOperationException("wrong administrator profile"); });
        Assert.Equal(1, result);
        Assert.False(touchedProfile);
        result = DanmuApi.App.Services.RunningInstanceExitRequester.RunInstaller(false, TimeSpan.FromSeconds(1),
            () => true, () => { touchedProfile = true; throw new InvalidOperationException("wrong administrator profile"); });
        Assert.Equal(1, result);
        Assert.False(touchedProfile);
    }

    [Fact]
    public void InstallerProbeUsesRealModernAndLegacyLocksWithoutReadingSettingsOrEndpointExe()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path, Path.Combine(directory.Path, "roaming"));
        Directory.CreateDirectory(paths.SettingsDirectory);
        File.WriteAllText(paths.SettingsFile, "invalid settings and runtime_root");
        File.WriteAllText(paths.InstanceEndpointFile, "exe=C:\\untrusted.exe\n");
        int Probe() => DanmuApi.App.Services.RunningInstanceExitRequester.RunInstaller(true, TimeSpan.Zero, () => false, () => paths);
        Assert.Equal(0, Probe());
        using (var owner = new AppInstanceLock(paths, _ => { }))
        {
            Assert.True(owner.TryAcquire());
            Assert.Equal(10, Probe());
        }
        using (var legacy = new FileStream(Path.Combine(paths.SettingsDirectory, "app.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            legacy.Lock(0, long.MaxValue);
            Assert.Equal(10, Probe());
        }
        Assert.Equal(0, Probe());
    }

    [Fact]
    public async Task RelayIgnoresUntrustedEndpointExecutableButKeepsAuthenticationAndCompletion()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path, Path.Combine(directory.Path, "settings"));
        using var owner = new AppInstanceLock(paths, _ => { });
        Assert.True(owner.TryAcquire());
        Assert.True(owner.StartControlServer(command =>
        {
            if (command == InstanceCommand.REQUEST_EXIT) _ = Task.Run(() => owner.Release());
        }).Succeeded);
        var content = File.ReadAllLines(paths.InstanceEndpointFile).Where(line => !line.StartsWith("exe=", StringComparison.Ordinal));
        File.WriteAllLines(paths.InstanceEndpointFile, content.Append("exe=C:\\untrusted.exe").ToArray());
        var result = await Task.Run(() => DanmuApi.App.Services.RunningInstanceExitRequester.RunInstaller(false,
            TimeSpan.FromSeconds(5), () => false, () => paths));
        Assert.Equal(0, result);
    }

    [Fact]
    public void InstallerOnlyExecutesExtractedPackageRelayAsOriginalUserAndChecksItsExitCode()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "installer", "DanmuApi.iss"))) root = root.Parent;
        Assert.NotNull(root);
        var script = File.ReadAllText(Path.Combine(root!.FullName, "installer", "DanmuApi.iss"));
        Assert.Contains("DestName: \"DanmuApi.ExitRelay.exe\"; Flags: dontcopy", script);
        Assert.Contains("ExtractTemporaryFile('DanmuApi.ExitRelay.exe')", script);
        Assert.Contains("ExecAsOriginalUser(ExpandConstant('{tmp}\\DanmuApi.ExitRelay.exe')", script);
        Assert.DoesNotContain("ReadRunningExecutable", script);
        Assert.DoesNotContain("Exec(ExePath", script);
        Assert.DoesNotContain("{userappdata}\\DanmuApi\\instance.lock", script);
        Assert.Contains("if ExitCode <> 0 then begin", script);
        Assert.Contains("--installer-instance-probe", script);
        Assert.Contains("0.5.13", script);
    }

    private static (int Port, string Token) ReadEndpoint(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in File.ReadAllLines(path, new UTF8Encoding(false, true)))
        {
            var line = rawLine.Trim();
            var separator = line.IndexOf('=');
            if (separator > 0)
            {
                values[line[..separator]] = line[(separator + 1)..];
            }
        }

        return (int.Parse(values["port"], System.Globalization.CultureInfo.InvariantCulture), values["token"]);
    }

    private static async Task<string> SendRawAsync(int port, string payload)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n", AutoFlush = true };
        await writer.WriteLineAsync(payload);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), leaveOpen: true);
        return await reader.ReadLineAsync() ?? string.Empty;
    }
}
