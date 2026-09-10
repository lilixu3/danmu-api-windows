using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
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
    private readonly bool _useStaging;
    private bool _combineStaging;
    private readonly bool _allowSelectedReuse;
    private readonly bool _allowCompositeValues;
    private readonly WrapPanel _selectedPanel = new() { Orientation = Orientation.Horizontal };
    private readonly WrapPanel _candidatePanel = new() { Orientation = Orientation.Horizontal };
    private readonly WrapPanel _stagingPanel = new() { Orientation = Orientation.Horizontal };
    private readonly Button _confirmStaging = new()
    {
        Name = "ConfirmTagStagingButton",
        Content = "确认加入已选",
        MinWidth = 108,
    };
    private readonly TextBlock _selectedEmpty = new() { Text = "尚未选择" };
    private readonly TextBlock _stagingEmpty = new() { Text = "点击下方候选来源暂存组合" };

    public TagPicker(
        IEnumerable<string> options,
        IEnumerable<string>? selected = null,
        bool compareTokens = false,
        bool singleSelect = false,
        bool useStaging = false,
        bool combineStaging = false,
        bool allowSelectedReuse = false,
        bool allowCompositeValues = false)
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

    public bool AddValue(string value)
    {
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
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = "已选", FontWeight = Avalonia.Media.FontWeight.SemiBold });
        content.Children.Add(new Border
        {
            Background = Avalonia.Media.Brushes.Transparent,
            Child = _selectedPanel,
            MinHeight = 34,
        });
        content.Children.Add(new TextBlock { Text = "候选", FontWeight = Avalonia.Media.FontWeight.SemiBold });
        content.Children.Add(new Border
        {
            Background = Avalonia.Media.Brushes.Transparent,
            Child = _candidatePanel,
            MinHeight = 34,
        });

        if (_useStaging)
        {
            content.Children.Insert(1, BuildStagingSurface());
        }

        return content;
    }

    private Control BuildStagingSurface()
    {
        var surface = new Border
        {
            Background = Avalonia.Media.Brushes.Transparent,
            BorderBrush = Avalonia.Media.Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
        };
        var panel = new StackPanel { Spacing = 7 };
        panel.Children.Add(new TextBlock
        {
            Text = _combineStaging ? "组合暂存" : "来源暂存",
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
        });
        panel.Children.Add(_stagingPanel);
        panel.Children.Add(_confirmStaging);
        surface.Child = panel;
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
        _selectedPanel.Children.Clear();
        if (SelectedItems.Count == 0)
        {
            _selectedPanel.Children.Add(_selectedEmpty);
        }
        else
        {
            foreach (var value in SelectedItems)
            {
                var select = new Button { Name = "SelectTagButton", Content = value, MinWidth = 40 };
                select.Classes.Add("chip");
                select.Click += (_, _) =>
                {
                    ActiveItem = value;
                    SelectionChanged?.Invoke(this, EventArgs.Empty);
                    Refresh();
                };
                var remove = new Button { Name = "RemoveSelectedTagButton", Content = "×", MinWidth = 28 };
                remove.Classes.Add("chip");
                remove.Click += (_, _) => RemoveValue(value);
                _selectedPanel.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 2,
                    Margin = new Thickness(0, 0, 6, 6),
                    Children = { select, remove },
                });
            }
        }

        _candidatePanel.Children.Clear();
        foreach (var value in _options)
        {
            var button = new Button { Name = "AddCandidateTagButton", Content = value, MinWidth = 54 };
            button.Classes.Add("chip");
            var disabled = _useStaging
                ? ContainsToken(StagingItems, value) || (!_allowSelectedReuse && IsSelected(value))
                : IsSelected(value);
            button.IsEnabled = !disabled;
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
            button.Margin = new Thickness(0, 0, 6, 6);
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
                        MinWidth = 54,
                    };
                    remove.Classes.Add("chip");
                    remove.Click += (_, _) => RemoveStagedValue(value);
                    remove.Margin = new Thickness(0, 0, 6, 6);
                    _stagingPanel.Children.Add(remove);
                }
            }

            _confirmStaging.IsEnabled = StagingItems.Count > 0;
        }
    }

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
        if (value.Length == 0 || !IsAllowedValue(value) || IsSelected(value))
        {
            return;
        }

        SelectedItems.Add(value);
    }

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
