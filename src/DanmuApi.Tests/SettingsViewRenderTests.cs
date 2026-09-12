using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DanmuApi.App.ViewModels;
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
            Assert.Equal(216, rail.Bounds.Width);

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
    /// 跨页约定：设置页与工具页的侧栏必须是同一套外观（216 宽 + 配置页的 rail 词汇）。
    /// 这两页并排切换时最容易被看出不一致，所以宽度与行的类名都要一致。
    /// 侧栏结构是静态 XAML，不依赖 DataContext，两页都可以直接实例化比较。
    /// </summary>
    [AvaloniaFact]
    public void SettingsAndToolsSidebarsStayTwins()
    {
        var settings = MeasureSidebar(new SettingsView());
        var tools = MeasureSidebar(new ToolsView());

        Assert.Equal(settings.Width, tools.Width);
        Assert.Equal(216, settings.Width);
        Assert.Equal(settings.ContainerClasses, tools.ContainerClasses);
        Assert.Equal(settings.ListClasses, tools.ListClasses);
        Assert.Equal(settings.TitleClasses, tools.TitleClasses);
        Assert.Equal(settings.CaptionClasses, tools.CaptionClasses);
    }

    /// <summary>
    /// rail 条目的「标题 + 说明」两行排版必须是全局样式，三页都真的生效。
    /// 这条防的正是本仓库反复出现的坑：类名写了、样式却只定义在某一页的页面作用域里，
    /// 于是另外两页的条目退化成裸文本（说明还会溢出侧栏）。判据取解析后的实际值，不看类名。
    /// </summary>
    [AvaloniaFact]
    public void RailItemTypographyIsAppliedOnAllThreePages()
    {
        AssertRailTypography(new ConfigurationView(), new ConfigurationCategory("api", "API 配置", "来自当前核心 envs.js", "\uE950"));
        AssertRailTypography(new ToolsView(), new ToolsSectionOption(ToolsSection.DanmuTest, "弹幕测试", "自动匹配、手动匹配和收藏"));
        AssertRailTypography(new SettingsView(), new SettingsCategory("general", "常规与启动", "关闭行为和开机自启"));
    }

    /// <summary>900 断点：设置页与工具页都必须是「900 并排、899 单列」，与配置页同一阈值。</summary>
    [AvaloniaTheory]
    [InlineData(900, true)]
    [InlineData(899, false)]
    public void SettingsWorkspaceCollapsesAtTheSharedBreakpoint(int width, bool sideBySide)
    {
        var view = new SettingsView();
        var window = new Window { Width = width, Height = 800, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            view.ApplyLayoutForWidth(width);
            window.UpdateLayout();

            var scroller = view.FindControl<ScrollViewer>("SettingsScroller")!;
            Assert.Equal(sideBySide ? 1 : 0, Grid.GetColumn(scroller));
            Assert.Equal(sideBySide ? 0 : 1, Grid.GetRow(scroller));
            Assert.Equal(sideBySide, view.FindControl<StackPanel>("RailHeader")!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertRailTypography<TView, TItem>(TView view, TItem item)
        where TView : UserControl
    {
        // 条目数据直接塞进 rail 列表：这条只验证两行排版，不依赖整页 ViewModel。
        var window = new Window { Width = 1280, Height = 800, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var list = view.GetVisualDescendants().OfType<ListBox>()
                .Single(box => box.Classes.Contains("rail-list"));
            list.ItemsSource = new[] { item };
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var title = list.GetVisualDescendants().OfType<TextBlock>()
                .Single(block => block.Classes.Contains("rail-title"));
            var caption = list.GetVisualDescendants().OfType<TextBlock>()
                .Single(block => block.Classes.Contains("rail-caption"));

            Assert.Equal(FontWeight.SemiBold, title.FontWeight);
            Assert.Equal(12.5, title.FontSize);
            Assert.Equal(10.5, caption.FontSize);
            Assert.True(caption.FontSize < title.FontSize, "说明字号必须小于标题");
            Assert.NotEqual(title.Foreground?.ToString(), caption.Foreground?.ToString());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// 内容块必须用满内容列。之前它带着 <c>MaxWidth="940"</c> + Stretch，在宽窗口里被居中，
    /// 两侧各留几百像素空白——窗口一最大化就看得很明显（用户反馈的正是这条）。
    /// 判定不靠看图：按内容块左右两侧的剩余空白算，超过页面内边距量级就说明又被居中收窄了。
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1280)]
    [InlineData(1920)]
    [InlineData(2560)]
    public void SettingsContentUsesTheWholeContentColumn(int width)
    {
        var model = CreateViewModel(new RecordingSettingsStore());
        var view = new SettingsView { DataContext = model };
        var window = new Window { Width = width, Height = 1000, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var content = view.GetVisualDescendants().OfType<ScrollViewer>()
                .First(scroller => Grid.GetColumn(scroller) == 1);
            var panel = Assert.IsType<StackPanel>(content.Content);
            // 内容块的父级是 ScrollViewer 模板里的 presenter，Bounds.X 与内容列不同坐标系，
            // 必须换算到同一个祖先下比较。
            var contentOrigin = content.TranslatePoint(default, view)
                ?? throw new InvalidOperationException("内容列不在可视树中");
            var panelOrigin = panel.TranslatePoint(default, view)
                ?? throw new InvalidOperationException("内容块不在可视树中");

            DumpRender(window, $"settings-{width}");

            const double tolerance = 32;
            var leftGap = panelOrigin.X - contentOrigin.X;
            var rightGap = contentOrigin.X + content.Bounds.Width - (panelOrigin.X + panel.Bounds.Width);
            Assert.True(
                leftGap <= tolerance && rightGap <= tolerance,
                $"{width} 宽下内容块被收窄居中：内容列 {content.Bounds.Width:0.#}，内容块 {panel.Bounds.Width:0.#}，" +
                $"左空白 {leftGap:0.#}，右空白 {rightGap:0.#}");
        }
        finally
        {
            window.Close();
        }
    }

    private static void DumpRender(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("DANMU_TEST_RENDER_DIRECTORY");
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
            new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(directory, $"{name}.png"));
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
