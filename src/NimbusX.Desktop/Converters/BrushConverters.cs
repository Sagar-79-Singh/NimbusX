using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace NimbusX.Desktop.Converters;

/// <summary>Maps a boolean state to one of two brushes.</summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    public Brush TrueBrush { get; set; } = Brushes.White;

    public Brush FalseBrush { get; set; } = Brushes.Gray;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool flag && flag ? TrueBrush : FalseBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("One-way converter.");
}

/// <summary>Maps a diagnostic severity name ("Info", "Warning", "Error") to the warning/danger colour.</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    public Brush InfoBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));

    public Brush WarningBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0xD2, 0x99, 0x22));

    public Brush ErrorBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string severity)
        {
            return InfoBrush;
        }

        if (severity.StartsWith("Error", StringComparison.OrdinalIgnoreCase) ||
            severity.StartsWith("Critical", StringComparison.OrdinalIgnoreCase))
        {
            return ErrorBrush;
        }

        return severity.StartsWith("Warn", StringComparison.OrdinalIgnoreCase) ? WarningBrush : InfoBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("One-way converter.");
}

/// <summary>Turns a 0..1 fraction into a percentage label such as "63%".</summary>
public sealed class FractionToPercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double fraction = value is double d ? d : 0d;
        return (fraction * 100d).ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("One-way converter.");
}
