using System;
using System.Collections.Generic;
using NimbusX.Core;
using NimbusX.Core.Windows;

namespace NimbusX.Mount;

/// <summary>What the caller asks NimbusX to mount. Secrets are never part of this object.</summary>
public sealed class MountRequest
{
    public MountRequest(string accountId, string displayName, string providerId, string remoteName)
    {
        AccountId = Guard.NotNullOrWhiteSpace(accountId, nameof(accountId));
        DisplayName = Guard.NotNullOrWhiteSpace(displayName, nameof(displayName));
        ProviderId = Guard.NotNullOrWhiteSpace(providerId, nameof(providerId));
        RemoteName = Guard.NotNullOrWhiteSpace(remoteName, nameof(remoteName));
    }

    public string AccountId { get; }

    public string DisplayName { get; }

    public string ProviderId { get; }

    /// <summary>rclone remote name, e.g. <c>nx_1a2b3c4d</c> (letters, digits and underscore only).</summary>
    public string RemoteName { get; }

    /// <summary>Optional sub folder inside the remote, e.g. <c>Games</c>.</summary>
    public string RemotePath { get; init; } = string.Empty;

    /// <summary>Letter the user picked; NimbusX falls back to the next free one.</summary>
    public char? PreferredDriveLetter { get; init; }

    public RcloneFlavor Flavor { get; init; } = RcloneFlavor.Standard;

    /// <summary>Explorer volume label; defaults to the account display name.</summary>
    public string? VolumeName { get; init; }

    /// <summary>This mount's share of the total cache budget.</summary>
    public long CacheBudgetBytes { get; init; }

    /// <summary>Free space rclone must leave on the cache volume.</summary>
    public long CacheMinFreeSpaceBytes { get; init; }

    /// <summary>Extra flags validated by <see cref="RcloneFlagCatalog"/> before use.</summary>
    public IReadOnlyList<string> ExtraFlags { get; init; } = Array.Empty<string>();

    /// <summary>True for the synthetic union mount that merges several accounts.</summary>
    public bool IsPool { get; init; }

    /// <summary>Accounts merged by a pool mount (empty for normal mounts).</summary>
    public IReadOnlyList<string> PoolAccountIds { get; init; } = Array.Empty<string>();

    /// <summary>Force a specific letter (pools) instead of letting the allocator pick.</summary>
    public DriveLetter? FixedDriveLetter { get; init; }

    public override string ToString() =>
        RemoteName + (string.IsNullOrWhiteSpace(RemotePath) ? ":" : ":" + RemotePath) + " -> " + (PreferredDriveLetter?.ToString() ?? "auto");
}
