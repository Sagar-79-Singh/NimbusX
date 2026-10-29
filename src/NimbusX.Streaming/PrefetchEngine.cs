using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NimbusX.Core;
using NimbusX.Core.Io;
using NimbusX.Core.Logging;
using NimbusX.Core.Security;
using NimbusX.Mount;

namespace NimbusX.Streaming;

/// <summary>Warms the VFS cache by reading files ahead of time, within a strict byte budget.</summary>
public interface IPrefetchEngine
{
    event EventHandler<PrefetchJob>? JobChanged;

    bool CanPrefetch { get; }

    bool IsPaused { get; }

    /// <summary>Bytes this run may still read.</summary>
    long RemainingBudgetBytes { get; }

    IReadOnlyList<PrefetchJob> Jobs { get; }

    PrefetchJob? Enqueue(string accountId, string relativePath, long targetBytes, int priority = 0);

    bool Cancel(string jobId);

    void CancelAll();

    void Pause();

    void Resume();

    /// <summary>Starts a new budget window (for example at app start or when the user presses "Prefetch now").</summary>
    void ResetBudget(long budgetBytes);
}

/// <summary>
/// Reads ahead through the rclone mount so the data a game or video needs next is already in the VFS cache.
/// </summary>
/// <remarks>
/// <para>
/// There is no separate download path: a read through the mounted drive is exactly what makes rclone fetch and
/// cache the data, which keeps one source of truth for the cache and the disk budget.
/// </para>
/// <para>
/// The engine stops when the session budget is used up or the cache manager reports disk pressure, and it reads in
/// 4 MiB chunks with a short pause between them so a running game never waits behind the prefetcher.
/// </para>
/// </remarks>
public sealed class PrefetchEngine : IPrefetchEngine, IDisposable
{
    private readonly IMountManager _mounts;
    private readonly IAppLogger _logger;
    private readonly Func<bool> _diskAllowsPrefetch;
    private readonly PrefetchOptions _options;
    private readonly object _gate = new();
    private readonly List<PrefetchJob> _jobs = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private Task? _worker;
    private long _sessionBudgetBytes;
    private long _consumedBytes;
    private bool _paused;
    private bool _disposed;

    public PrefetchEngine(
        IMountManager mounts,
        IAppLogger? logger = null,
        Func<bool>? diskAllowsPrefetch = null,
        PrefetchOptions? options = null,
        long sessionBudgetBytes = 2L * 1024 * 1024 * 1024)
    {
        _mounts = mounts ?? throw new ArgumentNullException(nameof(mounts));
        _logger = logger ?? NullLogger.Instance;
        _diskAllowsPrefetch = diskAllowsPrefetch ?? (() => true);
        _options = options ?? new PrefetchOptions();
        _sessionBudgetBytes = Math.Max(0, sessionBudgetBytes);
    }

