using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DanmuApi.App.Controls;
using DanmuApi.Core;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

/// <summary>
/// 合并类变量编辑器的回归用例。覆盖曾经真实出问题的地方：
/// <list type="number">
/// <item>PLATFORM_ORDER 能选平台，却没有任何入口把"已选"合成成合并值。</item>
/// <item>规则类变量的编辑界面看不到、也写不进"将要保存的值"。</item>
/// <item>目录里没有的条目被静默丢弃。</item>
/// </list>
/// 规则类编辑器现在与核心一致：<b>「变量值」文本就是唯一真相</b>，子表单只往它里面追加；
/// 保存前由调用方做严格校验（见 UiDialogService 的保存分支）。
/// </summary>
public sealed class MergeEditorRegressionTests
{
    private static readonly string[] Sources =
        ["tencent", "youku", "iqiyi", "imgo", "bilibili", "migu", "renren", "hanjutv",
         "sohu", "leshi", "xigua", "maiduidui", "aiyifan", "hongguo", "dandan", "bahamut", "animeko"];

    private static readonly string[] Platforms =
        ["qiyi", "bilibili1", "imgo", "youku", "qq", "migu", "renren", "hanjutv",
         "sohu", "leshi", "xigua", "maiduidui", "aiyifan", "hongguo", "dandan", "bahamut", "animeko", "custom"];

    private static CoreEnvDefinition Definition(
        string key,
        CoreEnvType type,
        IReadOnlyList<string>? options = null,
        IReadOnlyList<string>? sources = null) =>
        new(key, "test", type, key, options ?? [], sources ?? [], null, null, null, false, false);

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

    // ── PLATFORM_ORDER：已选 → 合并值 ────────────────────────────────────

    [AvaloniaFact]
    public void PlatformOrderCanComposeMergedValueFromSelection()
    {
        var editor = new OrderedTagsEditor(Platforms, [], allowComposites: true, allowMergeMode: true);

        // PLATFORM_ORDER 默认不开合并模式，用户需要时自己开。
        Assert.False(editor.MergeModeEnabled);
        editor.SetMergeMode(true);
        Assert.True(editor.MergeModeEnabled);

        Assert.True(editor.Picker.AddStagedValue("dandan"));
        Assert.True(editor.Picker.AddStagedValue("animeko"));
        Assert.True(editor.Picker.ConfirmStagedValues());

        Assert.Equal(["dandan&animeko"], editor.Values);
    }

    [AvaloniaFact]
    public void PlatformOrderMergeModeOffAddsDirectly()
    {
        var editor = new OrderedTagsEditor(Platforms, [], allowComposites: true, allowMergeMode: true);
        editor.SetMergeMode(false);

        Assert.False(editor.MergeModeEnabled);
        Assert.True(editor.Picker.AddValue("youku"));
        Assert.Equal(["youku"], editor.Values);
    }

    [AvaloniaFact]
    public void SourceOrderHasNoMergeMode()
    {
        var editor = new OrderedTagsEditor(Sources, ["dandan"], allowComposites: false, allowMergeMode: false);

        Assert.False(editor.MergeModeEnabled);
        Assert.False(editor.TryAddComposite("dandan&youku"));
        Assert.Equal(["dandan"], editor.Values);
    }

    // ── 拖动排序（核心是 HTML5 draggable，这里走指针按下/松手） ──────────

    [AvaloniaFact]
    public void DragReorderMovesSelectedEntry()
    {
        var editor = new OrderedTagsEditor(Sources, ["douban", "tencent", "youku"], allowComposites: false, allowMergeMode: false);

        Assert.True(editor.Picker.DropAt("youku", 0));
        Assert.Equal(["youku", "douban", "tencent"], editor.Values);

        Assert.True(editor.Picker.DropAt("youku", 2));
        Assert.Equal(["douban", "tencent", "youku"], editor.Values);
    }

