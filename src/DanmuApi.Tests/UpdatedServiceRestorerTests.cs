using DanmuApi.App.Services;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class UpdatedServiceRestorerTests
{
    [Fact]
    public async Task PreviouslyStoppedServiceIsNotStarted()
    {
        using var directory = new TemporaryDirectory();
        var controller = new Controller(false);
        await UpdatedServiceRestorer.RestoreAsync(false, controller, Path.Combine(directory.Path,"result"), new Diagnostics(),new RecordingDialogService());
        Assert.Equal(0,controller.Starts);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServiceResultDoesNotUndoApplicationUpdate(bool fail)
    {
        using var directory = new TemporaryDirectory();
        var controller = new Controller(fail);
        var dialogs = new RecordingDialogService();
        var report = Path.Combine(directory.Path,"result");
        await UpdatedServiceRestorer.RestoreAsync(true,controller,report,new Diagnostics(),dialogs);
        Assert.Equal(1,controller.Starts);
        Assert.Contains("软件更新已完成",File.ReadAllText(report));
        if(fail) Assert.Contains(dialogs.Messages,item=>item.IsError&&item.Message.Contains("服务恢复失败",StringComparison.Ordinal));
        else Assert.Empty(dialogs.Messages);
    }
    private sealed class Diagnostics:IAppDiagnostics
    {
        public string? LastDiagnostic{get;private set;}
        public void Record(string message,Exception? error=null)=>LastDiagnostic=message;
    }
    private sealed class Controller(bool fail):IRuntimeController
    {
        public int Starts{get;private set;}
        public RuntimeSnapshot Snapshot{get;private set;}=new(DesktopRuntimeState.Stopped);
        public event EventHandler<RuntimeSnapshot>? SnapshotChanged {add{} remove{}}
        public Task StartAsync(CancellationToken cancellationToken=default){Starts++;if(fail)throw new IOException("start failure");Snapshot=new(DesktopRuntimeState.Running);return Task.CompletedTask;}
        public Task StopAsync(CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task RestartAsync(CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task ShutdownAsync(CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task<AdoptionResult> AdoptAsync(CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public string? ReconcileLiveness()=>null;
    }
}
