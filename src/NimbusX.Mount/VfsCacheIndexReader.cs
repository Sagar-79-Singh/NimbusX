using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using NimbusX.Core.Logging;

namespace NimbusX.Mount;

/// <summary>A half-open byte range [Pos, Pos+Size).</summary>
public readonly struct ByteRange : IEquatable<ByteRange>
{
    public ByteRange(long pos, long size)
    {
        Pos = Math.Max(0, pos);
        Size = Math.Max(0, size);
    }

    public long Pos { get; }

    public long Size { get; }

    public long End => Pos + Size;

    public bool IsEmpty => Size <= 0;

    public ByteRange Intersect(ByteRange other)
    {
        long start = Math.Max(Pos, other.Pos);
        long end = Math.Min(End, other.End);
        return end <= start ? new ByteRange(start, 0) : new ByteRange(start, end - start);
    }

    public bool Equals(ByteRange other) => Pos == other.Pos && Size == other.Size;

    public override bool Equals(object? obj) => obj is ByteRange other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Pos, Size);

    public override string ToString() => "[" + Pos + "+" + Size + ")";
}

/// <summary>One rclone vfsMeta file (the on-disk range index rclone keeps per cached file).</summary>
public sealed class VfsMetaFile
{
    public VfsMetaFile(string path, long size, IReadOnlyList<ByteRange> ranges, string? fingerprint, bool dirty)
    {
        Path = path;
        Size = size;
        Ranges = ranges;
        Fingerprint = fingerprint;
        Dirty = dirty;
    }

    public string Path { get; }

    /// <summary>Size of the remote object according to rclone.</summary>
    public long Size { get; }

    /// <summary>Byte ranges that are present in the local cache.</summary>
    public IReadOnlyList<ByteRange> Ranges { get; }

    public string? Fingerprint { get; }

    public bool Dirty { get; }

    public long CachedBytes => Ranges.Sum(r => r.Size);
}

/// <summary>How much of a required byte set is already on disk according to rclone's own metadata.</summary>
public sealed class CoverageReport
{
    public CoverageReport(string metaPath, long requestedBytes, long coveredBytes, IReadOnlyList<ByteRange> missing, bool metaFileFound)
    {
        MetaPath = metaPath;
        RequestedBytes = requestedBytes;
        CoveredBytes = coveredBytes;
        Missing = missing;
        MetaFileFound = metaFileFound;
    }

    public string MetaPath { get; }

    public long RequestedBytes { get; }

    public long CoveredBytes { get; }

    public IReadOnlyList<ByteRange> Missing { get; }

    public bool MetaFileFound { get; }

    /// <summary>0..1 fraction of the requested bytes that are cached.</summary>
    public double Coverage => RequestedBytes <= 0 ? 0d : Math.Clamp((double)CoveredBytes / RequestedBytes, 0d, 1d);

    public bool IsComplete => MetaFileFound && Missing.Count == 0;

    public string Describe() =>
        "cache coverage " + (Coverage * 100d).ToString("0.0") + "% (" + CoveredBytes + " / " + RequestedBytes + " bytes" +
        (MetaFileFound ? string.Empty : ", no metadata yet") + ")";
}

/// <summary>
/// Reads rclone's vfsMeta JSON range index so "Ready to launch" is based on what rclone really holds on disk,
/// not on what NimbusX believes it prefetched.
/// </summary>
/// <remarks>
/// rclone writes one JSON document per cached file containing <c>Size</c> and <c>Rs</c> (the present ranges).
/// NimbusX reads the same files, intersects them with the byte ranges the prefetcher promised, and reports the
/// exact gaps. If rclone changes its format the reader returns "no metadata" instead of inventing coverage.
/// </remarks>
public static class VfsCacheIndexReader
{
    public const long BlockSize = 1024L * 1024L;

    /// <summary>Reads one vfsMeta file, or null when it does not exist or cannot be parsed.</summary>
    public static VfsMetaFile? TryRead(string metaPath, IAppLogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(metaPath) || !File.Exists(metaPath))
        {
            return null;
        }

