using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace NimbusX.Desktop.Controls;

/// <summary>
/// A tiny line chart for measured series (cache growth, prefetch rate). It draws exactly the samples it is given and
/// nothing when there are fewer than two samples, so an empty chart never looks like real data.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(IEnumerable<double>),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke),
        typeof(Brush),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill),
        typeof(Brush),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable<double>? Values
    {
        get => (IEnumerable<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 2 || height <= 2)
        {
            return;
        }

        double[] values = Values?.ToArray() ?? Array.Empty<double>();
        if (values.Length < 2)
        {
            // A flat baseline makes it obvious the chart has no samples yet.
            Pen baseline = new(new SolidColorBrush(Color.FromArgb(0x40, 0x8B, 0x98, 0xA9)), 1);
            drawingContext.DrawLine(baseline, new Point(0, height - 1), new Point(width, height - 1));
            return;
        }

        double min = values.Min();
        double max = values.Max();
        double range = Math.Max(max - min, 1e-9);

        Point[] points = new Point[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            double x = width * i / (values.Length - 1);
            double normalised = (values[i] - min) / range;
            double y = height - 2 - normalised * (height - 4);
            points[i] = new Point(x, y);
        }

        StreamGeometry line = new();
        using (StreamGeometryContext context = line.Open())
        {
            context.BeginFigure(points[0], isFilled: false, isClosed: false);
            context.PolyLineTo(points.Skip(1).ToArray(), isStroked: true, isSmoothJoin: true);
        }

        line.Freeze();
        Pen pen = new(Stroke, 1.6) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        drawingContext.DrawGeometry(null, pen, line);

        if (Fill is not null)
        {
            StreamGeometry area = new();
            using (StreamGeometryContext context = area.Open())
            {
                context.BeginFigure(new Point(0, height), isFilled: true, isClosed: true);
                context.LineTo(points[0], isStroked: false, isSmoothJoin: false);
                context.PolyLineTo(points.Skip(1).ToArray(), isStroked: false, isSmoothJoin: true);
                context.LineTo(new Point(width, height), isStroked: false, isSmoothJoin: false);
            }

            area.Freeze();
            drawingContext.DrawGeometry(Fill, null, area);
        }
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 48 : availableSize.Height);
}
