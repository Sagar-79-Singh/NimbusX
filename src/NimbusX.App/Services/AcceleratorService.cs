using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NimbusX.Core.Io;
using NimbusX.Core.Logging;
using NimbusX.Core.Text;
using NimbusX.Games;
using NimbusX.Mount;
using NimbusX.Streaming;

namespace NimbusX.App;

/// <summary>One file the accelerator will warm before a launch.</summary>
public sealed class PlannedFile
{
    public PlannedFile(string relativePath, long bytes, string reason)
    {
        RelativePath = relativePath;
        Bytes = Math.Max(0, bytes);
        Reason = reason;
    }

    public string RelativePath { get; }

    public long Bytes { get; }

    /// <summary>Why this file is in the plan (recorded hot file, the executable, ...).</summary>
    public string Reason { get; }

    public override string ToString() => RelativePath + " (" + ByteSize.Format(Bytes) + ", " + Reason + ")";
}

/// <summary>What the accelerator can do for one game right now, and how confident it is.</summary>
public sealed class AcceleratorPlan
{
    public AcceleratorPlan(
        string gameId,
        string gameName,
        IReadOnlyList<PlannedFile> files,
        double? predictedStartupSeconds,
        int sampleCount,
        string explanation)
    {
        GameId = gameId;
        GameName = gameName;
        Files = files ?? Array.Empty<PlannedFile>();
        PredictedStartupSeconds = predictedStartupSeconds;
        SampleCount = sampleCount;
        Explanation = explanation;
    }

    public string GameId { get; }

    public string GameName { get; }

    public IReadOnlyList<PlannedFile> Files { get; }

    public long TotalBytes => Files.Sum(f => f.Bytes);

    public double? PredictedStartupSeconds { get; }

    public int SampleCount { get; }

    /// <summary>Plain-language explanation of what the plan is based on.</summary>
    public string Explanation { get; }

    public string PredictedText =>
        PredictedStartupSeconds is null
            ? "Not enough launches to predict"
            : "About " + PredictedStartupSeconds.Value.ToString("0.0", CultureInfo.InvariantCulture) + " s to the first window (median of " + SampleCount + ")";

    public bool HasWork => Files.Count > 0;
}

/// <summary>Outcome of one prepare run, with measured numbers only.</summary>
public sealed class AcceleratorResult
{
    public AcceleratorResult(
        bool success,
        string gameId,
        long bytesRead,
        TimeSpan elapsed,
        int filesWarmed,
        double? measuredMegabytesPerSecond,
        string? error,
        string? fix)
    {
        Success = success;
        GameId = gameId;
        BytesRead = bytesRead;
        Elapsed = elapsed;
        FilesWarmed = filesWarmed;
        MeasuredMegabytesPerSecond = measuredMegabytesPerSecond;
        Error = error;
        Fix = fix;
    }

    public bool Success { get; }

    public string GameId { get; }

    public long BytesRead { get; }

    public TimeSpan Elapsed { get; }

    public int FilesWarmed { get; }

    /// <summary>Throughput measured by the prefetcher, or null when nothing was downloaded.</summary>
    public double? MeasuredMegabytesPerSecond { get; }

    public string? Error { get; }

    public string? Fix { get; }

    public string Summary =>
        Success
            ? "Warmed " + ByteSize.Format(BytesRead) + " in " + TextFormat.Duration(Elapsed) +
              (MeasuredMegabytesPerSecond is double rate ? " (" + rate.ToString("0.0", CultureInfo.InvariantCulture) + " MB/s measured)" : string.Empty)
            : "Prepare failed: " + Error;

    public static AcceleratorResult Fail(string gameId, string error, string fix) =>
        new(false, gameId, 0, TimeSpan.Zero, 0, null, error, fix);
}

/// <summary>
/// "Prepare / warm start": pre-loads the files a game reads first so its launch hits the local cache.
/// </summary>
/// <remarks>
/// <para>
/// The plan is built only from recorded evidence: the executable the user chose plus hot files NimbusX has actually
/// observed (or pinned). There is no guessing of file names, and the predicted startup time is the median of real
/// verified launches, shown with its sample count and hidden when there are too few launches to be meaningful.
/// </para>
/// <para>
/// The work itself is a normal read-ahead through the rclone mount, so the VFS cache stays the single source of
/// truth and the disk budget still applies.
/// </para>
/// </remarks>
public sealed class AcceleratorService
{
    private readonly GameStore _games;
    private readonly PrefetchEngine _prefetch;
    private readonly ProfileDatabase _profiles;
    private readonly IMountManager _mounts;
    private readonly IAppLogger _logger;

    public AcceleratorService(
        GameStore games,
        PrefetchEngine prefetch,
        ProfileDatabase profiles,
        IMountManager mounts,
        IAppLogger? logger = null)
    {
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _prefetch = prefetch ?? throw new ArgumentNullException(nameof(prefetch));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _mounts = mounts ?? throw new ArgumentNullException(nameof(mounts));
        _logger = logger ?? NullLogger.Instance;
    }

