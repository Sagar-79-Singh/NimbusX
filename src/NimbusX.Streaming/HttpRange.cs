using System;

namespace NimbusX.Streaming;

/// <summary>
/// Parses a single HTTP <c>Range</c> header against a known file size.
/// </summary>
/// <remarks>
/// NimbusX answers one range per request. Multi-range requests are refused with 416 rather than being silently
/// turned into a full-file download, because a player that asks for several ranges would otherwise re-download
/// the entire file through the cloud drive.
/// </remarks>
public static class HttpRange
{
    public const int MaxHeaderLength = 200;

    /// <summary>True when the request asked for a range at all.</summary>
    public static bool IsRangeRequest(string? header) =>
        !string.IsNullOrWhiteSpace(header) && header!.TrimStart().StartsWith("bytes=", StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(string? header, long size, out long start, out long end, out string? error)
    {
        start = 0;
        end = size > 0 ? size - 1 : 0;
        error = null;

        if (string.IsNullOrWhiteSpace(header))
        {
            return true;
        }

        string value = header!.Trim();
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            error = "Unsupported range unit (only 'bytes' is supported).";
            return false;
        }

        if (value.Length > MaxHeaderLength)
        {
            error = "The Range header is too long.";
            return false;
        }

        string spec = value.Substring("bytes=".Length).Trim();
        if (spec.Contains(',', StringComparison.Ordinal))
        {
            error = "Multiple ranges in one request are not supported; ask for one range at a time.";
            return false;
        }

        int dash = spec.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            error = "The Range header has no '-' separator.";
            return false;
        }

        string first = spec.Substring(0, dash).Trim();
        string second = spec.Substring(dash + 1).Trim();

        if (size <= 0)
        {
            error = "The file is empty.";
            return false;
        }

        if (first.Length == 0)
        {
            // Suffix range: bytes=-N means the last N bytes.
            if (!long.TryParse(second, out long suffix) || suffix <= 0)
            {
                error = "The suffix length is not a positive number.";
                return false;
            }

            start = Math.Max(0, size - suffix);
            end = size - 1;
            return true;
        }

        if (!long.TryParse(first, out start) || start < 0)
        {
            error = "The range start is not a valid number.";
            return false;
        }

        if (start >= size)
        {
            error = "The range starts beyond the end of the file.";
            return false;
        }

        if (second.Length == 0)
        {
            end = size - 1;
            return true;
        }

        if (!long.TryParse(second, out end) || end < start)
        {
            error = "The range end is not a valid number or is before the start.";
            return false;
        }

        if (end >= size)
        {
            end = size - 1;
        }

        return true;
    }
}
