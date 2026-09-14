using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace DanmuApi.App.Controls;

/// <summary>
/// Chip based selector used by configuration editors. The control owns the selection state;
/// callers never need to read ListBox.SelectedItems.
/// </summary>
public sealed class TagPicker : Border
{
    private readonly IReadOnlyList<string> _options;
    private readonly bool _compareTokens;
    private readonly bool _singleSelect;
    private readonly bool _collapseCandidates;
    private bool _useStaging;
    private bool _combineStaging;
    private readonly bool _allowSelectedReuse;
    private readonly bool _allowCompositeValues;
    private readonly bool _allowUnknownValues;
    private readonly WrapPanel _selectedPanel = new() { Orientation = Orientation.Horizontal };
    private readonly WrapPanel _candidatePanel = new() { Orientation = Orientation.Horizontal };
    private readonly WrapPanel _stagingPanel = new() { Orientation = Orientation.Horizontal };
    private readonly Button _confirmStaging = new()
    {
        Name = "ConfirmTagStagingButton",
        Content = "确认加入已选",
        MinWidth = 108,
    };
    private readonly TextBlock _selectedEmpty = new() { Text = "点击下方选项添加" };
    private readonly TextBlock _stagingEmpty = new() { Text = "点击上方「可选项」把平台放进暂存区" };
    private readonly TextBlock _stagingTitle = new();
    private readonly string _selectedLabelText;

    /// <summary>暂存区外壳。始终构建，用可见性跟随 <see cref="UseStaging"/>，这样合并模式可以随时开关。</summary>
    private Border? _stagingSurface;
    private StackPanel? _candidateSection;
    private Button? _candidateToggle;
    private bool _candidatesExpanded;

    /// <summary>拖动排序状态：按下时记住被拖的条目，松手时按落点重排（核心是 HTML5 draggable）。</summary>
    private readonly bool _enableReorder;
    private string? _draggingValue;
    private int _dropIndex = -1;
    private bool _candidatesEnabled = true;

    public TagPicker(
        IEnumerable<string> options,
        IEnumerable<string>? selected = null,
        bool compareTokens = false,
        bool singleSelect = false,
        bool useStaging = false,
        bool combineStaging = false,
        bool allowSelectedReuse = false,
        bool allowCompositeValues = false,
        bool collapseCandidates = false,
        bool allowUnknownValues = false,
        bool enableReorder = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options
            .Select(Clean)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _compareTokens = compareTokens;
        _singleSelect = singleSelect;
        _useStaging = useStaging;
        _combineStaging = combineStaging;
        _allowSelectedReuse = allowSelectedReuse;
        _allowCompositeValues = allowCompositeValues;
        _collapseCandidates = collapseCandidates;
        _allowUnknownValues = allowUnknownValues;
        _enableReorder = enableReorder;
        _selectedLabelText = enableReorder ? "已选择（可拖动调整顺序）" : "已选择";
        SelectedItems = new ObservableCollection<string>();
        StagingItems = new ObservableCollection<string>();
        SelectedItems.CollectionChanged += OnCollectionChanged;
        StagingItems.CollectionChanged += OnCollectionChanged;

        if (selected is not null)
        {
            foreach (var value in selected.Select(Clean).Where(value => value.Length > 0))
            {
                AddSelectedInternal(value);
            }
        }

        _confirmStaging.Classes.Add("secondary-action");
        _confirmStaging.Click += (_, _) => ConfirmStaging();
        _selectedEmpty.Classes.Add("body-muted");
        _stagingEmpty.Classes.Add("body-muted");

        if (_enableReorder)
        {
            // 拖动由胶囊自己捕获指针处理（见 Refresh 里的 PointerPressed/Moved/Released）。
            _selectedEmpty.Classes.Add("body-muted");
        }

        Padding = new Thickness(0);
        Child = BuildContent();
        Refresh();
    }

    public ObservableCollection<string> SelectedItems { get; }

    public ObservableCollection<string> StagingItems { get; }

    public string? ActiveItem { get; private set; }

    public event EventHandler? SelectionChanged;

    public IReadOnlyList<string> Values => SelectedItems.ToArray();

