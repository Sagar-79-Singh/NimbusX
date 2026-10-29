using System;
using System.Collections.Generic;
using System.Linq;
using NimbusX.Core;

namespace NimbusX.Cache;

/// <summary>The budget presets offered in the UI.</summary>
public static class CachePresets
{
    /// <summary>10, 20, 30 and 40 GB, the presets the app suggests.</summary>
    public static IReadOnlyList<int> Gigabytes { get; } = new[] { 10, 20, 30, 40 };

    /// <summary>Nearest preset for a budget, or 0 when the value is custom.</summary>
    public static int Match(long bytes)
    {
        foreach (int gb in Gigabytes)
        {
            if (Math.Abs(bytes - (long)gb * 1024L * 1024L * 1024L) < 1024L * 1024L * 512L)
            {
                return gb;
            }
        }

        return 0;
    }

    public static long ToBytes(int gigabytes) => (long)Math.Max(1, gigabytes) * 1024L * 1024L * 1024L;
}

/// <summary>
/// How the total cache budget is split across the mounted accounts.
/// </summary>
/// <remarks>
/// The total budget is a user promise ("use at most 20 GB"), not a suggestion: each mount gets an explicit share
/// that NimbusX passes to rclone as <c>--vfs-cache-max-size</c>. The plan also keeps
/// <see cref="ProtectedFreeSpaceBytes"/> untouched for Windows and allows a 10% overrun margin before the disk
/// safety guard pauses prefetching.
/// </remarks>
public sealed class CacheBudgetPlan
{
    public const double OverrunMargin = 0.10d;

    private readonly Dictionary<string, long> _shares;

    private CacheBudgetPlan(long totalBudgetBytes, long protectedFreeSpaceBytes, Dictionary<string, long> shares)
    {
        TotalBudgetBytes = totalBudgetBytes;
        ProtectedFreeSpaceBytes = protectedFreeSpaceBytes;
        _shares = shares;
    }

    /// <summary>Budget the user configured.</summary>
    public long TotalBudgetBytes { get; }

    /// <summary>Free space that must stay available for Windows.</summary>
    public long ProtectedFreeSpaceBytes { get; }

    /// <summary>Bytes NimbusX allows before it pauses new prefetches.</summary>
    public long PauseThresholdBytes => (long)(TotalBudgetBytes * (1d + OverrunMargin));

    public IReadOnlyDictionary<string, long> Shares => _shares;

    public int MountCount => _shares.Count;

    /// <summary>Share for one account (0 when it is not part of the plan).</summary>
    public long ShareFor(string accountId) =>
        _shares.TryGetValue(accountId ?? string.Empty, out long value) ? value : 0;

    /// <summary>Splits the budget evenly, honouring explicit per-account overrides.</summary>
    public static CacheBudgetPlan Create(
        long totalBudgetBytes,
        long protectedFreeSpaceBytes,
        IEnumerable<string> accountIds,
        IReadOnlyDictionary<string, long>? overrides = null)
    {
        List<string> ids = (accountIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        long budget = Math.Max(0, totalBudgetBytes);
        Dictionary<string, long> shares = new(StringComparer.OrdinalIgnoreCase);

        if (ids.Count == 0)
        {
            return new CacheBudgetPlan(budget, protectedFreeSpaceBytes, shares);
        }

        long overrideTotal = 0;
        int overrideCount = 0;
        if (overrides is not null)
        {
            foreach (string id in ids)
            {
                if (overrides.TryGetValue(id, out long value) && value > 0)
                {
                    overrideTotal += value;
                    overrideCount++;
                }
            }
        }

        long remaining = Math.Max(0, budget - overrideTotal);
        int unassigned = ids.Count - overrideCount;
        long baseShare = unassigned > 0 ? remaining / unassigned : 0;

        foreach (string id in ids.OrderBy(i => i, StringComparer.OrdinalIgnoreCase))
        {
            long share;
            if (overrides is not null && overrides.TryGetValue(id, out long explicitShare) && explicitShare > 0)
            {
                share = explicitShare;
            }
            else
            {
                share = baseShare;
            }

            shares[id] = Math.Max(MinimumShareBytes, share);
        }

        // Hand any rounding remainder to the first account so the shares add up to the budget.
        long assigned = shares.Values.Sum();
        if (assigned < budget && shares.Count > 0)
        {
            string first = shares.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).First();
            shares[first] += budget - assigned;
        }

        return new CacheBudgetPlan(budget, protectedFreeSpaceBytes, shares);
    }

    /// <summary>Smallest share NimbusX will hand to a mount (256 MiB).</summary>
    public const long MinimumShareBytes = 256L * 1024L * 1024L;

    /// <summary>
    /// Clamps a requested budget to what the volume can really give while keeping the Windows reserve free.
    /// </summary>
    public static long ClampToAvailable(long requestedBudgetBytes, long? freeBytes, long reserveBytes)
    {
        if (freeBytes is null)
        {
            return Math.Max(0, requestedBudgetBytes);
        }

        long available = Math.Max(0, freeBytes.Value - Math.Max(0, reserveBytes));
        if (requestedBudgetBytes <= 0)
        {
            return available;
        }

        return Math.Min(requestedBudgetBytes, available);
    }

    /// <summary>Human readable summary for the Smart Cache page.</summary>
    public string Describe()
    {
        if (MountCount == 0)
        {
            return "No drives mounted yet, so no cache shares are assigned.";
        }

        return Core.Text.ByteSize.Format(TotalBudgetBytes) + " split across " + MountCount +
               (MountCount == 1 ? " drive" : " drives") + ", keeping " +
               Core.Text.ByteSize.Format(ProtectedFreeSpaceBytes) + " free for Windows.";
    }
}
