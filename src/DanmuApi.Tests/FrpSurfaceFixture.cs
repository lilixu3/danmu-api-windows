using DanmuApi.Core.Frp;

namespace DanmuApi.Tests;

/// <summary>
/// 穿透界面渲染用例的公共夹具：一个"已装 frp、设置填好、隧道正常"的起始状态。
/// 两处渲染用例（概览地址卡、穿透监控页）共用同一组数值，避免两张图各说一套地址。
/// </summary>
internal static class FrpSurfaceFixture
{
    internal const string PublicEntry = "203.0.113.10:19321";

    internal const string FrpsServer = "frp.example.com";

    internal static FrpProxyStatus RunningProxy { get; } =
        new("danmu-api", "tcp", "running", string.Empty, "127.0.0.1:9321", PublicEntry);

    /// <summary>让假安装器报「已安装 0.71.0」、设置里填好 frps 地址，并让服务重新读一次设置。</summary>
    internal static async Task UseInstalledFrpAsync(FrpTestHarness harness)
    {
        harness.Installer.Version = "0.71.0";
        var defaults = FrpSettings.Default(9321);
        harness.Store.Save(defaults with
        {
            InstalledVersion = "0.71.0",
            Client = defaults.Client with { ServerAddress = FrpsServer },
        });
        var reload = await harness.Service.ReloadSettingsAsync();
        Assert.True(reload.Succeeded, reload.Message);
    }
}
