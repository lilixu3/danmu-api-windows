using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
using DanmuApi.App.Views;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed partial class MainWindowViewModelBehaviorTests
{
    [AvaloniaFact]
    public async Task MainWindowRendersAndNavigatesWhilePreparationBlocksStartThenShowsFailure()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(directory.Path, "test-token");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var preparation = new RuntimePreparationService(async (_, progress, token) =>
        {
            progress(new("VerifyingSource", 2, 10));
            entered.SetResult();
            await finish.Task.WaitAsync(token);
            throw new IOException("需要确认：依赖损坏");
        });
        await using var model = CreateViewModel(paths, new RecordingSettingsStore(),
            new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped)), preparation: preparation);
        var window = new MainWindow { DataContext = model };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(window.IsVisible);
            Assert.Equal(RuntimePreparationState.Pending, preparation.Snapshot.State);
            Assert.False(model.StartCommand.CanExecute(null));
            SaveThemeRender(window, "startup-main-first-frame-pending.png");
            var task = preparation.PrepareAsync();
            await entered.Task;
            Dispatcher.UIThread.RunJobs();
            model.NavigateTo("settings");
            window.UpdateLayout();
            Assert.Equal("settings", model.SelectedNavigationItem.Key);
            Assert.False(model.CanStart);
            Assert.Equal(20, model.PreparationProgress);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == model.PreparationMessage);
            SaveThemeRender(window, "startup-main-preparing-settings.png");
            finish.SetResult();
            await task;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(model.CanRepairPreparation);
            Assert.Contains("依赖损坏", model.PreparationMessage);
            Assert.False(model.CanStart);
            SaveThemeRender(window, "startup-main-failed-repair.png");
        }
        finally { finish.TrySetResult(); window.Close(); }
    }

    [AvaloniaFact]
    public void MainWindowReservesADragStripAndExtendsIntoTheTitleBarArea()
    {
        var window = new MainWindow();
        try
        {
            Assert.True(window.ExtendClientAreaToDecorationsHint);
            Assert.Equal(ExtendClientAreaChromeHints.PreferSystemChrome, window.ExtendClientAreaChromeHints);
            Assert.Equal(32, window.ExtendClientAreaTitleBarHeightHint);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var strip = window.FindControl<Grid>("TitleBarStrip");
            Assert.NotNull(strip);
            Assert.Equal(32, strip!.Bounds.Height);
            // The strip must not swallow caption dragging.
            Assert.False(strip.IsHitTestVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task PreparationProgressCoalescesUiUpdatesAndLeavesEndpointStateAlone()
    {
        using var directory = new TemporaryDirectory();
        var paths = CreateRuntime(directory.Path, "test-token");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<DanmuApi.Platform.BundledRuntimeProgress>? progressSink = null;
        await using var preparation = new RuntimePreparationService(async (_, progress, token) =>
        {
            progressSink = progress;
            entered.SetResult();
            await finish.Task.WaitAsync(token);
        });
        await using var model = CreateViewModel(paths, new RecordingSettingsStore(),
            new RecordingRuntimeController(new RuntimeSnapshot(DesktopRuntimeState.Stopped)), preparation: preparation);
        var messageUpdates = 0;
        var endpointUpdates = 0;
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.PreparationMessage)) messageUpdates++;
            if (args.PropertyName == nameof(MainWindowViewModel.EndpointItems)) endpointUpdates++;
        };
        var task = preparation.PrepareAsync();
        await entered.Task;
        // Fire the whole stream without yielding: only a coalesced update may be queued for the UI.
        for (var i = 0; i < 5000; i++) progressSink!(new("CheckingTargetMetadata", i + 1, 5000));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, messageUpdates);
        Assert.Contains("5000/5000", model.PreparationMessage);
        // Preparation progress must not rebuild endpoints (network enumeration) on the UI thread.
        Assert.Equal(0, endpointUpdates);
        finish.SetResult();
        await task;
        Dispatcher.UIThread.RunJobs();
        Assert.False(model.ShowPreparation);
        Assert.True(model.CanStart);
    }
}
