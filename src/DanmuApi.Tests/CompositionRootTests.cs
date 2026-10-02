using Avalonia.Controls.ApplicationLifetimes;
using DanmuApi.App.ViewModels;
using DanmuApi.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace DanmuApi.Tests;

/// <summary>
/// 组合根冒烟：直接解析真实的 App.ConfigureServices 图。新增注册写错依赖（类型未注册、
/// 工厂里解析错服务）只会在用户首次启动时暴露，这里把它变成可重复的测试。
/// </summary>
public sealed class CompositionRootTests
{
    [Fact]
    public async Task CorePageAndDependencyVerifierResolveFromTheRealCompositionRoot()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(Path.Combine(directory.Path, "runtime"), Path.Combine(directory.Path, "settings"));
        var lifetime = new ClassicDesktopStyleApplicationLifetime();

        var provider = DanmuApi.App.App.ConfigureServices(paths, lifetime, () => null).BuildServiceProvider();
        await using (provider)
        {
            // 本轮新增/改动的两个服务必须能从真实图里解析出来。
            var verifier = provider.GetRequiredService<DanmuApi.App.Services.CoreDependencyVerifier>();
            Assert.NotNull(verifier);
            var corePage = provider.GetRequiredService<CorePageViewModel>();
            Assert.NotNull(corePage);
            // 未安装核心时页面构造成功，说明构造函数里对磁盘的读取路径也被走通了。
            Assert.False(corePage.IsInstalled);

            // 通知与维护相关服务也一起解析，避免它们在别处仍被引用却已失去注册。
            Assert.NotNull(provider.GetRequiredService<DanmuApi.App.Services.IDesktopNotificationService>());
            Assert.NotNull(provider.GetRequiredService<DanmuApi.App.Services.RuntimePreparationService>());
            Assert.NotNull(provider.GetRequiredService<DanmuApi.App.Services.IRuntimeMaintenanceService>());
            Assert.NotNull(provider.GetRequiredService<DanmuApi.Core.ICoreDependencyService>());
            // 端口预检与管理员会话的工厂都挂了诊断汇，依赖写错只会在首次启动暴露，这里固定住。
            Assert.NotNull(provider.GetRequiredService<DanmuApi.Runtime.INodeSupervisor>());
            Assert.NotNull(provider.GetRequiredService<DanmuApi.App.Services.IAdminSessionService>());
            var outbound = provider.GetRequiredService<OutboundDirectViewModel>();
            var settings = provider.GetRequiredService<SettingsPageViewModel>();
            Assert.Same(outbound, settings.Outbound);
            Assert.NotNull(provider.GetRequiredService<DanmuApi.App.Services.IOutboundDirectService>());
            Assert.False(provider.GetRequiredService<IOutboundSettingsStore>().Read().Enabled);
            Assert.NotNull(provider.GetRequiredService<MainWindowViewModel>());
        }
    }

    /// <summary>
    /// 回归：软件更新检查过去从不带 Token，用户遇到的是"存了 Token、核心检查额度刷新了，
    /// 检查更新仍报超限"。修复点是 DI 接线——用显式工厂把 IGithubTokenProvider 传进
    /// ApplicationUpdateViewModel；而接线漏写时解析不会失败，所以必须断言它是**同一个 Token 源**，
    /// 否则这个缺陷会静默复发（这正是它第一次能溜过去的原因）。
    /// </summary>
    [Fact]
    public async Task UpdateCheckAndCoreChecksShareOneTokenSourceInTheRealCompositionRoot()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(Path.Combine(directory.Path, "runtime"), Path.Combine(directory.Path, "settings"));
        // 与用户"保存了 Token"的状态一致：先写进应用自己的凭据存储。
        new WindowsGithubTokenStore(paths.GithubTokenFile).Save("ghp_test_token_value");
        var lifetime = new ClassicDesktopStyleApplicationLifetime();

        var provider = DanmuApi.App.App.ConfigureServices(paths, lifetime, () => null).BuildServiceProvider();
        await using (provider)
        {
            // 核心检查与软件更新检查必须看到同一个 Token（同一份配置、同一套放行规则）。
            var shared = provider.GetRequiredService<DanmuApi.Core.IGithubTokenProvider>();
            Assert.Equal("ghp_test_token_value", DanmuApi.Core.GithubTokenPolicy.Read(shared));

            // 只看"能解析"证明不了接线：ApplicationUpdateViewModel 内部自建 ApplicationUpdateService，
            // 若工厂漏传 tokenProvider，解析照样成功、缺陷却回来了。这里直接读出私有字段核对。
            var viewModel = provider.GetRequiredService<ApplicationUpdateViewModel>();
            var remoteField = typeof(ApplicationUpdateViewModel)
                .GetField("_remote", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(remoteField);
            var remote = remoteField!.GetValue(viewModel);
            Assert.NotNull(remote);
            var tokenField = remote!.GetType()
                .GetField("tokenProvider", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(tokenField);
            var wired = Assert.IsAssignableFrom<DanmuApi.Core.IGithubTokenProvider>(tokenField!.GetValue(remote));
            Assert.Equal("ghp_test_token_value", DanmuApi.Core.GithubTokenPolicy.Read(wired));
        }
    }
}
