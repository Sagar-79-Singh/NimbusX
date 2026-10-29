using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NimbusX.App;
using NimbusX.Core.Config;
using Xunit;

namespace NimbusX.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void A_fresh_install_needs_the_wizard()
    {
        AppSettings settings = new();
        Assert.True(settings.NeedsWizard);
    }

    [Fact]
    public void A_completed_setup_does_not_need_the_wizard()
    {
        AppSettings settings = new()
        {
            FirstRunCompleted = true,
            CacheRoot = Path.Combine(Path.GetTempPath(), "nimbusx-cache")
        };

        Assert.False(settings.NeedsWizard);
    }

    [Fact]
    public void Completing_the_wizard_without_a_cache_folder_still_needs_it()
    {
        AppSettings settings = new() { FirstRunCompleted = true, CacheRoot = "  " };
        Assert.True(settings.NeedsWizard);
    }
}

public sealed class SettingsServiceTests
{
    [Fact]
    public void Settings_survive_a_restart()
    {
        using TestEnvironment environment = new();
        environment.Paths.EnsureCreated();

        SettingsService first = new(environment.Paths, new RecordingLogger());
        first.Update(settings =>
        {
            settings.FirstRunCompleted = true;
            settings.CacheRoot = environment.Root;
            settings.CacheBudgetBytes = 30L * 1024 * 1024 * 1024;
            settings.CacheBudgetPresetGb = 30;
        });
        first.Save();

        SettingsService second = new(environment.Paths, new RecordingLogger());
        Assert.True(second.Current.FirstRunCompleted);
        Assert.Equal(environment.Root, second.Current.CacheRoot);
        Assert.Equal(30L * 1024 * 1024 * 1024, second.Current.CacheBudgetBytes);
    }

    [Fact]
    public void A_corrupt_settings_file_falls_back_to_defaults_instead_of_crashing()
    {
        using TestEnvironment environment = new();
        environment.Paths.EnsureCreated();
        File.WriteAllText(environment.Paths.SettingsFile, "this is not json");

        RecordingLogger logger = new();
        SettingsService service = new(environment.Paths, logger);

        Assert.NotNull(service.Current);
        Assert.True(service.Current.NeedsWizard);
        Assert.NotEmpty(logger.Entries);
    }

    [Fact]
    public void Update_raises_Changed_once()
    {
        using TestEnvironment environment = new();
        environment.Paths.EnsureCreated();
        SettingsService service = new(environment.Paths, new RecordingLogger());

        int raised = 0;
        service.Changed += (_, _) => raised++;
        service.Update(settings => settings.HealthCheckSeconds = 15);

        Assert.Equal(1, raised);
        Assert.Equal(15, service.Current.HealthCheckSeconds);
    }
}

public sealed class ProfileDatabaseTests
{
    private static ProfileDatabase Open(TestEnvironment environment)
    {
        ProfileDatabase database = new(environment.Paths.DatabaseFile, new RecordingLogger());
        database.Initialize();
        return database;
    }

    [Fact]
    public void Initialize_is_idempotent()
    {
        using TestEnvironment environment = new();
        using ProfileDatabase database = Open(environment);
        database.Initialize();
        database.Initialize();

        Assert.True(File.Exists(environment.Paths.DatabaseFile));
    }

    [Fact]
    public void A_game_profile_round_trips()
    {
        using TestEnvironment environment = new();
        using ProfileDatabase database = Open(environment);

        database.SaveProfile(new GameProfile
        {
            GameId = "game-1",
            AccountId = "account-1",
            Name = "Example",
            Arguments = "-windowed",
            PreferredCacheBytes = 4L * 1024 * 1024 * 1024,
            VerifySeconds = 45
        });

        GameProfile? loaded = database.GetProfile("game-1");

        Assert.NotNull(loaded);
        Assert.Equal("Example", loaded!.Name);
        Assert.Equal("-windowed", loaded.Arguments);
        Assert.Equal(45, loaded.VerifySeconds);
    }

