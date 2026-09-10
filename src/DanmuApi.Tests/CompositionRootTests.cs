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
            Assert.NotNull(provider.GetRequiredService<MainWindowViewModel>());
        }
    }
}
