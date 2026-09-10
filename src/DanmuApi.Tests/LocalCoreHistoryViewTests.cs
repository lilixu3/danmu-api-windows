using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DanmuApi.App.Views;

namespace DanmuApi.Tests;

public sealed partial class CorePageViewModelTests
{
    [AvaloniaFact]
    public async Task LocalHistoryRestoreButtonRequiresConfirmationAndPassesSelectedRecord()
    {
        var record = new DanmuApi.Core.CoreVersionRecord("previous-local", DanmuApi.Core.ManagedCoreVariant.Stable, "history", Installed().Manifest!);
        var management = new RecordingManagementService { Installation = Installed(), LocalHistory = [record] };
        var dialogs = new RecordingDialogService();
        var model = CreateViewModel(management, new RecordingRoutePreferenceStore(false), dialogs);
        var view = new CorePageView { DataContext = model };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        window.Show();
        try
        {
            await model.OpenHistoryPageCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            var button = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<Button>()
                .Single(item => Equals(item.Content, "恢复此本地版本"));
            Assert.Same(model.RollbackCommand, button.Command);
            Assert.Same(record, button.CommandParameter);
            await model.RollbackCommand.ExecuteAsync(button.CommandParameter);
            Assert.Null(management.RestoredHistoryId);
            dialogs.Confirmation = true;
            await model.RollbackCommand.ExecuteAsync(button.CommandParameter);
            Assert.Equal(record.Id, management.RestoredHistoryId);
            Assert.Empty(dialogs.RoutePrompts);
            Assert.False(Assert.Single(dialogs.Messages).IsError);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LocalHistoryCanBeOpenedWithoutNetworkAndReturnedFrom()
    {
        var dialogs=new RecordingDialogService();
        var model=CreateViewModel(new RecordingManagementService(),new RecordingRoutePreferenceStore(false),dialogs);
        var window=new Window{Width=1000,Height=700,Content=new CorePageView{DataContext=model}};
        window.Show();
        try
        {
            await model.OpenHistoryPageCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(model.IsHistoryPage);
            Assert.False(model.IsOverviewPage);
            Assert.False(model.HasLocalHistory);
            Assert.Empty(dialogs.RoutePrompts);
            model.BackToOverviewCommand.Execute(null);
            Assert.True(model.IsOverviewPage);
            Assert.False(model.IsHistoryPage);
        }
        finally{window.Close();}
    }
}