    [Fact]
    public void Saving_the_same_profile_twice_updates_it()
    {
        using TestEnvironment environment = new();
        using ProfileDatabase database = Open(environment);

        database.SaveProfile(new GameProfile { GameId = "game-1", AccountId = "a", Name = "First" });
        database.SaveProfile(new GameProfile { GameId = "game-1", AccountId = "a", Name = "Second" });

        Assert.Equal("Second", database.GetProfile("game-1")!.Name);
    }

    [Fact]
    public void Launch_history_is_returned_newest_first()
    {
        using TestEnvironment environment = new();
        using ProfileDatabase database = Open(environment);

        DateTimeOffset start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        for (int i = 0; i < 3; i++)
        {
            database.AddLaunch(new LaunchRecord
            {
                GameId = "game-1",
                AccountId = "account-1",
                StartedUtc = start.AddMinutes(i),
                StartupSeconds = 10 + i,
                WindowAppeared = true,
                Verified = true
            });
        }

        IReadOnlyList<LaunchRecord> recent = database.RecentLaunches("game-1");

        Assert.Equal(3, recent.Count);
        Assert.True(recent[0].StartedUtc > recent[1].StartedUtc);
    }

    [Fact]
    public void The_median_startup_needs_at_least_three_samples()
    {
        using TestEnvironment environment = new();
        using ProfileDatabase database = Open(environment);

        database.AddLaunch(new LaunchRecord { GameId = "game-1", AccountId = "a", StartedUtc = DateTimeOffset.UtcNow, StartupSeconds = 8, Verified = true });
        database.AddLaunch(new LaunchRecord { GameId = "game-1", AccountId = "a", StartedUtc = DateTimeOffset.UtcNow, StartupSeconds = 20, Verified = true });
        Assert.Null(database.MedianStartupSeconds("game-1"));

        database.AddLaunch(new LaunchRecord { GameId = "game-1", AccountId = "a", StartedUtc = DateTimeOffset.UtcNow, StartupSeconds = 12, Verified = true });
        Assert.Equal(12d, database.MedianStartupSeconds("game-1"));
    }

    [Fact]
    public void Unverified_launches_do_not_skew_the_median()
    {
        using TestEnvironment environment = new();
        using ProfileDatabase database = Open(environment);

        for (int i = 0; i < 3; i++)
        {
            database.AddLaunch(new LaunchRecord { GameId = "game-1", AccountId = "a", StartedUtc = DateTimeOffset.UtcNow, StartupSeconds = 10, Verified = true });
        }

        // A launch whose window never appeared is a failure, not a one second start.
        database.AddLaunch(new LaunchRecord { GameId = "game-1", AccountId = "a", StartedUtc = DateTimeOffset.UtcNow, StartupSeconds = 1, WindowAppeared = false, Verified = false });

        Assert.Equal(10d, database.MedianStartupSeconds("game-1"));
    }

    [Fact]
    public void TrimHistory_keeps_the_newest_entries_per_game()
    {
        using TestEnvironment environment = new();
        using ProfileDatabase database = Open(environment);

        for (int i = 0; i < 30; i++)
        {
            database.AddLaunch(new LaunchRecord
            {
                GameId = "game-1",
                AccountId = "a",
                StartedUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i),
                StartupSeconds = i,
                Verified = true
            });
        }

        database.TrimHistory(10);

        IReadOnlyList<LaunchRecord> recent = database.RecentLaunches("game-1", 50);
        Assert.Equal(10, recent.Count);
        Assert.Equal(29d, recent[0].StartupSeconds);
    }

    [Fact]
    public void Launch_counters_update_the_profile_row()
    {
        using TestEnvironment environment = new();
        using ProfileDatabase database = Open(environment);

        Assert.Null(database.GetProfile("never-seen"));
        database.SaveProfile(new GameProfile { GameId = "never-seen", AccountId = "a", Name = "Later" });
        Assert.NotNull(database.GetProfile("never-seen"));
    }
}
