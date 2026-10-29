using System;
using System.Collections.Generic;
using System.Linq;
using NimbusX.Core.Text;

namespace NimbusX.Cache;

/// <summary>
/// The snapshot the Smart Cache page draws: budget, real measured usage and disk pressure.
/// </summary>
public sealed class CacheStatus
{
    public CacheStatus(
        CacheBudgetPlan plan,
        DiskSafetyState safety,
        IReadOnlyList<CacheUsage> usages,
        IReadOnlyList<CacheDescriptor> mounts,
        string? cacheRoot,
        string description)
    {
        Plan = plan;
        Safety = safety;
        Usages = usages ?? Array.Empty<CacheUsage>();
        Mounts = mounts ?? Array.Empty<CacheDescriptor>();
        CacheRoot = cacheRoot;
        Description = description;
    }

    public CacheBudgetPlan Plan { get; }

    public DiskSafetyState Safety { get; }

    public IReadOnlyList<CacheUsage> Usages { get; }

    public IReadOnlyList<CacheDescriptor> Mounts { get; }

    public string? CacheRoot { get; }

    /// <summary>Honest one-line summary, or "Unavailable" when nothing has been measured.</summary>
    public string Description { get; }

    public long TotalPhysicalBytes => Usages.Sum(u => u.PhysicalBytes);

    public long TotalLogicalBytes => Usages.Sum(u => u.LogicalBytes);

    public double BudgetUse => Plan.TotalBudgetBytes <= 0 ? 0d : (double)TotalPhysicalBytes / Plan.TotalBudgetBytes;

    public bool PrefetchPaused => Safety.PrefetchPaused || TotalPhysicalBytes >= Plan.PauseThresholdBytes;

    public string BudgetText => Plan.TotalBudgetBytes <= 0 ? "Unavailable" : ByteSize.Format(Plan.TotalBudgetBytes);

    public string UsedText => Usages.Count == 0 ? "Unavailable" : ByteSize.Format(TotalPhysicalBytes);

    public string FreeText =>
        Safety.FreeBytes >= 0 ? ByteSize.Format(Safety.FreeBytes) : "Unavailable";

    public CacheUsage? ForAccount(string accountId) =>
        Usages.FirstOrDefault(u => string.Equals(u.AccountId, accountId, StringComparison.OrdinalIgnoreCase));

    public CacheDescriptor? MountFor(string accountId) =>
        Mounts.FirstOrDefault(m => string.Equals(m.AccountId, accountId, StringComparison.OrdinalIgnoreCase));

    public static CacheStatus Empty(string explanation)
    {
        CacheBudgetPlan plan = CacheBudgetPlan.Create(0, 0, Array.Empty<string>());
        return new CacheStatus(
            plan,
            DiskSafetyState.Unknown(explanation),
            Array.Empty<CacheUsage>(),
            Array.Empty<CacheDescriptor>(),
            null,
            explanation);
    }

    public override string ToString() => Description + " | " + Safety.Level;
}
