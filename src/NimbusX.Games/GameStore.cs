using System;
using System.Collections.Generic;
using System.Linq;
using NimbusX.Core.Io;
using NimbusX.Core.Logging;

namespace NimbusX.Games;

/// <summary>Persists the game library (no secrets, just paths and measured launch facts).</summary>
public sealed class GameStore
{
    private readonly string _path;
    private readonly IAppLogger _logger;
    private readonly object _gate = new();
    private List<GameEntry> _games;

    public GameStore(string gamesFilePath, IAppLogger? logger = null)
    {
        _path = gamesFilePath ?? throw new ArgumentNullException(nameof(gamesFilePath));
        _logger = logger ?? NullLogger.Instance;
        _games = AtomicFile.ReadJson<List<GameEntry>>(_path) ?? new List<GameEntry>();
    }

    public IReadOnlyList<GameEntry> Games
    {
        get
        {
            lock (_gate)
            {
                return _games.ToArray();
            }
        }
    }

    public IReadOnlyList<GameEntry> ForAccount(string accountId)
    {
        lock (_gate)
        {
            return _games
                .Where(g => string.Equals(g.AccountId, accountId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(g => g.IsFavorite)
                .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
    }

    public GameEntry? Find(string gameId)
    {
        lock (_gate)
        {
            return _games.FirstOrDefault(g => string.Equals(g.Id, gameId, StringComparison.Ordinal));
        }
    }

    public GameEntry? FindByPath(string accountId, string relativeFolder)
    {
        string normalized = Normalize(relativeFolder);
        lock (_gate)
        {
            return _games.FirstOrDefault(g =>
                string.Equals(g.AccountId, accountId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Normalize(g.RelativeFolder), normalized, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Adds or updates a game by (account, folder). Returns the stored entry.</summary>
    public GameEntry Upsert(GameEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.RelativeFolder = Normalize(entry.RelativeFolder);
        entry.ExecutableRelativePath = Normalize(entry.ExecutableRelativePath);

        GameEntry stored;
        lock (_gate)
        {
            GameEntry? existing = _games.FirstOrDefault(g =>
                string.Equals(g.AccountId, entry.AccountId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(g.RelativeFolder, entry.RelativeFolder, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                _games.Add(entry);
                stored = entry;
            }
            else
            {
                existing.Name = string.IsNullOrWhiteSpace(entry.Name) ? existing.Name : entry.Name;
                existing.ExecutableRelativePath = string.IsNullOrWhiteSpace(entry.ExecutableRelativePath)
                    ? existing.ExecutableRelativePath
                    : entry.ExecutableRelativePath;
                existing.Confidence = entry.Confidence > existing.Confidence ? entry.Confidence : existing.Confidence;
                existing.EngineMarkers = entry.EngineMarkers.Count > 0 ? entry.EngineMarkers : existing.EngineMarkers;
                existing.MeasuredFolderBytes = entry.MeasuredFolderBytes > 0 ? entry.MeasuredFolderBytes : existing.MeasuredFolderBytes;
                existing.LastScanUtc = entry.LastScanUtc ?? existing.LastScanUtc;
                stored = existing;
            }
        }

        Save();
        return stored;
    }

    /// <summary>Records the outcome of one launch attempt.</summary>
    public void RecordLaunch(
        string gameId,
        bool windowAppeared,
        double? startupSeconds,
        int? exitCode,
        string? error,
        bool verified,
        IEnumerable<string>? hotFiles)
    {
        GameEntry? game = Find(gameId);
        if (game is null)
        {
            return;
        }

        lock (_gate)
        {
            game.LaunchCount++;
            game.LastLaunchUtc = DateTimeOffset.UtcNow;
            game.LastError = error;
            game.LastExitCode = exitCode;
            if (startupSeconds is not null)
            {
                game.LastStartupSeconds = startupSeconds;
                if (windowAppeared && (game.BestStartupSeconds is null || startupSeconds < game.BestStartupSeconds))
                {
                    game.BestStartupSeconds = startupSeconds;
                }
            }

            if (verified)
            {
                game.VerifiedLaunch = true;
                game.VerifiedUtc = DateTimeOffset.UtcNow;
            }

            if (hotFiles is not null)
            {
                List<string> merged = game.HotFiles.ToList();
                foreach (string file in hotFiles)
                {
                    string normalized = Normalize(file);
                    if (!string.IsNullOrWhiteSpace(normalized) && !merged.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                    {
                        merged.Add(normalized);
                    }
                }

                game.HotFiles = merged.Take(32).ToList();
            }
        }

        Save();
    }

    public bool Remove(string gameId)
    {
        bool removed;
        lock (_gate)
        {
            removed = _games.RemoveAll(g => string.Equals(g.Id, gameId, StringComparison.Ordinal)) > 0;
        }

        if (removed)
        {
            Save();
        }

        return removed;
    }

    public void SetFavorite(string gameId, bool favorite)
    {
        GameEntry? game = Find(gameId);
        if (game is null)
        {
            return;
        }

        game.IsFavorite = favorite;
        Save();
    }

    private void Save()
    {
        List<GameEntry> snapshot;
        lock (_gate)
        {
            snapshot = _games.ToList();
        }

        try
        {
            AtomicFile.WriteJson(_path, snapshot);
        }
        catch (Exception exception)
        {
            _logger.Error("Could not save the game library.", "Games", exception);
        }
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value!.Replace('\\', '/').Trim('/');
}
