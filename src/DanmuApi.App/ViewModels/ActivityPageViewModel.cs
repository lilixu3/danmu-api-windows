namespace DanmuApi.App.ViewModels;

public sealed class ActivityPageViewModel : ViewModelBase, IAsyncDisposable
{
    public ActivityPageViewModel(LogsPageViewModel logs)
    {
        Logs = logs ?? throw new ArgumentNullException(nameof(logs));
        Logs.Start();
    }

    public LogsPageViewModel Logs { get; }

    public ValueTask DisposeAsync() => Logs.DisposeAsync();
}
