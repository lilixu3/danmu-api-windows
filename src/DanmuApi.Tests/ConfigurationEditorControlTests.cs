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
        string[] sources = ["bilibili", "dandan", "animeko"];
        var custom = new CustomMergeRulesEditor(
            sources,
            [new CustomMergeRule(
                new CustomMergeEntity("副源", null, ["bilibili"]),
                false,
                new CustomMergeEntity("主源", 2, ["dandan"]),
                [new EpisodeRoute(new EpisodeRange(1, 1), new EpisodeRange(1, 1))])]);
        var customValue = CoreEnvStructuredValues.FormatCustomMergeRules(custom.Rules);
        Assert.Equal("副源@bilibili -> 主源/S02@dandan | E01>E01", customValue);

        var mappings = new AutoMatchMappingEditor(
            sources,
            [new AutoMatchMappingRule("源", 1, 1, 3, "目标 (2024)【番剧】", 1, 1, 3, "bilibili")]);
        var mappingDefinition = Definition("AUTO_MATCH_MAPPING_TABLE", options: sources);
        var mappingValue = CoreEnvStructuredValues.FormatAutoMatchMappings(mappingDefinition, mappings.Rules);
        Assert.Single(CoreEnvStructuredValues.ParseAutoMatchMappings(mappingDefinition, mappingValue));

        var offsets = new DanmuOffsetEditor(
            sources,
            [new DanmuOffsetRule("番剧", null, null, [], true, false, 1.5m)]);
        Assert.Equal("番剧@all:1.5", CoreEnvStructuredValues.FormatDanmuOffsets(offsets.Rules));

        var blacklist = new IpBlacklistEditor(
            [new IpBlacklistEntry(IpBlacklistEntryType.Cidr, "10.0.0.0/8")]);
        Assert.Equal("10.0.0.0/8", CoreEnvStructuredValues.FormatIpBlacklist(blacklist.Entries));
    }

    [AvaloniaFact]
    public void DanmuOffsetEditorRejectsConcreteSourcesTogetherWithAllSources()
    {
        Assert.Throws<FormatException>(() => new DanmuOffsetEditor(
            ["bilibili"],
            [new DanmuOffsetRule("番剧", null, null, ["bilibili"], true, false, 1m)]));
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

        var mappings = new MappingTableEditor("原名->新名;第二个->另一个");
        Assert.Equal("原名->新名;第二个->另一个", mappings.Value);

        var lines = new LineListEditor("屏蔽词一,屏蔽词二", ",");
        Assert.Equal("屏蔽词一,屏蔽词二", lines.Value);
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

    [AvaloniaFact]
    public void ConfigurationEditorWindowUsesHeaderScrollAndFooterRows()
    {
        var window = new ConfigurationEditorWindow(
            "测试编辑器",
            "说明",
            new StackPanel { Children = { new TextBlock { Text = "内容" } } });
        var layout = Assert.IsType<Grid>(window.Content);

        Assert.Equal(3, layout.RowDefinitions.Count);
        Assert.Equal(GridLength.Auto, layout.RowDefinitions[0].Height);
        Assert.Equal(GridLength.Star, layout.RowDefinitions[1].Height);
        Assert.Equal(GridLength.Auto, layout.RowDefinitions[2].Height);
        Assert.Equal(SizeToContent.WidthAndHeight, window.SizeToContent);
        Assert.True(double.IsNaN(window.Width));
        Assert.True(double.IsNaN(window.Height));
        Assert.Equal(920, window.MaxWidth);
        Assert.Equal(720, window.MaxHeight);
        Assert.Contains(layout.Children, child => child is ScrollViewer { MaxHeight: 520 });
        // 编辑器只保留取消/保存：清空与恢复默认已移到配置列表行的「清除」按钮，
        // 同一个动作不在两处出现（否则用户要在两套语义之间做选择）。
        // 按 Grid.Row 取页脚，不能用 OfType<StackPanel>().Single()：内容区也可能是 StackPanel。
        var footer = Assert.IsType<StackPanel>(layout.Children.Cast<Control>().Single(child => Grid.GetRow(child) == 2));
        Assert.Equal(new object[] { "取消", "保存" }, footer.Children.OfType<Button>().Select(button => button.Content!).ToArray());
    }

    [AvaloniaFact]
    public void ConfigurationEditorWindowCancelLeavesResultUnchanged()
    {
        var window = new ConfigurationEditorWindow("编辑", "说明", new TextBox());
        var layout = Assert.IsType<Grid>(window.Content);
        var footer = Assert.IsType<StackPanel>(layout.Children.Cast<Control>().Single(child => Grid.GetRow(child) == 2));
        var cancel = footer.Children.OfType<Button>().Single(button => Equals(button.Content, "取消"));
        Assert.Equal(CoreEnvEditAction.Cancel, window.Result.Action);

        cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(CoreEnvEditAction.Cancel, window.Result.Action);
    }

    /// <summary>
    /// 敏感变量编辑器：默认遮罩，显示/隐藏是输入框内部右侧的一个图标按钮，
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
        Assert.Empty(toggle.Content is string ? "有文字内容" : string.Empty);
        Assert.IsType<Grid>(toggle.Content);
        // 不是文字按钮
        Assert.Null(toggle.Content as string);
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
    /// 眼睛图标里的斜杠只在「已显示明文」时出现，这样按钮当前状态一眼可辨。
    /// </summary>
    [AvaloniaFact]
    public void SecretEditorEyeSlashReflectsVisibility()
    {
        var input = new TextBox { Text = "x" };
        var host = UiDialogService.AttachSecretToggle(input);
        var toggle = Assert.IsType<Button>(Assert.IsType<Grid>(host).Children[1]);
        var icon = Assert.IsType<Grid>(toggle.Content);
        var slash = Assert.IsType<Avalonia.Controls.Shapes.Path>(icon.Children[2]);

        Assert.False(slash.IsVisible);
        toggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(slash.IsVisible);
    }

    private static CoreEnvDefinition Definition(
        string key,
        IReadOnlyList<string>? options = null) =>
        new(
            key,
            "test",
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
