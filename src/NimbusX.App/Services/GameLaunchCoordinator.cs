using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NimbusX.Core.Logging;
using NimbusX.Games;

namespace NimbusX.App;

/// <summary>
/// Launches a game and stores the measured result in the SQLite history that the accelerator reads.
/// </summary>
/// <remarks>
/// The games service already records the launch in the JSON library; this coordinator adds the queryable history so
/// predictions can use medians and per-game launch timelines. Nothing is written when the measurement failed.
/// </remarks>
public sealed class GameLaunchCoordinator
{
    private readonly GamesService _games;
    private readonly ProfileDatabase _profiles;
    private readonly IAppLogger _logger;

    public GameLaunchCoordinator(GamesService games, ProfileDatabase profiles, IAppLogger? logger = null)
    {
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<LaunchResult> PlayAsync(string gameId, CancellationToken cancellationToken)
    {
        GameEntry? game = _games.Library.FirstOrDefault(g => string.Equals(g.Id, gameId, StringComparison.Ordinal));
        LaunchResult result = await _games.PlayAsync(gameId, cancellationToken).ConfigureAwait(false);

        if (game is null)
        {
            return result;
        }

        try
        {
            _profiles.AddLaunch(new LaunchRecord
            {
                GameId = gameId,
                AccountId = game.AccountId,
                StartedUtc = DateTimeOffset.UtcNow,
                StartupSeconds = result.StartupSeconds,
                WindowAppeared = result.WindowAppeared,
                Verified = result.Verified,
                ExitCode = result.ExitCode,
                Error = result.Error,
                CacheBytes = result.CacheBytesDuringLaunch
            });

            _profiles.TrimHistory(100);
        }
        catch (Exception exception)
        {
            // A database problem must never hide the launch result.
            _logger.Warn("Could not store the launch history: " + exception.Message, "Games");
        }

        return result;
    }

    public GameProfile? ProfileFor(string gameId)
    {
        try
        {
            return _profiles.GetProfile(gameId);
        }
        catch (Exception exception)
        {
            _logger.Debug("Could not read the game profile: " + exception.Message, "Games");
            return null;
        }
    }
}
