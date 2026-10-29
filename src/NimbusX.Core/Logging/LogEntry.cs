using System;

namespace NimbusX.Core.Logging;

/// <summary>
/// One application log record. <see cref="Message"/> is already redacted by the logger that produced it.
/// </summary>
public sealed class LogEntry
{
    public LogEntry(DateTimeOffset timestamp, LogLevel level, string category, string message, string? detail = null)
    {
        Timestamp = timestamp;
        Level = level;
        Category = category ?? string.Empty;
        Message = message ?? string.Empty;
        Detail = detail;
    }

    public DateTimeOffset Timestamp { get; }

    public LogLevel Level { get; }

    public string Category { get; }

    public string Message { get; }

    /// <summary>Optional multi-line detail (typically an exception stack trace), redacted.</summary>
    public string? Detail { get; }

    public string ToLogLine()
    {
        string line = $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level,-11}] {Category}: {Message}";
        return Detail is null ? line : line + Environment.NewLine + Detail;
    }

    public override string ToString() => $"{Timestamp:HH:mm:ss} {Level}: {Message}";
}
