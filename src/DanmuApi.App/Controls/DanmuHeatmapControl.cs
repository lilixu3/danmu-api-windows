using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DanmuApi.Runtime;

namespace DanmuApi.App.Controls;

public sealed class DanmuHeatmapControl : Control
{
    private const double ChartPadding = 8d;

    public static readonly StyledProperty<IReadOnlyList<DanmuHeatmapBucket>> BucketsProperty =
        AvaloniaProperty.Register<DanmuHeatmapControl, IReadOnlyList<DanmuHeatmapBucket>>(
            nameof(Buckets),
            Array.Empty<DanmuHeatmapBucket>());

    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<DanmuHeatmapControl, int>(
            nameof(SelectedIndex),
            -1,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    static DanmuHeatmapControl()
    {
        AffectsRender<DanmuHeatmapControl>(BucketsProperty, SelectedIndexProperty);
        AffectsMeasure<DanmuHeatmapControl>(BucketsProperty);
    }

    public DanmuHeatmapControl()
    {
        Focusable = true;
        MinHeight = 120;
        ClipToBounds = true;
    }

    public IReadOnlyList<DanmuHeatmapBucket> Buckets
    {
        get => GetValue(BucketsProperty);
        set => SetValue(BucketsProperty, value ?? Array.Empty<DanmuHeatmapBucket>());
    }

    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var buckets = Buckets;
        var chartWidth = Math.Max(0, Bounds.Width - ChartPadding * 2);
        var chartHeight = Math.Max(0, Bounds.Height - ChartPadding * 2);
        if (buckets.Count == 0 || chartWidth <= 0 || chartHeight <= 0)
        {
            return;
        }

        context.FillRectangle(new SolidColorBrush(Color.FromArgb(24, 128, 128, 128)), Bounds);
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(46, 128, 128, 128)), 1);
        for (var line = 1; line <= 3; line++)
        {
            var y = ChartPadding + chartHeight * line / 4d;
            context.DrawLine(gridPen, new Point(ChartPadding, y), new Point(ChartPadding + chartWidth, y));
        }

        var slotWidth = chartWidth / buckets.Count;
        for (var index = 0; index < buckets.Count; index++)
        {
            var bucket = buckets[index];
            var intensity = Math.Clamp(bucket.Intensity, 0d, 1d);
            var isSelected = index == SelectedIndex;
            var height = Math.Max(3d, chartHeight * (0.08d + intensity * 0.92d));
            var gap = Math.Clamp(slotWidth * 0.16d, 1d, 3d);
            var left = ChartPadding + slotWidth * index + gap / 2d;
            var width = Math.Max(1d, slotWidth - gap);
            var top = ChartPadding + chartHeight - height;
            var color = Interpolate(
                Color.FromRgb(66, 126, 234),
                Color.FromRgb(239, 68, 68),
                intensity);

            if (isSelected)
            {
                context.FillRectangle(
                    new SolidColorBrush(Color.FromArgb(36, 59, 130, 246)),
                    new Rect(ChartPadding + slotWidth * index, ChartPadding, slotWidth, chartHeight));
            }

            context.FillRectangle(
                new SolidColorBrush(color),
                new Rect(left, top, width, height),
                (float)Math.Min(3d, width / 2d));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs args)
    {
        base.OnPointerPressed(args);
        Focus();
        args.Pointer.Capture(this);
        SelectFromPosition(args.GetPosition(this));
        args.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs args)
    {
        base.OnPointerMoved(args);
        if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            SelectFromPosition(args.GetPosition(this));
            args.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs args)
    {
        base.OnPointerReleased(args);
        if (args.Pointer.Captured == this)
        {
            args.Pointer.Capture(null);
        }
        args.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs args)
    {
        base.OnKeyDown(args);
        if (SelectByKey(args.Key))
        {
            args.Handled = true;
        }
    }

    internal bool SelectByKey(Key key)
    {
        if (Buckets.Count == 0)
        {
            return false;
        }

        var next = key switch
        {
            Key.Home => 0,
            Key.End => Buckets.Count - 1,
            Key.Left => Math.Max(0, SelectedIndex < 0 ? 0 : SelectedIndex - 1),
            Key.Right => Math.Min(Buckets.Count - 1, SelectedIndex < 0 ? 0 : SelectedIndex + 1),
            _ => SelectedIndex,
        };
        if (key is not (Key.Home or Key.End or Key.Left or Key.Right))
        {
            return false;
        }

        SelectedIndex = next;
        return true;
    }

    internal void SelectAtRatio(double ratio)
    {
        if (Buckets.Count == 0)
        {
            SelectedIndex = -1;
            return;
        }

        SelectedIndex = Math.Clamp(
            (int)Math.Floor(Math.Clamp(ratio, 0d, 1d) * Buckets.Count),
            0,
            Buckets.Count - 1);
    }

    private void SelectFromPosition(Point position)
    {
        var chartWidth = Bounds.Width - ChartPadding * 2;
        if (chartWidth <= 0)
        {
            return;
        }

        SelectAtRatio((position.X - ChartPadding) / chartWidth);
    }

    private static Color Interpolate(Color start, Color end, double amount)
    {
        var ratio = Math.Clamp(amount, 0d, 1d);
        return Color.FromRgb(
            (byte)Math.Round(start.R + (end.R - start.R) * ratio),
            (byte)Math.Round(start.G + (end.G - start.G) * ratio),
            (byte)Math.Round(start.B + (end.B - start.B) * ratio));
    }
}
