using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DanmuApi.App.Views;

namespace DanmuApi.Tests;

/// <summary>
/// 设置页的可视契约（不靠看图）。
/// 设置页是全应用第二个「分类侧栏 + 内容区」的页面，工具页的侧栏刻意与它保持一致，
/// 所以这里既锁设置页自身，也锁「两页侧栏是同一套外观」这个跨页约定。
/// </summary>
public sealed partial class SettingsPageViewModelTests
{
    [AvaloniaTheory]
    [InlineData(960)]
    [InlineData(1280)]
    [InlineData(1920)]
    public void SettingsViewKeepsRailSidebarAndContentColumns(int width)
    {
        var model = CreateViewModel(new RecordingSettingsStore());
        var view = new SettingsView { DataContext = model };
        var window = new Window { Width = width, Height = 800, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            // 侧栏容器用 rail（新词汇），列表用 rail-list；内容区在第二列。
            var rail = view.GetVisualDescendants().OfType<Border>()
                .Single(border => border.Classes.Contains("rail"));
            var list = rail.GetVisualDescendants().OfType<ListBox>().Single();
            Assert.Contains("rail-list", list.Classes);
            Assert.DoesNotContain("settings-list", list.Classes);
            Assert.Equal(208, rail.Bounds.Width);

            var content = view.GetVisualDescendants().OfType<ScrollViewer>()
                .First(scroller => Grid.GetColumn(scroller) == 1);
            Assert.True(content.Bounds.Width > 0, "内容区必须有实际宽度");
            Assert.True(rail.Bounds.Bottom <= window.Height, $"侧栏纵向溢出：{rail.Bounds}");
            Assert.True(content.Bounds.Right <= window.Width + 0.5, $"内容区横向溢出：{content.Bounds}");

            // 分类行渲染出标题与说明两行。
            var texts = list.GetVisualDescendants().OfType<TextBlock>()
                .Select(block => block.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToList();
            Assert.Contains("主题与外观", texts);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// 主题三选项靠 <c>theme-option</c> / <c>theme-option-active</c> 这对类名表达选中态，
    /// 主题切换测试就是按这对类名断言的，不能改掉；这里再确认三个按钮都还在原位。
    /// </summary>
    [AvaloniaFact]
    public void SettingsViewKeepsThemeOptionClassContract()
    {
        var model = CreateViewModel(new RecordingSettingsStore());
        model.SelectedCategory = model.Categories.Single(category => category.Key == "theme");
        var view = new SettingsView { DataContext = model };
        var window = new Window { Width = 1200, Height = 800, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            foreach (var name in new[] { "ThemeSystemOption", "ThemeLightOption", "ThemeDarkOption" })
            {
                var option = view.FindControl<Button>(name);
                Assert.NotNull(option);
                Assert.Contains("theme-option", option!.Classes);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// 跨页约定：设置页与工具页的侧栏必须是同一套外观。
    /// 这两页并排切换时最容易被看出不一致，所以宽度与行的类名都要一致。
    /// 侧栏结构是静态 XAML，不依赖 DataContext，两页都可以直接实例化比较。
    /// </summary>
    [AvaloniaFact]
    public void SettingsAndToolsSidebarsStayTwins()
    {
        var settings = MeasureSidebar(new SettingsView());
        var tools = MeasureSidebar(new ToolsView());

        Assert.Equal(settings.Width, tools.Width);
        Assert.Equal(208, settings.Width);
        Assert.Equal(settings.ContainerClasses, tools.ContainerClasses);
        Assert.Equal(settings.ListClasses, tools.ListClasses);
        Assert.Equal(settings.TitleClasses, tools.TitleClasses);
        Assert.Equal(settings.CaptionClasses, tools.CaptionClasses);
    }

    private static (double Width, string ContainerClasses, string ListClasses, string TitleClasses, string CaptionClasses)
        MeasureSidebar(UserControl view)
    {
        var window = new Window { Width = 1280, Height = 800, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var rail = view.GetVisualDescendants().OfType<Border>()
                .First(border => border.Classes.Contains("rail"));
            var list = rail.GetVisualDescendants().OfType<ListBox>().First();
            // 分类行的标题与说明：靠 rail-title / rail-caption 两个类名区分主次。
            var title = list.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(block => block.Classes.Contains("rail-title"));
            var caption = list.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(block => block.Classes.Contains("rail-caption"));

            return (
                rail.Bounds.Width,
                string.Join(",", rail.Classes.OrderBy(name => name, StringComparer.Ordinal)),
                string.Join(",", list.Classes.OrderBy(name => name, StringComparer.Ordinal)),
                title is null ? "<none>" : string.Join(",", title.Classes.OrderBy(name => name, StringComparer.Ordinal)),
                caption is null ? "<none>" : string.Join(",", caption.Classes.OrderBy(name => name, StringComparer.Ordinal)));
        }
        finally
        {
            window.Close();
        }
    }
}
