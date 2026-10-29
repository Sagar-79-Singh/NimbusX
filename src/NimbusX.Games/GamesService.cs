using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NimbusX.Core;
using NimbusX.Core.Logging;
using NimbusX.Mount;
using NimbusX.Streaming;

namespace NimbusX.Games;

/// <summary>
/// Ties the scanner, the store and the launcher together. Every recorded number comes from a real launch.
/// </summary>
/// <remarks>
/// A launch is only marked verified when the process stayed alive for the configured window. Anything else is
/// recorded with the real error so the Games page can show the failure and a fix instead of a green tick.
/// </remarks>
public sealed class GamesService : IGamesService, IDisposable
{
    private readonly GameStore _store;
    private readonly GameLibraryScanner _scanner;
    private readonly GameLauncher _launcher;
    private readonly IAppLogger _logger;
    private bool _disposed;

    public GamesService(
        GameStore store,
        GameLibraryScanner scanner,
        GameLauncher launcher,
        IAppLogger? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _logger = logger ?? NullLogger.Instance;

        _launcher.GameStarted += (_, game) => GameStarted?.Invoke(this, game);
        _launcher.GameExited += (_, game) => GameExited?.Invoke(this, game);
    }

    public event EventHandler<RunningGame>? GameStarted;

    public event EventHandler<RunningGame>? GameExited;

    public IReadOnlyList<GameEntry> Library => _store.Games;

    public IReadOnlyList<RunningGame> Running => _launcher.Running;

    public IReadOnlyList<GameEntry> ForAccount(string accountId) => _store.ForAccount(accountId);

    public async Task<GameScanResult> ScanAsync(string accountId, string relativeRoot, ScanOptions? options, CancellationToken cancellationToken)
    {
        try
        {
            return await _scanner.ScanAsync(accountId, relativeRoot, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Warn("Game scan failed: " + exception.Message, "Games");
            return new GameScanResult(
                accountId,
                relativeRoot ?? string.Empty,
                Array.Empty<GameCandidate>(),
                0,
                TimeSpan.Zero,
                new[] { exception.Message });
        }
    }

    public GameEntry? AddCandidate(string accountId, GameCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return null;
        }

        GameEntry? existing = _store.FindByPath(accountId, candidate.RelativeFolder);
        GameEntry entry = existing ?? new GameEntry
        {
            AccountId = accountId,
            RelativeFolder = candidate.RelativeFolder
        };

        entry.Name = string.IsNullOrWhiteSpace(entry.Name) ? candidate.SuggestedName : entry.Name;
        entry.ExecutableRelativePath = candidate.ExecutableRelativePath;
        entry.Confidence = candidate.Confidence > entry.Confidence ? candidate.Confidence : entry.Confidence;
        entry.EngineMarkers = candidate.EngineMarkers.ToList();
        entry.MeasuredFolderBytes = candidate.FolderBytes > 0 ? candidate.FolderBytes : entry.MeasuredFolderBytes;
        entry.LastScanUtc = DateTimeOffset.UtcNow;

        return _store.Upsert(entry);
    }

    public GameEntry? AddManual(string accountId, string relativeFolder, string executableRelativePath, string? name)
    {
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(executableRelativePath))
        {
            return null;
        }

        string folder = string.IsNullOrWhiteSpace(relativeFolder) ? string.Empty : relativeFolder.Replace('\\', '/').Trim('/');
        GameEntry? existing = _store.FindByPath(accountId, folder);
        GameEntry entry = existing ?? new GameEntry
        {
            AccountId = accountId,
            RelativeFolder = folder
        };

        entry.ExecutableRelativePath = executableRelativePath.Replace('\\', '/').Trim('/');
        entry.Name = string.IsNullOrWhiteSpace(name)
            ? (string.IsNullOrWhiteSpace(folder) ? Path.GetFileNameWithoutExtension(entry.ExecutableRelativePath) : folder.Split('/').Last())
            : name!;
        entry.Confidence = GameConfidence.Confirmed;
        entry.LastScanUtc = DateTimeOffset.UtcNow;

        return _store.Upsert(entry);
    }

    public bool Remove(string gameId) => _store.Remove(gameId);

    public void SetFavorite(string gameId, bool favorite) => _store.SetFavorite(gameId, favorite);

    public async Task<LaunchResult> PlayAsync(string gameId, CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(gameId, nameof(gameId));

        GameEntry? game = _store.Find(gameId);
        if (game is null)
        {
            return LaunchResult.Fail(gameId, "That game is not in the library.", "Add it again from the Games page.");
        }

        if (string.IsNullOrWhiteSpace(game.ExecutableRelativePath))
        {
            return LaunchResult.Fail(
                gameId,
                "No executable is selected for '" + game.Name + "'.",
                "Press 'Change executable' on the game card and pick the .exe to start.");
        }

        LaunchRequest request = new()
        {
            GameId = game.Id,
            AccountId = game.AccountId,
            RelativeFolder = game.RelativeFolder,
            ExecutableRelativePath = game.ExecutableRelativePath,
            Arguments = game.Arguments,
            VerifySeconds = 60
        };

        LaunchResult result = await _launcher.LaunchAsync(request, cancellationToken).ConfigureAwait(false);

        _store.RecordLaunch(
            game.Id,
            result.WindowAppeared,
            result.StartupSeconds,
            result.ExitCode,
            result.Error,
            result.Verified,
            hotFiles: null);

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            _logger.Warn("Launch of '" + game.Name + "' did not verify: " + result.Error, "Games");
        }

        return result;
    }

    public void Forget(string gameId)
    {
        // The launcher keeps its own list; forgetting is only meaningful for the UI selection.
        _logger.Debug("Stopped watching game " + gameId + " (the process keeps running).", "Games");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _launcher.Dispose();
    }
}
