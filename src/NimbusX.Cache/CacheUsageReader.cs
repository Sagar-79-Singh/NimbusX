using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NimbusX.Core.Io;
using NimbusX.Core.Logging;
using NimbusX.Core.Text;
using NimbusX.Mount;

namespace NimbusX.Cache;

/// <summary>
/// Measures how much each mount's cache really uses on disk.
/// </summary>
/// <remarks>
/// Everything here is a measurement, never an estimate dressed up as a measurement. When a folder cannot be read
/// the reader reports <see cref="CacheUsage.Empty"/> with a zero budget share and the UI shows "Unavailable"
/// instead of a number that would be a guess.
/// </remarks>
public sealed class CacheUsageReader
{
    private readonly IAppLogger _logger;

    public CacheUsageReader(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Measures one mount cache folder.</summary>
    public CacheUsage Measure(CacheDescriptor descriptor, CacheBudgetPlan? plan = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        long budgetShare = plan?.ShareFor(descriptor.AccountId) ?? 0;
        if (string.IsNullOrWhiteSpace(descriptor.CacheDirectory))
        {
            return CacheUsage.Empty(descriptor.AccountId, string.Empty);
        }

        bool marker = MountCacheLayout.HasMarker(descriptor.CacheDirectory);
        if (!Directory.Exists(descriptor.CacheDirectory))
        {
            return new CacheUsage(
                descriptor.AccountId,
                descriptor.CacheDirectory,
                0,
                0,
                0,
                0,
                marker,
                budgetShare,
                DateTimeOffset.UtcNow);
        }

        (long logical, long physical, long fileCount) = DiskSpace.MeasureTree(descriptor.CacheDirectory);

        long cachedRangeBytes = 0;
        try
        {
            // rclone keys its vfs folder by remote name; when we cannot find the remote we simply report 0 ranges
            // rather than inventing coverage.
            string remoteName = RemoteNameHint(descriptor);
            if (!string.IsNullOrWhiteSpace(remoteName))
            {
                cachedRangeBytes = VfsCacheIndexReader.MeasureCachedBytes(descriptor.CacheDirectory, remoteName);
            }
        }
        catch (Exception exception)
        {
            _logger.Debug("Could not read vfs metadata for '" + descriptor.CacheDirectory + "': " + exception.Message, "Cache");
        }

        return new CacheUsage(
            descriptor.AccountId,
            descriptor.CacheDirectory,
            logical,
            physical,
            fileCount,
            cachedRangeBytes,
            marker,
            budgetShare,
            DateTimeOffset.UtcNow);
    }

    /// <summary>Measures every mount and returns them in the same order.</summary>
    public IReadOnlyList<CacheUsage> MeasureAll(IEnumerable<CacheDescriptor> descriptors, CacheBudgetPlan? plan = null)
    {
        List<CacheUsage> usages = new();
        foreach (CacheDescriptor descriptor in descriptors ?? Array.Empty<CacheDescriptor>())
        {
            usages.Add(Measure(descriptor, plan));
        }

        return usages;
    }

    /// <summary>Total physical bytes across all measured mounts.</summary>
    public long TotalPhysicalBytes(IEnumerable<CacheUsage> usages) =>
        (usages ?? Array.Empty<CacheUsage>()).Sum(u => u.PhysicalBytes);

    /// <summary>Whether the measured usage is over the pause threshold of the plan.</summary>
    public bool IsOverPauseThreshold(CacheBudgetPlan plan, IEnumerable<CacheUsage> usages)
    {
        ArgumentNullException.ThrowIfNull(plan);
        long total = TotalPhysicalBytes(usages);
        return total >= plan.PauseThresholdBytes;
    }

    /// <summary>
    /// Human readable line for the Smart Cache page: real numbers, or "Unavailable" when nothing was measured.
    /// </summary>
    public string Describe(IReadOnlyList<CacheUsage> usages, CacheBudgetPlan? plan)
    {
        if (usages is null || usages.Count == 0)
        {
            return "Unavailable";
        }

        long physical = usages.Sum(u => u.PhysicalBytes);
        long logical = usages.Sum(u => u.LogicalBytes);
        string text = ByteSize.Format(physical) + " on disk across " + usages.Count + " mount(s)";
        if (logical > 0 && logical != physical)
        {
            text += " (" + ByteSize.Format(logical) + " logical)";
        }

        if (plan is not null && plan.TotalBudgetBytes > 0)
        {
            text += " of " + ByteSize.Format(plan.TotalBudgetBytes) + " budget";
        }

        return text;
    }

    /// <summary>
    /// rclone's cache folder name is the remote name. NimbusX uses <c>nx_</c> remote names, and a display name is
    /// never a valid fallback, so only a name that looks like a remote is returned.
    /// </summary>
    private static string RemoteNameHint(CacheDescriptor descriptor)
    {
        string vfs = MountCacheLayout.VfsDirectory(descriptor.CacheDirectory);
        if (!Directory.Exists(vfs))
        {
            return string.Empty;
        }

        try
        {
            string[] directories = Directory.GetDirectories(vfs);
            return directories.Length == 1 ? Path.GetFileName(directories[0]) : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
