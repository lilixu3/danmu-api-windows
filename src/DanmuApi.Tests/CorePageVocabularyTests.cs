using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DanmuApi.App.Views;

namespace DanmuApi.Tests;

/// <summary>
/// 核心页重构后的词汇契约（不靠看图）。
///
/// 核心页与前面几页不同：它带一套自己的版式词汇（workbench 通版卡片 + wb-* 层次 +
/// variant-card 主开关 + row-entry 行入口）。这套词汇此前定义在 App.axaml 里，
/// 但全应用只有本页在用 —— 等于页面私有词汇混进了公共词汇表，本轮已搬进
/// CorePageView.axaml 的 UserControl.Styles。这里锁住搬迁后的两件事：
/// 1) 页面私有词汇仍然生效（样式确实被解析到，而不是搬丢了变成默认外观）；
/// 2) 通用角色（按钮、等宽文字、状态胶囊）已换成共用外壳词汇。
/// </summary>
public sealed partial class CorePageViewModelTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorePageKeepsItsLocalWorkbenchVocabulary(bool dark)
    {
        var model = CreateViewModel(new RecordingManagementService { Installation = Installed() },
            new RecordingRoutePreferenceStore(true), new RecordingDialogService());
        var view = new CorePageView { DataContext = model };
        var window = CreateCoreWindow(view, dark);
        try
        {
            var workbench = view.FindControl<Border>("WorkbenchPanel")!;

            // workbench 是「通版卡片」：靠 ClipToBounds 让子块的直角不盖住卡片圆角，
            // 并且不带内边距（内边距由子块 wb-strip 提供），换成 Shell 的 card 会破坏这两点。
            Assert.Contains("workbench", workbench.Classes);
            Assert.True(workbench.ClipToBounds, "workbench 必须裁剪，否则通版子块会盖住圆角");
            Assert.Equal(new CornerRadius(10), workbench.CornerRadius);
            Assert.NotNull(workbench.Background);

            // 搬迁后 wb-strip 仍然生效（Padding 是它撑开通版分隔线的关键值）。
            var strips = workbench.GetVisualDescendants().OfType<Border>()
                .Where(border => border.Classes.Contains("wb-strip"))
                .ToList();
            Assert.NotEmpty(strips);
            Assert.All(strips, strip => Assert.True(strip.Padding.Left > 0, "wb-strip 必须有内边距"));

            // 变体选择条的两个卡片是 row-entry/variant-card 级别的主开关，必须仍然是 Button 且渲染出宽度。
            var variantCards = view.GetVisualDescendants().OfType<Button>()
                .Where(button => button.Classes.Contains("variant-card"))
                .ToList();
            Assert.NotEmpty(variantCards);
            Assert.All(variantCards, card => Assert.True(card.Bounds.Width > 0));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// 状态片统一成外壳词汇的 chip：状态条里的三处状态（运行环境 / 核心依赖 / 可选 Redis）
    /// 加上身份带的安装状态，都不再是旧的 status-pill。
    /// chip 与 status-pill 的差别是圆角由 5 变成全圆角，这是本轮有意的视觉统一。
    /// </summary>
    [AvaloniaFact]
    public void CorePageStatusBadgesUseSharedChips()
    {
        var model = CreateViewModel(new RecordingManagementService { Installation = Installed() },
            new RecordingRoutePreferenceStore(true), new RecordingDialogService());
        var view = new CorePageView { DataContext = model };
        var window = CreateCoreWindow(view, false);
        try
        {
            var chips = view.GetVisualDescendants().OfType<Border>()
                .Where(border => border.Classes.Contains("chip"))
                .ToList();
            Assert.NotEmpty(chips);

            // 状态条本身仍然只读：里面一个按钮都不能有（原有约束，这里一并守住）。
            var statusBar = view.FindControl<Border>("StatusBar")!;
            Assert.Empty(statusBar.GetVisualDescendants().OfType<Button>());
            Assert.Contains(statusBar.GetVisualDescendants().OfType<Border>(),
                border => border.Classes.Contains("chip"));

            // 旧的 status-pill 不许再出现在可视树里。
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Border>(),
                border => border.Classes.Contains("status-pill"));
        }
        finally
        {
            window.Close();
        }
    }
}
