using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using NimbusX.Core.Logging;

namespace NimbusX.App;

/// <summary>A saved per-game profile.</summary>
public sealed class GameProfile
{
    public string GameId { get; set; } = string.Empty;

    public string AccountId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Extra command line arguments applied when this profile is used.</summary>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>Cache share this game prefers, in bytes (0 = use the account share).</summary>
    public long PreferredCacheBytes { get; set; }

    /// <summary>Seconds the launcher waits before marking a launch verified.</summary>
    public int VerifySeconds { get; set; } = 60;

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One measured launch, written after the launcher finishes.</summary>
public sealed class LaunchRecord
{
    public long Id { get; set; }

    public string GameId { get; set; } = string.Empty;

    public string AccountId { get; set; } = string.Empty;

    public DateTimeOffset StartedUtc { get; set; }

    public double? StartupSeconds { get; set; }

    public bool WindowAppeared { get; set; }

    public bool Verified { get; set; }

    public int? ExitCode { get; set; }

    public string? Error { get; set; }

    public long? CacheBytes { get; set; }
}

/// <summary>
/// SQLite store for game profiles and the launch history (WAL, one connection per operation).
/// </summary>
/// <remarks>
/// Only measured launch facts are stored; nothing here is estimated. The schema is created on first use and
/// <see cref="SchemaVersion"/> is written into the database so a future version can migrate instead of guessing.
/// A failed database never blocks mounting: callers get a clear exception from the write path and the app reports
/// it on the Diagnostics page.
/// </remarks>
public sealed class ProfileDatabase : IDisposable
{
    public const int SchemaVersion = 1;

    private readonly string _connectionString;
    private readonly IAppLogger _logger;
    private bool _disposed;

