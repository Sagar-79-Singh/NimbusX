using System;
using System.IO;
using System.Text;
using NimbusX.Core;
using NimbusX.Core.Io;
using NimbusX.Core.Json;
using NimbusX.Core.Logging;
using NimbusX.Core.Windows;

namespace NimbusX.Mount;

/// <summary>Contents of a <c>.nimbusx-cache</c> marker file.</summary>
public sealed class CacheMarkerContent
{
    public string App { get; set; } = AppInfo.Name;

    public int Schema { get; set; } = 1;

    public string AccountId { get; set; } = string.Empty;

    public string Drive { get; set; } = string.Empty;

    public string CacheRoot { get; set; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// The on-disk cache layout for one mount: <c>&lt;cacheRoot&gt;\m-&lt;driveId&gt;\{vfs,vfsMeta}</c> plus a marker file.
/// </summary>
/// <remarks>
/// Each mount owns a separate cache folder so two accounts can never collide, and every folder carries a
/// <c>.nimbusx-cache</c> marker. Cache clearing only ever deletes inside a folder that has a valid marker and
/// that is still inside the configured cache root, which is what makes "clear cache" safe.
/// </remarks>
public static class MountCacheLayout
{
    public const string FolderPrefix = "m-";
    public const string VfsFolderName = "vfs";
    public const string VfsMetaFolderName = "vfsMeta";

    /// <summary><c>&lt;cacheRoot&gt;\m-H</c> for drive H:.</summary>
    public static string CacheDirectoryFor(string cacheRoot, DriveLetter driveLetter)
    {
        Guard.NotNullOrWhiteSpace(cacheRoot, nameof(cacheRoot));
        if (!driveLetter.IsValid)
        {
            throw new ArgumentException("A valid drive letter is required.", nameof(driveLetter));
        }

        return Path.Combine(cacheRoot, FolderPrefix + driveLetter.Letter);
    }

    /// <summary>Folder rclone uses for cached file data (<c>--cache-dir</c> root is the parent).</summary>
    public static string VfsDirectory(string cacheDirectory) => Path.Combine(cacheDirectory, VfsFolderName);

    /// <summary>Folder rclone uses for cached range metadata.</summary>
    public static string VfsMetaDirectory(string cacheDirectory) => Path.Combine(cacheDirectory, VfsMetaFolderName);

    /// <summary>Path of the marker file for a cache folder.</summary>
    public static string MarkerPath(string cacheDirectory) => Path.Combine(cacheDirectory, AppInfo.CacheMarkerName);

    /// <summary>True when the folder carries a NimbusX cache marker.</summary>
    public static bool HasMarker(string cacheDirectory)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(cacheDirectory) && File.Exists(MarkerPath(cacheDirectory));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Reads the marker, or null when it is missing or unreadable.</summary>
    public static CacheMarkerContent? ReadMarker(string cacheDirectory)
    {
        try
        {
            return AtomicFile.ReadJson<CacheMarkerContent>(MarkerPath(cacheDirectory));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Creates the cache folder and writes/refreshes the marker.</summary>
    public static CacheMarkerContent EnsureCacheDirectory(
        string cacheRoot,
        DriveLetter driveLetter,
        string accountId,
        IAppLogger? logger = null)
    {
        string directory = CacheDirectoryFor(cacheRoot, driveLetter);
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(VfsDirectory(directory));
        Directory.CreateDirectory(VfsMetaDirectory(directory));

        CacheMarkerContent existing = ReadMarker(directory) ?? new CacheMarkerContent
        {
            AccountId = accountId,
            Drive = driveLetter.Display,
            CacheRoot = cacheRoot,
            CreatedUtc = DateTimeOffset.UtcNow
        };

        AtomicFile.WriteJson(MarkerPath(directory), existing);
        (logger ?? NullLogger.Instance).Debug("Cache folder ready: " + directory, "Cache");
        return existing;
    }

    /// <summary>Updates the marker after a mount moves to a different letter.</summary>
    public static void UpdateMarker(string cacheDirectory, string accountId, DriveLetter driveLetter, string cacheRoot)
    {
        CacheMarkerContent content = ReadMarker(cacheDirectory) ?? new CacheMarkerContent();
        content.AccountId = accountId;
        content.Drive = driveLetter.Display;
        content.CacheRoot = cacheRoot;
        AtomicFile.WriteJson(MarkerPath(cacheDirectory), content);
    }

    /// <summary>
    /// Path of the rclone vfsMeta JSON file for a file inside a mount.
    /// </summary>
    /// <remarks>
    /// rclone stores range metadata under <c>&lt;cache-dir&gt;\vfsMeta\&lt;remoteName&gt;\&lt;pathInRemote&gt;</c>.
    /// </remarks>
    public static string MetaPathFor(string cacheDirectory, string remoteName, string remoteRoot, string relativePath)
    {
        string root = Path.Combine(VfsMetaDirectory(cacheDirectory), remoteName);
        if (!string.IsNullOrWhiteSpace(remoteRoot))
        {
            string normalizedRoot = remoteRoot.Replace('/', Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar);
            if (normalizedRoot.Length > 0)
            {
                root = Path.Combine(root, normalizedRoot);
            }
        }

        string normalizedRelative = relativePath.Replace('/', Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar);
        return Path.Combine(root, normalizedRelative);
    }

    /// <summary>Describes the layout for diagnostics.</summary>
    public static string Describe(string cacheRoot, DriveLetter driveLetter)
    {
        string directory = CacheDirectoryFor(cacheRoot, driveLetter);
        StringBuilder builder = new();
        builder.AppendLine("Cache folder: " + directory);
        builder.AppendLine("Marker: " + (HasMarker(directory) ? "present" : "missing"));
        builder.AppendLine("vfs: " + VfsDirectory(directory));
        builder.AppendLine("vfsMeta: " + VfsMetaDirectory(directory));
        return builder.ToString();
    }
}
