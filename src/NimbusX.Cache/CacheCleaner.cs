using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NimbusX.Core;
using NimbusX.Core.Io;
using NimbusX.Core.Logging;
using NimbusX.Core.Text;
using NimbusX.Mount;

namespace NimbusX.Cache;

/// <summary>Outcome of clearing one or more cache folders.</summary>
public sealed class CacheClearResult
{
    public CacheClearResult(
        bool success,
        long bytesFreed,
        int filesDeleted,
        IReadOnlyList<string> skipped,
        string? error)
    {
        Success = success;
        BytesFreed = bytesFreed;
        FilesDeleted = filesDeleted;
        Skipped = skipped ?? Array.Empty<string>();
        Error = error;
    }

    public bool Success { get; }

    public long BytesFreed { get; }

    public int FilesDeleted { get; }

    /// <summary>Files that were protected (pinned, reparse point, or outside the marker folder).</summary>
    public IReadOnlyList<string> Skipped { get; }

    public string? Error { get; }

    public static CacheClearResult Fail(string error, IReadOnlyList<string>? skipped = null) =>
        new(false, 0, 0, skipped ?? Array.Empty<string>(), error);

    public static CacheClearResult Ok(long bytesFreed, int filesDeleted, IReadOnlyList<string> skipped) =>
        new(true, bytesFreed, filesDeleted, skipped, null);

    public string Describe() =>
        Success
            ? "Freed " + ByteSize.Format(BytesFreed) + " from " + FilesDeleted + " cache file(s)."
            : "Cache was not cleared: " + Error;
}

/// <summary>
/// Deletes cache data, but only inside a marked NimbusX cache folder that is still inside the configured cache
/// root and is not itself on a mounted cloud drive.
/// </summary>
/// <remarks>
/// <para>
/// This is the most dangerous code in the app, so it is deliberately paranoid: without a valid
/// <c>.nimbusx-cache</c> marker whose recorded root matches the configured root, nothing is deleted. Reparse
/// points (junctions/symlinks) are skipped rather than followed, so a link can never redirect a delete. Pinned
/// files are skipped unless the caller explicitly asks for them.
/// </para>
/// <para>
/// The cleaner never touches the cloud: it only ever removes files under the local cache folder, and rclone
/// re-downloads anything that is missing. Save games are never stored in the cache root, and a cache folder that
/// lives on a mounted drive is refused outright.
/// </para>
/// </remarks>
public sealed class CacheCleaner
{
    private readonly IAppLogger _logger;
    private readonly Func<string, long> _physicalSizeProvider;

    public CacheCleaner(IAppLogger? logger = null, Func<string, long>? physicalSizeProvider = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _physicalSizeProvider = physicalSizeProvider ?? DiskSpace.GetPhysicalSize;
    }

    /// <summary>Bytes that a clear would free (pinned files excluded).</summary>
    public long MeasureClearable(
        CacheDescriptor descriptor,
        string? cacheRoot,
        IReadOnlyCollection<string>? mountedDriveRoots,
        PinnedPathStore? pins)
    {
        ClearPlan plan = BuildPlan(descriptor, cacheRoot, mountedDriveRoots, pins, includePinned: false);
        return plan.Files.Sum(f => f.Bytes);
    }

    /// <summary>Deletes the cache data of one mount.</summary>
    public CacheClearResult ClearMount(
        CacheDescriptor descriptor,
        string? cacheRoot,
        IReadOnlyCollection<string>? mountedDriveRoots,
        PinnedPathStore? pins,
        bool includePinned = false)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        ClearPlan plan = BuildPlan(descriptor, cacheRoot, mountedDriveRoots, pins, includePinned);
        if (plan.Error is not null)
        {
            _logger.Warn("Refused to clear cache for " + descriptor.DisplayName + ": " + plan.Error, "Cache");
            return CacheClearResult.Fail(plan.Error, plan.Skipped);
        }

        long freed = 0;
        int deleted = 0;
        foreach (CacheFile file in plan.Files)
        {
            try
            {
                File.Delete(file.Path);
                freed += file.Bytes;
                deleted++;
            }
            catch (Exception exception)
            {
                _logger.Debug("Could not delete cache file '" + file.Path + "': " + exception.Message, "Cache");
            }
        }

        DeleteEmptyDirectories(plan.Folder!, plan.PinnedPaths);

        _logger.Info(
            "Cleared " + ByteSize.Format(freed) + " from the " + descriptor.DisplayName + " cache (" + deleted + " file(s)).",
            "Cache");

