using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace DanmuApi.App.Controls;

/// <summary>
/// 有序多选编辑器（SOURCE_ORDER / PLATFORM_ORDER）。
///
/// <para>
/// 合并模式的语义照抄核心自带前端的多选分支（<c>systemsettings.js</c> 里
/// <c>shouldShowMergeMode = MERGE_SOURCE_PAIRS || PLATFORM_ORDER</c> 那段）：
/// 开启合并模式后，点候选先进暂存区，再点「确认组合（合成一项）」把它们合成一个
/// <c>a&amp;b</c> 条目写进已选；关闭时点候选直接进已选。
/// </para>
///
/// <para>
/// 这里刻意不再提供"手打 bilibili&amp;dandan"的输入框：它不消费已选，
/// 是"能选但写不进去"的误导来源。
/// </para>
/// </summary>
public sealed class OrderedTagsEditor : Border
{
    private readonly TagPicker _picker;
    private readonly TextBlock _error;
    private readonly TextBlock _unknownWarning;
    private readonly TextBlock? _mergeModeHint;
    private readonly bool _allowComposites;
    private readonly bool _mergeModeRequired;
    private readonly CheckBox? _mergeMode;

    /// <param name="allowComposites">是否允许组合值（`a&amp;b`）。</param>
    /// <param name="allowMergeMode">是否提供「合并模式」开关。</param>
    /// <param name="mergeModeRequired">
    /// 是否要求必须开启合并模式才能写入。合并源专用变量（MERGE_SOURCE_PAIRS）为 true：
    /// 它只接受合并组，关闭合并模式时**禁止写入**，而不是退化成单源写入。
    /// </param>
    /// <param name="mergeModeDefault">合并模式的初始状态。支持单源+合并的变量（PLATFORM_ORDER）默认关闭。</param>
    public OrderedTagsEditor(
        IEnumerable<string> options,
        IEnumerable<string> selected,
        bool allowComposites,
        bool allowMergeMode = false,
        bool mergeModeRequired = false,
        bool mergeModeDefault = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(selected);
        _allowComposites = allowComposites;
        _mergeModeRequired = mergeModeRequired;

        // 合并模式下才启用暂存流程；关闭时点候选直接进已选。
        // 拖动排序在两种模式下都开着（核心对所有多选都调 setupDragAndDrop）。
        _picker = new TagPicker(
            options,
            selected,
            compareTokens: true,
            useStaging: allowMergeMode && mergeModeDefault,
            combineStaging: allowMergeMode && mergeModeDefault,
            allowSelectedReuse: allowMergeMode,
            allowCompositeValues: allowComposites || allowMergeMode,
            allowUnknownValues: true,
            enableReorder: true);

        _error = new TextBlock
        {
            Name = "OrderedTagsErrorText",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            IsVisible = false,
        };
        _error.Classes.Add("danger-text");

        _unknownWarning = new TextBlock
        {
            Name = "OrderedTagsUnknownWarning",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            IsVisible = false,
        };
        _unknownWarning.Classes.Add("cap");

        // 排序只保留拖动（用户反馈上移/下移多余）。
        // 键盘仍可达：聚焦条目后按 Ctrl + 左右方向键移动（见 TagPicker 的 KeyDown）。
        var orderHint = new TextBlock
        {
            Text = "拖动「已选择」里的条目可调整顺序；键盘上可聚焦条目后按 Ctrl + 左右方向键移动。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        orderHint.Classes.Add("form-help");

        var content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _picker,
                orderHint,
                _unknownWarning,
                _error,
            },
        };