    public AcceleratorPlan BuildPlan(string gameId)
    {
        GameEntry? game = _games.Find(gameId)
            ?? throw new InvalidOperationException("That game is not in the library.");

        double? median = SafeMedian(gameId);
        int samples = _profiles.RecentLaunches(gameId, 50).Count(r => r.Verified);

        MountSnapshot? snapshot = _mounts.GetSnapshot(game.AccountId);
        if (snapshot is null)
        {
            return new AcceleratorPlan(
                game.Id,
                game.Name,
                Array.Empty<PlannedFile>(),
                median,
                samples,
                "Mount " + game.Name + "'s drive first; the accelerator reads through the mounted drive.");
        }

        List<PlannedFile> files = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        void Add(string relativePath, string reason)
        {
            string normalized = relativePath.Replace('\\', '/').Trim('/');
            if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
            {
                return;
            }

            long bytes = 0;
            try
            {
                string local = PathGuard.CombineUnder(snapshot.DriveRoot, normalized);
                FileInfo info = new(local);
                if (info.Exists)
                {
                    bytes = info.Length;
                    files.Add(new PlannedFile(normalized, bytes, reason));
                }
            }
            catch (Exception exception)
            {
                _logger.Debug("Accelerator could not measure '" + normalized + "': " + exception.Message, "Accelerator");
            }
        }

        foreach (string hot in game.HotFiles)
        {
            Add(hot, "recorded as read during a launch");
        }

        if (!string.IsNullOrWhiteSpace(game.ExecutableRelativePath))
        {
            Add(game.ExecutableRelativePath, "the executable itself");
        }

        string explanation;
        if (files.Count == 0)
        {
            explanation = "Nothing measured yet: launch the game once through NimbusX so the files it reads can be recorded.";
        }
        else if (game.HotFiles.Count == 0)
        {
            explanation = "Only the executable is known so far. After a launch NimbusX records the files the game actually reads and warms those next time.";
        }
        else
        {
            explanation = "Warms the executable and " + game.HotFiles.Count + " recorded hot file(s).";
        }

        return new AcceleratorPlan(game.Id, game.Name, files, median, samples, explanation);
    }

    /// <summary>Reads the planned files ahead of time and reports what it actually transferred.</summary>
    public async Task<AcceleratorResult> PrepareAsync(
        string gameId,
        IProgress<PrefetchJob>? progress,
        CancellationToken cancellationToken)
    {
        AcceleratorPlan plan = BuildPlan(gameId);
        if (!plan.HasWork)
        {
            return AcceleratorResult.Fail(
                gameId,
                plan.Explanation,
                "Launch the game once, then prepare it again.");
        }

        GameEntry? game = _games.Find(gameId);
        if (game is null)
        {
            return AcceleratorResult.Fail(gameId, "That game is not in the library.", "Add it again from the Game Library.");
        }

        long bytesBefore = 0;
        int warmed = 0;
        DateTimeOffset started = DateTimeOffset.UtcNow;

        foreach (PlannedFile file in plan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            PrefetchJob? job = _prefetch.Enqueue(game.AccountId, file.RelativePath, targetBytes: 0, priority: 10);
            if (job is null)
            {
                continue;
            }

            progress?.Report(job);

            // Wait for this job to finish so the report is about real bytes, not an estimate.
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                PrefetchJob current = _prefetch.Jobs.FirstOrDefault(j => j.Id == job.Id) ?? job;
                if (current.State is PrefetchState.Completed or PrefetchState.Cancelled or PrefetchState.Failed)
                {
                    bytesBefore += current.BytesRead;
                    if (current.State == PrefetchState.Completed)
                    {
                        warmed++;
                    }
                    else if (!string.IsNullOrWhiteSpace(current.Error))
                    {
                        _logger.Warn("Accelerator could not warm '" + file.RelativePath + "': " + current.Error, "Accelerator");
                    }

                    break;
                }
            }
        }

        TimeSpan elapsed = DateTimeOffset.UtcNow - started;
        double? rate = elapsed.TotalSeconds > 0 && bytesBefore > 0
            ? bytesBefore / elapsed.TotalSeconds / (1024d * 1024d)
            : null;

        _logger.Info(
            "Prepared '" + plan.GameName + "': " + ByteSize.Format(bytesBefore) + " in " + TextFormat.Duration(elapsed) + ".",
            "Accelerator");

        return new AcceleratorResult(true, gameId, bytesBefore, elapsed, warmed, rate, null, null);
    }

    private double? SafeMedian(string gameId)
    {
        try
        {
            return _profiles.MedianStartupSeconds(gameId, minimumSamples: 3);
        }
        catch (Exception exception)
        {
            _logger.Debug("Could not read launch history: " + exception.Message, "Accelerator");
            return null;
        }
    }
}