        return CacheClearResult.Ok(freed, deleted, plan.Skipped);
    }

    /// <summary>Deletes the cache data of several mounts.</summary>
    public CacheClearResult ClearAll(
        IEnumerable<CacheDescriptor> descriptors,
        string? cacheRoot,
        IReadOnlyCollection<string>? mountedDriveRoots,
        PinnedPathStore? pins,
        bool includePinned = false)
    {
        long freed = 0;
        int deleted = 0;
        List<string> skipped = new();
        bool anySuccess = false;
        string? lastError = null;

        foreach (CacheDescriptor descriptor in descriptors ?? Array.Empty<CacheDescriptor>())
        {
            CacheClearResult result = ClearMount(descriptor, cacheRoot, mountedDriveRoots, pins, includePinned);
            if (result.Success)
            {
                anySuccess = true;
                freed += result.BytesFreed;
                deleted += result.FilesDeleted;
            }
            else
            {
                lastError = result.Error;
            }

            skipped.AddRange(result.Skipped);
        }

        return anySuccess || lastError is null
            ? CacheClearResult.Ok(freed, deleted, skipped)
            : CacheClearResult.Fail(lastError, skipped);
    }

    private ClearPlan BuildPlan(
        CacheDescriptor descriptor,
        string? cacheRoot,
        IReadOnlyCollection<string>? mountedDriveRoots,
        PinnedPathStore? pins,
        bool includePinned)
    {
        List<string> skipped = new();
        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            return ClearPlan.Failed("No cache root is configured.");
        }

        string folder = descriptor.CacheDirectory;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return ClearPlan.Failed("The cache folder does not exist (nothing to clear).");
        }

        if (!PathGuard.IsUnder(folder, cacheRoot))
        {
            return ClearPlan.Failed("The cache folder is outside the configured cache root.");
        }

        if (PathGuard.IsSamePath(folder, cacheRoot))
        {
            return ClearPlan.Failed("Refusing to clear the cache root itself.");
        }

        if (PathGuard.IsDriveRoot(folder))
        {
            return ClearPlan.Failed("Refusing to clear a drive root.");
        }

        foreach (string mountedRoot in mountedDriveRoots ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(mountedRoot) && PathGuard.IsUnder(folder, mountedRoot))
            {
                return ClearPlan.Failed(
                    "The cache folder is on a mounted cloud drive (" + mountedRoot + "). Move the cache to a local disk first.");
            }
        }

        if (!MountCacheLayout.HasMarker(folder))
        {
            return ClearPlan.Failed("The folder has no NimbusX cache marker, so it was not created by NimbusX.");
        }

        CacheMarkerContent? marker = MountCacheLayout.ReadMarker(folder);
        if (marker is null)
        {
            return ClearPlan.Failed("The cache marker could not be read.");
        }

        if (!string.IsNullOrWhiteSpace(marker.CacheRoot) && !PathGuard.IsSamePath(marker.CacheRoot, cacheRoot!))
        {
            return ClearPlan.Failed("The folder belongs to a different cache root (" + marker.CacheRoot + ").");
        }

        if (!string.IsNullOrWhiteSpace(marker.AccountId) &&
            !string.Equals(marker.AccountId, descriptor.AccountId, StringComparison.OrdinalIgnoreCase))
        {
            return ClearPlan.Failed("The folder belongs to a different account.");
        }

        if (descriptor.IsMounted)
        {
            return ClearPlan.Failed("Unmount the drive before clearing its cache.");
        }

        List<string> pinnedPaths = new();
        if (pins is not null)
        {
            foreach (PinnedEntry entry in pins.ForAccount(descriptor.AccountId))
            {
                string pinnedPath = Path.Combine(
                    MountCacheLayout.VfsDirectory(folder),
                    entry.RemoteName,
                    PinnedPathStore.NormalizeRelative(entry.RelativePath).Replace('/', Path.DirectorySeparatorChar));
                pinnedPaths.Add(Path.GetFullPath(pinnedPath));
            }
        }

        string markerPath = Path.GetFullPath(MountCacheLayout.MarkerPath(folder));
        List<CacheFile> files = new();

        foreach (string file in EnumerateFilesSafely(folder, skipped))
        {
            string full = Path.GetFullPath(file);
            if (string.Equals(full, markerPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!PathGuard.IsUnder(full, folder))
            {
                skipped.Add(full + " (outside the cache folder)");
                continue;
            }

            bool isPinned = pinnedPaths.Any(p => PathGuard.IsUnder(full, p) || PathGuard.IsSamePath(full, p));
            if (isPinned && !includePinned)
            {
                skipped.Add(full + " (pinned)");
                continue;
            }

            long bytes = 0;
            try
            {
                bytes = _physicalSizeProvider(full);
            }
            catch (Exception)
            {
                // size unknown; deletion still counts files
            }

            files.Add(new CacheFile(full, bytes));
        }

        return new ClearPlan(folder, files, skipped, pinnedPaths, null);
    }

    private static IEnumerable<string> EnumerateFilesSafely(string folder, List<string> skipped)
    {
        Stack<string> pending = new();
        pending.Push(folder);

        while (pending.Count > 0)
        {
            string current = pending.Pop();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(current);
            }
            catch (Exception)
            {
                skipped.Add(current + " (could not be read)");
                continue;
            }

            foreach (string entry in entries)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception)
                {
                    skipped.Add(entry + " (could not be inspected)");
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    skipped.Add(entry + " (reparse point, not followed)");
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                yield return entry;
            }
        }
    }

    private static void DeleteEmptyDirectories(string root, IReadOnlyList<string> protectedPaths)
    {
        List<string> directories = new();
        try
        {
            directories.AddRange(Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories));
        }
        catch (Exception)
        {
            return;
        }

        directories.Sort((a, b) => b.Length.CompareTo(a.Length));
        foreach (string directory in directories)
        {
            if (protectedPaths.Any(p => PathGuard.IsUnder(p, directory)))
            {
                continue;
            }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch (Exception)
            {
                // keep going
            }
        }
    }

    private sealed class CacheFile
    {
        public CacheFile(string path, long bytes)
        {
            Path = path;
            Bytes = bytes;
        }

        public string Path { get; }

        public long Bytes { get; }
    }

    private sealed class ClearPlan
    {
        public ClearPlan(string? folder, List<CacheFile> files, List<string> skipped, List<string> pinnedPaths, string? error)
        {
            Folder = folder;
            Files = files;
            Skipped = skipped;
            PinnedPaths = pinnedPaths;
            Error = error;
        }

        public string? Folder { get; }

        public List<CacheFile> Files { get; }

        public List<string> Skipped { get; }

        public List<string> PinnedPaths { get; }

        public string? Error { get; }

        public static ClearPlan Failed(string error) =>
            new(null, new List<CacheFile>(), new List<string>(), new List<string>(), error);
    }
}
