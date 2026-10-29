using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NimbusX.Core;
using NimbusX.Core.Io;
using NimbusX.Core.Logging;
using NimbusX.Mount;
using NimbusX.Streaming;

namespace NimbusX.Games;

/// <summary>What to launch.</summary>
public sealed class LaunchRequest
{
    public string GameId { get; set; } = string.Empty;

    public string AccountId { get; set; } = string.Empty;

    /// <summary>Folder inside the mounted drive (forward slashes).</summary>
    public string RelativeFolder { get; set; } = string.Empty;

    /// <summary>Executable inside the mounted drive (forward slashes).</summary>
    public string ExecutableRelativePath { get; set; } = string.Empty;

    public string Arguments { get; set; } = string.Empty;

    /// <summary>How long the game must stay up before NimbusX marks the launch verified.</summary>
    public int VerifySeconds { get; set; } = 60;

    /// <summary>How long to wait for the game window before continuing with verification anyway.</summary>
    public int WindowWaitSeconds { get; set; } = 90;
}

/// <summary>Result of one launch attempt, with only measured facts.</summary>
public sealed class LaunchResult
{
    private LaunchResult(
        bool success,
        string gameId,
        int? processId,
        double? startupSeconds,
        bool windowAppeared,
        bool verified,
        int? exitCode,
        string? error,
        string? fix,
        long? cacheBytesDuringLaunch,
        IReadOnlyList<string> logTail)
    {
        Success = success;
        GameId = gameId;
        ProcessId = processId;
        StartupSeconds = startupSeconds;
        WindowAppeared = windowAppeared;
        Verified = verified;
        ExitCode = exitCode;
        Error = error;
        Fix = fix;
        CacheBytesDuringLaunch = cacheBytesDuringLaunch;
        LogTail = logTail ?? Array.Empty<string>();
    }

    public bool Success { get; }

    public string GameId { get; }

    public int? ProcessId { get; }

    public double? StartupSeconds { get; }

    public bool WindowAppeared { get; }

    /// <summary>True only when the game stayed up for the whole verification window.</summary>
    public bool Verified { get; }

    public int? ExitCode { get; }

    public string? Error { get; }

    public string? Fix { get; }

    /// <summary>Bytes rclone reported as fetched during the launch, or null when rclone did not answer.</summary>
    public long? CacheBytesDuringLaunch { get; }

    public IReadOnlyList<string> LogTail { get; }

    public string StartupText =>
        StartupSeconds is null
            ? "Unavailable"
            : StartupSeconds.Value.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    public static LaunchResult Fail(string gameId, string error, string fix, IReadOnlyList<string>? logTail = null) =>
        new(false, gameId, null, null, false, false, null, error, fix, null, logTail ?? Array.Empty<string>());

    /// <summary>The outcome of a launch that NimbusX started and watched to the end of the verification window.</summary>
    public static LaunchResult Completed(
        string gameId,
        int? processId,
        double? startupSeconds,
        bool windowAppeared,
        bool verified,
        int? exitCode,
        string? error,
        string? fix,
        long? cacheBytesDuringLaunch,
        IReadOnlyList<string> logTail) =>
        new(
            verified,
            gameId,
            processId,
            startupSeconds,
            windowAppeared,
            verified,
            exitCode,
            error,
            fix,
            cacheBytesDuringLaunch,
            logTail ?? Array.Empty<string>());
}

/// <summary>A game process NimbusX is currently watching.</summary>
public sealed class RunningGame
{
    public RunningGame(string gameId, string name, string accountId, Process process, DateTimeOffset startedUtc)
    {
        GameId = gameId;
        Name = name;
        AccountId = accountId;
        Process = process;
        StartedUtc = startedUtc;
    }

    public string GameId { get; }

    public string Name { get; }

    public string AccountId { get; }

    public Process Process { get; }

    public DateTimeOffset StartedUtc { get; }

    public int ProcessId => Process.Id;

    public TimeSpan Uptime => DateTimeOffset.UtcNow - StartedUtc;

    /// <summary>Exit code once the process has exited (null while it is still running).</summary>
    public int? ExitCode { get; internal set; }

    public DateTimeOffset? ExitedUtc { get; internal set; }

