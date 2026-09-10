namespace DanmuApi.App.ViewModels;

public sealed record NavigationItem(string Key, string Title, string Description, string Icon);

public sealed record PlaceholderPageViewModel(string Title, string Description, string Icon)
{
    public string StatusText => "此模块尚未接入当前测试版本";
}
