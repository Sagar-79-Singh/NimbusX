using System;
using NimbusX.Core.Text;

namespace NimbusX.Cache;

/// <summary>One mount's cache folder, described for measuring and clearing.</summary>
public sealed class CacheDescriptor
{
    public CacheDescriptor(string accountId, string displayName, string cacheDirectory, bool isMounted)
    {
        AccountId = accountId ?? string.Empty;
        DisplayName = displayName ?? string.Empty;
        CacheDirectory = cacheDirectory ?? string.Empty;
        IsMounted = isMounted;
    }

    public string AccountId { get; }

    public string DisplayName { get; }

    public string CacheDirectory { get; }

    public bool IsMounted { get; }

    public override string ToString() => DisplayName + " -> " + CacheDirectory;
}

/// <summary>
/// Physical and logical size of one cache folder.
/// </summary>
/// <remarks>
/// rclone's VFS cache files are sparse: a 20 GB game file that has only 2 GB downloaded occupies about 2 GB on
/// disk while its logical length reads 20 GB. NimbusX reports both and always budgets against the physical size,
/// which is what Windows really needs.
/// </remarks>
public sealed class CacheUsage
{
    public CacheUsage(
        string accountId,
        string cacheDirectory,
        long logicalBytes,
        long physicalBytes,
        long fileCount,
        long cachedRangeBytes,
        bool markerPresent,
        long budgetShareBytes,
        DateTimeOffset measuredUtc)
    {
        AccountId = accountId;
        CacheDirectory = cacheDirectory;
        LogicalBytes = logicalBytes;
        PhysicalBytes = physicalBytes;
        FileCount = fileCount;
        CachedRangeBytes = cachedRangeBytes;
        MarkerPresent = markerPresent;
        BudgetShareBytes = budgetShareBytes;
        MeasuredUtc = measuredUtc;
    }

    public string AccountId { get; }

    public string CacheDirectory { get; }

    /// <summary>Sum of the sparse files' logical lengths.</summary>
    public long LogicalBytes { get; }

    /// <summary>Space actually allocated on disk (GetCompressedFileSizeW).</summary>
    public long PhysicalBytes { get; }

    public long FileCount { get; }

    /// <summary>Bytes rclone reports as present in its range metadata.</summary>
    public long CachedRangeBytes { get; }

    public bool MarkerPresent { get; }

    public long BudgetShareBytes { get; }

    public DateTimeOffset MeasuredUtc { get; }

    /// <summary>Physical / logical ratio (1.0 means no sparseness).</summary>
    public double Density => LogicalBytes <= 0 ? 0d : Math.Clamp((double)PhysicalBytes / LogicalBytes, 0d, 1d);

    public double BudgetUse => BudgetShareBytes <= 0 ? 0d : (double)PhysicalBytes / BudgetShareBytes;

    public string PhysicalText => ByteSize.Format(PhysicalBytes);

    public string LogicalText => ByteSize.Format(LogicalBytes);

    public string Summary =>
        PhysicalText + " on disk (" + LogicalText + " logical, " + (Density * 100d).ToString("0") + "% allocated)";

    public static CacheUsage Empty(string accountId, string cacheDirectory) =>
        new(accountId, cacheDirectory, 0, 0, 0, 0, false, 0, DateTimeOffset.UtcNow);
}
