using System;
using System.Globalization;
using System.Text;

namespace NimbusX.Core.Text;

/// <summary>Human friendly text helpers shared by the UI, CLI and logs.</summary>
public static class TextFormat
{
    /// <summary>"3 s", "4 min", "2 h 5 min".</summary>
    public static string Duration(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            return "Unavailable";
        }

        if (value.TotalSeconds < 60)
        {
            return (int)Math.Round(value.TotalSeconds) + " s";
        }

        if (value.TotalMinutes < 60)
        {
            int minutes = (int)value.TotalMinutes;
            int seconds = value.Seconds;
            return seconds == 0 ? minutes + " min" : minutes + " min " + seconds + " s";
        }

        if (value.TotalHours < 24)
        {
            int hours = (int)value.TotalHours;
            int minutes = value.Minutes;
            return minutes == 0 ? hours + " h" : hours + " h " + minutes + " min";
        }

        return (int)value.TotalDays + " d " + value.Hours + " h";
    }

    /// <summary>"just now", "5 min ago", "yesterday", "3 days ago".</summary>
    public static string Relative(DateTimeOffset when, DateTimeOffset? now = null)
    {
        DateTimeOffset reference = now ?? DateTimeOffset.Now;
        TimeSpan delta = reference - when;
        if (delta < TimeSpan.Zero)
        {
            delta = TimeSpan.Zero;
        }

        if (delta.TotalSeconds < 45)
        {
            return "just now";
        }

        if (delta.TotalMinutes < 60)
        {
            int minutes = Math.Max(1, (int)delta.TotalMinutes);
            return minutes + (minutes == 1 ? " min ago" : " min ago");
        }

        if (delta.TotalHours < 24)
        {
            int hours = (int)delta.TotalHours;
            return hours == 1 ? "1 hour ago" : hours + " hours ago";
        }

        if (delta.TotalDays < 2)
        {
            return "yesterday";
        }

        int days = (int)delta.TotalDays;
        return days + " days ago";
    }

    /// <summary>Shortens a path for display, keeping the tail.</summary>
    public static string ShortPath(string? path, int maxLength = 48)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        if (path.Length <= maxLength)
        {
            return path;
        }

        int tail = Math.Max(8, maxLength - 3);
        return "..." + path[^tail..];
    }

    /// <summary>Truncates to <paramref name="maxLength"/> characters adding an ellipsis.</summary>
    public static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || maxLength <= 0)
        {
            return string.Empty;
        }

        return text.Length <= maxLength ? text : text[..Math.Max(0, maxLength - 1)] + "\u2026";
    }

    /// <summary>Converts a display name into a filesystem/rclone-safe identifier.</summary>
    public static string Slug(string? text, int maxLength = 32)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "item";
        }

        StringBuilder builder = new(text.Length);
        bool lastWasDash = false;
        foreach (char raw in text.Trim().ToLowerInvariant())
        {
            char c = raw;
            bool keep = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
            if (keep)
            {
                builder.Append(c);
                lastWasDash = false;
            }
            else if (!lastWasDash && builder.Length > 0)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        string result = builder.ToString().Trim('-');
        if (result.Length == 0)
        {
            result = "item";
        }

        return result.Length <= maxLength ? result : result[..maxLength].Trim('-');
    }

    /// <summary>Percentage with no decimals, clamped to 0..100.</summary>
    public static string Percent(double fraction, int decimals = 0)
    {
        double value = Math.Clamp(fraction * 100d, 0d, 100d);
        return value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>Thousands separated integer.</summary>
    public static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"1 game", "12 games". Returns the noun that follows a count, already inflected for it.</summary>
    public static string Pluralize(long count, string singular)
    {
        Guard.NotNullOrWhiteSpace(singular, nameof(singular));
        if (count == 1)
        {
            return singular;
        }

        char last = singular[singular.Length - 1];
        if (last is 's' or 'x' or 'z' || singular.EndsWith("ch", StringComparison.OrdinalIgnoreCase)
            || singular.EndsWith("sh", StringComparison.OrdinalIgnoreCase))
        {
            return singular + "es";
        }

        if (last is 'y' && singular.Length > 1 && !IsVowel(singular[singular.Length - 2]))
        {
            return singular[..^1] + "ies";
        }

        return singular + "s";

        static bool IsVowel(char value) => "aeiouAEIOU".IndexOf(value) >= 0;
    }

    /// <summary>Formats a rate in Mbps.</summary>
    public static string Megabits(double megabytesPerSecond) =>
        (megabytesPerSecond * 8d).ToString("0.0", CultureInfo.InvariantCulture) + " Mbps";
}
