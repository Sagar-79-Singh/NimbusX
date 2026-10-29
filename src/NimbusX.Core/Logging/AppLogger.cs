using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NimbusX.Core.Logging;

/// <summary>
/// Thread safe application logger. Writes a daily rolling text file and keeps a small in-memory ring buffer
/// for the Diagnostics page. Every message is passed through <see cref="SecretRedactor"/> first.
/// </summary>
public sealed class AppLogger : IAppLogger, IDisposable
{
    private const long MaxFileBytes = 8L * 1024L * 1024L;
    private const int DefaultMemoryCapacity = 500;

    private readonly object _gate = new();
    private readonly Queue<LogEntry> _recent = new();
    private readonly int _memoryCapacity;
    private readonly LogLevel _minimumLevel;
    private readonly string? _directory;

    private StreamWriter? _writer;
    private DateTime _writerDate;
    private long _writtenBytes;
    private bool _disposed;

    public AppLogger(string? logDirectory, LogLevel minimumLevel = LogLevel.Information, int memoryCapacity = DefaultMemoryCapacity)
    {
        _minimumLevel = minimumLevel;
        _memoryCapacity = Math.Max(16, memoryCapacity);
        _directory = string.IsNullOrWhiteSpace(logDirectory) ? null : logDirectory;

        if (_directory is not null)
        {
            try
            {
                Directory.CreateDirectory(_directory);
            }
            catch (Exception)
            {
                _directory = null;
            }
        }
    }

    public event EventHandler<LogEntry>? EntryWritten;

    public string? LogDirectory => _directory;

    public string? CurrentLogFile
    {
        get
        {
            lock (_gate)
            {
                return _writer?.BaseStream is FileStream fs ? fs.Name : null;
            }
        }
    }

    public void Trace(string message, string category = "App") => Write(LogLevel.Trace, message, category, null);

    public void Debug(string message, string category = "App") => Write(LogLevel.Debug, message, category, null);

    public void Info(string message, string category = "App") => Write(LogLevel.Information, message, category, null);

    public void Warn(string message, string category = "App") => Write(LogLevel.Warning, message, category, null);

    public void Error(string message, string category = "App", Exception? exception = null) =>
        Write(LogLevel.Error, message, category, exception);

    public void Critical(string message, string category = "App", Exception? exception = null) =>
        Write(LogLevel.Critical, message, category, exception);

    public IReadOnlyList<LogEntry> RecentEntries(int count)
    {
        if (count <= 0)
        {
            return Array.Empty<LogEntry>();
        }

        lock (_gate)
        {
            int skip = Math.Max(0, _recent.Count - count);
            List<LogEntry> result = new(Math.Min(count, _recent.Count));
            int index = 0;
            foreach (LogEntry entry in _recent)
            {
                if (index++ >= skip)
                {
                    result.Add(entry);
                }
            }

            return result;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CloseWriter();
        }
    }

    private void Write(LogLevel level, string message, string category, Exception? exception)
    {
        if (level < _minimumLevel)
        {
            return;
        }

        string safeMessage = SecretRedactor.Redact(message);
        string? safeDetail = exception is null ? null : SecretRedactor.Redact(Explain(exception));
        LogEntry entry = new(DateTimeOffset.Now, level, category ?? "App", safeMessage, safeDetail);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _recent.Enqueue(entry);
            while (_recent.Count > _memoryCapacity)
            {
                _recent.Dequeue();
            }

            AppendLine(entry);
        }

        try
        {
            EntryWritten?.Invoke(this, entry);
        }
        catch (Exception)
        {
            // A faulty subscriber must never break logging.
        }
    }

    private void AppendLine(LogEntry entry)
    {
        if (_directory is null)
        {
            return;
        }

        try
        {
            DateTime today = DateTime.Now.Date;
            if (_writer is null || _writerDate != today || _writtenBytes > MaxFileBytes)
            {
                CloseWriter();
                string file = Path.Combine(
                    _directory,
                    "nimbusx-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-" +
                    DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture) + ".log");
                FileStream stream = new(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                _writerDate = today;
                _writtenBytes = stream.Length;
            }

            string line = entry.ToLogLine();
            _writer.WriteLine(line);
            _writtenBytes += line.Length + Environment.NewLine.Length;
        }
        catch (Exception)
        {
            // Logging must never throw into application code.
            CloseWriter();
        }
    }

    private void CloseWriter()
    {
        try
        {
            _writer?.Flush();
            _writer?.Dispose();
        }
        catch (Exception)
        {
            // ignore
        }
        finally
        {
            _writer = null;
        }
    }

    private static string Explain(Exception exception)
    {
        StringBuilder builder = new();
        Exception? current = exception;
        int depth = 0;
        while (current is not null && depth < 6)
        {
            builder.Append(current.GetType().Name).Append(": ").AppendLine(current.Message);
            if (!string.IsNullOrWhiteSpace(current.StackTrace))
            {
                builder.AppendLine(current.StackTrace);
            }

            current = current.InnerException;
            depth++;
        }

        return builder.ToString();
    }
}
