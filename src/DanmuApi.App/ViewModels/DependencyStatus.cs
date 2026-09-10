namespace DanmuApi.App.ViewModels;

/// <summary>Compact dependency-health state for the core page footer; the long-form
/// status text stays in the details panel.</summary>
public enum DependencyStatus
{
    Unknown,
    Busy,
    Healthy,
    Warning,
    Failed,
    Canceled,
}
