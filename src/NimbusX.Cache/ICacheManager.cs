using System;
using System.Collections.Generic;
using NimbusX.Core.Config;

namespace NimbusX.Cache;

/// <summary>Keeps the cache budget, measurements, disk safety, pins and clearing in one place.</summary>
public interface ICacheManager
{
    /// <summary>Raised after <see cref="Refresh"/> when the measured status changed.</summary>
    event EventHandler<CacheStatus>? StatusChanged;

    /// <summary>Last measured status (never fabricated: unmeasured values read "Unavailable").</summary>
    CacheStatus Current { get; }

    AppSettings Settings { get; }

    /// <summary>Builds the budget plan for the given accounts from the current settings.</summary>
    CacheBudgetPlan CreatePlan(IEnumerable<string> accountIds);

    /// <summary>The <c>--vfs-cache-max-size</c> value a mount for this account should use.</summary>
    long BudgetFor(string accountId, IEnumerable<string>? allAccountIds = null);

    /// <summary>Measures the cache folders of the accounts that are mounted right now.</summary>
    CacheStatus Refresh();

    /// <summary>Measures one account without touching the persisted status.</summary>
    CacheUsage Measure(string accountId);

    /// <summary>Applies a preset budget (10/20/30/40 GB). Returns false for an unknown preset.</summary>
    bool SetBudgetPreset(int gigabytes);

    /// <summary>Applies a custom budget in bytes; zero or negative is rejected.</summary>
    bool SetCustomBudget(long bytes);

    /// <summary>Overrides one mount's share. Passing zero removes the override.</summary>
    void SetShareOverride(string accountId, long bytes);

    /// <summary>Refuses to set a budget larger than the free space of the cache volume.</summary>
    long ClampBudgetToFreeSpace(long requestedBytes);

    CacheClearResult ClearAccount(string accountId, bool includePinned = false);

    CacheClearResult ClearEverything(bool includePinned = false);

    /// <summary>How much a clear would free right now (pinned files excluded).</summary>
    long MeasureClearable();

    IReadOnlyList<PinnedEntry> Pins(string accountId);

    PinnedEntry? PinPath(string accountId, string relativePath);

    bool UnpinPath(string accountId, string relativePath);
}