    public bool CombineStaging
    {
        get => _combineStaging;
        set
        {
            if (_combineStaging == value)
            {
                return;
            }

            _combineStaging = value;
            Refresh();
        }
    }

    /// <summary>
    /// 是否走"暂存 → 确认"两步流程。合并模式要靠它：开启后点候选先进暂存区，
    /// 点「确认组合」才把暂存项合成一项；关闭时点候选直接进已选（与核心前端关闭合并模式一致）。
    /// 切换只影响暂存区的可见性与点击去向，不动已选内容。
    /// </summary>
    public bool UseStaging
    {
        get => _useStaging;
        set
        {
            if (_useStaging == value)
            {
                return;
            }

            _useStaging = value;
            if (_stagingSurface is not null)
            {
                _stagingSurface.IsVisible = value;
            }

            Refresh();
        }
    }

    public bool AddValue(string value)
    {
        // 写入门禁：合并源专用变量在关闭合并模式时 CandidatesEnabled=false，
        // 此时**任何**写入路径都要被挡住（不只是把按钮置灰），否则程序化调用仍能写进去。
        if (!_candidatesEnabled)
        {
            return false;
        }

        var clean = Clean(value);
        if (clean.Length == 0 || !IsAllowedValue(clean) || IsSelected(clean))
        {
            return false;
        }

        if (_singleSelect)
        {
            SelectedItems.Clear();
        }

        SelectedItems.Add(clean);
        ActiveItem = clean;
        Refresh();
        return true;
    }

    public bool AddStagedValue(string value)
    {
        if (!_candidatesEnabled)
        {
            return false;
        }

        if (!_useStaging)
        {
            return AddValue(value);
        }

        var clean = Clean(value);
        if (clean.Length == 0 || !IsAllowedValue(clean) || ContainsToken(StagingItems, clean))
        {
            return false;
        }

        StagingItems.Add(clean);
        Refresh();
        return true;
    }

    public void RemoveValue(string value)
    {
        var item = SelectedItems.FirstOrDefault(current =>
            string.Equals(current, Clean(value), StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return;
        }

        SelectedItems.Remove(item);
        if (string.Equals(ActiveItem, item, StringComparison.OrdinalIgnoreCase))
        {
            ActiveItem = SelectedItems.LastOrDefault();
        }

        Refresh();
    }

    public void MoveActive(int delta)
    {
        if (ActiveItem is null)
        {
            return;
        }

        var index = SelectedItems.IndexOf(ActiveItem);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= SelectedItems.Count)
        {
            return;
        }

        SelectedItems.Move(index, target);
        Refresh();
    }

    /// <summary>
    /// 把某个已选条目移到指定下标。拖动排序松手时调用，也是顺序调整的可测入口
    /// （指针事件在无头环境不好模拟）。
    /// </summary>
    public bool DropAt(string value, int targetIndex)
    {
        var from = SelectedItems.IndexOf(value);
        if (from < 0 || targetIndex < 0 || targetIndex >= SelectedItems.Count || from == targetIndex)
        {
            return false;
        }

        SelectedItems.Move(from, targetIndex);
        ActiveItem = value;
        Refresh();
        return true;
    }

    /// <summary>拖动中：记住被拖的条目，并把落点高亮出来。</summary>
    public void BeginReorder(string value)
    {
        if (!_enableReorder || !SelectedItems.Contains(value))
        {
            return;
        }

        _draggingValue = value;
    }

    /// <summary>当前正在拖动的条目（供断言与调试）。</summary>
    public string? DraggingValue => _draggingValue;

    /// <summary>
    /// 是否允许点候选写入。合并源专用变量（MERGE_SOURCE_PAIRS）在关闭合并模式时会被置为 false：
    /// 它只接受"合并组"，不允许退化成单源写入，所以干脆禁止写入而不是悄悄改成单源。
    /// </summary>
    public bool CandidatesEnabled
    {
        get => _candidatesEnabled;
        set
        {
            if (_candidatesEnabled == value)
            {
                return;
            }

            _candidatesEnabled = value;
            Refresh();
        }
    }