    public ProfileDatabase(string databaseFile, IAppLogger? logger = null)
    {
        FilePath = databaseFile ?? throw new ArgumentNullException(nameof(databaseFile));
        _logger = logger ?? NullLogger.Instance;

        string? directory = Path.GetDirectoryName(databaseFile);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory!);
        }

        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 10
        };

        _connectionString = builder.ToString();
    }

    public string FilePath { get; }

    /// <summary>Creates the schema when it is missing. Safe to call on every start.</summary>
    public void Initialize()
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA foreign_keys=ON;

            CREATE TABLE IF NOT EXISTS schema_info (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS game_profiles (
                game_id TEXT PRIMARY KEY,
                account_id TEXT NOT NULL,
                name TEXT NOT NULL,
                arguments TEXT NOT NULL DEFAULT '',
                preferred_cache_bytes INTEGER NOT NULL DEFAULT 0,
                verify_seconds INTEGER NOT NULL DEFAULT 60,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS launch_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                game_id TEXT NOT NULL,
                account_id TEXT NOT NULL,
                started_utc TEXT NOT NULL,
                startup_seconds REAL NULL,
                window_appeared INTEGER NOT NULL,
                verified INTEGER NOT NULL,
                exit_code INTEGER NULL,
                error TEXT NULL,
                cache_bytes INTEGER NULL
            );

            CREATE INDEX IF NOT EXISTS ix_launch_history_game ON launch_history(game_id, started_utc DESC);

            INSERT INTO schema_info(key, value) VALUES ('schema_version', '1')
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.ExecuteNonQuery();
    }

    public void SaveProfile(GameProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO game_profiles(game_id, account_id, name, arguments, preferred_cache_bytes, verify_seconds, updated_utc)
            VALUES ($game, $account, $name, $arguments, $cache, $verify, $updated)
            ON CONFLICT(game_id) DO UPDATE SET
                account_id = excluded.account_id,
                name = excluded.name,
                arguments = excluded.arguments,
                preferred_cache_bytes = excluded.preferred_cache_bytes,
                verify_seconds = excluded.verify_seconds,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$game", profile.GameId);
        command.Parameters.AddWithValue("$account", profile.AccountId);
        command.Parameters.AddWithValue("$name", profile.Name ?? string.Empty);
        command.Parameters.AddWithValue("$arguments", profile.Arguments ?? string.Empty);
        command.Parameters.AddWithValue("$cache", Math.Max(0, profile.PreferredCacheBytes));
        command.Parameters.AddWithValue("$verify", Math.Max(5, profile.VerifySeconds));
        command.Parameters.AddWithValue("$updated", profile.UpdatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public GameProfile? GetProfile(string gameId)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT game_id, account_id, name, arguments, preferred_cache_bytes, verify_seconds, updated_utc FROM game_profiles WHERE game_id = $game LIMIT 1;";
        command.Parameters.AddWithValue("$game", gameId ?? string.Empty);

        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new GameProfile
        {
            GameId = reader.GetString(0),
            AccountId = reader.GetString(1),
            Name = reader.GetString(2),
            Arguments = reader.GetString(3),
            PreferredCacheBytes = reader.GetInt64(4),
            VerifySeconds = reader.GetInt32(5),
            UpdatedUtc = ParseTimestamp(reader.GetString(6))
        };
    }

    public void DeleteProfile(string gameId)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM game_profiles WHERE game_id = $game;";
        command.Parameters.AddWithValue("$game", gameId ?? string.Empty);
        command.ExecuteNonQuery();
    }

    public long AddLaunch(LaunchRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO launch_history(game_id, account_id, started_utc, startup_seconds, window_appeared, verified, exit_code, error, cache_bytes)
            VALUES ($game, $account, $started, $startup, $window, $verified, $exit, $error, $cache);
            """;
        command.Parameters.AddWithValue("$game", record.GameId);
        command.Parameters.AddWithValue("$account", record.AccountId);
        command.Parameters.AddWithValue("$started", record.StartedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$startup", (object?)record.StartupSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$window", record.WindowAppeared ? 1 : 0);
        command.Parameters.AddWithValue("$verified", record.Verified ? 1 : 0);
        command.Parameters.AddWithValue("$exit", (object?)record.ExitCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)record.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$cache", (object?)record.CacheBytes ?? DBNull.Value);

        command.ExecuteNonQuery();

        using SqliteCommand identity = connection.CreateCommand();
        identity.CommandText = "SELECT last_insert_rowid();";
        object? inserted = identity.ExecuteScalar();
        return inserted is long id ? id : 0;
    }

    public IReadOnlyList<LaunchRecord> RecentLaunches(string gameId, int count = 20)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, game_id, account_id, started_utc, startup_seconds, window_appeared, verified, exit_code, error, cache_bytes
            FROM launch_history
            WHERE game_id = $game
            ORDER BY started_utc DESC
            LIMIT $take;
            """;
        command.Parameters.AddWithValue("$game", gameId ?? string.Empty);
        command.Parameters.AddWithValue("$take", Math.Clamp(count, 1, 500));

        List<LaunchRecord> records = new();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(new LaunchRecord
            {
                Id = reader.GetInt64(0),
                GameId = reader.GetString(1),
                AccountId = reader.GetString(2),
                StartedUtc = ParseTimestamp(reader.GetString(3)),
                StartupSeconds = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                WindowAppeared = reader.GetInt32(5) != 0,
                Verified = reader.GetInt32(6) != 0,
                ExitCode = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                Error = reader.IsDBNull(8) ? null : reader.GetString(8),
                CacheBytes = reader.IsDBNull(9) ? null : reader.GetInt64(9)
            });
        }

        return records;
    }

    /// <summary>Median measured startup time, or null when there is not enough data to be honest about it.</summary>
    public double? MedianStartupSeconds(string gameId, int minimumSamples = 3)
    {
        List<double> samples = new();
        foreach (LaunchRecord record in RecentLaunches(gameId, 50))
        {
            // Only a launch that showed its window and stayed up for the verify window is a startup sample.
            if (record.WindowAppeared && record.Verified && record.StartupSeconds is double seconds && seconds > 0)
            {
                samples.Add(seconds);
            }
        }

        if (samples.Count < Math.Max(1, minimumSamples))
        {
            return null;
        }

        samples.Sort();
        int middle = samples.Count / 2;
        return samples.Count % 2 == 1 ? samples[middle] : (samples[middle - 1] + samples[middle]) / 2d;
    }

    public int TrimHistory(int keepPerGame = 100)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM launch_history
            WHERE id IN (
                SELECT id FROM (
                    SELECT id, ROW_NUMBER() OVER (PARTITION BY game_id ORDER BY started_utc DESC) AS position
                    FROM launch_history
                )
                WHERE position > $keep
            );
            """;
        command.Parameters.AddWithValue("$keep", Math.Clamp(keepPerGame, 5, 1000));
        return command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        SqliteConnection connection = new(_connectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch (Exception exception)
        {
            connection.Dispose();
            _logger.Error("Could not open the profile database at " + FilePath + ".", "Profiles", exception);
            throw;
        }
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SqliteConnection.ClearAllPools();
    }
}
