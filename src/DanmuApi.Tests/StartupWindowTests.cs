using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DanmuApi.App.Views;

namespace DanmuApi.Tests;

public sealed class StartupWindowTests
{
    [AvaloniaFact]
    public void PhaseCompletionExplainsRemainingWorkAndNextPhaseResetsBar()
    {
        var window = new StartupWindow();
        window.Show();
        try
        {
            var bar = Assert.Single(window.GetVisualDescendants().OfType<ProgressBar>());
            foreach (var (phase, next) in new[] { ("VerifyingSource", "备份"), ("BackingUp", "暂存"),
                ("Staging", "替换"), ("Replacing", "清理"), ("Snapshotting", "提交"), ("CleaningUp", "继续启动") })
            {
                window.ShowProgress(new(phase, 3, 3));
                Assert.Equal(100, bar.Value);
                Assert.False(bar.IsIndeterminate);
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text =>
                    text.Text?.Contains("当前阶段 3 / 3", StringComparison.Ordinal) == true && text.Text.Contains(next, StringComparison.Ordinal));
                window.ShowProgress(new("BackingUp", 0, 3));
                Assert.Equal(0, bar.Value);
            }
            window.ShowProgress(new("InspectingCleanup", 120, 0));
            Assert.True(bar.IsIndeterminate);
            Assert.Equal(0, bar.Value);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.Contains("120 / 0", StringComparison.Ordinal) == true);
            window.ShowProgress(new("Recovering", 0, 0));
            Assert.True(bar.IsIndeterminate);
            window.ShowProgress(new("Completed", 1, 1));
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.Contains("继续应用启动", StringComparison.Ordinal) == true);
            window.ShowFailure("磁盘拒绝访问");
            Assert.False(bar.IsVisible);
            window.ShowProgress(new("ReadingManifest", 0, 1));
            Assert.True(bar.IsVisible);
            Assert.Equal(0, bar.Value);
        }
        finally { window.AllowClose = true; window.Close(); }
    }

    [AvaloniaFact]
    public void PreparationWindowShowsProgressAndExplicitFailureBeforeMainWindowExists()
    {
        var window = new StartupWindow();
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.IsVisible);
            Assert.True(Assert.Single(window.GetVisualDescendants().OfType<ProgressBar>()).IsIndeterminate);
            window.ShowFailure("运行环境校验失败：node.exe");
            Assert.False(Assert.Single(window.GetVisualDescendants().OfType<ProgressBar>()).IsVisible);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.Contains("运行环境校验失败：node.exe", StringComparison.Ordinal) == true);
            Assert.Single(window.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "取消启动"));
        }
        finally { window.AllowClose = true; window.Close(); }
    }
}
