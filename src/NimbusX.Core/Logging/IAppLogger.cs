using System;
using System.Collections.Generic;

namespace NimbusX.Core.Logging;

/// <summary>
/// Application logger. Implementations must redact secrets before persisting or displaying anything.
/// </summary>
public interface IAppLogger
{
    event EventHandler<LogEntry>? EntryWritten;

    void Trace(string message, string category = "App");

    void Debug(string message, string category = "App");

    void Info(string message, string category = "App");

    void Warn(string message, string category = "App");

    void Error(string message, string category = "App", Exception? exception = null);

    void Critical(string message, string category = "App", Exception? exception = null);

    /// <summary>Most recent entries, newest last.</summary>
    IReadOnlyList<LogEntry> RecentEntries(int count);

    /// <summary>Folder that holds the rolling log files, or null when logging to memory only.</summary>
    string? LogDirectory { get; }

    /// <summary>Path of the current rolling log file, or null.</summary>
    string? CurrentLogFile { get; }
}
