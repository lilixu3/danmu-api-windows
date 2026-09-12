namespace DanmuApi.App.Services;

/// <summary>
/// 外壳页面跳转的迟绑定入口：工具页里的页面（例如本地弹幕的「核心配置」）需要跳到
/// 其它页面，但它们在 MainWindowViewModel 之前就完成装配，构造期拿不到外壳。
/// 由 App 在外壳就绪后把 <c>MainWindowViewModel.NavigateTo</c> 填进来。
/// </summary>
public sealed class ShellNavigationAccessor
{
    public Action<string>? NavigateTo { get; set; }
}
