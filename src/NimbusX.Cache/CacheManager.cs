using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NimbusX.Core;
using NimbusX.Core.Config;
using NimbusX.Core.Io;
using NimbusX.Core.Logging;
using NimbusX.Core.Text;
using NimbusX.Mount;

namespace NimbusX.Cache;

/// <summary>
/// Owns the cache budget: it converts the user's preset into a per-mount <c>--vfs-cache-max-size</c>, measures what
/// the cache really uses, pauses prefetching when the disk gets tight, and clears data only inside marked folders.
/// </summary>
/// <remarks>
/// Every number this class shows comes from a measurement of the cache folder (sparse-aware physical size) or from
/// rclone's own range metadata. When a folder cannot be read the status says "Unavailable" instead of guessing.
/// </remarks>
public sealed class CacheManager : ICacheManager, IDisposable
{
    private readonly SettingsService _settings;
    private readonly IMountManager _mounts;
    private readonly IAppLogger _logger;
    private readonly PinnedPathStore _pins;
    private readonly CacheCleaner _cleaner;
    private readonly CacheUsageReader _reader;
    private readonly DiskSafetyGuard _guard;
    private readonly object _gate = new();
    private CacheStatus _status;
    private bool _disposed;

    public CacheManager(
        SettingsService settings,
        IMountManager mounts,
        IAppLogger logger,
        PinnedPathStore pins,
        CacheCleaner? cleaner = null,
        CacheUsageReader? reader = null,
        DiskSafetyGuard? guard = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _mounts = mounts ?? throw new ArgumentNullException(nameof(mounts));
        _logger = logger ?? NullLogger.Instance;
        _pins = pins ?? throw new ArgumentNullException(nameof(pins));
        _cleaner = cleaner ?? new CacheCleaner(_logger);
        _reader = reader ?? new CacheUsageReader(_logger);
        _guard = guard ?? new DiskSafetyGuard();

        _guard.StateChanged += OnGuardStateChanged;
        _status = CacheStatus.Empty("Cache has not been measured yet.");
    }

    public event EventHandler<CacheStatus>? StatusChanged;

    public CacheStatus Current
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public AppSettings Settings => _settings.Current;

    public CacheBudgetPlan CreatePlan(IEnumerable<string> accountIds)
    {
        AppSettings settings = _settings.Current;
        long budget = Math.Max(0, settings.CacheBudgetBytes);
        long reserve = Math.Max(0, settings.ProtectedFreeSpaceBytes);
        IReadOnlyDictionary<string, long>? overrides =
            settings.CacheShareOverride.Count > 0 ? settings.CacheShareOverride : null;

        return CacheBudgetPlan.Create(budget, reserve, accountIds, overrides);
    }

    public long BudgetFor(string accountId, IEnumerable<string>? allAccountIds = null)
    {
        Guard.NotNullOrWhiteSpace(accountId, nameof(accountId));

        List<string> ids = allAccountIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                           ?? _mounts.Snapshots.Select(s => s.AccountId).ToList();

        if (!ids.Contains(accountId, StringComparer.OrdinalIgnoreCase))
        {
            ids.Add(accountId);
        }

        CacheBudgetPlan plan = CreatePlan(ids);
        long share = plan.ShareFor(accountId);

        // rclone can only parse whole units, and a share below the minimum is not useful, so the plan's floor wins.
        return share > 0 ? share : CacheBudgetPlan.MinimumShareBytes;
    }

    public CacheStatus Refresh()
    {
        AppSettings settings = _settings.Current;
        string? cacheRoot = settings.CacheRoot;

        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            return Publish(CacheStatus.Empty("No cache folder is configured yet. Choose one in Settings -> Cache."));
        }

        if (!Directory.Exists(cacheRoot))
        {
            return Publish(CacheStatus.Empty("The cache folder '" + cacheRoot + "' does not exist. Pick another folder in Settings -> Cache."));
        }

        IReadOnlyList<MountSnapshot> snapshots = _mounts.Snapshots;
        List<CacheDescriptor> descriptors = snapshots
            .Where(s => !string.IsNullOrWhiteSpace(s.CacheDirectory))
            .Select(s => new CacheDescriptor(s.AccountId, s.DisplayName, s.CacheDirectory, IsMountedPhase(s.Phase)))
            .ToList();

        CacheBudgetPlan plan = CreatePlan(descriptors.Select(d => d.AccountId));
        IReadOnlyList<CacheUsage> usages = _reader.MeasureAll(descriptors, plan);

