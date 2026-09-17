using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DuCom.Controls;

public sealed record PlotRenderPoint(DateTimeOffset TimestampUtc, double Value);
public sealed record PlotRenderSeries(string Name, string Unit, string AxisId, Color Color, IReadOnlyList<PlotRenderPoint> Points);

public sealed class TimeSeriesPlotControl : FrameworkElement
{
    private IReadOnlyList<PlotRenderSeries> _series = [];
    private Point? _crosshair;
    private double _zoom = 1;
    private double _pan;

    public void SetSeries(IReadOnlyList<PlotRenderSeries> series)
    {
        _series = series ?? [];
        InvalidateVisual();
    }

    public void ResetView()
    {
        _zoom = 1;
        _pan = 0;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        Rect area = new(52, 12, Math.Max(0, ActualWidth - 64), Math.Max(0, ActualHeight - 40));
        Brush background = TryFindResource("Brush.PanelSurface") as Brush ?? Brushes.Transparent;
        Brush gridBrush = TryFindResource("Brush.PanelBorder") as Brush ?? Brushes.Gray;
        Brush textBrush = TryFindResource("Brush.TextSecondary") as Brush ?? Brushes.Gray;
        drawingContext.DrawRectangle(background, null, new Rect(RenderSize));
        if (area.Width < 20 || area.Height < 20) return;

        Pen grid = new(gridBrush, 1);
        for (int index = 0; index <= 5; index++)
        {
            double y = area.Top + area.Height * index / 5;
            drawingContext.DrawLine(grid, new Point(area.Left, y), new Point(area.Right, y));
        }

        PlotRenderPoint[] all = _series.SelectMany(item => item.Points).ToArray();
        if (all.Length == 0)
        {
            DrawText(drawingContext, "No samples", new Point(area.Left + 12, area.Top + 12), textBrush);
            return;
        }

        long rawMinTicks = all.Min(item => item.TimestampUtc.UtcDateTime.Ticks);
        long rawMaxTicks = all.Max(item => item.TimestampUtc.UtcDateTime.Ticks);
        double fullRange = Math.Max(1, rawMaxTicks - rawMinTicks);
        double visibleRange = fullRange / _zoom;
        double center = rawMaxTicks - visibleRange / 2 + _pan * visibleRange;
        double minTicks = center - visibleRange / 2;
        double maxTicks = center + visibleRange / 2;

        Dictionary<string, (double Min, double Max)> ranges = _series.GroupBy(item => item.AxisId).ToDictionary(
            group => group.Key,
            group =>
            {
                double[] values = group.SelectMany(item => item.Points)
                    .Where(point => point.TimestampUtc.UtcDateTime.Ticks >= minTicks && point.TimestampUtc.UtcDateTime.Ticks <= maxTicks)
                    .Select(point => point.Value).ToArray();
                if (values.Length == 0) return (0d, 1d);
                double min = values.Min();
                double max = values.Max();
                if (min == max) { min -= 1; max += 1; }
                return (min, max);
            });

        foreach (PlotRenderSeries series in _series)
        {
            (double min, double max) = ranges[series.AxisId];
            StreamGeometry geometry = new();
            using (StreamGeometryContext context = geometry.Open())
            {
                bool started = false;
                foreach (PlotRenderPoint point in series.Points)
                {
                    double ticks = point.TimestampUtc.UtcDateTime.Ticks;
                    if (ticks < minTicks || ticks > maxTicks) continue;
                    Point rendered = new(
                        area.Left + (ticks - minTicks) / visibleRange * area.Width,
                        area.Bottom - (point.Value - min) / (max - min) * area.Height);
                    if (!started) { context.BeginFigure(rendered, false, false); started = true; }
                    else context.LineTo(rendered, true, false);
                }
            }
            geometry.Freeze();
            Pen pen = new(new SolidColorBrush(series.Color), 1.5);
            pen.Freeze();
            drawingContext.DrawGeometry(null, pen, geometry);
        }

        DateTimeOffset left = new((long)minTicks, TimeSpan.Zero);
        DateTimeOffset right = new((long)maxTicks, TimeSpan.Zero);
        DrawText(drawingContext, left.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture), new Point(area.Left, area.Bottom + 5), textBrush);
        DrawText(drawingContext, right.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture), new Point(area.Right - 54, area.Bottom + 5), textBrush);

        if (_crosshair is Point mouse && area.Contains(mouse))
        {
            Pen crosshair = new(textBrush, 1) { DashStyle = DashStyles.Dot };
            drawingContext.DrawLine(crosshair, new Point(mouse.X, area.Top), new Point(mouse.X, area.Bottom));
            drawingContext.DrawLine(crosshair, new Point(area.Left, mouse.Y), new Point(area.Right, mouse.Y));
            PlotRenderPoint? nearest = all.MinBy(point => Math.Abs((area.Left + (point.TimestampUtc.UtcDateTime.Ticks - minTicks) / visibleRange * area.Width) - mouse.X));
            if (nearest is not null)
            {
                ToolTip = $"{nearest.TimestampUtc.ToLocalTime():HH:mm:ss.fff}  {nearest.Value:G6}";
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e) { _crosshair = e.GetPosition(this); InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { _crosshair = null; ToolTip = null; InvalidateVisual(); }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) _pan = Math.Clamp(_pan - Math.Sign(e.Delta) * 0.1, -1, 1);
        else _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.25 : 0.8), 1, 100);
        InvalidateVisual();
        e.Handled = true;
    }

    private void DrawText(DrawingContext context, string text, Point point, Brush brush) => context.DrawText(
        new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), point);
}
