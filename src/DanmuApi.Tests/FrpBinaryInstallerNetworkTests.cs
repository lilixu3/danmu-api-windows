using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DanmuApi.Core;
using DanmuApi.Core.Frp;
using DanmuApi.Platform;
using DanmuApi.Platform.Frp;

namespace DanmuApi.Tests;

/// <summary>
/// 走真实网络的安装链路测试：从 GitHub 官方仓库下载 frp → 官方 SHA256 校验 → 解压 → PE 架构复核。
///
/// 按仓库惯例用环境变量开关（网络类用例不做默认门控）：设置 <c>DANMU_TEST_FRP_INSTALL=1</c> 才会执行。
/// 它会真的下载约 14MB 的官方包，因此不进常规回归。
/// </summary>
public sealed class FrpBinaryInstallerNetworkTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("DANMU_TEST_FRP_INSTALL") == "1";

    [SkippableFact]
    public async Task InstallDownloadsVerifiesAndLandsAUsableBinary()
    {
        Skip.IfNot(Enabled, "DANMU_TEST_FRP_INSTALL 未设为 1；跳过真实下载测试");

        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(Path.Combine(directory.Path, "root"), Path.Combine(directory.Path, "data"));
        var diagnostics = new FrpTestDiagnostics();
        var installer = CreateInstaller(paths, diagnostics);

        var version = await ResolveLatestVersionAsync();
        var result = await installer.InstallAsync(version);

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal(version, result.Version);
        Assert.Equal(version, installer.InstalledVersion);

        var frpc = installer.ExecutablePath(version, serverRole: false);
        var frps = installer.ExecutablePath(version, serverRole: true);
        Assert.True(File.Exists(frpc), $"未落地 frpc.exe：{frpc}");
        Assert.True(File.Exists(frps), $"未落地 frps.exe：{frps}");

        // PE 架构必须与宿主一致：架构不符的 exe 只会在启动时以 0xc000007b 之类的方式失败。
        Assert.Equal(ExpectedMachine(), ExecutableImage.Machine(frpc));
        Assert.Equal(ExpectedMachine(), ExecutableImage.Machine(frps));

        // 让 frp 自己校验我们生成的配置语法（0.71.0 支持 verify 子命令）。
        var configPath = Path.Combine(paths.FrpConfigDirectory, FrpConfigWriter.ClientFileName);
        Directory.CreateDirectory(paths.FrpConfigDirectory);
        var client = FrpSettings.Default(9321).Client with { ServerAddress = "frp.example.com" };
        File.WriteAllText(
            configPath,
            FrpConfigWriter.WriteClient(client, "t", "admin", "p"),
            new UTF8Encoding(false));
        var verify = RunVerify(frpc, configPath);
        Assert.Equal(0, verify.ExitCode);
        Assert.Contains("syntax is ok", verify.Output, StringComparison.Ordinal);

        // 幂等：已安装时不再重复下载。
        var again = await installer.InstallAsync(version);
        Assert.True(again.Succeeded, again.Diagnostic);
        Assert.Contains("无需重复下载", again.Diagnostic, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task CorruptedCacheIsRejectedAndRefetchedInsteadOfInstalled()
    {
        Skip.IfNot(Enabled, "DANMU_TEST_FRP_INSTALL 未设为 1；跳过真实下载测试");

        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(Path.Combine(directory.Path, "root"), Path.Combine(directory.Path, "data"));
        var diagnostics = new FrpTestDiagnostics();
        var installer = CreateInstaller(paths, diagnostics);

        var version = await ResolveLatestVersionAsync();
        Assert.True((await installer.InstallAsync(version)).Succeeded);

        // 把缓存包改坏一个字节再删掉已安装目录：安装器必须发现校验不通过、重新下载，而不是解压这份坏包。
        var cache = Path.Combine(paths.FrpCacheDirectory, FrpReleaseCatalog.ForVersion(version, RuntimeInformation.ProcessArchitecture).FileName);
        Assert.True(File.Exists(cache), $"缓存包不存在：{cache}");
        var bytes = await File.ReadAllBytesAsync(cache);
        bytes[bytes.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(cache, bytes);
        Directory.Delete(Path.Combine(paths.FrpBinaryDirectory, version), recursive: true);

        var result = await installer.InstallAsync(version);

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Contains(diagnostics.Messages, message => message.Contains("校验未通过", StringComparison.Ordinal));
        Assert.Equal(ExpectedMachine(), ExecutableImage.Machine(installer.ExecutablePath(version, serverRole: false)));
    }

    private sealed class OriginalRoute : IGithubRoutePreferenceStore
    {
        public GithubRoutePreference Read() => new(GithubProxyCatalog.OriginalId, true);

        public void Confirm(string proxyId) => throw new NotSupportedException();

        public void Invalidate() => throw new NotSupportedException();
    }

    private static FrpBinaryInstaller CreateInstaller(AppPaths paths, FrpTestDiagnostics diagnostics) =>
        new(
            paths,
            new GithubFileDownloader(
                new HttpClient { Timeout = TimeSpan.FromSeconds(120) },
                routePreferences: new OriginalRoute()),
            new HttpClient { Timeout = TimeSpan.FromSeconds(60) },
            diagnosticSink: message => diagnostics.Record(message));

    private static async Task<string> ResolveLatestVersionAsync()
    {
        var discovery = new FrpReleaseDiscovery(new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
        var release = await discovery.GetLatestAsync(RuntimeInformation.ProcessArchitecture);
        return release.Version;
    }

    private static ushort ExpectedMachine() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => 0x8664,
        Architecture.Arm64 => 0xAA64,
        _ => throw new NotSupportedException($"测试宿主架构不支持：{RuntimeInformation.ProcessArchitecture}"),
    };

    private static (int ExitCode, string Output) RunVerify(string executable, string configPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = $"verify -c \"{configPath}\"",
            WorkingDirectory = Path.GetDirectoryName(configPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        }) ?? throw new InvalidOperationException("无法启动 frpc.exe verify");

        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "frpc.exe verify 超时");
        return (process.ExitCode, output);
    }
}
