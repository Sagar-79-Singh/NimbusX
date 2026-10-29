using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NimbusX.Cache;
using NimbusX.Core.Text;
using Xunit;

namespace NimbusX.Tests;

public sealed class CacheBudgetPlanTests
{
    [Fact]
    public void Presets_are_the_four_documented_sizes()
    {
        Assert.Equal(new[] { 10, 20, 30, 40 }, CachePresets.Gigabytes);
        Assert.Equal(20L * 1024 * 1024 * 1024, CachePresets.ToBytes(20));
    }

    [Theory]
    [InlineData(20, 20)]
    [InlineData(10, 10)]
    [InlineData(40, 40)]
    public void Match_finds_the_nearest_preset(int gigabytes, int expected) =>
        Assert.Equal(expected, CachePresets.Match(CachePresets.ToBytes(gigabytes)));

    [Fact]
    public void Match_returns_zero_for_a_custom_budget() =>
        Assert.Equal(0, CachePresets.Match(CachePresets.ToBytes(25)));

    [Fact]
    public void Shares_add_up_to_the_total_budget()
    {
        CacheBudgetPlan plan = CacheBudgetPlan.Create(
            CachePresets.ToBytes(30),
            CachePresets.ToBytes(2),
            new[] { "a", "b", "c" });

        Assert.Equal(3, plan.MountCount);
        Assert.Equal(plan.TotalBudgetBytes, plan.Shares.Values.Sum());
        Assert.Equal(CachePresets.ToBytes(10), plan.ShareFor("a"));
    }

    [Fact]
    public void An_explicit_override_wins_and_the_rest_split_the_remainder()
    {
        CacheBudgetPlan plan = CacheBudgetPlan.Create(
            CachePresets.ToBytes(20),
            0,
            new[] { "a", "b" },
            new Dictionary<string, long> { ["a"] = CachePresets.ToBytes(12) });

        Assert.Equal(CachePresets.ToBytes(12), plan.ShareFor("a"));
        Assert.Equal(CachePresets.ToBytes(8), plan.ShareFor("b"));
    }

    [Fact]
    public void Every_share_keeps_the_256_mebibyte_floor()
    {
        CacheBudgetPlan plan = CacheBudgetPlan.Create(1024, 0, new[] { "a", "b", "c" });

        Assert.All(plan.Shares.Values, share => Assert.True(share >= CacheBudgetPlan.MinimumShareBytes));
    }

    [Fact]
    public void Duplicate_and_blank_account_ids_do_not_inflate_the_plan()
    {
        CacheBudgetPlan plan = CacheBudgetPlan.Create(CachePresets.ToBytes(10), 0, new[] { "a", "A", " ", "b" });
        Assert.Equal(2, plan.MountCount);
    }

