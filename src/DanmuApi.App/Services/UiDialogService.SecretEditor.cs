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

        var slash = new ShapePath
        {
            Data = Geometry.Parse("M4,20 L20,4"),
            Stretch = Stretch.Uniform,
            Width = 16,
            Height = 16,
            StrokeThickness = 1.7,
            StrokeLineCap = PenLineCap.Round,
            IsVisible = false,
        };
        var eye = new Grid
        {
            Width = 16,
            Height = 16,
            Children =
            {
                new ShapePath
                {
                    Data = Geometry.Parse("M2,12 C5,6.5 8.4,4.6 12,4.6 C15.6,4.6 19,6.5 22,12 C19,17.5 15.6,19.4 12,19.4 C8.4,19.4 5,17.5 2,12 Z"),
                    Stretch = Stretch.Uniform,
                    StrokeThickness = 1.7,
                    StrokeJoin = PenLineJoin.Round,
                },
                new ShapePath
                {
                    Data = Geometry.Parse("M12,8.7 A3.3,3.3 0 1 0 12,15.3 A3.3,3.3 0 1 0 12,8.7 Z"),
                    Stretch = Stretch.Uniform,
                },
                slash,
            },
        };

        var toggle = new Button
        {
            Content = eye,
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

        // 描边与填充色跟随主题：用 DynamicResource 绑定，主题变更时图标不会留下旧颜色。
        // （Avalonia 11 的代码里没有 SetResourceReference，用索引器绑定语法 `[!prop] = …`。）
        foreach (var shape in new[] { (ShapePath)eye.Children[0], (ShapePath)eye.Children[1], slash })
        {
            shape[!Shape.StrokeProperty] = new DynamicResourceExtension("TextSecondaryBrush");
        }

        eye.Children[1][!Shape.FillProperty] = new DynamicResourceExtension("TextSecondaryBrush");

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
