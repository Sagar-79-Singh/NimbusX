using System.Collections.Generic;
using NimbusX.Core.Windows;

namespace NimbusX.Mount;

/// <summary>
/// Everything needed to start one rclone mount process. Built by <see cref="MountManager"/> from a
/// <see cref="MountRequest"/> plus allocated letter, rc credentials and cache folder.
/// </summary>
public sealed class MountSpec
{
    public MountSpec(
        string accountId,
        string displayName,
        string providerId,
        string remoteName,
        string remotePath,
        DriveLetter driveLetter,
        string cacheDirectory,
        string logFilePath,
        string configPath,
        int rcPort,
        string rcUser)
    {
        AccountId = accountId;
        DisplayName = displayName;
        ProviderId = providerId;
        RemoteName = remoteName;
        RemotePath = remotePath ?? string.Empty;
        DriveLetter = driveLetter;
        CacheDirectory = cacheDirectory;
        LogFilePath = logFilePath;
        ConfigPath = configPath;
        RcPort = rcPort;
        RcUser = rcUser;
    }

    public string AccountId { get; }

    public string DisplayName { get; }

    public string ProviderId { get; }

    public string RemoteName { get; }

    public string RemotePath { get; }

    public DriveLetter DriveLetter { get; }

    public string CacheDirectory { get; }

    public string LogFilePath { get; }

    public string ConfigPath { get; }

    public int RcPort { get; }

    public string RcUser { get; }

    public RcloneFlavor Flavor { get; init; } = RcloneFlavor.Standard;

    public string? VolumeName { get; init; }

    public long CacheBudgetBytes { get; init; }

    public long CacheMinFreeSpaceBytes { get; init; }

    public IReadOnlyList<string> ExtraFlags { get; init; } = new List<string>();

    public bool IsPool { get; init; }

    public IReadOnlyList<string> PoolAccountIds { get; init; } = new List<string>();

    /// <summary><c>nx_1a2b3c4d:</c> or <c>nx_1a2b3c4d:Games</c>.</summary>
    public string RemoteSpec => string.IsNullOrWhiteSpace(RemotePath) ? RemoteName + ":" : RemoteName + ":" + RemotePath;

    /// <summary>Mount point passed to rclone, e.g. <c>H:</c>.</summary>
    public string MountPoint => DriveLetter.Display;

    /// <summary>Copy of this spec with new rc credentials (used when a crashed mount is restarted).</summary>
    public MountSpec WithRc(int rcPort, string rcUser)
    {
        return new MountSpec(
            AccountId,
            DisplayName,
            ProviderId,
            RemoteName,
            RemotePath,
            DriveLetter,
            CacheDirectory,
            LogFilePath,
            ConfigPath,
            rcPort,
            rcUser)
        {
            Flavor = Flavor,
            VolumeName = VolumeName,
            CacheBudgetBytes = CacheBudgetBytes,
            CacheMinFreeSpaceBytes = CacheMinFreeSpaceBytes,
            ExtraFlags = ExtraFlags,
            IsPool = IsPool,
            PoolAccountIds = PoolAccountIds
        };
    }

    public override string ToString() => RemoteSpec + " -> " + MountPoint;
}
