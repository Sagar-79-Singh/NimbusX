using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NimbusX.Core;
using NimbusX.Core.Logging;

namespace NimbusX.Mount;

/// <summary>
/// Follows an rclone <c>-vv</c> log file and raises one event per complete line.
/// </summary>
/// <remarks>
/// The prediction engine needs every VFS read line, but rclone writes the log asynchronously, so a tailer is
/// used instead of stdout (rclone's own log file is more complete and survives a restart). The tailer copes with
/// the file not existing yet, with rotation (file replaced) and with truncation (length shrinks).
/// </remarks>
public sealed class RcloneLogTailer : IDisposable
{
    private readonly string _path;
    private readonly IAppLogger _logger;
    private readonly object _gate = new();
    private readonly List<byte> _pending = new();
    private readonly byte[] _buffer = new byte[64 * 1024];

    private FileStream? _stream;
    private long _position;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private bool _disposed;

    public RcloneLogTailer(string logFilePath, IAppLogger? logger = null)
    {
        _path = Guard.NotNullOrWhiteSpace(logFilePath, nameof(logFilePath));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Raised for every complete log line (already redacted).</summary>
    public event EventHandler<string>? LineReceived;

    public string LogFilePath => _path;

    /// <summary>Number of lines read since the tailer started.</summary>
    public long LinesRead { get; private set; }

    /// <summary>Timestamp of the last line read.</summary>
    public DateTimeOffset? LastLineUtc { get; private set; }

    /// <summary>True while the polling loop runs.</summary>
    public bool IsRunning => _loopTask is { IsCompleted: false };

    /// <summary>Starts the background polling loop.</summary>
    public void Start(TimeSpan interval, CancellationToken cancellationToken)
    {
        if (_loopTask is not null)
        {
            return;
        }

        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = Task.Run(() => LoopAsync(interval, _loopCts.Token), CancellationToken.None);
    }

    /// <summary>Stops the polling loop.</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? cts = _loopCts;
        Task? task = _loopTask;
        _loopCts = null;
        _loopTask = null;
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (Exception)
        {
            // ignore
        }

        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }

        cts.Dispose();
    }

    private async Task LoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Poll();
            }
            catch (Exception exception)
            {
                _logger.Debug("Log tailer error: " + exception.Message, "Mount");
            }

            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Reads everything appended since the last poll and raises <see cref="LineReceived"/> per line.
    /// Returns the number of lines raised. Public so tests can drive it deterministically.
    /// </summary>
    public int Poll()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return 0;
            }

            if (_stream is null)
            {
                if (!File.Exists(_path))
                {
                    return 0;
                }

                _stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                _position = 0;
            }

            if (_stream.Length < _position)
            {
                // Truncated or recreated: start over from the beginning of the new content.
                _position = 0;
                _pending.Clear();
                _stream.Seek(0, SeekOrigin.Begin);
            }

            _stream.Seek(_position, SeekOrigin.Begin);
            int lines = 0;
            int read;
            while ((read = _stream.Read(_buffer, 0, _buffer.Length)) > 0)
            {
                _position += read;
                for (int i = 0; i < read; i++)
                {
                    byte b = _buffer[i];
                    if (b == (byte)'\n')
                    {
                        string line = Encoding.UTF8.GetString(_pending.ToArray()).TrimEnd('\r');
                        _pending.Clear();
                        if (line.Length > 0)
                        {
                            lines++;
                            LinesRead++;
                            LastLineUtc = DateTimeOffset.UtcNow;
                            Raise(line);
                        }

                        continue;
                    }

                    _pending.Add(b);
                    if (_pending.Count > 1024 * 1024)
                    {
                        // A single absurdly long line: drop it rather than growing without bound.
                        _pending.Clear();
                    }
                }
            }

            return lines;
        }
    }

    /// <summary>One parsed log line.</summary>
    public sealed class LineEventArgs : EventArgs
    {
        public LineEventArgs(string line)
        {
            Line = line;
        }

        public string Line { get; }
    }

    private void Raise(string line)
    {
        EventHandler<string>? handler = LineReceived;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, SecretRedactor.Redact(line));
        }
        catch (Exception)
        {
            // A subscriber must never break log following.
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
            try
            {
                _stream?.Dispose();
            }
            catch (Exception)
            {
                // ignore
            }
            finally
            {
                _stream = null;
            }

            _pending.Clear();
        }
    }
}
