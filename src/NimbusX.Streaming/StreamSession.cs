using System;
using System.Threading;
using NimbusX.Core.Text;

namespace NimbusX.Streaming;

/// <summary>One file being served to a player over the loopback proxy.</summary>
/// <remarks>
/// The URL contains an unguessable token and is bound to 127.0.0.1, so nothing outside this machine can use it.
/// The token is never written to a log line: logs only use <see cref="Id"/>.
/// </remarks>
public sealed class StreamSession
{
    private long _bytesServed;
    private long _requests;
    private long _rangeRequests;
    private long _lastAccessTicks;
    private long _startedTicks;

    public StreamSession(
        string id,
        string accountId,
        string displayName,
        string driveLetter,
        string relativePath,
        string localPath,
        string url,
        MediaInfo media,
        DateTimeOffset openedUtc)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        AccountId = accountId;
        DisplayName = displayName;
        DriveLetter = driveLetter;
        RelativePath = relativePath;
        LocalPath = localPath;
        Url = url;
        Media = media;
        OpenedUtc = openedUtc;
        _lastAccessTicks = openedUtc.UtcTicks;
        _startedTicks = Environment.TickCount64;
    }

    public string Id { get; }

    public string AccountId { get; }

    public string DisplayName { get; }

    public string DriveLetter { get; }

    public string RelativePath { get; }

    /// <summary>Path on the mounted drive (never handed to the UI as a "cloud URL").</summary>
    public string LocalPath { get; }

    /// <summary>Loopback URL for an external player. Contains the session token.</summary>
    public string Url { get; }

    public MediaInfo Media { get; }

    public DateTimeOffset OpenedUtc { get; }

    public bool IsClosed { get; private set; }

    public DateTimeOffset? ClosedUtc { get; private set; }

    public long BytesServed => Interlocked.Read(ref _bytesServed);

    public long Requests => Interlocked.Read(ref _requests);

    public long RangeRequests => Interlocked.Read(ref _rangeRequests);

    public DateTimeOffset LastAccessUtc => new(Interlocked.Read(ref _lastAccessTicks), TimeSpan.Zero);

    /// <summary>Real measured transfer rate since the session opened (0 until data actually moved).</summary>
    public double MeasuredBytesPerSecond
    {
        get
        {
            long elapsedMs = Environment.TickCount64 - Interlocked.Read(ref _startedTicks);
            long bytes = BytesServed;
            return elapsedMs <= 0 || bytes <= 0 ? 0d : bytes * 1000d / elapsedMs;
        }
    }

    public string ThroughputText =>
        MeasuredBytesPerSecond <= 0 ? "Unavailable" : ByteSize.FormatRate(MeasuredBytesPerSecond);

    public string ServedText => ByteSize.Format(BytesServed) + " served";

    public void RecordRequest(bool isRange)
    {
        Interlocked.Increment(ref _requests);
        if (isRange)
        {
            Interlocked.Increment(ref _rangeRequests);
        }

        Interlocked.Exchange(ref _lastAccessTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public void AddBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        Interlocked.Add(ref _bytesServed, bytes);
        Interlocked.Exchange(ref _lastAccessTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public void Close(DateTimeOffset utcNow)
    {
        if (IsClosed)
        {
            return;
        }

        IsClosed = true;
        ClosedUtc = utcNow;
    }

    public override string ToString() =>
        DisplayName + " " + RelativePath + " (" + Media.Container + ", " + ServedText + ")";
}

/// <summary>Result of asking the streaming service to serve a file.</summary>
public sealed class StreamOpenResult
{
    private StreamOpenResult(StreamSession? session, string? error, string? fix)
    {
        Session = session;
        Error = error;
        Fix = fix;
    }

    public StreamSession? Session { get; }

    public string? Error { get; }

    public string? Fix { get; }

    public bool Success => Session is not null;

    public static StreamOpenResult Ok(StreamSession session) => new(session, null, null);

    public static StreamOpenResult Fail(string error, string fix) => new(null, error, fix);
}
