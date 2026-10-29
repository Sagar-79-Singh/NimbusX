using System;
using System.Collections.Generic;
using System.Linq;
using NimbusX.Core;
using NimbusX.Core.Io;
using NimbusX.Core.Logging;
using NimbusX.Core.Text;

namespace NimbusX.Cache;

/// <summary>A file the user wants to keep cached (for example a level that loads slowly).</summary>
public sealed class PinnedEntry
{
    public string AccountId { get; set; } = string.Empty;

    /// <summary>rclone remote name of the mount the file belongs to.</summary>
    public string RemoteName { get; set; } = string.Empty;

    /// <summary>Path inside the mount, using forward slashes.</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>Size of the file when it was pinned (0 when unknown).</summary>
    public long Bytes { get; set; }

    public DateTimeOffset PinnedUtc { get; set; } = DateTimeOffset.UtcNow;

    public string Key => (AccountId + "|" + RelativePath).ToLowerInvariant();

    public override string ToString() => RelativePath + " (" + ByteSize.Format(Bytes) + ")";
}

/// <summary>
/// Remembers pinned files so a manual cache clear never throws away something the user asked to keep.
/// </summary>
/// <remarks>
/// rclone has no "pin" flag for its VFS cache, so NimbusX protects pinned files from its own clearing and warms
/// them again with the prefetcher after rclone evicts them. That is what the Smart Cache page reports as "pinned".
/// </remarks>
public sealed class PinnedPathStore
{
    private readonly string _path;
    private readonly IAppLogger _logger;
    private readonly object _gate = new();
    private List<PinnedEntry> _entries;

    public PinnedPathStore(string pinsFilePath, IAppLogger? logger = null)
    {
        _path = Guard.NotNullOrWhiteSpace(pinsFilePath, nameof(pinsFilePath));
        _logger = logger ?? NullLogger.Instance;
        _entries = AtomicFile.ReadJson<List<PinnedEntry>>(_path) ?? new List<PinnedEntry>();
    }

    public IReadOnlyList<PinnedEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public IReadOnlyList<PinnedEntry> ForAccount(string accountId)
    {
        lock (_gate)
        {
            return _entries
                .Where(e => string.Equals(e.AccountId, accountId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
    }

    public bool IsPinned(string accountId, string relativePath)
    {
        string key = (accountId + "|" + NormalizeRelative(relativePath)).ToLowerInvariant();
        lock (_gate)
        {
            return _entries.Any(e => e.Key == key);
        }
    }

    public void Pin(string accountId, string remoteName, string relativePath, long bytes)
    {
        string normalized = NormalizeRelative(relativePath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("A file path is required.", nameof(relativePath));
        }

        lock (_gate)
        {
            _entries.RemoveAll(e =>
                string.Equals(e.AccountId, accountId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.RelativePath, normalized, StringComparison.OrdinalIgnoreCase));

            _entries.Add(new PinnedEntry
            {
                AccountId = accountId,
                RemoteName = remoteName,
                RelativePath = normalized,
                Bytes = Math.Max(0, bytes),
                PinnedUtc = DateTimeOffset.UtcNow
            });
        }

        Save();
    }

    public bool Unpin(string accountId, string relativePath)
    {
        string normalized = NormalizeRelative(relativePath);
        bool removed;
        lock (_gate)
        {
            removed = _entries.RemoveAll(e =>
                string.Equals(e.AccountId, accountId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.RelativePath, normalized, StringComparison.OrdinalIgnoreCase)) > 0;
        }

        if (removed)
        {
            Save();
        }

        return removed;
    }

    public long TotalPinnedBytes(string accountId) => ForAccount(accountId).Sum(e => e.Bytes);

    /// <summary>Normalises separators so "Games\x.bin" and "Games/x.bin" are the same pin.</summary>
    public static string NormalizeRelative(string? relativePath) =>
        string.IsNullOrWhiteSpace(relativePath)
            ? string.Empty
            : relativePath!.Replace('\\', '/').Trim('/');

    private void Save()
    {
        List<PinnedEntry> snapshot;
        lock (_gate)
        {
            snapshot = _entries.ToList();
        }

        try
        {
            AtomicFile.WriteJson(_path, snapshot);
        }
        catch (Exception exception)
        {
            _logger.Error("Could not save cache pins.", "Cache", exception);
        }
    }
}
