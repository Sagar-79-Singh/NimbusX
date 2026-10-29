using System;
using System.Threading;
using NimbusX.Core.Text;

namespace NimbusX.Streaming;

/// <summary>Lifecycle of one prefetch job.</summary>
public enum PrefetchState
{
    Queued = 0,
    Running,
    Paused,
    Completed,
    Cancelled,
    Failed
}

/// <summary>
/// One read-ahead run over a file on a mounted drive.
/// </summary>
/// <remarks>
/// Progress is only ever the number of bytes the reader actually consumed. A job that stopped early because the
/// session budget ran out is reported as <see cref="PrefetchState.Cancelled"/> with a reason, never as complete.
/// </remarks>
public sealed class PrefetchJob
{
    private long _bytesRead;
    private long _startedTicks;

    public PrefetchJob(string id, string accountId, string displayName, string relativePath, string localPath, long targetBytes, int priority)
    {
        Id = id;
        AccountId = accountId;
        DisplayName = displayName;
        RelativePath = relativePath;
        LocalPath = localPath;
        TargetBytes = Math.Max(0, targetBytes);
        Priority = priority;
        QueuedUtc = DateTimeOffset.UtcNow;
    }

    public string Id { get; }

    public string AccountId { get; }

    public string DisplayName { get; }

    public string RelativePath { get; }

    public string LocalPath { get; }

    /// <summary>Bytes the job will read, or 0 when the whole file should be warmed.</summary>
    public long TargetBytes { get; }

    public int Priority { get; }

    public DateTimeOffset QueuedUtc { get; }

    public DateTimeOffset? StartedUtc { get; private set; }

    public DateTimeOffset? FinishedUtc { get; private set; }

    public PrefetchState State { get; private set; } = PrefetchState.Queued;

    public long BytesRead => Interlocked.Read(ref _bytesRead);

    public string? Error { get; private set; }

    public string? StopReason { get; private set; }

    public double MeasuredBytesPerSecond
    {
        get
        {
            long started = Interlocked.Read(ref _startedTicks);
            if (started <= 0)
            {
                return 0d;
            }

            long elapsedMs = Environment.TickCount64 - started;
            long bytes = BytesRead;
            return elapsedMs <= 0 || bytes <= 0 ? 0d : bytes * 1000d / elapsedMs;
        }
    }

    public string ProgressText =>
        TargetBytes > 0
            ? ByteSize.Format(BytesRead) + " of " + ByteSize.Format(TargetBytes)
            : ByteSize.Format(BytesRead);

    public string RateText =>
        MeasuredBytesPerSecond <= 0 ? "Unavailable" : ByteSize.FormatRate(MeasuredBytesPerSecond);

    internal void MarkRunning()
    {
        State = PrefetchState.Running;
        StartedUtc = DateTimeOffset.UtcNow;
        Interlocked.Exchange(ref _startedTicks, Environment.TickCount64);
    }

    internal void AddBytes(int bytes) => Interlocked.Add(ref _bytesRead, bytes);

    internal void MarkPaused()
    {
        if (State == PrefetchState.Running)
        {
            State = PrefetchState.Paused;
        }
    }

    internal void MarkResumed()
    {
        if (State == PrefetchState.Paused)
        {
            State = PrefetchState.Running;
        }
    }

    internal void Finish(PrefetchState state, string? reason = null, string? error = null)
    {
        State = state;
        FinishedUtc = DateTimeOffset.UtcNow;
        StopReason = reason;
        Error = error;
    }

    public override string ToString() => State + ": " + RelativePath + " (" + ProgressText + ")";
}

/// <summary>Options that bound a prefetch run.</summary>
public sealed class PrefetchOptions
{
    /// <summary>Bytes read per request. Matches rclone's <c>--vfs-read-chunk-size 4M</c> so one read fills one chunk.</summary>
    public const int ChunkBytes = 4 * 1024 * 1024;

    /// <summary>How long one job may hold the worker before the next job runs.</summary>
    public TimeSpan MaxJobDuration { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Pause between chunks so the prefetcher never starves the game's own reads.</summary>
    public TimeSpan ChunkPause { get; set; } = TimeSpan.FromMilliseconds(25);

    /// <summary>Maximum jobs kept in the finished list.</summary>
    public int FinishedHistory { get; set; } = 20;
}
