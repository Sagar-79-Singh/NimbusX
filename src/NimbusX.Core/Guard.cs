using System;
using System.Diagnostics.CodeAnalysis;

namespace NimbusX.Core;

/// <summary>
/// Small argument validation helpers. Kept intentionally tiny so test code can use the same calls.
/// </summary>
public static class Guard
{
    public static string NotNullOrWhiteSpace([NotNull] string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be null, empty or whitespace.", parameterName);
        }

        return value;
    }

    public static T NotNull<T>(T? value, string parameterName)
        where T : class
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        return value;
    }

    public static int Positive(int value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Value must be greater than zero.");
        }

        return value;
    }

    public static long NotNegative(long value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Value must not be negative.");
        }

        return value;
    }

    public static int Port(int value, string parameterName)
    {
        if (value <= 0 || value > 65535)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Port must be between 1 and 65535.");
        }

        return value;
    }

    public static T InRange<T>(T value, T min, T max, string parameterName)
        where T : IComparable<T>
    {
        if (value.CompareTo(min) < 0 || value.CompareTo(max) > 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"Value must be between {min} and {max}.");
        }

        return value;
    }
}
