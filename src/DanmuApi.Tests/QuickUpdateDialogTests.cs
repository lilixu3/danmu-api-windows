using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
using DanmuApi.Core;
using DanmuApi.Platform;
using Xunit;

namespace DanmuApi.Tests;

/// <summary>
/// 侧栏「有更新」卡片点开的快速更新弹窗：两种流程都要能在弹窗里走完，
/// 且软件那条的按钮/进度必须跟着 ApplicationUpdateViewModel 的实时状态变。
/// </summary>
public sealed class QuickUpdateDialogTests
{
    [Fact]
    public void ApplicationFlowMirrorsTheSoftwareUpdateState()
    {
        using var directory = new TemporaryDirectory();
        var app = CreateApplicationUpdate(new SettingsStore(Path.Combine(directory.Path, "updates.properties")));
        app.AvailableVersion = "0.6.0-preview.1";
        app.HasUpdate = true;

        using var dialog = QuickUpdateDialogViewModel.ForApplication(app);

        Assert.True(dialog.IsAppFlow);
        Assert.Equal("软件可更新", dialog.Title);
        Assert.Contains("0.6.0-preview.1", dialog.Summary, StringComparison.Ordinal);
        Assert.Equal("下载更新包", dialog.PrimaryText);

        app.CanInstall = true;
        Assert.Equal("更新并重启", dialog.PrimaryText);

        app.IsBusy = true;
        Assert.False(dialog.CanPrimary);
        app.IsBusy = false;

        app.IsDownloading = true;
        app.ProgressPercent = 42;
        app.Status = "正在下载 8.4 / 20.0 MB";
        Assert.True(dialog.ShowProgress);
        Assert.Equal(42, dialog.ProgressPercent);
        Assert.Equal("正在下载 8.4 / 20.0 MB", dialog.Status);
        // 没在跑事情的时候不给「取消下载」挂一个死按钮。
        Assert.False(dialog.CanCancel);
        app.IsBusy = true;
        Assert.True(dialog.CanCancel);
    }

    [Fact]
    public void ApplicationFlowClosesOnceTheUpdateIsGone()
    {
        using var directory = new TemporaryDirectory();
        var app = CreateApplicationUpdate(new SettingsStore(Path.Combine(directory.Path, "updates.properties")));
        app.AvailableVersion = "0.6.0-preview.1";
        app.HasUpdate = true;
        using var dialog = QuickUpdateDialogViewModel.ForApplication(app);

        var closed = 0;
        dialog.CloseRequested += (_, _) => closed++;

        app.HasUpdate = false;
        Assert.Equal(1, closed);

        // 已经关掉以后不再重复触发（否则窗口会二次 Close）。
        app.HasUpdate = true;
        app.HasUpdate = false;
        Assert.Equal(1, closed);
    }

    [Fact]
    public void ApplicationSecondaryActionSkipsTheVersionAndCloses()
    {
        using var directory = new TemporaryDirectory();
        var settings = new SettingsStore(Path.Combine(directory.Path, "updates.properties"));
        var app = CreateApplicationUpdate(settings);
        app.AvailableVersion = "0.6.0-preview.1";
        app.HasUpdate = true;
        using var dialog = QuickUpdateDialogViewModel.ForApplication(app);

        var closed = false;
        dialog.CloseRequested += (_, _) => closed = true;
        dialog.SecondaryCommand.Execute(null);

        Assert.True(closed);
        Assert.Equal("0.6.0-preview.1", settings.Read()["app_update_skipped"]);
    }

    [Fact]
    public async Task CorePrimaryActionClosesFirstAndThenRunsTheApply()
    {
        var invoked = 0;
        var closed = 0;
        using var dialog = QuickUpdateDialogViewModel.ForCore(
            ManagedCoreVariant.Stable,
            "abcdef1234567890abcdef1234567890abcdef12",
            "9876543210fedcba9876543210fedcba98765432",
            "修复合并规则",
            applyCoreAsync: () =>
            {
                // 关窗必须发生在下载之前：线路确认与进度弹窗都是主窗口的模态框。
                Assert.Equal(1, closed);
                invoked++;
                return Task.CompletedTask;
            },
            openCorePage: () => { });
        dialog.CloseRequested += (_, _) => closed++;

        Assert.True(dialog.IsCoreFlow);
        Assert.Equal("核心可更新 · 稳定核心", dialog.Title);
        Assert.Equal("abcdef1 → 9876543", dialog.Summary);
        Assert.Equal("修复合并规则", dialog.Detail);

        await dialog.PrimaryCommand.ExecuteAsync(null);
        Assert.Equal(1, closed);
        Assert.Equal(1, invoked);
    }

    [Fact]
    public void CoreSecondaryActionOpensTheCorePageAndCloses()
    {
        var navigated = 0;
        using var dialog = QuickUpdateDialogViewModel.ForCore(
            ManagedCoreVariant.Dev,
            "1111111111111111111111111111111111111111",
            "2222222222222222222222222222222222222222",
            "",
            applyCoreAsync: () => Task.CompletedTask,
            openCorePage: () => navigated++);

        var closed = false;
        dialog.CloseRequested += (_, _) => closed = true;
        dialog.SecondaryCommand.Execute(null);

        Assert.Equal(1, navigated);
        Assert.True(closed);
        // 变体名要出现在标题里：三套核心各自有各自的更新，不能只写「核心可更新」。
        Assert.Equal("核心可更新 · 开发核心", dialog.Title);
        // 提交标题为空时给出影响面说明，而不是留一片空白。
        Assert.Contains("配置与日志不受影响", dialog.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CardIconsAreSvgPathDataRatherThanFontGlyphs()
    {
        Assert.StartsWith("M", QuickUpdateDialogViewModel.CoreIcon, StringComparison.Ordinal);
        Assert.StartsWith("M", QuickUpdateDialogViewModel.ApplicationIcon, StringComparison.Ordinal);
        Assert.True(QuickUpdateDialogViewModel.CoreIcon.Length > 40);
        Assert.DoesNotContain(QuickUpdateDialogViewModel.ApplicationIcon, QuickUpdateDialogViewModel.CoreIcon, StringComparison.Ordinal);
    }

    private static ApplicationUpdateViewModel CreateApplicationUpdate(ISettingsStore settings) =>
        new(settings, new RecordingDialogService(), new Notifications(), new Diagnostics());

    private sealed class Notifications : IDesktopNotificationService
    {
        public Task<DesktopNotificationResult> ShowAsync(string title, string message, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DesktopNotificationResult(true, "test"));
    }

    private sealed class Diagnostics : IAppDiagnostics
    {
        public string? LastDiagnostic { get; private set; }

        public void Record(string message, Exception? error = null) => LastDiagnostic = message;
    }
}