    public bool IsAlive
    {
        get
        {
            try
            {
                return !Process.HasExited;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}

/// <summary>
/// Launches a game from a mounted cloud drive, measures how long it takes to get to a window, verifies that it
/// stays up, and pauses prefetching while it runs.
/// </summary>
/// <remarks>
/// NimbusX never injects code into a game, never reads its memory and never touches anti-cheat. It starts the
/// executable the user chose and observes only what Windows exposes about that process (start, main window handle,
/// exit code) plus the rclone log of the drive it runs from.
/// </remarks>
public sealed class GameLauncher : IDisposable
{
    private const int WindowPollMilliseconds = 100;

    private readonly IMountManager _mounts;
    private readonly IPrefetchEngine _prefetch;
    private readonly IAppLogger _logger;
    private readonly List<RunningGame> _running = new();
    private readonly object _gate = new();
    private bool _disposed;

    public GameLauncher(IMountManager mounts, IPrefetchEngine prefetch, IAppLogger? logger = null)
    {
        _mounts = mounts ?? throw new ArgumentNullException(nameof(mounts));
        _prefetch = prefetch ?? throw new ArgumentNullException(nameof(prefetch));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Raised when a game process starts and NimbusX begins watching it.</summary>
    public event EventHandler<RunningGame>? GameStarted;

    /// <summary>Raised when a watched game exits.</summary>
    public event EventHandler<RunningGame>? GameExited;

    public IReadOnlyList<RunningGame> Running
    {
        get
        {
            lock (_gate)
            {
                _running.RemoveAll(g => !g.IsAlive);
                return _running.ToArray();
            }
        }
    }

    public bool AnyGameRunning => Running.Count > 0;

    public async Task<LaunchResult> LaunchAsync(LaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        MountSnapshot? snapshot = _mounts.GetSnapshot(request.AccountId);
        if (snapshot is null)
        {
            return LaunchResult.Fail(
                request.GameId,
                "The cloud drive for this game is not mounted.",
                "Mount the drive from the Dashboard and press Play again.");
        }

        if (snapshot.Phase is not (MountPhase.Ready or MountPhase.Degraded))
        {
            return LaunchResult.Fail(
                request.GameId,
                "The drive is " + snapshot.Phase + ", so the game cannot start from it.",
                "Wait for the drive to show Ready, or press Repair on the dashboard, then try again.");
        }

        string executable;
        try
        {
            executable = PathGuard.CombineUnder(snapshot.DriveRoot, request.ExecutableRelativePath);
        }
        catch (Exception exception)
        {
            return LaunchResult.Fail(
                request.GameId,
                "The executable path is not valid: " + exception.Message,
                "Pick the executable again from the mounted drive.");
        }

        if (!PathGuard.IsUnder(executable, snapshot.DriveRoot))
        {
            return LaunchResult.Fail(
                request.GameId,
                "The executable is outside " + snapshot.DriveLetter + ":.",
                "Pick the executable again from the mounted drive; paths that escape the mount are refused.");
        }

        if (!File.Exists(executable))
        {
            return LaunchResult.Fail(
                request.GameId,
                "The executable was not found: " + request.ExecutableRelativePath,
                "Refresh the drive in Explorer; the file may have moved in the cloud.");
        }

        string workingDirectory = Path.GetDirectoryName(executable) ?? snapshot.DriveRoot;
        long? cacheBefore = await ReadCacheBytesAsync(request.AccountId, cancellationToken).ConfigureAwait(false);

        // Game-aware mode: prefetch must not compete with the game's first reads.
        bool wasPaused = _prefetch.IsPaused;
        if (!wasPaused)
        {
            _prefetch.Pause();
        }

        Process process;
        try
        {
            ProcessStartInfo startInfo = new(executable)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                Arguments = request.Arguments ?? string.Empty
            };

            Process? started = Process.Start(startInfo);
            if (started is null)
            {
                throw new InvalidOperationException("Windows did not return a process handle.");
            }

            process = started;
        }
        catch (Exception exception)
        {
            if (!wasPaused)
            {
                _prefetch.Resume();
            }

            _logger.Warn("Could not start '" + request.ExecutableRelativePath + "': " + exception.Message, "Games");
            return LaunchResult.Fail(
                request.GameId,
                "Windows could not start the game: " + exception.Message,
                "Check that the file is a real executable and that the drive is still mounted, then try again.");
        }

        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        RunningGame running = new(request.GameId, Path.GetFileNameWithoutExtension(executable), request.AccountId, process, startedUtc);
        lock (_gate)
        {
            _running.Add(running);
        }

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => OnProcessExited(running, process);
        GameStarted?.Invoke(this, running);
        _logger.Info("Launched '" + running.Name + "' from " + snapshot.DriveLetter + ": (pid " + running.ProcessId + ").", "Games");

        double? startupSeconds = null;
        bool windowAppeared = false;
        bool verified = false;
        int? exitCode = null;
        string? error = null;
        string? fix = null;

        DateTimeOffset windowDeadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, request.WindowWaitSeconds));
        while (DateTimeOffset.UtcNow < windowDeadline && !cancellationToken.IsCancellationRequested)
        {
            if (HasExited(process))
            {
                break;
            }

            try
            {
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    windowAppeared = true;
                    startupSeconds = (DateTimeOffset.UtcNow - startedUtc).TotalSeconds;
                    break;
                }
            }
            catch (Exception)
            {
                // the process may exit between checks
            }

            try
            {
                await Task.Delay(WindowPollMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (HasExited(process))
        {
            exitCode = SafeExitCode(process);
            error = "The game exited during startup with code " + (exitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown") + ".";
            fix = "Open the rclone log for this drive; missing Windows runtime files (Visual C++ or .NET) are the most common cause.";
        }
        else
        {
            int verifySeconds = Math.Max(5, request.VerifySeconds);
            DateTimeOffset verifyDeadline = DateTimeOffset.UtcNow.AddSeconds(verifySeconds);
            while (DateTimeOffset.UtcNow < verifyDeadline && !cancellationToken.IsCancellationRequested)
            {
                if (HasExited(process))
                {
                    break;
                }

                try
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (HasExited(process))
            {
                exitCode = SafeExitCode(process);
                error = "The game exited after " + (int)(DateTimeOffset.UtcNow - startedUtc).TotalSeconds + " s with code " +
                        (exitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown") + ".";
                fix = "If the game closed immediately, check its log file on the drive and the rclone log for read errors.";
            }
            else
            {
                verified = true;
            }
        }

        if (!verified && !wasPaused)
        {
            _prefetch.Resume();
        }

        long? cacheAfter = await ReadCacheBytesAsync(request.AccountId, cancellationToken).ConfigureAwait(false);
        long? delta = cacheBefore is not null && cacheAfter is not null ? Math.Max(0, cacheAfter.Value - cacheBefore.Value) : null;

        IReadOnlyList<string> logTail = verified
            ? Array.Empty<string>()
            : _mounts.GetLogTail(request.AccountId, 25);

        _logger.Info(
            "Launch of '" + running.Name + "': " + (verified ? "verified" : "not verified") +
            " (window " + (windowAppeared ? "after " + startupSeconds?.ToString("0.0", CultureInfo.InvariantCulture) + " s" : "not detected") +
            ", exit code " + (exitCode?.ToString(CultureInfo.InvariantCulture) ?? "n/a") + ").",
            "Games");

        return LaunchResult.Completed(
            request.GameId,
            running.ProcessId,
            startupSeconds,
            windowAppeared,
            verified,
            exitCode,
            error,
            fix,
            delta,
            logTail);
    }

    /// <summary>Uses <c>vfs/stats</c> to read how much rclone really fetched.</summary>
    private async Task<long?> ReadCacheBytesAsync(string accountId, CancellationToken cancellationToken)
    {
        RcloneRcClient? rc = _mounts.GetRcClient(accountId);
        if (rc is null)
        {
            return null;
        }

        try
        {
            JsonObject? stats = await rc.VfsStatsAsync(null, cancellationToken).ConfigureAwait(false);
            return stats is null ? null : SumBytesUsed(stats);
        }
        catch (Exception exception)
        {
            _logger.Debug("Could not read vfs/stats: " + exception.Message, "Games");
            return null;
        }
    }

    /// <summary>Sums every numeric field named "bytesUsed" in the statistics tree.</summary>
    private static long SumBytesUsed(JsonNode node)
    {
        long total = 0;
        if (node is JsonObject obj)
        {
            foreach (KeyValuePair<string, JsonNode?> property in obj)
            {
                if (string.Equals(property.Key, "bytesUsed", StringComparison.OrdinalIgnoreCase) &&
                    property.Value is JsonValue value &&
                    value.TryGetValue(out long bytes))
                {
                    total += bytes;
                }
                else if (property.Value is not null)
                {
                    total += SumBytesUsed(property.Value);
                }
            }
        }

        return total;
    }

    private void OnProcessExited(RunningGame running, Process process)
    {
        running.ExitCode ??= SafeExitCode(process);
        running.ExitedUtc = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            _running.Remove(running);
        }

        // Game-aware mode ends with the last game: prefetch may compete for the drive again.
        if (Running.Count == 0 && _prefetch.IsPaused)
        {
            _prefetch.Resume();
        }

        _logger.Info(
            "'" + running.Name + "' exited after " + running.Uptime.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) +
            " s with code " + (running.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown") + ".",
            "Games");

        try
        {
            GameExited?.Invoke(this, running);
        }
        catch (Exception exception)
        {
            _logger.Error("A game exit subscriber threw.", "Games", exception);
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Win32Exception)
        {
            return true;
        }
    }

    private static int? SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Games keep running when NimbusX closes; only stop watching them. Nothing is killed: closing the app must
        // never drop a player out of their game.
        _disposed = true;
        lock (_gate)
        {
            _running.Clear();
        }
    }
}