    /// <summary>落点所在的条目下标；不在任何条目上返回 -1。</summary>
    private int IndexAt(Point position)
    {
        for (var index = 0; index < _selectedPanel.Children.Count; index++)
        {
            var bounds = _selectedPanel.Children[index].Bounds;
            if (bounds.Width <= 0)
            {
                continue;
            }

            if (position.X >= bounds.X && position.X <= bounds.Right &&
                position.Y >= bounds.Y && position.Y <= bounds.Bottom)
            {
                return index;
            }
        }

        return -1;
    }

    public void ClearValues()
    {
        SelectedItems.Clear();
        ActiveItem = null;
        Refresh();
    }

    public void ClearStaging()
    {
        StagingItems.Clear();
        Refresh();
    }

    public void RemoveStagedValue(string value)
    {
        var item = StagingItems.FirstOrDefault(current =>
            string.Equals(current, Clean(value), StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            StagingItems.Remove(item);
            Refresh();
        }
    }

    public bool ConfirmStagedValues()
    {
        if (StagingItems.Count == 0)
        {
            return false;
        }

        var before = SelectedItems.Count;
        ConfirmStaging();
        return SelectedItems.Count > before;
    }

    private Control BuildContent()
    {
        // 核心多选分支的层级：标签「已选择」→ 虚线框（已选胶囊 + 合并模式暂存区）
        // → 标签「可选项 (点击添加)」→ 候选胶囊。
        var content = new StackPanel { Spacing = 8 };

        var selectedLabel = new TextBlock { Text = _selectedLabelText };
        selectedLabel.Classes.Add("form-label");
        content.Children.Add(selectedLabel);

        var dropZone = new StackPanel { Spacing = 10 };
        dropZone.Children.Add(_selectedPanel);

        _stagingSurface = BuildStagingSurface();
        _stagingSurface.IsVisible = _useStaging;
        dropZone.Children.Add(_stagingSurface);

        var zoneBorder = new Border { Child = dropZone };
        zoneBorder.Classes.Add("tag-drop-zone");
        content.Children.Add(zoneBorder);

        _candidateSection = new StackPanel { Spacing = 8 };
        var candidateLabel = new TextBlock { Text = "可选项 (点击添加)" };
        candidateLabel.Classes.Add("form-label");
        _candidateSection.Children.Add(candidateLabel);
        _candidateSection.Children.Add(_candidatePanel);

        if (_collapseCandidates)
        {
            // 候选清单默认收起：一个实体就有十几个候选，全部铺开会把对话框撑到上千像素。
            _candidateToggle = new Button
            {
                Name = "ToggleCandidateTagsButton",
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            _candidateToggle.Classes.Add("secondary-action");
            _candidateToggle.Click += (_, _) => SetCandidatesExpanded(!_candidatesExpanded);
            _candidateSection.IsVisible = false;
            content.Children.Add(_candidateToggle);
        }

        content.Children.Add(_candidateSection);
        return content;
    }

    /// <summary>展开/收起候选清单。收起时按钮上带上还有多少项可加，避免"看不到就没得选"。</summary>
    public void SetCandidatesExpanded(bool expanded)
    {
        _candidatesExpanded = expanded;
        if (_candidateSection is not null)
        {
            _candidateSection.IsVisible = expanded;
        }

        UpdateCandidateToggle();
    }

    public bool CandidatesExpanded => _candidatesExpanded;

    private void UpdateCandidateToggle()
    {
        if (_candidateToggle is null)
        {
            return;
        }

        var available = RefreshCandidateAvailability();
        _candidateToggle.Content = _candidatesExpanded
            ? "收起候选"
            : $"选择候选（{available} 项可加）";
    }

    private Border BuildStagingSurface()
    {
        // 核心 .staging-area：强调底色 + 虚线边 + 前缀「合并组暂存区:」+ 右侧圆形确认钮。
        var panel = new StackPanel { Spacing = 8 };
        _stagingTitle.Classes.Add("form-label");
        panel.Children.Add(_stagingTitle);
        panel.Children.Add(_stagingPanel);

        var confirmRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _confirmStaging },
        };
        panel.Children.Add(confirmRow);

        var surface = new Border { Child = panel, IsVisible = false };
        surface.Classes.Add("staging-zone");
        return surface;
    }

