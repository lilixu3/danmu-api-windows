using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;

namespace DanmuApi.App.Controls;

/// <summary>
/// 布尔开关。对应核心自带前端的 <c>type === 'boolean'</c> 分支：
/// 胶囊轨道 48×26、圆钮 20px、右侧「启用 / 禁用」文案。
/// </summary>
public sealed class SwitchEditor : Border
{
    private const double ThumbInset = 3;

    private readonly Border _track = new();
    private readonly Ellipse _thumb = new();
    private readonly TextBlock _caption = new();
    private bool _isOn;

    public SwitchEditor(bool initialValue, string onText = "启用", string offText = "禁用")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(onText);
        ArgumentException.ThrowIfNullOrWhiteSpace(offText);
        OnText = onText;
        OffText = offText;

        _track.Classes.Add("switch-track");
        _thumb.Classes.Add("switch-thumb");
        _caption.VerticalAlignment = VerticalAlignment.Center;

        var trackContent = new Grid();
        trackContent.Children.Add(_thumb);
        _track.Child = trackContent;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(_track);
        row.Children.Add(_caption);

        Child = row;
        Background = Avalonia.Media.Brushes.Transparent;
        Padding = new Thickness(0);
        Cursor = new Cursor(StandardCursorType.Hand);

        SetValueCore(initialValue);
        PointerPressed += (_, _) => IsOn = !IsOn;
    }

    public string OnText { get; }

    public string OffText { get; }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value)
            {
                return;
            }

            SetValueCore(value);
            IsOnChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? IsOnChanged;

    public string CaptionText => _caption.Text ?? string.Empty;

    private void SetValueCore(bool value)
    {
        _isOn = value;
        _caption.Text = value ? OnText : OffText;
        _thumb.HorizontalAlignment = value ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _thumb.Margin = value
            ? new Thickness(0, 0, ThumbInset, 0)
            : new Thickness(ThumbInset, 0, 0, 0);
        _track.Classes.Set("on", value);
    }
}