        string volume = DiskSpace.VolumeRootFor(cacheRoot) ?? cacheRoot!;
        DiskSafetyState safety = _guard.Evaluate(volume, plan, usages);

        string description = _reader.Describe(usages, plan);
        if (usages.Count == 0)
        {
            description = "No drives are mounted, so there is no cache data to measure.";
        }

        return Publish(new CacheStatus(plan, safety, usages, descriptors, cacheRoot, description));
    }

    public CacheUsage Measure(string accountId)
    {
        Guard.NotNullOrWhiteSpace(accountId, nameof(accountId));
        MountSnapshot? snapshot = _mounts.GetSnapshot(accountId);
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.CacheDirectory))
        {
            return CacheUsage.Empty(accountId, string.Empty);
        }

        CacheBudgetPlan plan = CreatePlan(_mounts.Snapshots.Select(s => s.AccountId));
        CacheDescriptor descriptor = new(
            snapshot.AccountId,
            snapshot.DisplayName,
            snapshot.CacheDirectory,
            IsMountedPhase(snapshot.Phase));

        return _reader.Measure(descriptor, plan);
    }

    public bool SetBudgetPreset(int gigabytes)
    {
        if (!CachePresets.Gigabytes.Contains(gigabytes))
        {
            _logger.Warn("Refused cache preset " + gigabytes + " GB; only 10, 20, 30 and 40 GB are supported.", "Cache");
            return false;
        }

        long bytes = CachePresets.ToBytes(gigabytes);
        long clamped = ClampBudgetToFreeSpace(bytes);

        _settings.Update(settings =>
        {
            settings.CacheBudgetBytes = clamped;
            settings.CacheBudgetPresetGb = clamped == bytes ? gigabytes : 0;
            settings.CacheBudgetIsCustom = clamped != bytes;
        });

        if (clamped != bytes)
        {
            _logger.Warn(
                "The " + gigabytes + " GB cache does not fit on the cache volume; NimbusX set " +
                ByteSize.Format(clamped) + " instead.",
                "Cache");
        }

        Refresh();
        return true;
    }

    public bool SetCustomBudget(long bytes)
    {
        if (bytes <= 0)
        {
            _logger.Warn("Refused a cache budget of " + bytes + " bytes.", "Cache");
            return false;
        }

        _settings.Update(settings =>
        {
            settings.CacheBudgetBytes = bytes;
            settings.CacheBudgetPresetGb = 0;
            settings.CacheBudgetIsCustom = true;
        });

        Refresh();
        return true;
    }

    public void SetShareOverride(string accountId, long bytes)
    {
        Guard.NotNullOrWhiteSpace(accountId, nameof(accountId));

        _settings.Update(settings =>
        {
            if (bytes <= 0)
            {
                settings.CacheShareOverride.Remove(accountId);
            }
            else
            {
                settings.CacheShareOverride[accountId] = bytes;
            }
        });

        Refresh();
    }

    public long ClampBudgetToFreeSpace(long requestedBytes)
    {
        string? cacheRoot = _settings.Current.CacheRoot;
        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            return Math.Max(0, requestedBytes);
        }

        string volume = DiskSpace.VolumeRootFor(cacheRoot) ?? cacheRoot!;
        long? free = DiskSpace.GetFreeBytes(volume);
        return CacheBudgetPlan.ClampToAvailable(requestedBytes, free, _settings.Current.ProtectedFreeSpaceBytes);
    }

    public CacheClearResult ClearAccount(string accountId, bool includePinned = false)
    {
        Guard.NotNullOrWhiteSpace(accountId, nameof(accountId));

        MountSnapshot? snapshot = _mounts.GetSnapshot(accountId);
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.CacheDirectory))
        {
            return CacheClearResult.Fail(
                "No cache folder is known for this account.",
                null);
        }

        CacheDescriptor descriptor = new(
            snapshot.AccountId,
            snapshot.DisplayName,
            snapshot.CacheDirectory,
            IsMountedPhase(snapshot.Phase));

        CacheClearResult result = _cleaner.ClearMount(
            descriptor,
            _settings.Current.CacheRoot,
            MountedDriveRoots(),
            _pins,
            includePinned);

        Refresh();
        return result;
    }

    public CacheClearResult ClearEverything(bool includePinned = false)
    {
        List<CacheDescriptor> descriptors = _mounts.Snapshots
            .Where(s => !string.IsNullOrWhiteSpace(s.CacheDirectory))
            .Select(s => new CacheDescriptor(s.AccountId, s.DisplayName, s.CacheDirectory, IsMountedPhase(s.Phase)))
            .ToList();

        if (descriptors.Count == 0)
        {
            return CacheClearResult.Fail("No drives are mounted, so there is no cache folder to clear.");
        }

        CacheClearResult result = _cleaner.ClearAll(
            descriptors,
            _settings.Current.CacheRoot,
            MountedDriveRoots(),
            _pins,
            includePinned);

        Refresh();
        return result;
    }

    public long MeasureClearable()
    {
        string? cacheRoot = _settings.Current.CacheRoot;
        IReadOnlyList<string> mountedRoots = MountedDriveRoots();

        long total = 0;
        foreach (MountSnapshot snapshot in _mounts.Snapshots)
        {
            if (string.IsNullOrWhiteSpace(snapshot.CacheDirectory))
            {
                continue;
            }

            CacheDescriptor descriptor = new(
                snapshot.AccountId,
                snapshot.DisplayName,
                snapshot.CacheDirectory,
                IsMountedPhase(snapshot.Phase));

            total += _cleaner.MeasureClearable(descriptor, cacheRoot, mountedRoots, _pins);
        }

        return total;
    }

    public IReadOnlyList<PinnedEntry> Pins(string accountId) => _pins.ForAccount(accountId);

    public PinnedEntry? PinPath(string accountId, string relativePath)
    {
        Guard.NotNullOrWhiteSpace(accountId, nameof(accountId));

        string normalized = PinnedPathStore.NormalizeRelative(relativePath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("A file path inside the mounted drive is required.", nameof(relativePath));
        }

        MountSnapshot? snapshot = _mounts.GetSnapshot(accountId);
        if (snapshot is null)
        {
            _logger.Warn("Cannot pin '" + normalized + "': that drive is not mounted.", "Cache");
            return null;
        }

        string localPath = Path.Combine(snapshot.DriveRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
        long bytes = 0;
        try
        {
            FileInfo info = new(localPath);
            if (info.Exists)
            {
                bytes = info.Length;
            }
            else
            {
                _logger.Warn("Pinned path '" + normalized + "' does not exist on " + snapshot.DriveLetter + " yet; it will be pin-warmed after it is read once.", "Cache");
            }
        }
        catch (Exception exception)
        {
            _logger.Warn("Could not read the size of '" + normalized + "': " + exception.Message, "Cache");
        }

        _pins.Pin(snapshot.AccountId, snapshot.RemoteName, normalized, bytes);
        return _pins.ForAccount(snapshot.AccountId)
            .FirstOrDefault(p => string.Equals(p.RelativePath, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public bool UnpinPath(string accountId, string relativePath)
    {
        Guard.NotNullOrWhiteSpace(accountId, nameof(accountId));
        return _pins.Unpin(accountId, relativePath);
    }

    /// <summary>Pauses prefetching when the disk guard says so (game reads are never blocked by this).</summary>
    public bool CanPrefetch => !Current.PrefetchPaused;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _guard.StateChanged -= OnGuardStateChanged;
    }

    private static bool IsMountedPhase(MountPhase phase) =>
        phase is MountPhase.Ready or MountPhase.Starting or MountPhase.Degraded;

    private IReadOnlyList<string> MountedDriveRoots() =>
        _mounts.Snapshots
            .Select(s => s.DriveRoot)
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private CacheStatus Publish(CacheStatus status)
    {
        bool changed;
        lock (_gate)
        {
            changed = _status is null ||
                      _status.Description != status.Description ||
                      _status.TotalPhysicalBytes != status.TotalPhysicalBytes ||
                      _status.Safety.Level != status.Safety.Level ||
                      _status.Usages.Count != status.Usages.Count;
            _status = status;
        }

        if (changed)
        {
            try
            {
                StatusChanged?.Invoke(this, status);
            }
            catch (Exception exception)
            {
                _logger.Error("A cache status subscriber threw.", "Cache", exception);
            }
        }

        return status;
    }

    private void OnGuardStateChanged(object? sender, DiskSafetyState state)
    {
        if (state.PrefetchPaused)
        {
            _logger.Warn("Prefetch paused: " + state.Explanation, "Cache");
        }
    }
}
