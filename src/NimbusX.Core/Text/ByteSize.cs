using System;
using System.Globalization;

namespace NimbusX.Core.Text;

/// <summary>
/// Byte formatting/parsing. Only binary units are used (1 KB = 1024 bytes) to match how Windows and rclone
/// report cache and disk sizes.
/// </summary>
public static class ByteSize
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    /// <summary>Formats a byte count, e.g. <c>1.5 GB</c>.</summary>
    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            return "Unavailable";
        }

        double value = bytes;
        int unit = 0;
        while (value >= 1024d && unit < Units.Length - 1)
        {
            value /= 1024d;
            unit++;
        }

        string format = unit == 0 ? "0" : value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return value.ToString(format, CultureInfo.InvariantCulture) + " " + Units[unit];
    }

    /// <summary>Short form used on compact cards, e.g. <c>1.5G</c>.</summary>
    public static string FormatCompact(long bytes)
    {
        if (bytes < 0)
        {
            return "?";
        }

        double value = bytes;
        int unit = 0;
        while (value >= 1024d && unit < Units.Length - 1)
        {
            value /= 1024d;
            unit++;
        }

        string suffix = unit == 0 ? "B" : Units[unit][..1];
        return value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + suffix;
    }

    /// <summary>Formats a transfer rate, e.g. <c>12.3 MB/s</c>.</summary>
    public static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
        {
            return "0 B/s";
        }

        return Format((long)bytesPerSecond) + "/s";
    }

    /// <summary>Parses "10 GB", "10GB", "10g", "1073741824".</summary>
    public static bool TryParse(string? text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string cleaned = text.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        int splitAt = 0;
        while (splitAt < cleaned.Length && (char.IsDigit(cleaned[splitAt]) || cleaned[splitAt] == '.' || cleaned[splitAt] == ','))
        {
            splitAt++;
        }

        if (splitAt == 0)
        {
            return false;
        }

        string numberPart = cleaned[..splitAt].Replace(',', '.');
        string unitPart = cleaned[splitAt..];
        if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || number < 0)
        {
            return false;
        }

        double multiplier = unitPart switch
        {
            "" or "B" => 1d,
            "K" or "KB" or "KIB" => 1024d,
            "M" or "MB" or "MIB" => 1024d * 1024d,
            "G" or "GB" or "GIB" => 1024d * 1024d * 1024d,
            "T" or "TB" or "TIB" => 1024d * 1024d * 1024d * 1024d,
            "P" or "PB" or "PIB" => 1024d * 1024d * 1024d * 1024d * 1024d,
            _ => -1d
        };

        if (multiplier < 0)
        {
            return false;
        }

        double result = number * multiplier;
        if (result > long.MaxValue)
        {
            return false;
        }

        bytes = (long)Math.Round(result);
        return true;
    }

    public static long Gigabytes(double gigabytes) => (long)(gigabytes * 1024d * 1024d * 1024d);

    public static long Megabytes(double megabytes) => (long)(megabytes * 1024d * 1024d);

    public static long Mebibytes(long count) => count * 1024L * 1024L;

    public const long Gibibyte = 1024L * 1024L * 1024L;

    public const long Mebibyte = 1024L * 1024L;
}
