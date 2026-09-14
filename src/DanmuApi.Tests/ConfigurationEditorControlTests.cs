using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DanmuApi.App.Controls;
using DanmuApi.App.Services;
using DanmuApi.App.Views;
using DanmuApi.Core;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class ConfigurationEditorControlTests
{
    [AvaloniaFact]
    public void MergeSourcePairsEditorKeepsMultipleGroupsAndAllowsSharedSource()
    {
        var definition = Definition(
            "MERGE_SOURCE_PAIRS",
            options: ["bilibili", "dandan", "animeko"]);
        var editor = new MergeSourcePairsEditor(
            definition.Options,
            [new MergeSourceGroup("bilibili", ["dandan"])]);

        Assert.True(editor.Picker.AddStagedValue("bilibili"));
        Assert.True(editor.Picker.AddStagedValue("animeko"));
        Assert.True(editor.ConfirmStaging());
        Assert.Equal(2, editor.Groups.Count);
        Assert.Equal("bilibili", editor.Groups[0].Primary);
        Assert.Equal(["dandan"], editor.Groups[0].Secondaries);
        Assert.Equal("bilibili", editor.Groups[1].Primary);
        Assert.Equal(["animeko"], editor.Groups[1].Secondaries);
    }

    [AvaloniaFact]
    public void ColorPaletteEditorHandlesEmptyInputWithoutThrowing()
    {
        var editor = new ColorPaletteEditor(
            Definition("COLOR_POOL"),
            string.Empty);

        Assert.False(editor.TryAddInputColor(string.Empty));
        Assert.Contains("请输入", editor.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(editor.Colors);
        Assert.Equal(string.Empty, editor.GetValue());
    }

    [AvaloniaFact]
    public void ColorPaletteEditorAddsHexDecimalAndBatchColors()
    {
        var editor = new ColorPaletteEditor(
            Definition("COLOR_POOL"),
            string.Empty);

        Assert.True(editor.TryAddInputColor("#FF6600"));
        Assert.True(editor.TryAddBatch("65280, #0000FF"));

        Assert.Equal([0xFF6600u, 0x00FF00u, 0x0000FFu], editor.Colors);
        Assert.Equal("16737792,65280,255", editor.GetValue());
    }

    [AvaloniaFact]
    public void ExtractedStructuredEditorsExposeStrictModels()
    {
        var mappingDefinition = Definition("AUTO_MATCH_MAPPING_TABLE", category: "match");
        // 两个映射表现在共用核心那套 map 界面（批量框 + 逐行 原值->映射值），沿用同一份校验。
        var mappings = new MappingTableEditor(mappingDefinition, "永生 S05E02 -> 永生 S01E58;海贼王 S02E01 -> 航海王(1999)【动漫】 S01E62");
        mappings.Validate();
        Assert.Equal(2, mappings.RowCount);
        Assert.Contains("永生 S05E02->永生 S01E58", mappings.Value, StringComparison.Ordinal);

        var blacklist = new IpBlacklistEditor(
            [new IpBlacklistEntry(IpBlacklistEntryType.Cidr, "10.0.0.0/8")]);
        Assert.Equal("10.0.0.0/8", CoreEnvStructuredValues.FormatIpBlacklist(blacklist.Entries));
    }

    [AvaloniaFact]
    public void OrderedTagsEditorSeparatesSourceAndPlatformCombinationRules()
    {
        var sourceOrder = new OrderedTagsEditor(["bilibili", "dandan"], [], allowComposites: false);
        Assert.False(sourceOrder.TryAddComposite("bilibili&dandan"));
        Assert.Empty(sourceOrder.Values);

        var platformOrder = new OrderedTagsEditor(["bilibili", "dandan"], [], allowComposites: true);
        Assert.True(platformOrder.TryAddComposite("bilibili&dandan"));
        Assert.False(platformOrder.TryAddComposite("dandan&bilibili"));
        Assert.Equal(["bilibili&dandan"], platformOrder.Values);
    }

    [AvaloniaFact]
    public void ExtractedListEditorsPreserveConfigurationFormats()
    {
        var vod = new VodServersEditor("主站@https://example.com,https://backup.example.com");
        Assert.Equal("主站@https://example.com,https://backup.example.com", vod.Value);

        var mappings = new MappingTableEditor(Definition("TITLE_MAPPING_TABLE", category: "match"), "原名->新名;第二个->另一个");
        Assert.Equal("原名->新名;第二个->另一个", mappings.Value);
    }

    [AvaloniaFact]
    public void GradientEditorRejectsOneCustomColorAndAcceptsSkin()
    {
        var definition = Definition("GRADIENT_COLORS");
        var editor = new ColorPaletteEditor(definition, string.Empty);

        editor.AddColor(0xFF0000);
        Assert.Throws<FormatException>(() => editor.GetValue());

        editor.ResetColors();
        Assert.True(editor.IsSkinMode == false);
        Assert.True(editor.TryAddBatch("16711680,255"));
        Assert.Equal("16711680,255", editor.GetValue());
    }

    /// <summary>
    /// 弹窗结构对齐核心自带前端 #env-modal：头部（标题 + 圆形关闭）、
    /// 三个只读字段（变量类别 / 变量名 / 值类型）、动态控件区、描述、底部两个等宽按钮。
    /// </summary>
    [AvaloniaFact]
    public void ConfigurationEditorWindowFollowsCoreModalStructure()
    {
        var definition = Definition("CUSTOM_MERGE_RULES", category: "source");
        var window = new ConfigurationEditorWindow(
            definition,
            "说明",
            new StackPanel { Children = { new TextBlock { Text = "内容" } } });

        Assert.Equal("编辑配置项", window.Title);

        var key = Find<TextBlock>(window, "KeyBlock");
        Assert.Equal("CUSTOM_MERGE_RULES", key.Text);
        Assert.Equal("数据源配置", Find<TextBlock>(window, "CategoryBlock").Text);
        Assert.Equal("文本", Find<TextBlock>(window, "TypeBlock").Text);
        Assert.Equal("说明", Find<SelectableTextBlock>(window, "DescriptionBlock").Text);

        // 底部两个等宽按钮：保存（左）与 取消（右），头部另有一个圆形关闭按钮
        Assert.Equal("保存", window.SaveActionButton.Content);
        Assert.Equal("取消", Find<Button>(window, "CancelButton").Content);
        Assert.True(Find<Button>(window, "CloseButton").Content is string);

        Assert.Equal(CoreEnvEditAction.Cancel, window.Result.Action);
    }

    [AvaloniaFact]
    public void ConfigurationEditorWindowCancelLeavesResultUnchanged()
    {
        var window = new ConfigurationEditorWindow("编辑配置项", "说明", new TextBox());
        Assert.Equal(CoreEnvEditAction.Cancel, window.Result.Action);

        Find<Button>(window, "CancelButton")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(CoreEnvEditAction.Cancel, window.Result.Action);
    }

    /// <summary>弹窗的错误提示是底部那一条（保存被校验拒绝时显示）。</summary>
    [AvaloniaFact]
    public void ConfigurationEditorWindowSurfacesErrorMessage()
    {
        var window = new ConfigurationEditorWindow("编辑配置项", "说明", new TextBox());

        Assert.Null(window.ErrorMessage);
        window.ErrorMessage = "值不合法";

        var error = Find<TextBlock>(window, "ErrorBlock");
        Assert.True(error.IsVisible);
        Assert.Equal("值不合法", error.Text);
    }

    /// <summary>
    /// 用户手动拉高弹窗时，中间内容区必须跟着长（不能固定高度、只留上下空白）。
    /// 关键是显示后立刻把 <c>SizeToContent</c> 从 Height 切成 Manual：
    /// Height 模式下用户拉伸会被弹回，内容区也就没法跟着变。
    /// </summary>
    [AvaloniaFact]
    public void ContentAreaGrowsWhenWindowIsResizedTaller()
    {
        var definition = Definition("TEST_KEY", category: "test");
        var window = new ConfigurationEditorWindow(
            definition,
            "说明",
            new StackPanel { Children = { new TextBox { MinHeight = 120 } } });
        window.Show();
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var scroll = Find<ScrollViewer>(window, "ContentScroll");
            Assert.Equal(SizeToContent.Manual, window.SizeToContent);

            var before = scroll.Bounds.Height;
            var windowBefore = window.Bounds.Height;

            window.Height = windowBefore + 220;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.True(
                scroll.Bounds.Height > before + 100,
                $"拉高窗口后内容区应变高：window {windowBefore:F0}→{window.Bounds.Height:F0}，" +
                $"scroll {before:F0}→{scroll.Bounds.Height:F0}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>头尾固定、只有中间滚动：内容再长也不用拉到底部才能点保存。
    /// （用户实机反馈"保存和取消必须拉到底才出现"。）</summary>
    [AvaloniaFact]
    public void ConfigurationEditorWindowPinsFooterOutsideTheScrollArea()
    {
        var window = new ConfigurationEditorWindow("编辑配置项", "说明", new TextBox());
        var layout = Assert.IsType<Grid>(window.Content);
        Assert.Equal(3, layout.RowDefinitions.Count);

        var scroll = Descendants<ScrollViewer>(window).First();
        var scrollContent = Descendants<Control>(scroll).ToArray();

        Assert.DoesNotContain(Find<Button>(window, "SaveButton"), scrollContent);
        Assert.DoesNotContain(Find<Button>(window, "CancelButton"), scrollContent);
        Assert.DoesNotContain(Find<Button>(window, "CloseButton"), scrollContent);
        // 描述属于内容，跟着中间区滚动（与核心一致：它在底部按钮之前）。
        Assert.Contains(Find<SelectableTextBlock>(window, "DescriptionBlock"), scrollContent);
        Assert.Contains(Find<TextBlock>(window, "KeyBlock"), scrollContent);
        Assert.Contains(Find<ContentControl>(window, "EditorHost"), scrollContent);
    }

    /// <summary>弹窗宽度不再是写死的 560：按宿主窗口自适应，并夹在上下限之间。</summary>
    [AvaloniaFact]
    public void ConfigurationEditorWindowComputesAdaptiveWidth()
    {
        var narrow = ConfigurationEditorWindow.ComputePreferredWidth(ownerWidth: 900, workingWidth: 1920);
        var wide = ConfigurationEditorWindow.ComputePreferredWidth(ownerWidth: 1920, workingWidth: 1920);
        var huge = ConfigurationEditorWindow.ComputePreferredWidth(ownerWidth: 3840, workingWidth: 3840);
        var tiny = ConfigurationEditorWindow.ComputePreferredWidth(ownerWidth: 600, workingWidth: 700);

        Assert.Equal(520, narrow);
        Assert.Equal(1056, wide);          // 1920 × 0.55
        Assert.Equal(1080, huge);          // 夹在上限
        Assert.True(wide > narrow);
        Assert.True(tiny <= 700 - 48);
        Assert.True(tiny >= 520);
        // 拿不到宿主窗口宽度时退回核心弹窗的 560
        Assert.Equal(560, ConfigurationEditorWindow.ComputePreferredWidth(ownerWidth: null, workingWidth: 1920));
    }

    private static T Find<T>(Control root, string name) where T : Control
    {
        var match = Descendants<T>(root).FirstOrDefault(control => control.Name == name);
        Assert.NotNull(match);
        return match!;
    }

    private static List<T> Descendants<T>(Control root) where T : Control
    {
        var found = new List<T>();
        Walk(root);
        return found;

        void Walk(Control control)
        {
            if (control is T match)
            {
                found.Add(match);
            }

            switch (control)
            {
                case Border { Child: Control child }:
                    Walk(child);
                    break;
                case Panel panel:
                    foreach (var item in panel.Children.OfType<Control>())
                    {
                        Walk(item);
                    }
                    break;
                case ContentControl { Content: Control content }:
                    Walk(content);
                    break;
            }
        }
    }

    /// <summary>
    /// 敏感变量编辑器：默认遮罩，显示/隐藏是输入框内部右侧的一个眼睛图标按钮，
    /// 不再是跟「取消/保存」并列的那个「显示敏感值」文字按钮。
    /// </summary>
    [AvaloniaFact]
    public void SecretEditorHostsEyeToggleInsideTheInput()
    {
        var input = new TextBox { Text = "super-secret-token" };
        var host = UiDialogService.AttachSecretToggle(input);

        // 默认遮罩
        Assert.Equal('•', input.PasswordChar);

        // 容器里只有输入框和一个按钮，按钮贴在输入框内部右侧
        var grid = Assert.IsType<Grid>(host);
        Assert.Equal(2, grid.Children.Count);
        Assert.Same(input, grid.Children[0]);
        var toggle = Assert.IsType<Button>(grid.Children[1]);
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right, toggle.HorizontalAlignment);
        Assert.Equal(Avalonia.Layout.VerticalAlignment.Center, toggle.VerticalAlignment);
        // 不是文字按钮，内容是画出来的图标
        Assert.Null(toggle.Content as string);
        Assert.IsType<Panel>(toggle.Content);
        // 右侧留白，长值不会滑到图标底下
        Assert.True(input.Padding.Right >= 30, $"输入框右侧留白不足：{input.Padding}");

        // 点一下显示明文
        toggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal('\0', input.PasswordChar);
        Assert.Equal("super-secret-token", input.Text);

        // 再点一下回到遮罩
        toggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal('•', input.PasswordChar);
    }

    /// <summary>
    /// 眼睛图标必须是「轮廓 + 瞳孔画在同一个 Path 里」。
    /// 拆成两个 Path 时 Path.Stretch 各自独立生效，只有 7×7 边界的瞳孔会被单独放大
    /// 到占满整个控件、糊住轮廓，图标就变成一个认不出的实心圆点（实际发生过）。
    /// 这条断言把「单一几何、只描边」这个结构锁住。
    /// </summary>
    [AvaloniaFact]
    public void SecretEditorEyeIconKeepsOutlineAndPupilInOneGeometry()
    {
        var input = new TextBox { Text = "x" };
        var host = UiDialogService.AttachSecretToggle(input);
        // 描边色是 DynamicResource 绑定，未进入可视树时读回来是 null，先挂到窗口上再断言。
        var window = new Window { Width = 400, Height = 120, Content = host };
        window.Show();
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var toggle = Assert.IsType<Button>(Assert.IsType<Grid>(host).Children[1]);
            var icon = Assert.IsType<Panel>(toggle.Content);

            // 图标层只有两条 Path：眼睛（轮廓+瞳孔）与遮罩斜杠。
            // 瞳孔若被拆成第三条 Path，它 7×7 的几何边界会被 Uniform 单独放大到占满控件。
            Assert.Equal(2, icon.Children.Count);
            var eye = Assert.IsType<Avalonia.Controls.Shapes.Path>(icon.Children[0]);
            var slash = Assert.IsType<Avalonia.Controls.Shapes.Path>(icon.Children[1]);

            // 眼睛那条几何的边界必须是杏仁形（明显宽大于高）。
            // 只装一个瞳孔的话边界接近正方形，这条断言能把它区分出来。
            var bounds = eye.Data!.Bounds;
            Assert.True(
                bounds.Width / bounds.Height > 1.2,
                $"眼睛几何边界不像杏仁形（轮廓和瞳孔可能被拆开了）：{bounds}");

            // 只描边、不填充：填充会把轮廓和瞳孔一起填成一坨
            Assert.Null(eye.Fill);
            Assert.NotNull(eye.Stroke);
            Assert.NotNull(slash.Stroke);
            Assert.Null(slash.Fill);

            // 斜杠默认不显示，点开后出现
            Assert.False(slash.IsVisible);
            toggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.True(slash.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    private static CoreEnvDefinition Definition(
        string key,
        IReadOnlyList<string>? options = null,
        string category = "test") =>
        new(
            key,
            category,
            CoreEnvType.Text,
            key,
            options ?? [],
            options ?? [],
            null,
            null,
            null,
            false,
            false);
}
