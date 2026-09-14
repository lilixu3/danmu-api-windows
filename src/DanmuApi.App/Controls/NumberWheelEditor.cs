using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DanmuApi.App.Controls;

/// <summary>
/// 数字滚轮。对应核心自带前端的 <c>type === 'number'</c> 分支：
/// 面板底框内左侧 ▲/▼ 竖排按钮、右侧 30px 强调色数字，下方一条 range 滑块。
/// </summary>
public sealed class NumberWheelEditor : StackPanel
{
    private readonly double _minimum;
    private readonly double _maximum;
    private readonly TextBlock _display = new();
    private readonly Slider _slider = new();
    private double _value;

    public NumberWheelEditor(double minimum, double maximum, double? initial)
    {
        if (maximum <= minimum)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum), "数字上限必须大于下限");
        }

        _minimum = minimum;
        _maximum = maximum;
        // 核心口径：空值按下限处理（const currentValue = value || min）。
        _value = initial ?? minimum;
        _value = Math.Clamp(_value, minimum, maximum);

        Orientation = Orientation.Vertical;
        Spacing = 8;

        _display.Classes.Add("number-display");

        var up = new Button { Content = "▲", Name = "NumberStepUpButton" };
        up.Classes.Add("number-step");
        up.Click += (_, _) => Step(1);

        var down = new Button { Content = "▼", Name = "NumberStepDownButton" };
        down.Classes.Add("number-step");
        down.Click += (_, _) => Step(-1);

        var controls = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Children = { up, down },
        };

        var pickerContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { controls, _display },
        };
        var picker = new Border { Child = pickerContent };
        picker.Classes.Add("number-picker");

        _slider.Minimum = minimum;
        _slider.Maximum = maximum;
        _slider.Value = _value;
        _slider.IsSnapToTickEnabled = true;
        _slider.TickFrequency = 1;
        _slider.PropertyChanged += (_, args) =>
        {
            if (args.Property == Slider.ValueProperty)
            {
                SetValueCore(_slider.Value);
            }
        };

        Children.Add(picker);
        Children.Add(_slider);

        SetValueCore(_value);
    }

    /// <summary>真实数值（写回配置时用，不带千分位/小数点噪声）。</summary>
    public double Value => _value;

    /// <summary>用于保存的文本：整数不写小数点，与核心 <c>#num-value.textContent</c> 一致。</summary>
    public string ValueText => _value == Math.Floor(_value)
        ? ((long)_value).ToString(CultureInfo.InvariantCulture)
        : _value.ToString("G29", CultureInfo.InvariantCulture);

    public double Minimum => _minimum;

    public double Maximum => _maximum;

    public string DisplayText => _display.Text ?? string.Empty;

    private void Step(double delta)
    {
        SetValueCore(Math.Clamp(_value + delta, _minimum, _maximum));
        _slider.Value = _value;
    }

    private void SetValueCore(double value)
    {
        _value = value;
        _display.Text = ValueText;
    }
}
