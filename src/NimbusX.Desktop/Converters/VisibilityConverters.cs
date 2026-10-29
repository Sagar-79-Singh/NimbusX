using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NimbusX.Desktop.Converters;

/// <summary>true to Visible; set <see cref="Invert"/> to flip it. Used instead of a converter per page.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        if (Invert)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("One-way converter.");
}

/// <summary>Visible when a string has content; <see cref="Invert"/> shows it when the string is empty.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool hasText = value is string text && !string.IsNullOrWhiteSpace(text);
        if (Invert)
        {
            hasText = !hasText;
        }

        return hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("One-way converter.");
}

/// <summary>Visible when the bound object is not null.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool present = value is not null;
        if (Invert)
        {
            present = !present;
        }

        return present ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("One-way converter.");
}

/// <summary>Visible when a collection count is greater than zero.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int count = value is int number ? number : 0;
        bool any = count > 0;
        if (Invert)
        {
            any = !any;
        }

        return any ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("One-way converter.");
}
