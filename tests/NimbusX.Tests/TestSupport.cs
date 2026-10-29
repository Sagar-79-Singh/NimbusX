using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NimbusX.Core.Config;
using NimbusX.Core.Logging;

namespace NimbusX.Tests;

/// <summary>A folder that deletes itself, so tests never leave state behind.</summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder(string? label = null)
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "nimbusx-tests",
            (label ?? "case") + "-" + Guid.NewGuid().ToString("n").Substring(0, 8));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts)
    {
        string result = Path;
        foreach (string part in parts)
        {
            result = System.IO.Path.Combine(result, part);
        }

        return result;
    }

    public string Write(string relativePath, string contents)
    {
        string full = Combine(relativePath);
        string? folder = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        File.WriteAllText(full, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return full;
    }

    public string WriteBytes(string relativePath, byte[] bytes)
    {
        string full = Combine(relativePath);
        string? folder = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        File.WriteAllBytes(full, bytes);
        return full;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked file in a temp folder is not worth failing a test over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Collects log entries in memory so tests can assert on them.</summary>
public sealed class RecordingLogger : IAppLogger
{
    private readonly List<LogEntry> _entries = new();

    public event EventHandler<LogEntry>? EntryWritten;

    public string? LogDirectory => null;

    public string? CurrentLogFile => null;

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToArray();
            }
        }
    }

    public void Trace(string message, string category = "App") => Record(LogLevel.Trace, message, category);

    public void Debug(string message, string category = "App") => Record(LogLevel.Debug, message, category);

    public void Info(string message, string category = "App") => Record(LogLevel.Information, message, category);

    public void Warn(string message, string category = "App") => Record(LogLevel.Warning, message, category);

    public void Error(string message, string category = "App", Exception? exception = null) =>
        Record(LogLevel.Error, message + (exception is null ? string.Empty : " " + exception.Message), category);

    public void Critical(string message, string category = "App", Exception? exception = null) =>
        Record(LogLevel.Critical, message + (exception is null ? string.Empty : " " + exception.Message), category);

    public IReadOnlyList<LogEntry> RecentEntries(int count)
    {
        IReadOnlyList<LogEntry> all = Entries;
        int start = Math.Max(0, all.Count - Math.Max(0, count));
        LogEntry[] slice = new LogEntry[all.Count - start];
        for (int i = 0; i < slice.Length; i++)
        {
            slice[i] = all[start + i];
        }

        return slice;
    }

    private void Record(LogLevel level, string message, string category)
    {
        // Everything the app logs is redacted; mirror that here so tests see what users would see.
        LogEntry entry = new(DateTimeOffset.UtcNow, level, category, SecretRedactor.Redact(message));
        lock (_entries)
        {
            _entries.Add(entry);
        }

        EntryWritten?.Invoke(this, entry);
    }
}

/// <summary>Builds the smallest possible real PE header so <c>PeInspector</c> can be tested without a compiler.</summary>
public static class PeBuilder
{
    public static byte[] Executable(ushort subsystem = 2, ushort machine = 0x8664, int padding = 0)
    {
        byte[] bytes = new byte[0x400 + padding];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';

        int peOffset = 0x80;
        BitConverter.GetBytes(peOffset).CopyTo(bytes, 0x3C);

        bytes[peOffset] = (byte)'P';
        bytes[peOffset + 1] = (byte)'E';
        bytes[peOffset + 2] = 0;
        bytes[peOffset + 3] = 0;

        BitConverter.GetBytes(machine).CopyTo(bytes, peOffset + 4);

        int optional = peOffset + 24;
        BitConverter.GetBytes((ushort)0x010B).CopyTo(bytes, optional);
        BitConverter.GetBytes(subsystem).CopyTo(bytes, optional + 68);
        return bytes;
    }

    /// <summary>Readable bytes that are long enough to have a DOS header, but do not start with "MZ".</summary>
    public static byte[] NotAnExecutable() =>
        Encoding.ASCII.GetBytes(
            "This file is plain text, not a Windows executable. It is deliberately longer than the 64 byte "
            + "DOS header so the inspector has to reject it on its contents rather than on its size.");

    /// <summary>A file too short to hold a DOS header at all.</summary>
    public static byte[] TooShortForADosHeader() => Encoding.ASCII.GetBytes("MZ");
}

/// <summary>Stands in for the real data folder so settings and paths can be tested in isolation.</summary>
public sealed class TestEnvironment : IDisposable
{
    private readonly TempFolder _folder;

    public TestEnvironment()
    {
        _folder = new TempFolder("env");
        Paths = new AppPaths(_folder.Combine("data"));
    }

    public AppPaths Paths { get; }

    public string Root => _folder.Path;

    public void Dispose() => _folder.Dispose();
}
