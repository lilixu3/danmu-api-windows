using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace DanmuApi.App.Controls;

/// <summary>
/// 单选标签。对应核心自带前端的 <c>type === 'select'</c> 分支：
/// 「选择值」下方一排可换行胶囊，选中项用强调色实心。
/// 与多选编辑器的区别是点一个即替换而非追加。
/// </summary>
public sealed class TagSelectEditor : StackPanel
{
    private readonly List<Button> _buttons = [];
    private readonly IReadOnlyList<string> _options;
    private string _value;

    public TagSelectEditor(IEnumerable<string> options, string? initial)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Where(option => !string.IsNullOrWhiteSpace(option)).ToArray();
        if (_options.Count == 0)
        {
            throw new FormatException("该变量在核心 envs.js 里没有声明可选值，无法用单选编辑器");
        }

        _value = initial ?? string.Empty;

        Orientation = Orientation.Vertical;
        Spacing = 0;

        var row = ConfigForm.PillRow();
        foreach (var option in _options)
        {
            var button = new Button { Content = option, Name = "TagOptionButton" };
            button.Classes.Add("tag-pill");
            var captured = option;
            button.Click += (_, _) => Select(captured);
            _buttons.Add(button);
            row.Children.Add(button);
        }

        Children.Add(row);
        RefreshSelection();
    }

    public string Value => _value;

    public IReadOnlyList<string> Options => _options;

    public Button? SelectedButton => _buttons.FirstOrDefault(button => button.Classes.Contains("selected"));

    public event EventHandler? SelectionChanged;

    public void Select(string option)
    {
        if (!_options.Contains(option, StringComparer.Ordinal))
        {
            throw new FormatException($"“{option}”不在核心声明可选值内：{string.Join("、", _options)}");
        }

        if (string.Equals(_value, option, StringComparison.Ordinal))
        {
            return;
        }

        _value = option;
        RefreshSelection();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshSelection()
    {
        foreach (var button in _buttons)
        {
            button.Classes.Set("selected", Equals(button.Content, _value));
        }
    }
}
