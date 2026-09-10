using System.ComponentModel;

namespace DanmuApi.App.ViewModels;

public sealed class OverviewPageViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _shell;

    public OverviewPageViewModel(MainWindowViewModel shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        // Navigation creates fresh page models; the shell must not retain old pages.
        var observer = new PageObserver(this);
        Shell.PropertyChanged += observer.OnChanged;
        Trend.PropertyChanged += observer.OnChanged;
    }

    public MainWindowViewModel Shell => _shell;
    public RequestRateHistory Trend => _shell.RequestTrend;
    public string ServiceTitle => Shell.Runtime.State switch
    {
        DanmuApi.Runtime.DesktopRuntimeState.Running => "运行中",
        DanmuApi.Runtime.DesktopRuntimeState.Stopped => "已停止",
        _ => Shell.StatusText,
    };
    public bool HasFailure => Shell.Runtime.State == DanmuApi.Runtime.DesktopRuntimeState.Failed;
    public bool ShowRestart => Shell.IsServiceRunning && Shell.CanRestart;
    public bool HasTrendSamples => Shell.IsServiceRunning && Trend.Points.Any(point => point.Rate.HasValue);

    private sealed class PageObserver(OverviewPageViewModel page)
    {
        private readonly WeakReference<OverviewPageViewModel> _page = new(page);

        public void OnChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (!_page.TryGetTarget(out var target))
            {
                if (sender is INotifyPropertyChanged source) source.PropertyChanged -= OnChanged;
                return;
            }
            if (args.PropertyName == nameof(MainWindowViewModel.StatusText))
            {
                target.OnPropertyChanged(nameof(ServiceTitle));
                target.OnPropertyChanged(nameof(HasFailure));
            }
            if (args.PropertyName is nameof(MainWindowViewModel.IsServiceRunning) or nameof(MainWindowViewModel.CanRestart))
                target.OnPropertyChanged(nameof(ShowRestart));
            if (args.PropertyName is nameof(RequestRateHistory.Points) or nameof(MainWindowViewModel.IsServiceRunning))
                target.OnPropertyChanged(nameof(HasTrendSamples));
        }
    }
}
