using DanmuApi.Core;

namespace DanmuApi.Tests;

public sealed partial class CorePageViewModelTests
{
    [Fact]
    public async Task PendingUpdateShowsProgressAndCancellationRestoresCommands()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new StubScheduler(null)
        {
            ManualOperation = async token =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("unreachable");
            },
        };
        var dialogs = new RecordingDialogService();
        var model = CreateViewModel(new RecordingManagementService { Installation = Installed() },
            new RecordingRoutePreferenceStore(true), dialogs, scheduler: scheduler);

        var operation = model.CheckUpdateCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(model.IsBusy);
        Assert.Equal(["检查核心更新"], dialogs.ProgressTitles);
        dialogs.ActiveProgressCancellation!.Cancel();
        await operation.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(model.IsBusy);
        Assert.True(model.CheckUpdateCommand.CanExecute(null));
        Assert.Contains(dialogs.Messages, message => message.Title.Contains("已取消", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AsynchronousHttpFailureIsReportedAndDoesNotChangeRoutes()
    {
        var scheduler = new StubScheduler(null)
        {
            ManualOperation = async _ =>
            {
                await Task.Yield();
                throw new GithubRemoteException(GithubFailureKind.Http, "HTTP 503");
            },
        };
        var dialogs = new RecordingDialogService();
        var routes = new RecordingRoutePreferenceStore(true);
        var model = CreateViewModel(new RecordingManagementService { Installation = Installed() }, routes, dialogs, scheduler: scheduler);

        await model.CheckUpdateCommand.ExecuteAsync(null);

        Assert.False(model.IsBusy);
        Assert.Equal(0, routes.InvalidateCalls);
        Assert.Contains(dialogs.Messages, message => message.IsError && message.Message.Contains("HTTP 503", StringComparison.Ordinal));
    }
}
