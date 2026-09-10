using System.Reflection;

namespace DanmuApi.App.ViewModels;

public sealed class AboutPageViewModel(MainWindowViewModel shell) : ViewModelBase
{
    public MainWindowViewModel Shell { get; } = shell;
    public string Version => typeof(AboutPageViewModel).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AboutPageViewModel).Assembly.GetName().Version?.ToString()
        ?? "未提供版本信息";
}