        if (allowMergeMode)
        {
            _mergeMode = new CheckBox
            {
                Name = "OrderedTagsMergeModeCheckBox",
                Content = "合并模式（把暂存的平台组合为一项，再点「确认组合」写入）",
                IsChecked = mergeModeDefault,
            };
            _mergeMode.Click += (_, _) => ApplyMergeMode(_mergeMode.IsChecked == true);

            var hint = new TextBlock
            {
                Text = "开启后点候选平台会进入暂存区，用 & 连接；点「确认组合（合成一项）」才成为一条已选。",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            };
            hint.Classes.Add("body-muted");
            content.Children.Insert(1, hint);

            // 合并源专用变量：关闭合并模式时明确说明"不能写入"，而不是让用户点了没反应。
            _mergeModeHint = new TextBlock
            {
                Name = "OrderedTagsMergeRequiredHint",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                IsVisible = false,
            };
            _mergeModeHint.Classes.Add("danger-text");
            content.Children.Insert(2, _mergeModeHint);

            content.Children.Insert(0, _mergeMode);
            ApplyMergeMode(mergeModeDefault);
        }

        Child = content;
        _picker.SelectionChanged += (_, _) => RefreshUnknownWarning();
        RefreshUnknownWarning();
    }

    /// <summary>合并模式开关变化。外层用它同步提示文案。</summary>
    public event EventHandler? MergeModeToggled;

    public IReadOnlyList<string> Values => _picker.Values;

    public TagPicker Picker => _picker;

    /// <summary>已选中但核心未声明的条目，为空表示全部合法。</summary>
    public IReadOnlyList<string> UnknownValues => _picker.UnknownValues;

    public bool MergeModeEnabled => _mergeMode?.IsChecked == true;

    /// <summary>供测试与合并模式联动使用：直接设置合并模式状态。</summary>
    public void SetMergeMode(bool enabled)
    {
        if (_mergeMode is null)
        {
            return;
        }

        _mergeMode.IsChecked = enabled;
        ApplyMergeMode(enabled);
    }

    /// <summary>合并模式是否必需（关闭即禁止写入）。</summary>
    public bool MergeModeRequired => _mergeModeRequired;

    /// <summary>当前是否允许写入（合并源专用变量关闭合并模式时为 false）。</summary>
    public bool CanWrite => !_mergeModeRequired || MergeModeEnabled;

    /// <summary>
    /// 应用合并模式：开启走"暂存 → 确认组合"；关闭时若这个变量只接受合并源，
    /// 则**禁止写入**（候选置灰 + 明确提示），不退化成单源写入。
    /// </summary>
    private void ApplyMergeMode(bool enabled)
    {
        _picker.UseStaging = enabled;
        _picker.CombineStaging = enabled;
        _picker.CandidatesEnabled = !_mergeModeRequired || enabled;

        if (_mergeModeHint is not null)
        {
            _mergeModeHint.Text = _mergeModeRequired && !enabled
                ? "该变量只支持合并来源：请先开启合并模式，再点候选进入暂存区并点「确认组合」。"
                : string.Empty;
            _mergeModeHint.IsVisible = _mergeModeRequired && !enabled;
        }

        MergeModeToggled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把暂存区里的项合成一个组合条目（合并模式下才允许）。</summary>
    public bool TryAddComposite(string value)
    {
        if (!_allowComposites)
        {
            SetError("当前顺序配置不允许组合值。");
            return false;
        }

        if (!_picker.AddValue(value))
        {
            SetError("组合中的每一项都必须来自当前核心 envs.js，且组合及条目不能重复。");
            return false;
        }

        SetError(null);
        RefreshUnknownWarning();
        return true;
    }

    private void RefreshUnknownWarning()
    {
        var unknown = _picker.UnknownValues;
        if (unknown.Count == 0)
        {
            _unknownWarning.IsVisible = false;
            _unknownWarning.Text = string.Empty;
            return;
        }

        _unknownWarning.Text =
            $"以下条目当前核心 envs.js 未声明：{string.Join("、", unknown)}。" +
            "本编辑器会原样保留它们，但保存时会被核心校验拒绝，请先移除或改成核心支持的项。";
        _unknownWarning.IsVisible = true;
    }

    private void SetError(string? message)
    {
        _error.Text = message ?? string.Empty;
        _error.IsVisible = !string.IsNullOrWhiteSpace(message);
    }
}
