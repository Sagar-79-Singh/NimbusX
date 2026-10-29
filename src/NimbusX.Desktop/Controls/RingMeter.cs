using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace NimbusX.Desktop.Controls;

/// <summary>A circular usage meter used for quota and cache budget tiles.</summary>
public sealed class RingMeter : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(RingMeter),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness),
        typeof(double),
        typeof(RingMeter),
        new FrameworkPropertyMetadata(7d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush),
        typeof(Brush),
        typeof(RingMeter),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0x40, 0x8B, 0x98, 0xA9)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke),
        typeof(Brush),
        typeof(RingMeter),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF)), FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 4)
        {
            return;
        }

        double thickness = Math.Clamp(Thickness, 1, size / 3);
        double radius = (size - thickness) / 2;
        Point center = new(ActualWidth / 2, ActualHeight / 2);

        Pen track = new(TrackBrush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        drawingContext.DrawEllipse(null, track, center, radius, radius);

        double fraction = Math.Clamp(Value, 0d, 1d);
        if (fraction <= 0.0001)
        {
            return;
        }

        double angle = fraction * 359.99;
        Point start = PointOnCircle(center, radius, -90);
        Point end = PointOnCircle(center, radius, -90 + angle);

        StreamGeometry geometry = new();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(start, isFilled: false, isClosed: false);
            context.ArcTo(end, new Size(radius, radius), 0, angle > 180, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        Pen pen = new(Stroke, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        drawingContext.DrawGeometry(null, pen, geometry);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 72 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 72 : availableSize.Height);

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        double radians = degrees * Math.PI / 180d;
        return new Point(center.X + (radius * Math.Cos(radians)), center.Y + (radius * Math.Sin(radians)));
    }
}
