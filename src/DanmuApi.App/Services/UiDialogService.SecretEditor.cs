using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace DanmuApi.App.Services;

/// <summary>
/// 敏感值输入框：默认遮罩，右侧中间嵌一个眼睛图标，点一下显示/隐藏。
///
/// 用图标而不是「显示敏感值」按钮，是因为按钮会跟「取消 / 保存」排在同一行，
/// 三个按钮里有两个是动作、一个是输入控件开关，视觉上分不清主次；
/// 眼睛贴在输入框里表达的是「这是这个输入框自己的显示开关」，语义更准也更省空间。
///
/// 图标用自绘 Path 几何，不引第三方图标库（避免许可与字体依赖）。
/// </summary>
public sealed partial class UiDialogService
{
    /// <summary>
    /// 构造敏感值输入区：返回可直接放进对话框布局的容器，
    /// 并通过 <paramref name="input"/> 暴露文本框（读值、设初始值都走它）。
    /// </summary>
    private static Control CreateSecretEditor(string initial, string watermark, out TextBox input)
    {
        input = new TextBox
        {
            Text = initial,
            PasswordChar = '•',
            Watermark = watermark,
        };
        return AttachSecretToggle(input);
    }

    /// <summary>
    /// 给已有的文本框加眼睛图标（外部先造好 TextBox 的场合用它）。
    /// 返回包住文本框的容器，调用方把这个容器放进布局即可。
    /// internal 供测试直接断言「图标在框内、点击能切换遮罩」。
    /// </summary>
    internal static Control AttachSecretToggle(TextBox input)
    {
        ArgumentNullException.ThrowIfNull(input);
        input.PasswordChar = '•';
        input.MinHeight = 34;
        // 右侧留出眼睛的位置，否则长值会滑到图标底下（被挡住的正好是最关键的尾部字符）。
        input.Padding = new Thickness(10, 6, 38, 6);

        // 眼睛轮廓与瞳孔必须画在同一个 Path 里：Path 的 Stretch 是各自独立生效的，
        // 拆成两个 Path 时瞳孔（几何边界只有 7×7）会被单独放大到占满整个控件，
        // 变成一个糊住轮廓的实心圆点——图标就完全看不出是眼睛了。
        // 同一份几何共享一次缩放，比例才不会走样。
        var eye = new ShapePath
        {
            Data = Geometry.Parse(
                // 杏仁形外轮廓
                "M2,12 C5,6.5 8.4,4.6 12,4.6 C15.6,4.6 19,6.5 22,12 " +
                "C19,17.5 15.6,19.4 12,19.4 C8.4,19.4 5,17.5 2,12 Z " +
                // 瞳孔
                "M12,8.2 A3.8,3.8 0 1 0 12,15.8 A3.8,3.8 0 1 0 12,8.2 Z"),
            Stretch = Stretch.Uniform,
            Width = 18,
            Height = 18,
            StrokeThickness = 1.6,
            StrokeJoin = PenLineJoin.Round,
            StrokeLineCap = PenLineCap.Round,
            // 只描边不填充：填充会把瞳孔和轮廓一起填成一坨。
            Fill = null,
        };

        // 斜杠单独一条 Path，但端点取和外轮廓完全相同的边界（x 2~22，y 4.6~19.4），
        // 这样它和外轮廓的 Uniform 缩放比例一致，叠上去才对得齐。
        var slash = new ShapePath
        {
            Data = Geometry.Parse("M2,19.4 L22,4.6"),
            Stretch = Stretch.Uniform,
            Width = 18,
            Height = 18,
            StrokeThickness = 1.6,
            StrokeLineCap = PenLineCap.Round,
            IsVisible = false,
        };

        var icon = new Panel { Width = 18, Height = 18, Children = { eye, slash } };

        var toggle = new Button
        {
            Content = icon,
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0),
            // 让按钮不抢焦点：编辑框里按 Tab 应该去「取消/保存」，不该停在图标上。
            Focusable = false,
        };
        ToolTip.SetTip(toggle, "显示敏感值");

        // 描边色跟随主题：用 DynamicResource 绑定，主题变更时图标不会留下旧颜色。
        // （Avalonia 11 的代码里没有 SetResourceReference，用索引器绑定语法 `[!prop] = …`。）
        foreach (var shape in new[] { eye, slash })
        {
            shape[!Shape.StrokeProperty] = new DynamicResourceExtension("TextSecondaryBrush");
        }

        toggle.Click += (_, _) =>
        {
            var reveal = input.PasswordChar != '\0';
            input.PasswordChar = reveal ? '\0' : '•';
            slash.IsVisible = reveal;
            ToolTip.SetTip(toggle, reveal ? "隐藏敏感值" : "显示敏感值");
        };

        return new Grid
        {
            Children =
            {
                input,
                toggle,
            },
        };
    }
}
