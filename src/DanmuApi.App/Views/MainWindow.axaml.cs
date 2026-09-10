using Avalonia.Controls;
using Avalonia.Styling;
using DanmuApi.App.Services;

namespace DanmuApi.App.Views;

public partial class MainWindow : Window
{
    private AppLifecycleCoordinator? _lifecycleCoordinator;

    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => ApplyTitleBarTheme();
        ActualThemeVariantChanged += (_, _) => ApplyTitleBarTheme();
    }

    /// <summary>Optional diagnostics sink; startup wiring sets it so a failed title-bar theme is recorded.</summary>
    public IAppDiagnostics? Diagnostics { get; set; }

    public void AttachLifecycle(AppLifecycleCoordinator lifecycleCoordinator)
    {
        _lifecycleCoordinator = lifecycleCoordinator ?? throw new ArgumentNullException(nameof(lifecycleCoordinator));
        Closing += OnClosing;
        Activated += OnActivated;
    }

    private void ApplyTitleBarTheme()
    {
        if (!ImmersiveTitleBar.TryApply(this, ActualThemeVariant == ThemeVariant.Dark, out var error))
            Diagnostics?.Record($"设置窗口标题栏深浅色失败: {error}");
    }

    private void OnActivated(object? sender, EventArgs args)
    {
        var coordinator = _lifecycleCoordinator
            ?? throw new InvalidOperationException("主窗口生命周期协调器尚未接入");
        coordinator.NotifyMainWindowActivated();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        var coordinator = _lifecycleCoordinator
            ?? throw new InvalidOperationException("主窗口生命周期协调器尚未接入");
        _ = coordinator.HandleMainWindowClosingAsync(this, args);
    }
}
