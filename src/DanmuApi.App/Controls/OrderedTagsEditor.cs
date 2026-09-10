using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace DanmuApi.App.Controls;

public sealed class OrderedTagsEditor : Border
{
    private readonly TagPicker _picker;
    private readonly TextBox _compositeInput;
    private readonly TextBlock _error;
    private readonly bool _allowComposites;

    public OrderedTagsEditor(
        IEnumerable<string> options,
        IEnumerable<string> selected,
        bool allowComposites)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(selected);
        _allowComposites = allowComposites;
        _picker = new TagPicker(
            options,
            selected,
            compareTokens: true,
            allowCompositeValues: allowComposites);
        _compositeInput = new TextBox
        {
            Name = "OrderedTagsCompositeInput",
            Watermark = "例如 bilibili&dandan",
            MinWidth = 260,
            IsVisible = allowComposites,
        };
        _error = new TextBlock
        {
            Name = "OrderedTagsErrorText",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            IsVisible = false,
        };
        _error.Classes.Add("danger-text");

        var addComposite = new Button
        {
            Name = "AddOrderedCompositeButton",
            Content = "添加组合",
            IsVisible = allowComposites,
        };
        addComposite.Classes.Add("secondary-action");
        addComposite.Click += (_, _) => TryAddComposite(_compositeInput.Text ?? string.Empty);

        var up = new Button { Name = "MoveOrderedTagUpButton", Content = "上移" };
        up.Classes.Add("secondary-action");
        up.Click += (_, _) => _picker.MoveActive(-1);
        var down = new Button { Name = "MoveOrderedTagDownButton", Content = "下移" };
        down.Classes.Add("secondary-action");
        down.Click += (_, _) => _picker.MoveActive(1);

        Child = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _picker,
                new WrapPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { _compositeInput, addComposite, up, down },
                },
                _error,
            },
        };
    }

    public IReadOnlyList<string> Values => _picker.Values;

    public TagPicker Picker => _picker;

    public bool TryAddComposite(string value)
    {
        if (!_allowComposites)
        {
            SetError("当前来源顺序不允许组合值。");
            return false;
        }

        if (!_picker.AddValue(value))
        {
            SetError("组合中的每个平台都必须来自当前核心 envs.js，且组合及平台不能重复。");
            return false;
        }

        _compositeInput.Text = string.Empty;
        SetError(null);
        return true;
    }

    private void SetError(string? message)
    {
        _error.Text = message ?? string.Empty;
        _error.IsVisible = !string.IsNullOrWhiteSpace(message);
    }
}
