using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DanmuApi.Tests;

/// <summary>
/// 列表行悬停底色的回归守卫。
///
/// 背景：全局 <c>ListBoxItem:pointerover</c> 曾用 <c>LogRowHoverBrush</c>（浅色主题下是
/// #18202B 的近黑色），那是给日志页深色终端用的。它全局生效会让所有列表在浅色主题下
/// 变成「深底 + 深字」，完全看不清——这个 bug 前后被反馈过两次。
///
/// 这里断言的是用户真正感知到的属性：浅色主题下悬停底色必须是亮的（可读），
/// 并且日志终端那块深色面板里的悬停仍然是暗的。不直接比较 token 颜色值，
/// 这样调整配色时测试不会因为数值变动而误报。
/// </summary>
public sealed class ListHoverContrastTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ListRowHoverIsReadableInBothThemes(bool dark)
    {
        using var scope = new ThemeScope(dark ? ThemeVariant.Dark : ThemeVariant.Light);
        var color = ResolveHoverBackground(isLogTerminal: false);

        if (dark)
        {
            // 深色主题：悬停底应该是深色面，不能亮成白块。
            Assert.True(Luminance(color) < 0.35, $"深色主题悬停底过亮：{color}");
        }
        else
        {
            // 浅色主题：悬停底必须是亮的。这就是被反馈两次的那个 bug 的判据。
            Assert.True(Luminance(color) > 0.7, $"浅色主题悬停底过暗（黑底深字）：{color}");
        }
    }

    /// <summary>
    /// 日志终端是深色面板、行文字是浅色，所以它的悬停底色在浅色主题下也必须保持深色；
    /// 与上一条一起锁住「按子树限定」这个做法。
    /// </summary>
    [AvaloniaFact]
    public void LogTerminalRowsKeepDarkHoverInLightTheme()
    {
        using var scope = new ThemeScope(ThemeVariant.Light);
        var normal = ResolveHoverBackground(isLogTerminal: false);
        var terminal = ResolveHoverBackground(isLogTerminal: true);

        Assert.True(Luminance(normal) > 0.7, $"普通列表悬停底应为亮色：{normal}");
        Assert.True(Luminance(terminal) < 0.35, $"日志终端悬停底应为深色：{terminal}");
    }

    /// <summary>
    /// 深色终端里的文字必须用 Log* 这套为深色面配的 token，不能用页面级的
    /// TextSecondaryBrush：浅色主题下终端底色是 #0F141C（近黑），而 TextSecondaryBrush 是
    /// #5D6672（深灰），对比度只有约 3.2:1，低于小字号 4.5:1 的可读线，空状态提示基本看不清。
    /// 这条按对比度断言，避免以后有人把终端里的文字改回页面级类名。
    /// </summary>
    [AvaloniaFact]
    public void LogTerminalEmptyStateTextStaysReadableOnDarkSurface()
    {
        using var scope = new ThemeScope(ThemeVariant.Light);

        var terminal = new Border { Classes = { "log-terminal" } };
        var message = new TextBlock { Classes = { "terminal-text" }, Text = "占位" };
        terminal.Child = message;
        var window = new Window { Width = 400, Height = 200, Content = terminal };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var surface = Assert.IsAssignableFrom<ISolidColorBrush>(terminal.Background).Color;
            var text = Assert.IsAssignableFrom<ISolidColorBrush>(message.Foreground).Color;
            Assert.True(Luminance(surface) < 0.35, $"终端底色应为深色：{surface}");
            var ratio = ContrastRatio(surface, text);
            Assert.True(ratio >= 4.5, $"终端内文字对比度不足（{ratio:0.0}:1）：文字 {text} / 底色 {surface}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>WCAG 对比度（明度比）。</summary>
    private static double ContrastRatio(Color first, Color second)
    {
        var a = Luminance(first);
        var b = Luminance(second);
        var lighter = Math.Max(a, b);
        var darker = Math.Min(a, b);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static Color ResolveHoverBackground(bool isLogTerminal)
    {
        var list = new ListBox { ItemsSource = new[] { "row" }, Width = 240, Height = 60 };
        Control content = isLogTerminal
            ? new Border { Classes = { "log-terminal" }, Child = list }
            : list;
        var window = new Window { Width = 320, Height = 140, Content = content };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var container = list.GetVisualDescendants().OfType<ListBoxItem>().First();
            ((IPseudoClasses)container.Classes).Set(":pointerover", true);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var presenter = container.GetVisualDescendants().OfType<ContentPresenter>().First();
            // 主题字典里的画刷解析出来是不可变实现，按接口取色即可。
            return Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color;
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>相对亮度（sRGB 线性化后加权），只用来判断「亮 / 暗」。</summary>
    private static double Luminance(Color color) =>
        (0.2126 * Linear(color.R)) + (0.7152 * Linear(color.G)) + (0.0722 * Linear(color.B));

    private static double Linear(byte channel)
    {
        var value = channel / 255.0;
        return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private sealed class ThemeScope : IDisposable
    {
        private readonly ThemeVariant? _original;

        public ThemeScope(ThemeVariant variant)
        {
            _original = Application.Current!.RequestedThemeVariant;
            Application.Current.RequestedThemeVariant = variant;
        }

        public void Dispose() => Application.Current!.RequestedThemeVariant = _original;
    }
}
