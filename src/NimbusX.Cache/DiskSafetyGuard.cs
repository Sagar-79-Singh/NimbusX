using System;
using System.Collections.Generic;
using System.Linq;
using NimbusX.Core;

namespace NimbusX.Cache;

/// <summary>How much trouble the cache volume is in.</summary>
public enum DiskPressureLevel
{
    /// <summary>Plenty of room.</summary>
    Normal = 0,

    /// <summary>Above the budget; prefetch pauses at the overrun threshold.</summary>
    Warning = 1,

    /// <summary>The Windows reserve is gone; nothing new may be cached.</summary>
    Critical = 2
}

/// <summary>Snapshot of cache pressure for one volume.</summary>
public sealed class DiskSafetyState
{
    public DiskSafetyState(
        long budgetBytes,
        long usedBytes,
        long reserveBytes,
        long freeBytes,
        DiskPressureLevel level,
        bool prefetchPaused,
        string explanation)
    {
        BudgetBytes = budgetBytes;
        UsedBytes = usedBytes;
        ReserveBytes = reserveBytes;
        FreeBytes = freeBytes;
        Level = level;
        PrefetchPaused = prefetchPaused;
        Explanation = explanation;
    }

    public long BudgetBytes { get; }

    public long UsedBytes { get; }

    /// <summary>Free space that must stay available on the volume.</summary>
    public long ReserveBytes { get; }

    public long FreeBytes { get; }

    public DiskPressureLevel Level { get; }

    /// <summary>True when prefetching must pause right now (game reads still get through).</summary>
    public bool PrefetchPaused { get; }

    public string Explanation { get; }

    /// <summary>0..1 usage of the budget (1 = exactly at budget).</summary>
    public double BudgetUse => BudgetBytes <= 0 ? 0d : Math.Clamp((double)UsedBytes / BudgetBytes, 0d, 2d);

    public static DiskSafetyState Unknown(string explanation) =>
        new(0, 0, 0, 0, DiskPressureLevel.Normal, false, explanation);

    public override string ToString() => Level + " (" + (BudgetUse * 100d).ToString("0") + "% of budget): " + Explanation;
}

/// <summary>
/// Protects the cache volume: compares the configured budget with the real free space, keeps the Windows reserve
/// and pauses prefetching when the disk is under pressure.
/// </summary>
/// <remarks>
/// The guard never deletes anything itself (rclone owns its cache) and never changes a Windows setting. It reports
/// a level that the prefetcher obeys, so a full disk slows background warming down instead of starving the game or
/// the operating system.
/// </remarks>
public sealed class DiskSafetyGuard
{
    private readonly Func<string?, long?> _freeSpaceProvider;
    private readonly object _gate = new();
    private DiskSafetyState _state = DiskSafetyState.Unknown("No cache folder configured yet.");

    public DiskSafetyGuard(Func<string?, long?>? freeSpaceProvider = null)
    {
        _freeSpaceProvider = freeSpaceProvider ?? Core.Io.DiskSpace.GetFreeBytes;
    }

    /// <summary>Raised when the level or the pause flag changes.</summary>
    public event EventHandler<DiskSafetyState>? StateChanged;

    public DiskSafetyState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>True when prefetching is currently allowed.</summary>
    public bool CanPrefetch => !State.PrefetchPaused;

    /// <summary>
    /// Evaluates the guard.
    /// </summary>
    /// <param name="volumePath">Path on the cache volume (any folder on it).</param>
    /// <param name="budgetBytes">Total configured cache budget.</param>
    /// <param name="usedBytes">Bytes the cache really occupies (physical, sparse aware).</param>
    /// <param name="reserveBytes">Free space Windows must keep.</param>
    public DiskSafetyState Evaluate(string? volumePath, long budgetBytes, long usedBytes, long reserveBytes)
    {
        long? free = _freeSpaceProvider(volumePath);
        long freeBytes = free ?? -1;

        DiskPressureLevel level = DiskPressureLevel.Normal;
        bool paused = false;
        string explanation;

        if (free is null)
        {
            explanation = "The cache volume could not be read, so prefetching is paused for safety.";
            level = DiskPressureLevel.Critical;
            paused = true;
        }
        else if (freeBytes < Math.Max(0, reserveBytes))
        {
            level = DiskPressureLevel.Critical;
            paused = true;
            explanation = "Only " + Core.Text.ByteSize.Format(freeBytes) + " left on the cache volume, which is below the " +
                          Core.Text.ByteSize.Format(reserveBytes) + " reserved for Windows. Prefetch paused.";
        }
        else
        {
            long pauseThreshold = (long)(Math.Max(0, budgetBytes) * (1d + CacheBudgetPlan.OverrunMargin));
            if (budgetBytes > 0 && usedBytes >= pauseThreshold)
            {
                level = DiskPressureLevel.Warning;
                paused = true;
                explanation = "Cache is at " + Core.Text.ByteSize.Format(usedBytes) + " of a " +
                              Core.Text.ByteSize.Format(budgetBytes) + " budget (" +
                              (usedBytes * 100d / Math.Max(1, budgetBytes)).ToString("0") +
                              "%). Prefetch paused so the disk can settle.";
            }
            else if (budgetBytes > 0 && usedBytes >= budgetBytes)
            {
                level = DiskPressureLevel.Warning;
                explanation = "Cache reached its budget (" + Core.Text.ByteSize.Format(usedBytes) + " of " +
                              Core.Text.ByteSize.Format(budgetBytes) + "). rclone will evict the oldest data.";
            }
            else
            {
                explanation = "Cache uses " + Core.Text.ByteSize.Format(Math.Max(0, usedBytes)) + " of a " +
                              Core.Text.ByteSize.Format(Math.Max(0, budgetBytes)) + " budget; " +
                              Core.Text.ByteSize.Format(Math.Max(0, freeBytes)) + " free on the volume.";
            }
        }

        DiskSafetyState state = new(budgetBytes, usedBytes, reserveBytes, Math.Max(0, freeBytes), level, paused, explanation);

        bool changed;
        lock (_gate)
        {
            changed = _state.Level != state.Level ||
                      _state.PrefetchPaused != state.PrefetchPaused ||
                      _state.Explanation != state.Explanation;
            _state = state;
        }

        if (changed)
        {
            StateChanged?.Invoke(this, state);
        }

        return state;
    }

    /// <summary>Evaluates using a cache usage list (helper for the UI).</summary>
    public DiskSafetyState Evaluate(string? volumePath, CacheBudgetPlan plan, IEnumerable<CacheUsage> usages)
    {
        ArgumentNullException.ThrowIfNull(plan);
        long used = (usages ?? Array.Empty<CacheUsage>()).Sum(u => u.PhysicalBytes);
        return Evaluate(volumePath, plan.TotalBudgetBytes, used, plan.ProtectedFreeSpaceBytes);
    }
}
