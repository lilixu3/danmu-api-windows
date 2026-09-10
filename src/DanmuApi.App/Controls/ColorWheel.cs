using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace DanmuApi.App.Controls;

public sealed class ColorWheel : Control
{
    public static readonly StyledProperty<double> HueProperty =
        AvaloniaProperty.Register<ColorWheel, double>(nameof(Hue), 0d);

    public ColorWheel()
    {
        Width = 210;
        Height = 210;
        MinWidth = 160;
        MinHeight = 160;
        PointerPressed += OnPointerEvent;
        PointerMoved += OnPointerEvent;
    }

    public double Hue
    {
        get => GetValue(HueProperty);
        set => SetValue(HueProperty, value % 360d < 0 ? value % 360d + 360d : value % 360d);
    }

    public event EventHandler? HueChanged;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HueProperty)
        {
            HueChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var center = new Point(Bounds.Width / 2d, Bounds.Height / 2d);
        var outer = Math.Max(20d, Math.Min(Bounds.Width, Bounds.Height) / 2d - 6d);
        var inner = outer * 0.22d;
        var thickness = Math.Max(2d, outer - inner);
        var middle = (outer + inner) / 2d;

        for (var index = 0; index < 120; index++)
        {
            var hue = index * 3d;
            var start = (hue - 90d) * Math.PI / 180d;
            var end = (hue + 3d - 90d) * Math.PI / 180d;
            var startPoint = new Point(
                center.X + inner * Math.Cos(start),
                center.Y + inner * Math.Sin(start));
            var endPoint = new Point(
                center.X + outer * Math.Cos(end),
                center.Y + outer * Math.Sin(end));
            context.DrawLine(
                new Pen(new SolidColorBrush(HslToColor(hue, 100d, 50d)), thickness),
                startPoint,
                endPoint);
        }

        context.DrawEllipse(Brushes.Transparent, new Pen(Brushes.White, 2), center, inner, inner);
        var markerAngle = (Hue - 90d) * Math.PI / 180d;
        var marker = new Point(
            center.X + middle * Math.Cos(markerAngle),
            center.Y + middle * Math.Sin(markerAngle));
        context.DrawEllipse(Brushes.Transparent, new Pen(Brushes.Black, 2), marker, 7, 7);
        context.DrawEllipse(Brushes.Transparent, new Pen(Brushes.White, 1), marker, 5, 5);
    }

    private void OnPointerEvent(object? sender, PointerEventArgs args)
    {
        var point = args.GetCurrentPoint(this);
        if (args.RoutedEvent == PointerMovedEvent && !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var center = new Point(Bounds.Width / 2d, Bounds.Height / 2d);
        var dx = point.Position.X - center.X;
        var dy = point.Position.Y - center.Y;
        var radius = Math.Sqrt(dx * dx + dy * dy);
        var outer = Math.Min(Bounds.Width, Bounds.Height) / 2d - 6d;
        var inner = outer * 0.22d;
        if (radius < inner || radius > outer)
        {
            return;
        }

        var hue = Math.Atan2(dy, dx) * 180d / Math.PI + 90d;
        Hue = hue < 0 ? hue + 360d : hue;
        args.Handled = true;
    }

    private static Color HslToColor(double hue, double saturation, double lightness)
    {
        saturation /= 100d;
        lightness /= 100d;
        var k = (double n) => (n + hue / 30d) % 12d;
        var a = saturation * Math.Min(lightness, 1d - lightness);
        var f = (double n) => lightness - a * Math.Max(-1d, Math.Min(Math.Min(k(n) - 3d, 9d - k(n)), 1d));
        return Color.FromRgb(
            ToByte(f(0) * 255d),
            ToByte(f(8) * 255d),
            ToByte(f(4) * 255d));
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value), 0d, 255d);
}
