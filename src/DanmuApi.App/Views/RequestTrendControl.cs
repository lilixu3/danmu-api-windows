using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DanmuApi.App.ViewModels;

namespace DanmuApi.App.Views;

/// <summary>Linear samples on an elapsed-time axis. Missing measurements explicitly break the line.</summary>
public sealed class RequestTrendControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<RequestRatePoint>?> PointsProperty =
        AvaloniaProperty.Register<RequestTrendControl, IReadOnlyList<RequestRatePoint>?>(nameof(Points));
    public static readonly StyledProperty<IBrush> LineBrushProperty =
        AvaloniaProperty.Register<RequestTrendControl, IBrush>(nameof(LineBrush), Brushes.Teal);
    public static readonly StyledProperty<IBrush> LabelBrushProperty =
        AvaloniaProperty.Register<RequestTrendControl, IBrush>(nameof(LabelBrush), Brushes.Gray);
    public IReadOnlyList<RequestRatePoint>? Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public IBrush LineBrush { get => GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public IBrush LabelBrush { get => GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    static RequestTrendControl() => AffectsRender<RequestTrendControl>(PointsProperty, LineBrushProperty, LabelBrushProperty);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var plot = new Rect(40, 16, Math.Max(1, Bounds.Width - 52), Math.Max(1, Bounds.Height - 48));
        var points = Points ?? [];
        var maximum = Math.Max(1, points.Where(p => p.Rate.HasValue).Select(p => p.Rate!.Value).DefaultIfEmpty().Max() * 1.15);
        var intervals = plot.Height < 90 ? 2 : 4;
        for (var i = 0; i <= intervals; i++)
        {
            var y = plot.Bottom - plot.Height * i / intervals;
            using (context.PushOpacity(.16)) context.DrawLine(new Pen(LabelBrush, 1), new Point(plot.Left, y), new Point(plot.Right, y));
            Label(context, (maximum * i / intervals).ToString("0.#", CultureInfo.InvariantCulture), new Point(0, y - 7));
        }
        var end = points.Count > 0 ? points[^1].Seconds : 0;
        var span = points.Count > 1 ? Math.Max(5, end - points[0].Seconds) : 5;
        Label(context, $"−{span:0} 秒", new Point(plot.Left, plot.Bottom + 12));
        Label(context, "最近采样", new Point(Math.Max(plot.Left, plot.Right - 52), plot.Bottom + 12));
        Point? previous = null;
        foreach (var sample in points)
        {
            if (sample.Rate is not { } rate) { previous = null; continue; }
            var point = new Point(plot.Right - (end - sample.Seconds) / span * plot.Width,
                plot.Bottom - rate / maximum * plot.Height);
            if (previous is { } from) context.DrawLine(new Pen(LineBrush, 2.5), from, point);
            context.DrawEllipse(LineBrush, null, point, 2.5, 2.5);
            previous = point;
        }
    }

    private void Label(DrawingContext context, string text, Point position) => context.DrawText(
        new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, LabelBrush), position);
}