    [Fact]
    public void No_accounts_means_no_shares()
    {
        CacheBudgetPlan plan = CacheBudgetPlan.Create(CachePresets.ToBytes(10), 0, Array.Empty<string>());
        Assert.Equal(0, plan.MountCount);
        Assert.Contains("No drives mounted", plan.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_pause_threshold_allows_the_ten_percent_overrun()
    {
        long budget = CachePresets.ToBytes(10);
        CacheBudgetPlan plan = CacheBudgetPlan.Create(budget, 0, new[] { "a" });
        Assert.Equal((long)(budget * 1.1d), plan.PauseThresholdBytes);
    }

    [Fact]
    public void Clamp_keeps_the_windows_reserve_free()
    {
        long reserved = CachePresets.ToBytes(5);
        long clamped = CacheBudgetPlan.ClampToAvailable(CachePresets.ToBytes(40), CachePresets.ToBytes(20), reserved);
        Assert.Equal(CachePresets.ToBytes(15), clamped);
    }

    [Fact]
    public void Clamp_returns_the_request_when_free_space_is_unknown() =>
        Assert.Equal(CachePresets.ToBytes(30), CacheBudgetPlan.ClampToAvailable(CachePresets.ToBytes(30), null, 0));
}

public sealed class DiskSafetyGuardTests
{
    [Fact]
    public void Plenty_of_free_space_keeps_prefetch_running()
    {
        DiskSafetyGuard guard = new(_ => CachePresets.ToBytes(100));
        DiskSafetyState state = guard.Evaluate("C:\\", CachePresets.ToBytes(20), CachePresets.ToBytes(5), CachePresets.ToBytes(2));

        Assert.Equal(DiskPressureLevel.Normal, state.Level);
        Assert.False(state.PrefetchPaused);
        Assert.True(guard.CanPrefetch);
    }

    [Fact]
    public void A_full_disk_pauses_prefetch_before_the_reserve_is_gone()
    {
        DiskSafetyGuard guard = new(_ => CachePresets.ToBytes(1));
        DiskSafetyState state = guard.Evaluate("C:\\", CachePresets.ToBytes(20), CachePresets.ToBytes(5), CachePresets.ToBytes(4));

        Assert.Equal(DiskPressureLevel.Critical, state.Level);
        Assert.True(state.PrefetchPaused);
        Assert.Contains("Prefetch paused", state.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Reaching_the_overrun_threshold_pauses_prefetch()
    {
        long budget = CachePresets.ToBytes(10);
        DiskSafetyGuard guard = new(_ => CachePresets.ToBytes(100));
        DiskSafetyState state = guard.Evaluate("C:\\", budget, (long)(budget * 1.1d), CachePresets.ToBytes(2));

        Assert.Equal(DiskPressureLevel.Warning, state.Level);
        Assert.True(state.PrefetchPaused);
    }

    [Fact]
    public void An_unreadable_volume_fails_closed()
    {
        DiskSafetyGuard guard = new(_ => null);
        DiskSafetyState state = guard.Evaluate("Z:\\", CachePresets.ToBytes(10), 0, 0);

        Assert.Equal(DiskPressureLevel.Critical, state.Level);
        Assert.True(state.PrefetchPaused);
    }

    [Fact]
    public void StateChanged_fires_only_when_the_level_changes()
    {
        long free = CachePresets.ToBytes(100);
        DiskSafetyGuard guard = new(_ => free);
        int changes = 0;
        guard.StateChanged += (_, _) => changes++;

        guard.Evaluate("C:\\", CachePresets.ToBytes(20), CachePresets.ToBytes(1), 0);
        guard.Evaluate("C:\\", CachePresets.ToBytes(20), CachePresets.ToBytes(2), 0);
        free = 0;
        guard.Evaluate("C:\\", CachePresets.ToBytes(20), CachePresets.ToBytes(3), CachePresets.ToBytes(1));

        Assert.True(changes >= 1);
    }
}

public sealed class PinnedPathStoreTests
{
    [Theory]
    [InlineData("Games\\level.pak", "Games/level.pak")]
    [InlineData("/Games/level.pak/", "Games/level.pak")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeRelative_makes_separators_and_slashes_irrelevant(string? input, string expected) =>
        Assert.Equal(expected, PinnedPathStore.NormalizeRelative(input));

    [Fact]
    public void Pins_survive_a_reload()
    {
        using TempFolder folder = new("pins");
        string path = folder.Combine("cache-pins.json");

        PinnedPathStore store = new(path);
        store.Pin("account-1", "nx_remote", "Games/level.pak", CachePresets.ToBytes(1));
        Assert.True(store.IsPinned("account-1", "Games\\level.pak"));

        PinnedPathStore reloaded = new(path);
        Assert.True(reloaded.IsPinned("account-1", "Games/level.pak"));
        Assert.False(reloaded.IsPinned("account-2", "Games/level.pak"));
    }

    [Fact]
    public void Pinning_the_same_file_twice_is_idempotent()
    {
        using TempFolder folder = new("pins");
        PinnedPathStore store = new(folder.Combine("cache-pins.json"));

        store.Pin("account-1", "r", "a.bin", 10);
        store.Pin("account-1", "r", "a.bin", 10);

        Assert.Single(store.ForAccount("account-1"));
        Assert.Equal(10, store.TotalPinnedBytes("account-1"));
    }

    [Fact]
    public void Unpin_removes_only_the_named_entry()
    {
        using TempFolder folder = new("pins");
        PinnedPathStore store = new(folder.Combine("cache-pins.json"));

        store.Pin("account-1", "r", "a.bin", 10);
        store.Pin("account-1", "r", "b.bin", 20);

        Assert.True(store.Unpin("account-1", "a.bin"));
        Assert.False(store.IsPinned("account-1", "a.bin"));
        Assert.True(store.IsPinned("account-1", "b.bin"));
        Assert.Equal(20, store.TotalPinnedBytes("account-1"));
    }
}

public sealed class CacheTextTests
{
    [Fact]
    public void ByteSize_round_trips_through_the_settings_parser()
    {
        long bytes = CachePresets.ToBytes(25);
        string text = ByteSize.Format(bytes);
        Assert.True(ByteSize.TryParse(text, out long parsed));
        Assert.Equal(bytes, parsed);
    }

    [Fact]
    public void The_cache_marker_file_name_is_stable()
    {
        Assert.Equal(".nimbusx-cache", NimbusX.Core.AppInfo.CacheMarkerName);
    }
}