    private void ConfirmStaging()
    {
        if (StagingItems.Count == 0)
        {
            return;
        }

        if (_combineStaging)
        {
            AddValue(string.Join('&', StagingItems));
        }
        else
        {
            foreach (var value in StagingItems.ToArray())
            {
                var clean = Clean(value);
                if (clean.Length > 0 && IsAllowedValue(clean) &&
                    !SelectedItems.Any(item => string.Equals(item, clean, StringComparison.OrdinalIgnoreCase)))
                {
                    SelectedItems.Add(clean);
                    ActiveItem = clean;
                }
            }
            Refresh();
        }

        StagingItems.Clear();
        Refresh();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Refresh()
    {
        _stagingTitle.Text = _combineStaging ? "合并组暂存区:" : "暂存区:";
        _confirmStaging.Content = "✓";
        _confirmStaging.Classes.Add("staging-confirm");
        _confirmStaging.Classes.Remove("secondary-action");
        ToolTip.SetTip(_confirmStaging, "确认添加该组");

        _selectedPanel.Children.Clear();
        if (SelectedItems.Count == 0)
        {
            _selectedPanel.Children.Add(_selectedEmpty);
        }
        else
        {
            foreach (var value in SelectedItems)
            {
                var unknown = IsUnknownValue(value);
                var text = new TextBlock
                {
                    // 前缀 ⚠ 而不是只换颜色：纯靠颜色在强调色实心胶囊上看不出来。
                    Text = unknown ? $"⚠ {value}" : value,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var remove = new Button { Name = "RemoveSelectedTagButton", Content = "×" };
                remove.Classes.Add("tag-remove");
                remove.Click += (_, _) => RemoveValue(value);

                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(text);
                content.Children.Add(remove);

                var pill = new Border { Child = content };
                pill.Classes.Add("tag-selected");
                if (unknown)
                {
                    ToolTip.SetTip(
                        pill,
                        "当前核心 envs.js 未声明该项。保存会被拒绝，请移除它或改用核心支持的项。");
                }

                // 点一下设为「上移 / 下移」的作用对象；拖动则按落点重排。
                // 必须由被按下的胶囊自己**捕获指针**并在它身上收 Moved/Released：
                // 只挂面板级事件时指针没被捕获，拖动过程中事件收不到，位置自然不动。
                pill.PointerPressed += (_, args) =>
                {
                    ActiveItem = value;
                    BeginReorder(value);
                    args.Pointer.Capture(pill);
                    pill.Opacity = 0.6;
                    SelectionChanged?.Invoke(this, EventArgs.Empty);
                };
                pill.PointerMoved += (_, args) =>
                {
                    if (_draggingValue is not null)
                    {
                        _dropIndex = IndexAt(args.GetPosition(_selectedPanel));
                    }
                };
                pill.PointerReleased += (_, args) =>
                {
                    var dragged = _draggingValue;
                    args.Pointer.Capture(null);
                    _draggingValue = null;
                    _dropIndex = -1;
                    pill.Opacity = 1;
                    if (dragged is not null)
                    {
                        DropAt(dragged, IndexAt(args.GetPosition(_selectedPanel)));
                    }
                };
                // 键盘可达：聚焦胶囊后按 Ctrl + ←/→ 左右移动（拖动之外的第二条路径）。
                pill.Focusable = _enableReorder;
                if (_enableReorder)
                {
                    pill.KeyDown += (_, args) =>
                    {
                        if (!args.KeyModifiers.HasFlag(KeyModifiers.Control))
                        {
                            return;
                        }

                        var delta = args.Key switch
                        {
                            Key.Left => -1,
                            Key.Right => 1,
                            _ => 0,
                        };
                        if (delta == 0)
                        {
                            return;
                        }

                        ActiveItem = value;
                        MoveActive(delta);
                        args.Handled = true;
                    };
                }

                _selectedPanel.Children.Add(pill);
            }
        }

        _candidatePanel.Children.Clear();
        foreach (var value in _options)
        {
            var button = new Button { Name = "AddCandidateTagButton", Content = value };
            button.Classes.Add("tag-pill");
            button.IsEnabled = !IsCandidateDisabled(value);
            button.Click += (_, _) =>
            {
                if (_useStaging)
                {
                    AddStagedValue(value);
                }
                else
                {
                    AddValue(value);
                }
            };
            _candidatePanel.Children.Add(button);
        }

        if (_useStaging)
        {
            _stagingPanel.Children.Clear();
            if (StagingItems.Count == 0)
            {
                _stagingPanel.Children.Add(_stagingEmpty);
            }
            else
            {
                for (var index = 0; index < StagingItems.Count; index++)
                {
                    if (index > 0)
                    {
                        _stagingPanel.Children.Add(new TextBlock
                        {
                            Text = "&",
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 0, 6, 6),
                        });
                    }

                    var value = StagingItems[index];
                    var remove = new Button
                    {
                        Name = "RemoveStagedTagButton",
                        Content = $"{value} ×",
                    };
                    remove.Classes.Add("source-pill");
                    remove.Click += (_, _) => RemoveStagedValue(value);
                    _stagingPanel.Children.Add(remove);
                }
            }

            _confirmStaging.IsEnabled = StagingItems.Count > 0;
        }

        UpdateCandidateToggle();
    }

    private bool IsCandidateDisabled(string value) =>
        !_candidatesEnabled ||
        (_useStaging
            ? ContainsToken(StagingItems, value) || (!_allowSelectedReuse && IsSelected(value))
            : IsSelected(value));

    /// <summary>还有多少候选可以点（折叠按钮上要显示这个数）。</summary>
    private int RefreshCandidateAvailability() =>
        _options.Count(value => !IsCandidateDisabled(value));

    private bool IsSelected(string value)
    {
        if (_combineStaging || (_allowCompositeValues && value.Contains('&', StringComparison.Ordinal)))
        {
            var normalized = NormalizeComposite(value);
            return SelectedItems.Any(item =>
                string.Equals(NormalizeComposite(item), normalized, StringComparison.OrdinalIgnoreCase));
        }

        if (!_compareTokens)
        {
            return SelectedItems.Any(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));
        }

        return ContainsToken(SelectedItems, value);
    }

