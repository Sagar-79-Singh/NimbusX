using System;
using System.Collections.Generic;

namespace NimbusX.Core.Logging;

/// <summary>Logger that discards everything. Used by unit tests and by CLI verbs that must stay quiet.</summary>
public sealed class NullLogger : IAppLogger
{
    public static readonly NullLogger Instance = new();

    private NullLogger()
    {
    }

    public event EventHandler<LogEntry>? EntryWritten
    {
        add { }
        remove { }
    }

    public string? LogDirectory => null;

    public string? CurrentLogFile => null;

    public void Trace(string message, string category = "App")
    {
    }

    public void Debug(string message, string category = "App")
    {
    }

    public void Info(string message, string category = "App")
    {
    }

    public void Warn(string message, string category = "App")
    {
    }

    public void Error(string message, string category = "App", Exception? exception = null)
    {
    }

    public void Critical(string message, string category = "App", Exception? exception = null)
    {
    }

    public IReadOnlyList<LogEntry> RecentEntries(int count) => Array.Empty<LogEntry>();
}
