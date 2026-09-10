using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using DanmuApi.Core;
using DanmuApi.Runtime;

namespace DanmuApi.App.Controls;

public sealed class ColorPaletteEditor : Border
{
    private readonly bool _isGradient;
    private readonly List<uint> _colors;
    private readonly ComboBox _mode;
    private readonly ComboBox _skins;
    private readonly ColorWheel _wheel = new() { Name = "ColorPaletteWheel" };
    private readonly Slider _lightness = new()
    {
        Name = "ColorPaletteLightnessSlider",
        Minimum = 10,
        Maximum = 90,
        Value = 50,
        Width = 220,
    };
    private readonly TextBox _hexInput = new()
    {
        Name = "ColorPaletteHexInput",
        Watermark = "#FF6699 或 16737945",
        MinWidth = 220,
    };
    private readonly TextBox _batchInput = new()
    {
        Name = "ColorPaletteBatchInput",
        Watermark = "批量颜色：#FF0000, 65280, #0000FF",
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 58,
    };
    private readonly WrapPanel _colorPanel = new() { Orientation = Orientation.Horizontal };
    private readonly Rectangle _preview = new() { Width = 48, Height = 48, RadiusX = 8, RadiusY = 8 };
    private readonly StackPanel _customPanel = new() { Spacing = 12 };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };

    public ColorPaletteEditor(CoreEnvDefinition definition, string initial)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(initial);
        _isGradient = definition.Key == "GRADIENT_COLORS";
        var parsed = _isGradient
            ? CoreEnvStructuredValues.ParseGradientPalette(initial)
            : new CoreGradientPalette(null, CoreEnvStructuredValues.ParseColorPool(initial));
        _colors = new List<uint>(parsed.Colors);
        _mode = new ComboBox
        {
            Name = "ColorPaletteModeComboBox",
            ItemsSource = _isGradient ? new[] { "自定义颜色", "预设皮肤" } : new[] { "自定义颜色" },
            SelectedIndex = _isGradient && parsed.Skin is not null ? 1 : 0,
            MinWidth = 170,
        };
        _skins = new ComboBox
        {
            Name = "ColorPaletteSkinComboBox",
            ItemsSource = CoreEnvStructuredValues.GradientSkins,
            SelectedItem = parsed.Skin ?? CoreEnvStructuredValues.GradientSkins[0],
            MinWidth = 170,
            IsVisible = _isGradient && parsed.Skin is not null,
        };
        _error.Classes.Add("danger-text");

        _mode.SelectionChanged += (_, _) =>
        {
            var useSkin = _isGradient && _mode.SelectedIndex == 1;
            _skins.IsVisible = useSkin;
            _customPanel.IsVisible = !useSkin;
        };
        _wheel.HueChanged += (_, _) => RefreshPreview();
        _lightness.PropertyChanged += (_, args) =>
        {
            if (args.Property == Slider.ValueProperty)
            {
                RefreshPreview();
            }
        };

        Child = BuildContent();
        RenderColors();
        RefreshPreview();
    }

    public string? ErrorMessage
    {
        get => _error.Text;
        set
        {
            _error.Text = value ?? string.Empty;
            _error.IsVisible = !string.IsNullOrWhiteSpace(value);
        }
    }

    public IReadOnlyList<uint> Colors => _colors.ToArray();

    public bool IsSkinMode => _isGradient && _mode.SelectedIndex == 1;

    public string? SelectedSkin => IsSkinMode ? _skins.SelectedItem?.ToString() : null;

    public string GetValue()
    {
        var value = IsSkinMode
            ? CoreEnvStructuredValues.FormatGradientPalette(new CoreGradientPalette(SelectedSkin, []))
            : _isGradient
                ? CoreEnvStructuredValues.FormatGradientPalette(new CoreGradientPalette(null, _colors.ToArray()))
                : CoreEnvStructuredValues.FormatColorPool(_colors);
        if (_isGradient)
        {
            _ = CoreEnvStructuredValues.ParseGradientPalette(value);
        }
        else
        {
            _ = CoreEnvStructuredValues.ParseColorPool(value);
        }

        return value;
    }

    public void ClearError() => ErrorMessage = null;

    public void AddColor(uint color)
    {
        if (color <= 0xFFFFFF && !_colors.Contains(color))
        {
            _colors.Add(color);
            RenderColors();
        }
    }

    public bool TryAddInputColor(string raw)
    {
        if (!TryParseColor(raw, out var color))
        {
            ErrorMessage = "请输入 6 位十六进制 RGB 或 0 到 16777215 的十进制颜色值。";
            return false;
        }

        ClearError();
        AddColor(color);
        return true;
    }

    public bool TryAddBatch(string raw)
    {
        var invalid = new List<string>();
        var parsed = new List<uint>();
        foreach (var part in raw.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseColor(part, out var color))
            {
                parsed.Add(color);
            }
            else
            {
                invalid.Add(part);
            }
        }

        if (invalid.Count > 0 || parsed.Count == 0)
        {
            ErrorMessage = invalid.Count > 0
                ? $"存在无法识别的颜色：{string.Join(", ", invalid)}"
                : "请输入至少一个颜色值。";
            return false;
        }

        ClearError();
        foreach (var color in parsed)
        {
            AddColor(color);
        }

        _batchInput.Text = string.Empty;
        return true;
    }

    public void ResetColors()
    {
        _colors.Clear();
        RenderColors();
    }

    private Control BuildContent()
    {
        _customPanel.Children.Add(new TextBlock { Text = "当前颜色池", FontWeight = FontWeight.SemiBold });
        _customPanel.Children.Add(_colorPanel);
        _customPanel.Children.Add(new TextBlock { Text = "取色", FontWeight = FontWeight.SemiBold });

        var colorControls = new StackPanel { Spacing = 8 };
        colorControls.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _preview, CreateMutedText("色相与亮度预览") },
        });
        colorControls.Children.Add(new TextBlock { Text = "亮度" });
        colorControls.Children.Add(_lightness);
        var addCurrent = new Button { Name = "AddCurrentColorButton", Content = "添加当前颜色" };
        addCurrent.Classes.Add("secondary-action");
        addCurrent.Click += (_, _) => AddColor(HslToDecimal(_wheel.Hue, _lightness.Value));
        colorControls.Children.Add(addCurrent);
        colorControls.Children.Add(new TextBlock { Text = "输入颜色" });
        var addInput = new Button { Name = "AddInputColorButton", Content = "添加输入颜色" };
        addInput.Classes.Add("secondary-action");
        addInput.Click += (_, _) => TryAddInputColor(_hexInput.Text ?? string.Empty);
        colorControls.Children.Add(new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _hexInput, addInput },
        });
        var colorLayout = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemWidth = 250,
            Children = { _wheel, colorControls },
        };
        _customPanel.Children.Add(colorLayout);

        _customPanel.Children.Add(new TextBlock { Text = "批量添加", FontWeight = FontWeight.SemiBold });
        var batchAdd = new Button { Name = "AddBatchColorsButton", Content = "确认批量添加" };
        batchAdd.Classes.Add("secondary-action");
        batchAdd.Click += (_, _) => TryAddBatch(_batchInput.Text ?? string.Empty);
        var random = new Button { Name = "AddRandomColorButton", Content = "随机添加" };
        random.Classes.Add("secondary-action");
        random.Click += (_, _) => AddColor((uint)Random.Shared.Next(0x1000000));
        var reset = new Button { Name = "ResetColorPaletteButton", Content = "恢复默认" };
        reset.Classes.Add("danger-action");
        reset.Click += (_, _) => ResetColors();
        _customPanel.Children.Add(_batchInput);
        _customPanel.Children.Add(new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { batchAdd, random, reset },
        });

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(_isGradient
            ? CreateLabeledControl("渐变模式", _mode)
            : CreateMutedText("颜色池"));
        content.Children.Add(_skins);
        content.Children.Add(_customPanel);
        content.Children.Add(_error);
        return content;
    }

    private void RenderColors()
    {
        _colorPanel.Children.Clear();
        if (_colors.Count == 0)
        {
            _colorPanel.Children.Add(CreateMutedText("未配置，将使用核心默认颜色池"));
            return;
        }

        foreach (var color in _colors.ToArray())
        {
            var swatch = new Rectangle
            {
                Width = 34,
                Height = 34,
                RadiusX = 6,
                RadiusY = 6,
                Fill = new SolidColorBrush(ToAvaloniaColor(color)),
            };
            var remove = new Button
            {
                Name = "RemovePaletteColorButton",
                Content = "×",
                MinWidth = 28,
                MinHeight = 28,
            };
            remove.Classes.Add("chip");
            remove.Click += (_, _) =>
            {
                _colors.Remove(color);
                RenderColors();
            };
            _colorPanel.Children.Add(new StackPanel
            {
                Spacing = 4,
                Margin = new Thickness(0, 0, 8, 8),
                Children =
                {
                    swatch,
                    new TextBlock
                    {
                        Text = CoreEnvStructuredValues.FormatHexColor(color),
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    remove,
                },
            });
        }
    }

    private void RefreshPreview()
    {
        _preview.Fill = new SolidColorBrush(ToAvaloniaColor(HslToDecimal(_wheel.Hue, _lightness.Value)));
    }

    private static bool TryParseColor(string raw, out uint color)
    {
        color = 0;
        var text = raw.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        if (text.StartsWith('#'))
        {
            if (text.Length != 7)
            {
                return false;
            }

            try
            {
                color = CoreEnvStructuredValues.ParseHexColor(text);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        return uint.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out color)
            && color <= 0xFFFFFF;
    }

    private static uint HslToDecimal(double hue, double lightnessValue)
    {
        var h = hue / 60d;
        var l = lightnessValue / 100d;
        var c = 1d - Math.Abs(2d * l - 1d);
        var x = c * (1d - Math.Abs(h % 2d - 1d));
        var (r1, g1, b1) = h switch
        {
            < 1 => (c, x, 0d),
            < 2 => (x, c, 0d),
            < 3 => (0d, c, x),
            < 4 => (0d, x, c),
            < 5 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        var m = l - c / 2d;
        return (uint)(Math.Clamp((int)Math.Round((r1 + m) * 255d), 0, 255) << 16 |
                      Math.Clamp((int)Math.Round((g1 + m) * 255d), 0, 255) << 8 |
                      Math.Clamp((int)Math.Round((b1 + m) * 255d), 0, 255));
    }

    private static Color ToAvaloniaColor(uint color) =>
        Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color);

    private static TextBlock CreateMutedText(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.Classes.Add("body-muted");
        return block;
    }

    private static StackPanel CreateLabeledControl(string label, Control control) =>
        new()
        {
            Spacing = 4,
            Children = { new TextBlock { Text = label }, control },
        };
}
