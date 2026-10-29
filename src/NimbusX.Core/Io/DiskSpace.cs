using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace NimbusX.Core.Io;

/// <summary>
/// Disk space and sparse-file helpers.
/// </summary>
/// <remarks>
/// rclone's VFS cache files are sparse on NTFS, so the logical file size is much larger than the space the
/// file actually occupies. NimbusX reports both and always budgets against the physical size, obtained with
/// <c>GetCompressedFileSizeW</c> (which reports allocated size for sparse files).
/// </remarks>
public static class DiskSpace
{
    /// <summary>Free bytes on the volume that contains <paramref name="path"/>, or null.</summary>
    public static long? GetFreeBytes(string? path)
    {
        try
        {
            string root = VolumeRootFor(path);
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            DriveInfo info = new(root);
            return info.IsReady ? info.AvailableFreeSpace : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Total bytes on the volume that contains <paramref name="path"/>, or null.</summary>
    public static long? GetTotalBytes(string? path)
    {
        try
        {
            string root = VolumeRootFor(path);
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            DriveInfo info = new(root);
            return info.IsReady ? info.TotalSize : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Volume root (e.g. <c>C:\</c>) for a path, or null.</summary>
    public static string? VolumeRootFor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            string full = Path.GetFullPath(path!);
            string? root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root))
            {
                return root;
            }

            // A path that does not exist yet: fall back to its first segment.
            int index = full.IndexOf(Path.DirectorySeparatorChar, 3);
            return index > 0 ? full[..(index + 1)] : full;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Enumerates fixed drives with their free space, largest free first.</summary>
    public static IReadOnlyList<LocalVolume> GetVolumes()
    {
        List<LocalVolume> volumes = new();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception)
        {
            return volumes;
        }

        foreach (DriveInfo drive in drives)
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }

                volumes.Add(new LocalVolume(
                    drive.Name,
                    drive.DriveFormat ?? string.Empty,
                    drive.TotalSize,
                    drive.AvailableFreeSpace,
                    drive.VolumeLabel ?? string.Empty));
            }
            catch (Exception)
            {
                // ignore unreadable volumes
            }
        }

        volumes.Sort((a, b) => b.FreeBytes.CompareTo(a.FreeBytes));
        return volumes;
    }

    /// <summary>Physical (allocated) size of a file. Falls back to the logical length when unavailable.</summary>
    public static long GetPhysicalSize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return 0;
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                uint low = GetCompressedFileSizeW(path, out uint high);
                if (low == 0xFFFFFFFFu)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != 0)
                    {
                        return new FileInfo(path).Length;
                    }
                }

                return ((long)high << 32) + low;
            }
            catch (Exception)
            {
                return new FileInfo(path).Length;
            }
        }

        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Sums logical and physical size of every file under a folder (no decompression, no hashing).</summary>
    public static (long Logical, long Physical, long FileCount) MeasureTree(string? directory)
    {
        long logical = 0;
        long physical = 0;
        long files = 0;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return (0, 0, 0);
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(directory!, "*", SearchOption.AllDirectories))
            {
                try
                {
                    FileInfo info = new(file);
                    logical += info.Length;
                    physical += GetPhysicalSize(file);
                    files++;
                }
                catch (Exception)
                {
                    // file vanished or is locked
                }
            }
        }
        catch (Exception)
        {
            // permission issues on a mount
        }

        return (logical, physical, files);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetCompressedFileSizeW")]
    private static extern uint GetCompressedFileSizeW(string fileName, out uint fileSizeHigh);
}

/// <summary>A local fixed volume and its capacity.</summary>
public sealed class LocalVolume
{
    public LocalVolume(string root, string fileSystem, long totalBytes, long freeBytes, string label)
    {
        Root = root;
        FileSystem = fileSystem;
        TotalBytes = totalBytes;
        FreeBytes = freeBytes;
        Label = label;
    }

    public string Root { get; }

    public string FileSystem { get; }

    public long TotalBytes { get; }

    public long FreeBytes { get; }

    public string Label { get; }

    public bool IsNtfs => string.Equals(FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Root + " (" + Text.ByteSize.Format(FreeBytes) + " free)";
}
