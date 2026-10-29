using System;

namespace NimbusX.Streaming;

/// <summary>What kind of file the streaming proxy is looking at.</summary>
public enum MediaKind
{
    Unknown = 0,
    Video,
    Audio,
    Image,
    Archive,
    Iso,
    Executable,
    Other
}

/// <summary>
/// Container/size/duration facts read from the file itself.
/// </summary>
/// <remarks>
/// Duration is only filled in when it was parsed out of the container (MP4 <c>mvhd</c> or Matroska <c>Segment&gt;Info</c>).
/// For everything else it stays <c>null</c> and the UI shows "Unavailable" instead of inventing a runtime.
/// </remarks>
public sealed class MediaInfo
{
    public MediaInfo(
        MediaKind kind,
        string container,
        long sizeBytes,
        double? durationSeconds,
        bool isSeekable,
        string detail)
    {
        Kind = kind;
        Container = container;
        SizeBytes = sizeBytes;
        DurationSeconds = durationSeconds;
        IsSeekable = isSeekable;
        Detail = detail;
    }

    public MediaKind Kind { get; }

    /// <summary>Container/format name, e.g. "MP4", "Matroska", "ISO 9660", "ZIP".</summary>
    public string Container { get; }

    public long SizeBytes { get; }

    public double? DurationSeconds { get; }

    public bool IsSeekable { get; }

    /// <summary>How the facts were obtained, so a log never claims more than was measured.</summary>
    public string Detail { get; }

    public string DurationText =>
        DurationSeconds is null
            ? "Unavailable"
            : TimeSpan.FromSeconds(DurationSeconds.Value).ToString(DurationSeconds.Value >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    public string SizeText => Core.Text.ByteSize.Format(SizeBytes);

    public static MediaInfo Unknown(long sizeBytes) =>
        new(MediaKind.Unknown, "Unknown", sizeBytes, null, false, "No recognised signature in the first bytes of the file.");

    public override string ToString() => Container + " (" + Kind + "), " + SizeText + ", " + DurationText;
}