    private static string NormalizeComposite(string value) =>
        string.Join('&', value
            .Split('&', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.ToLowerInvariant())
            .OrderBy(token => token, StringComparer.Ordinal));

    private bool ContainsToken(IEnumerable<string> values, string value)
    {
        var clean = Clean(value);
        return values
            .SelectMany(item => _compareTokens ? item.Split('&') : [item])
            .Any(item => string.Equals(Clean(item), clean, StringComparison.OrdinalIgnoreCase));
    }

    private void AddSelectedInternal(string value)
    {
        if (value.Length == 0 || IsSelected(value))
        {
            return;
        }

        // 装载存量值时，目录里没有的项也保留：丢掉它们会让"打开编辑器再保存"静默改写用户配置。
        // 用户主动新增仍然严格校验（AddValue 走 IsAllowedValue），不受这个开关影响。
        if (!IsAllowedValue(value) && !_allowUnknownValues)
        {
            return;
        }

        SelectedItems.Add(value);
    }

    /// <summary>
    /// 已选中但当前核心 envs.js 未声明的条目。上层必须显式处理（提示或拒绝保存），
    /// 不允许像以前那样静默丢弃。
    /// </summary>
    public IReadOnlyList<string> UnknownValues =>
        SelectedItems.Where(IsUnknownValue).ToArray();

    private bool IsUnknownValue(string value) =>
        value
            .Split('&', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(token => !_options.Contains(token, StringComparer.OrdinalIgnoreCase));

    private bool IsAllowedValue(string value)
    {
        var tokens = value.Split('&', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!_allowCompositeValues && tokens.Length > 1)
        {
            return false;
        }
        return tokens.Length > 0 &&
               tokens.Distinct(StringComparer.OrdinalIgnoreCase).Count() == tokens.Length &&
               tokens.All(token =>
                   _options.Any(option => string.Equals(option, token, StringComparison.OrdinalIgnoreCase)));
    }

    private static string Clean(string value) => value.Trim();
}