    [AvaloniaFact]
    public void DragReorderIgnoresUnknownEntryOrSamePosition()
    {
        var editor = new OrderedTagsEditor(Sources, ["douban", "tencent"], allowComposites: false, allowMergeMode: false);

        Assert.False(editor.Picker.DropAt("not-selected", 0));
        Assert.False(editor.Picker.DropAt("douban", 0));
        Assert.False(editor.Picker.DropAt("douban", 9));
        Assert.Equal(["douban", "tencent"], editor.Values);
    }

    /// <summary>指针按下才开始拖动；松手落点由 <c>DropAt</c> 统一处理。</summary>
    [AvaloniaFact]
    public void ReorderOnlyStartsForSelectedEntries()
    {
        var editor = new OrderedTagsEditor(Sources, ["douban"], allowComposites: false, allowMergeMode: false);

        editor.Picker.BeginReorder("douban");
        Assert.Equal("douban", editor.Picker.DraggingValue);

        editor.Picker.BeginReorder("tencent");
        Assert.Equal("douban", editor.Picker.DraggingValue);
    }

    /// <summary>
    /// 真实指针路径：按下胶囊 → 移动 → 松手，条目顺序必须真的变。
    /// （用户实机反馈"拖动无效、根本不能变动位置"，所以这里走完整的鼠标事件链，
    /// 而不是只调 <c>DropAt</c>。）
    /// </summary>
    [AvaloniaFact]
    public void RealPointerDragReordersSelectedEntries()
    {
        var editor = new OrderedTagsEditor(Sources, ["douban", "tencent", "youku"], allowComposites: false, allowMergeMode: false);
        var window = new Avalonia.Controls.Window { Width = 600, Height = 400, Content = editor };
        window.Show();
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var picker = editor.Picker;
            var pills = Descendants<Border>(picker).Where(b => b.Classes.Contains("tag-selected")).ToArray();
            Assert.Equal(3, pills.Length);

            // 把第 3 个（youku）拖到第 1 个的位置
            var sourcePoint = Center(pills[2], window);
            var targetPoint = Center(pills[0], window);

            window.MouseDown(sourcePoint, Avalonia.Input.MouseButton.Left);
            Assert.Equal("youku", picker.DraggingValue);

            window.MouseMove(targetPoint);
            window.MouseUp(targetPoint, Avalonia.Input.MouseButton.Left);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.Null(picker.DraggingValue);
            Assert.Equal(["youku", "douban", "tencent"], editor.Values);
        }
        finally
        {
            window.Close();
        }
    }

    private static Avalonia.Point Center(Control control, Visual relativeTo) =>
        control.TranslatePoint(
            new Avalonia.Point(control.Bounds.Width / 2, control.Bounds.Height / 2),
            relativeTo)!.Value;

    /// <summary>顺序配置的标签要提示"可拖动调整顺序"。</summary>
    [AvaloniaFact]
    public void OrderedEditorAdvertisesDragReordering()
    {
        var editor = new OrderedTagsEditor(Sources, [], allowComposites: false, allowMergeMode: false);
        var label = Descendants<TextBlock>(editor).First(block => block.Text?.StartsWith("已选择", StringComparison.Ordinal) == true);

        Assert.Contains("拖动", label.Text!, StringComparison.Ordinal);
    }

    // ── 合并模式门禁 ────────────────────────────────────────────────────

    /// <summary>
    /// PLATFORM_ORDER 同时支持单平台与合并平台：默认**不开**合并模式，用户需要时自己开。
    /// </summary>
    [AvaloniaFact]
    public void PlatformOrderDefaultsToMergeModeOff()
    {
        var editor = new OrderedTagsEditor(
            Platforms, [], allowComposites: true, allowMergeMode: true, mergeModeRequired: false, mergeModeDefault: false);

        Assert.False(editor.MergeModeEnabled);
        Assert.True(editor.CanWrite);
        // 关闭合并模式时点候选直接进已选（单平台写入是允许的）
        Assert.True(editor.Picker.AddValue("youku"));
        Assert.Equal(["youku"], editor.Values);
    }

    /// <summary>
    /// 只接受合并源的变量：关闭合并模式时**禁止写入**，绝不退化成单源写入。
    /// </summary>
    [AvaloniaFact]
    public void MergeSourcePairsForbidsWritingWhenMergeModeOff()
    {
        var editor = new MergeSourcePairsEditor(Sources, []);
        Assert.True(editor.MergeModeEnabled);
        Assert.True(editor.CanWrite);

        editor.SetMergeMode(false);

        Assert.False(editor.MergeModeEnabled);
        Assert.False(editor.CanWrite);
        // 候选被禁用 → 无法写入暂存区
        Assert.False(editor.Picker.AddStagedValue("dandan"));
        Assert.Empty(editor.Picker.StagingItems);
        Assert.Empty(editor.Groups);
    }

    [AvaloniaFact]
    public void MergeSourcePairsWritesGroupWhenMergeModeOn()
    {
        var editor = new MergeSourcePairsEditor(Sources, []);
        editor.SetMergeMode(true);

        Assert.True(editor.Picker.AddStagedValue("dandan"));
        Assert.True(editor.Picker.AddStagedValue("animeko"));
        Assert.True(editor.ConfirmStaging());

        Assert.Equal("dandan&animeko", CoreEnvStructuredValues.FormatMergeSourcePairs(editor.Groups));
    }

    /// <summary>「必需合并模式」的顺序编辑器关闭合并模式时同样禁止写入，并给出原因。</summary>
    [AvaloniaFact]
    public void MergeRequiredOrderedEditorBlocksWritingWhenOff()
    {
        var editor = new OrderedTagsEditor(
            Platforms, [], allowComposites: true, allowMergeMode: true, mergeModeRequired: true, mergeModeDefault: true);

        Assert.True(editor.CanWrite);
        editor.SetMergeMode(false);
        Assert.False(editor.CanWrite);
        Assert.False(editor.Picker.AddValue("youku"));
        Assert.Empty(editor.Values);

        var hint = Descendants<TextBlock>(editor)
            .Single(block => block.Name == "OrderedTagsMergeRequiredHint");
        Assert.True(hint.IsVisible);
        Assert.Contains("只支持合并来源", hint.Text!, StringComparison.Ordinal);
    }

    // ── 目录未声明项：保留并告警，不再静默丢弃 ──────────────────────────

    [AvaloniaFact]
    public void UnknownSelectionsArePreservedAndFlagged()
    {
        var picker = new TagPicker(["dandan", "youku"], ["dandan", "newplatform"], allowUnknownValues: true);

        Assert.Equal(["dandan", "newplatform"], picker.Values);
        Assert.Equal(["newplatform"], picker.UnknownValues);
    }

    [AvaloniaFact]
    public void UnknownSelectionsAreDroppedWhenNotOptedIn()
    {
        // 旧行为，写下来是为了说明为什么必须显式开 allowUnknownValues。
        var picker = new TagPicker(["dandan", "youku"], ["dandan", "newplatform"]);

        Assert.Equal(["dandan"], picker.Values);
    }

    [AvaloniaFact]
    public void OrderedTagsEditorWarnsAboutUnknownEntries()
    {
        var editor = new OrderedTagsEditor(Platforms, ["dandan&animeko", "newplatform"], allowComposites: true, allowMergeMode: true);

        Assert.Equal(["newplatform"], editor.UnknownValues);
        var warning = Descendants<TextBlock>(editor).Single(block => block.Name == "OrderedTagsUnknownWarning");
        Assert.True(warning.IsVisible);
        Assert.Contains("newplatform", warning.Text!, StringComparison.Ordinal);
    }

    // ── CUSTOM_MERGE_RULES：变量值即真相 + 子表单追加 ──────────────────

    [AvaloniaFact]
    public void CustomMergeRulesValueIsTheRawTextBox()
    {
        var definition = Definition("CUSTOM_MERGE_RULES", CoreEnvType.Text, sources: Sources);
        const string initial = "天气之子@bilibili -> 天气之子@dandan";
        var editor = new CustomMergeRulesEditor(definition, initial);

        Assert.Equal(initial, editor.Value);
        editor.Validate();
    }

    [AvaloniaFact]
    public void CustomMergeRulesSubFormAppendsRuleAndClearsForm()
    {
        var definition = Definition("CUSTOM_MERGE_RULES", CoreEnvType.Text, sources: Sources);
        var editor = new CustomMergeRulesEditor(definition, string.Empty);
        var boxes = Descendants<TextBox>(editor);
        var secondary = boxes.Single(box => box.Name == "MergeSecondaryEntityBox");
        var primary = boxes.Single(box => box.Name == "MergePrimaryEntityBox");
        var route = boxes.Single(box => box.Name == "MergeRouteBox");

        secondary.Text = "我推的孩子/S01@bahamut";
        primary.Text = "我推的孩子/S03@dandan";
        route.Text = "E25~E35>E25~E35";

        var confirm = Descendants<Button>(editor).Single(button => button.Name == "ConfirmMergeRuleButton");
        confirm.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(
            "我推的孩子/S01@bahamut -> 我推的孩子/S03@dandan | E25~E35>E25~E35",
            editor.Value);
        // 追加后表单清空、子表单收起（与核心 appendMergeRule 一致）
        Assert.Equal(string.Empty, secondary.Text);
        Assert.Equal(string.Empty, primary.Text);
        Assert.Equal(string.Empty, route.Text);
        Assert.False(editor.IsRulePanelOpen);

        editor.Validate();
    }

    [AvaloniaFact]
    public void CustomMergeRulesSubFormAppendsWithSemicolonBetweenRules()
    {
        var definition = Definition("CUSTOM_MERGE_RULES", CoreEnvType.Text, sources: Sources);
        var editor = new CustomMergeRulesEditor(definition, "A@bilibili -> A@dandan");
        var boxes = Descendants<TextBox>(editor);
        boxes.Single(box => box.Name == "MergeSecondaryEntityBox").Text = "B@bilibili";
        boxes.Single(box => box.Name == "MergePrimaryEntityBox").Text = "B@dandan";
        Descendants<Button>(editor)
            .Single(button => button.Name == "ConfirmMergeRuleButton")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Equal("A@bilibili -> A@dandan;B@bilibili -> B@dandan", editor.Value);
        editor.Validate();
    }

    [AvaloniaFact]
    public void CustomMergeRulesBlockedRelationDropsRoute()
    {
        var definition = Definition("CUSTOM_MERGE_RULES", CoreEnvType.Text, sources: Sources);
        var editor = new CustomMergeRulesEditor(definition, string.Empty);
        var boxes = Descendants<TextBox>(editor);
        boxes.Single(box => box.Name == "MergeSecondaryEntityBox").Text = "A@bilibili";
        boxes.Single(box => box.Name == "MergePrimaryEntityBox").Text = "B@dandan";
        boxes.Single(box => box.Name == "MergeRouteBox").Text = "E01>E01";
        editor.RelationBox.SelectedIndex = 1;
        editor.AppendRuleFromForm();

        // 阻断关系不带集数路由
        Assert.Equal("A@bilibili × B@dandan", editor.Value);
        editor.Validate();
    }

    /// <summary>来源快捷标签：没有 @ 追加 @xxx，已有 @ 追加 &xxx（核心 appendSourceToMerge）。</summary>
    [AvaloniaFact]
    public void CustomMergeRulesSourcePillInsertsIntoFocusedEntity()
    {
        var definition = Definition("CUSTOM_MERGE_RULES", CoreEnvType.Text, sources: Sources);
        var editor = new CustomMergeRulesEditor(definition, string.Empty);
        var pills = Descendants<Button>(editor).Where(button => button.Name == "MergeSourcePill").ToArray();
        Assert.Equal(Sources.Length, pills.Length);

        // 默认落点是副源：先写剧名，再点两个来源 → @dandan 之后追加 &bilibili
        var secondary = Descendants<TextBox>(editor).Single(box => box.Name == "MergeSecondaryEntityBox");
        secondary.Text = "天气之子";
        Assert.False(editor.FocusedEntityIsPrimary);
        pills.Single(pill => Equals(pill.Content, "dandan")).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        pills.Single(pill => Equals(pill.Content, "bilibili")).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("天气之子@dandan&bilibili", secondary.Text);

        // 切到主源后追加落在主源框（核心的 setMergeFocus('prim')）
        editor.SetFocusEntity(primary: true);
        Assert.True(editor.FocusedEntityIsPrimary);
        var primary = Descendants<TextBox>(editor).Single(box => box.Name == "MergePrimaryEntityBox");
        primary.Text = "天气之子";
        pills.Single(pill => Equals(pill.Content, "youku")).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("天气之子@youku", primary.Text);
    }

    [AvaloniaFact]
    public void CustomMergeRulesFillSetsEntityAndOpensPanel()
    {
        var definition = Definition("CUSTOM_MERGE_RULES", CoreEnvType.Text, sources: Sources);
        var editor = new CustomMergeRulesEditor(definition, string.Empty);
        IRecentDataFillTarget target = editor;

        var message = target.FillMergeEntity(false, "天气之子", "dandan");

        Assert.NotNull(message);
        Assert.Contains("副源实体", message!, StringComparison.Ordinal);
        Assert.True(editor.IsRulePanelOpen);
        var secondary = Descendants<TextBox>(editor).Single(box => box.Name == "MergeSecondaryEntityBox");
        Assert.Equal("天气之子@dandan", secondary.Text);

        target.FillMergeEntity(true, "天气之子", "youku");
        var primary = Descendants<TextBox>(editor).Single(box => box.Name == "MergePrimaryEntityBox");
        Assert.Equal("天气之子@youku", primary.Text);
    }

    /// <summary>缓存里的来源不一定在 MERGE_ALLOWED_SOURCES 里（例如 douban），必须显式说明而不是"点了没反应"。</summary>
    [AvaloniaFact]
    public void CustomMergeRulesFillExplainsDisallowedSource()
    {
        var definition = Definition("CUSTOM_MERGE_RULES", CoreEnvType.Text, sources: Sources);
        var editor = new CustomMergeRulesEditor(definition, string.Empty);
        IRecentDataFillTarget target = editor;

        var message = target.FillMergeEntity(false, "天气之子", "douban");

        Assert.NotNull(message);
        Assert.Contains("douban", message!, StringComparison.Ordinal);
        Assert.Contains("未填入", message!, StringComparison.Ordinal);
        var secondary = Descendants<TextBox>(editor).Single(box => box.Name == "MergeSecondaryEntityBox");
        Assert.Equal(string.Empty, secondary.Text ?? string.Empty);
    }

    /// <summary>坏规则不会静默落盘：保存前校验要抛错并带上原因。</summary>
    [AvaloniaFact]
    public void CustomMergeRulesValidateRejectsMalformedValue()
    {
        var definition = Definition("CUSTOM_MERGE_RULES", CoreEnvType.Text, sources: Sources);
        var editor = new CustomMergeRulesEditor(definition, "这行不是规则");

        var error = Assert.Throws<FormatException>(editor.Validate);
        Assert.Contains("CUSTOM_MERGE_RULES", error.Message, StringComparison.Ordinal);
    }

    // ── DANMU_OFFSET：同样"变量值即真相" ────────────────────────────────

    [AvaloniaFact]
    public void DanmuOffsetValueIsTheRawTextBox()
    {
        var definition = Definition("DANMU_OFFSET", CoreEnvType.Text, sources: Sources);
        const string initial = "东方/S03/E02@tencent%:11";
        var editor = new DanmuOffsetEditor(definition, initial);

        Assert.Equal(initial, editor.Value);
        editor.Validate();
    }

    [AvaloniaFact]
    public void DanmuOffsetSubFormComposesRule()
    {
        var definition = Definition("DANMU_OFFSET", CoreEnvType.Text, sources: Sources);
        var editor = new DanmuOffsetEditor(definition, "已有@all:5");
        var boxes = Descendants<TextBox>(editor);
        boxes.Single(box => box.Name == "OffsetAnimeBox").Text = "东方";
        boxes.Single(box => box.Name == "OffsetSeasonBox").Text = "3";
        boxes.Single(box => box.Name == "OffsetEpisodeBox").Text = "2";
        boxes.Single(box => box.Name == "OffsetSecondsBox").Text = "11";

        var percent = Descendants<CheckBox>(editor).Single(box => box.Name == "OffsetPercentCheckBox");
        percent.IsChecked = true;

        var pill = Descendants<Button>(editor)
            .Single(button => button.Name == "OffsetSourcePill" && Equals(button.Content, "tencent"));
        pill.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Descendants<Button>(editor)
            .Single(button => button.Name == "ConfirmOffsetRuleButton")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Equal("已有@all:5,东方/S03/E02@tencent%:11", editor.Value);
        // 表单被清空、来源取消选中、子表单收起
        Assert.Equal(string.Empty, boxes.Single(box => box.Name == "OffsetAnimeBox").Text);
        Assert.False(percent.IsChecked);
        Assert.Empty(editor.SelectedSources);
        Assert.False(editor.IsRulePanelOpen);

        editor.Validate();
    }

    [AvaloniaFact]
    public void DanmuOffsetEpisodeWithoutSeasonIsRejected()
    {
        var definition = Definition("DANMU_OFFSET", CoreEnvType.Text, sources: Sources);
        var editor = new DanmuOffsetEditor(definition, string.Empty);
        var boxes = Descendants<TextBox>(editor);
        boxes.Single(box => box.Name == "OffsetAnimeBox").Text = "东方";
        boxes.Single(box => box.Name == "OffsetEpisodeBox").Text = "2";
        boxes.Single(box => box.Name == "OffsetSecondsBox").Text = "11";

        var error = Assert.Throws<FormatException>(editor.AppendRuleFromForm);
        Assert.Contains("季数", error.Message, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void DanmuOffsetFillCleansTitleAndSelectsSource()
    {
        var definition = Definition("DANMU_OFFSET", CoreEnvType.Text, sources: Sources);
        var editor = new DanmuOffsetEditor(definition, string.Empty);
        IRecentDataFillTarget target = editor;

        var message = target.FillOffsetEntity("天气之子(2019)【动漫】from dandan", "dandan");

        Assert.NotNull(message);
        Assert.True(editor.IsRulePanelOpen);
        var anime = Descendants<TextBox>(editor).Single(box => box.Name == "OffsetAnimeBox");
        Assert.Equal("天气之子", anime.Text);
        Assert.Equal(["dandan"], editor.SelectedSources);
    }

    [AvaloniaFact]
    public void DanmuOffsetValidateRejectsMalformedValue()
    {
        var definition = Definition("DANMU_OFFSET", CoreEnvType.Text, sources: Sources);
        var editor = new DanmuOffsetEditor(definition, "没有冒号的规则");

        Assert.Throws<FormatException>(editor.Validate);
    }

    // ── 空值覆盖门禁 ────────────────────────────────────────────────────

    [Fact]
    public void EmptySaveIsRefusedWhenConfigurationAlreadyHasValue()
    {
        var error = Assert.Throws<FormatException>(() =>
            DanmuApi.App.Services.UiDialogService.GuardAgainstEmptyOverwrite("CUSTOM_MERGE_RULES", string.Empty, configured: true));

        Assert.Contains("清除", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptySaveIsAllowedWhenNothingWasConfiguredYetOrValueIsNotBlank()
    {
        DanmuApi.App.Services.UiDialogService.GuardAgainstEmptyOverwrite("CUSTOM_MERGE_RULES", string.Empty, configured: false);
        DanmuApi.App.Services.UiDialogService.GuardAgainstEmptyOverwrite("CUSTOM_MERGE_RULES", "a@b -> c@d", configured: true);
    }
}
