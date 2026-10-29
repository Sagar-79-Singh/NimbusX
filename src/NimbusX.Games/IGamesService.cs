using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimbusX.Games;

/// <summary>Game library, scanning, launching and game-aware cache behaviour.</summary>
public interface IGamesService
{
    event EventHandler<RunningGame>? GameStarted;

    event EventHandler<RunningGame>? GameExited;

    IReadOnlyList<GameEntry> Library { get; }

    IReadOnlyList<RunningGame> Running { get; }

    IReadOnlyList<GameEntry> ForAccount(string accountId);

    /// <summary>Scans a mounted drive for game folders. Never throws for a missing drive; read the result.</summary>
    Task<GameScanResult> ScanAsync(string accountId, string relativeRoot, ScanOptions? options, CancellationToken cancellationToken);

    /// <summary>Stores a scan candidate in the library.</summary>
    GameEntry? AddCandidate(string accountId, GameCandidate candidate);

    /// <summary>Adds a game by hand (the user knows better than any heuristic).</summary>
    GameEntry? AddManual(string accountId, string relativeFolder, string executableRelativePath, string? name);

    bool Remove(string gameId);

    void SetFavorite(string gameId, bool favorite);

    /// <summary>Launches a game and records the measured result. Returns the result whether or not it verified.</summary>
    Task<LaunchResult> PlayAsync(string gameId, CancellationToken cancellationToken);

    /// <summary>Stops watching a game without touching the process.</summary>
    void Forget(string gameId);
}