    public event EventHandler<PrefetchJob>? JobChanged;

    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _paused;
            }
        }
    }

    public bool CanPrefetch => !IsPaused && RemainingBudgetBytes > 0 && _diskAllowsPrefetch();

    public long RemainingBudgetBytes => Math.Max(0, Interlocked.Read(ref _sessionBudgetBytes) - Interlocked.Read(ref _consumedBytes));

    public IReadOnlyList<PrefetchJob> Jobs
    {
        get
        {
            lock (_gate)
            {
                return _jobs.ToArray();
            }
        }
    }

    public PrefetchJob? Enqueue(string accountId, string relativePath, long targetBytes, int priority = 0)
    {
        Guard.NotNullOrWhiteSpace(accountId, nameof(accountId));

        MountSnapshot? snapshot = _mounts.GetSnapshot(accountId);
        if (snapshot is null)
        {
            _logger.Warn("Prefetch skipped: the drive for account " + accountId + " is not mounted.", "Prefetch");
            return null;
        }

        if (snapshot.Phase is not (MountPhase.Ready or MountPhase.Degraded))
        {
            _logger.Warn("Prefetch skipped: " + snapshot.DisplayName + " is " + snapshot.Phase + ".", "Prefetch");
            return null;
        }

        string normalized = string.IsNullOrWhiteSpace(relativePath)
            ? string.Empty
            : relativePath.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        string localPath;
        try
        {
            localPath = PathGuard.CombineUnder(snapshot.DriveRoot, normalized);
        }
        catch (Exception exception)
        {
            _logger.Warn("Prefetch skipped: '" + normalized + "' is not inside " + snapshot.DriveLetter + ": (" + exception.Message + ").", "Prefetch");
            return null;
        }

        if (!File.Exists(localPath))
        {
            _logger.Warn("Prefetch skipped: '" + normalized + "' is not present on " + snapshot.DriveLetter + ":.", "Prefetch");
            return null;
        }

        PrefetchJob job = new(
            RandomSecrets.Hex(5),
            snapshot.AccountId,
            snapshot.DisplayName,
            normalized,
            localPath,
            targetBytes,
            priority);

        lock (_gate)
        {
            _jobs.Add(job);
        }

        EnsureWorker();
        _signal.Release();
        Raise(job);
        return job;
    }

    public bool Cancel(string jobId)
    {
        PrefetchJob? job;
        lock (_gate)
        {
            job = _jobs.FirstOrDefault(j => string.Equals(j.Id, jobId, StringComparison.Ordinal));
        }

        if (job is null)
        {
            return false;
        }

        if (job.State is PrefetchState.Completed or PrefetchState.Cancelled or PrefetchState.Failed)
        {
            return false;
        }

        job.Finish(PrefetchState.Cancelled, "Cancelled by the user.");
        Raise(job);
        return true;
    }

    public void CancelAll()
    {
        foreach (PrefetchJob job in Jobs)
        {
            if (job.State is PrefetchState.Queued or PrefetchState.Running or PrefetchState.Paused)
            {
                job.Finish(PrefetchState.Cancelled, "Cancelled by the user.");
                Raise(job);
            }
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            _paused = true;
        }

        foreach (PrefetchJob job in Jobs)
        {
            job.MarkPaused();
            Raise(job);
        }

        _logger.Info("Prefetch paused.", "Prefetch");
    }

    public void Resume()
    {
        lock (_gate)
        {
            _paused = false;
        }

        foreach (PrefetchJob job in Jobs)
        {
            job.MarkResumed();
            Raise(job);
        }

        _signal.Release();
        _logger.Info("Prefetch resumed with " + Core.Text.ByteSize.Format(RemainingBudgetBytes) + " of budget left.", "Prefetch");
    }

    public void ResetBudget(long budgetBytes)
    {
        Interlocked.Exchange(ref _sessionBudgetBytes, Math.Max(0, budgetBytes));
        Interlocked.Exchange(ref _consumedBytes, 0);
        _logger.Info("Prefetch budget reset to " + Core.Text.ByteSize.Format(_sessionBudgetBytes) + ".", "Prefetch");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _signal.Release();
        try
        {
            _worker?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // shutting down
        }

        _cts.Dispose();
        _signal.Dispose();
    }

    private void EnsureWorker()
    {
        if (_worker is not null)
        {
            return;
        }

        lock (_gate)
        {
            _worker ??= Task.Run(() => WorkerLoopAsync(_cts.Token));
        }
    }

    private async Task WorkerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            PrefetchJob? job = NextQueued();
            if (job is null)
            {
                try
                {
                    await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            if (IsPaused || !CanPrefetch)
            {
                job.MarkPaused();
                Raise(job);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            await RunJobAsync(job, cancellationToken).ConfigureAwait(false);
        }
    }

    private PrefetchJob? NextQueued()
    {
        lock (_gate)
        {
            return _jobs
                .Where(j => j.State == PrefetchState.Queued)
                .OrderByDescending(j => j.Priority)
                .ThenBy(j => j.QueuedUtc)
                .FirstOrDefault();
        }
    }

    private async Task RunJobAsync(PrefetchJob job, CancellationToken cancellationToken)
    {
        job.MarkRunning();
        Raise(job);

        long fileLength;
        try
        {
            fileLength = new FileInfo(job.LocalPath).Length;
        }
        catch (Exception exception)
        {
            job.Finish(PrefetchState.Failed, null, "Could not read the file size: " + exception.Message);
            Raise(job);
            return;
        }

        long limit = job.TargetBytes > 0 ? Math.Min(job.TargetBytes, fileLength) : fileLength;
        byte[] buffer = new byte[PrefetchOptions.ChunkBytes];
        DateTimeOffset deadline = DateTimeOffset.UtcNow + _options.MaxJobDuration;

        try
        {
            using FileStream stream = new(
                job.LocalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                PrefetchOptions.ChunkBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            long position = 0;
            while (position < limit)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    job.Finish(PrefetchState.Cancelled, "NimbusX is shutting down.");
                    Raise(job);
                    return;
                }

                if (IsPaused)
                {
                    job.MarkPaused();
                    Raise(job);
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                    job.MarkResumed();
                    Raise(job);
                    continue;
                }

                if (!_diskAllowsPrefetch())
                {
                    job.Finish(PrefetchState.Cancelled, "Paused because the cache disk is under pressure.");
                    Raise(job);
                    _logger.Warn("Prefetch of '" + job.RelativePath + "' stopped: the cache disk is under pressure.", "Prefetch");
                    return;
                }

                if (RemainingBudgetBytes <= 0)
                {
                    job.Finish(PrefetchState.Cancelled, "The prefetch session budget of " + Core.Text.ByteSize.Format(_sessionBudgetBytes) + " was used up.");
                    Raise(job);
                    return;
                }

                if (DateTimeOffset.UtcNow > deadline)
                {
                    job.Finish(PrefetchState.Cancelled, "The job reached its " + _options.MaxJobDuration.TotalMinutes.ToString("0") + " minute limit.");
                    Raise(job);
                    return;
                }

                int want = (int)Math.Min(buffer.Length, Math.Min(limit - position, RemainingBudgetBytes));
                int read = await stream.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                position += read;
                Interlocked.Add(ref _consumedBytes, read);
                job.AddBytes(read);
                Raise(job);

                if (_options.ChunkPause > TimeSpan.Zero)
                {
                    await Task.Delay(_options.ChunkPause, cancellationToken).ConfigureAwait(false);
                }
            }

            bool complete = position >= limit;
            job.Finish(
                complete ? PrefetchState.Completed : PrefetchState.Cancelled,
                complete ? null : "The file ended before the requested range.");
            Raise(job);

            if (complete)
            {
                _logger.Info(
                    "Prefetched " + job.ProgressText + " of '" + job.RelativePath + "' at " + job.RateText + ".",
                    "Prefetch");
            }
        }
        catch (OperationCanceledException)
        {
            job.Finish(PrefetchState.Cancelled, "NimbusX is shutting down.");
            Raise(job);
        }
        catch (Exception exception)
        {
            job.Finish(PrefetchState.Failed, null, exception.Message);
            Raise(job);
            _logger.Warn("Prefetch of '" + job.RelativePath + "' failed: " + exception.Message, "Prefetch");
        }
        finally
        {
            TrimHistory();
        }
    }

    private void TrimHistory()
    {
        lock (_gate)
        {
            List<PrefetchJob> finished = _jobs
                .Where(j => j.State is PrefetchState.Completed or PrefetchState.Cancelled or PrefetchState.Failed)
                .OrderByDescending(j => j.FinishedUtc)
                .ToList();

            foreach (PrefetchJob stale in finished.Skip(_options.FinishedHistory))
            {
                _jobs.Remove(stale);
            }
        }
    }

    private void Raise(PrefetchJob job)
    {
        try
        {
            JobChanged?.Invoke(this, job);
        }
        catch (Exception exception)
        {
            _logger.Error("A prefetch subscriber threw.", "Prefetch", exception);
        }
    }
}