        try
        {
            string text = File.ReadAllText(metaPath);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            JsonNode? node = JsonNode.Parse(text);
            if (node is not JsonObject root)
            {
                return null;
            }

            long size = JsonLong(root, "Size") ?? 0;
            List<ByteRange> ranges = new();
            if (root.TryGetPropertyValue("Rs", out JsonNode? rs) && rs is JsonArray array)
            {
                foreach (JsonNode? item in array)
                {
                    if (item is not JsonObject range)
                    {
                        continue;
                    }

                    long pos = JsonLong(range, "Pos") ?? 0;
                    long length = JsonLong(range, "Size") ?? 0;
                    if (length > 0)
                    {
                        ranges.Add(new ByteRange(pos, length));
                    }
                }
            }

            string? fingerprint = null;
            if (root.TryGetPropertyValue("Fingerprint", out JsonNode? fp) && fp is JsonValue value &&
                value.TryGetValue(out string? fpText))
            {
                fingerprint = fpText;
            }

            bool dirty = root.TryGetPropertyValue("Dirty", out JsonNode? dirtyNode) && dirtyNode is JsonValue dirtyValue &&
                         dirtyValue.TryGetValue(out bool dirtyFlag) && dirtyFlag;

            return new VfsMetaFile(metaPath, size, Merge(ranges), fingerprint, dirty);
        }
        catch (Exception exception)
        {
            (logger ?? NullLogger.Instance).Debug("Could not read vfsMeta '" + metaPath + "': " + exception.Message, "Cache");
            return null;
        }
    }

    /// <summary>
    /// Verifies how much of <paramref name="required"/> is present for a file inside a mount.
    /// </summary>
    public static CoverageReport VerifyCoverage(
        string cacheDirectory,
        string remoteName,
        string remoteRoot,
        string relativePath,
        IEnumerable<ByteRange> required,
        IAppLogger? logger = null)
    {
        List<ByteRange> wanted = Merge(required ?? Array.Empty<ByteRange>());
        long requestedBytes = wanted.Sum(r => r.Size);
        string metaPath = MountCacheLayout.MetaPathFor(cacheDirectory, remoteName, remoteRoot, relativePath);
        VfsMetaFile? meta = TryRead(metaPath, logger);

        if (meta is null)
        {
            string? alternate = FindMetaFile(cacheDirectory, remoteName, remoteRoot, relativePath);
            if (alternate is not null)
            {
                meta = TryRead(alternate, logger);
                metaPath = alternate;
            }
        }

        if (meta is null)
        {
            return new CoverageReport(metaPath, requestedBytes, 0, wanted, metaFileFound: false);
        }

        long covered = 0;
        List<ByteRange> missing = new();
        foreach (ByteRange range in wanted)
        {
            long coveredInRange = 0;
            foreach (ByteRange present in meta.Ranges)
            {
                coveredInRange += present.Intersect(range).Size;
            }

            covered += Math.Min(coveredInRange, range.Size);
            if (coveredInRange < range.Size)
            {
                missing.AddRange(Subtract(range, meta.Ranges));
            }
        }

        return new CoverageReport(metaPath, requestedBytes, covered, Merge(missing), metaFileFound: true);
    }

    /// <summary>Finds a metadata file when the encoded on-disk name differs from the plain path.</summary>
    public static string? FindMetaFile(string cacheDirectory, string remoteName, string remoteRoot, string relativePath)
    {
        try
        {
            string root = Path.Combine(MountCacheLayout.VfsMetaDirectory(cacheDirectory), remoteName);
            if (!string.IsNullOrWhiteSpace(remoteRoot))
            {
                root = Path.Combine(root, remoteRoot.Replace('/', Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar));
            }

            if (!Directory.Exists(root))
            {
                return null;
            }

            string fileName = Path.GetFileName(relativePath);
            string directory = Path.Combine(root, Path.GetDirectoryName(relativePath)?.Replace('/', Path.DirectorySeparatorChar) ?? string.Empty);
            if (Directory.Exists(directory))
            {
                string exact = Path.Combine(directory, fileName);
                if (File.Exists(exact))
                {
                    return exact;
                }
            }

            return Directory
                .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Total cached bytes for a mount, from all metadata files.</summary>
    public static long MeasureCachedBytes(string cacheDirectory, string remoteName)
    {
        string metaRoot = Path.Combine(MountCacheLayout.VfsMetaDirectory(cacheDirectory), remoteName);
        if (!Directory.Exists(metaRoot))
        {
            return 0;
        }

        long total = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(metaRoot, "*", SearchOption.AllDirectories))
            {
                VfsMetaFile? meta = TryRead(file);
                if (meta is not null)
                {
                    total += meta.CachedBytes;
                }
            }
        }
        catch (Exception)
        {
            // ignore: a partially written metadata file is normal while rclone is running
        }

        return total;
    }

    /// <summary>Converts a block index into a byte range.</summary>
    public static ByteRange BlockRange(long blockIndex, long blockSize = BlockSize)
    {
        long pos = Math.Max(0, blockIndex) * blockSize;
        return new ByteRange(pos, blockSize);
    }

    /// <summary>Merges overlapping/adjacent ranges (stable, sorted by position).</summary>
    public static List<ByteRange> Merge(IEnumerable<ByteRange> ranges)
    {
        List<ByteRange> sorted = ranges.Where(r => !r.IsEmpty).OrderBy(r => r.Pos).ToList();
        List<ByteRange> merged = new();
        foreach (ByteRange range in sorted)
        {
            if (merged.Count == 0)
            {
                merged.Add(range);
                continue;
            }

            ByteRange last = merged[^1];
            if (range.Pos <= last.End)
            {
                long end = Math.Max(last.End, range.End);
                merged[^1] = new ByteRange(last.Pos, end - last.Pos);
            }
            else
            {
                merged.Add(range);
            }
        }

        return merged;
    }

    /// <summary>Ranges in <paramref name="range"/> that are not covered by <paramref name="present"/>.</summary>
    public static List<ByteRange> Subtract(ByteRange range, IEnumerable<ByteRange> present)
    {
        List<ByteRange> gaps = new();
        long cursor = range.Pos;
        foreach (ByteRange covered in Merge(present).Where(p => p.End > range.Pos && p.Pos < range.End))
        {
            if (covered.Pos > cursor)
            {
                gaps.Add(new ByteRange(cursor, covered.Pos - cursor));
            }

            cursor = Math.Max(cursor, covered.End);
            if (cursor >= range.End)
            {
                return gaps;
            }
        }

        if (cursor < range.End)
        {
            gaps.Add(new ByteRange(cursor, range.End - cursor));
        }

        return gaps;
    }

    private static long? JsonLong(JsonNode node, string name)
    {
        if (node is not JsonObject obj || !obj.TryGetPropertyValue(name, out JsonNode? value) || value is null)
        {
            return null;
        }

        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue(out long number))
            {
                return number;
            }

            if (jsonValue.TryGetValue(out double doubleNumber))
            {
                return (long)doubleNumber;
            }
        }

        return null;
    }
}
