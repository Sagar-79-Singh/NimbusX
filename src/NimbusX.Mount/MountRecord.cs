using System;
using System.Collections.Generic;
using NimbusX.Core.Security;

namespace NimbusX.Mount;

/// <summary>
/// What NimbusX persists about a mount so it can re-attach to a still running rclone after a restart.
/// </summary>
/// <remarks>
/// The rc password is not stored here: only the secret store key name is, and the value itself is DPAPI
/// protected (see <c>SecretKeys.RcPassword</c>).
/// </remarks>
public sealed class MountRecord
{
    public string AccountId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string ProviderId { get; set; } = string.Empty;

    public string RemoteName { get; set; } = string.Empty;

    public string RemotePath { get; set; } = string.Empty;

    public string DriveLetter { get; set; } = string.Empty;

    public string CacheDirectory { get; set; } = string.Empty;

    public string LogFilePath { get; set; } = string.Empty;

    public string ConfigPath { get; set; } = string.Empty;

    public int RcPort { get; set; }

    public string RcUser { get; set; } = string.Empty;

    public string RcPasswordSecretKey { get; set; } = string.Empty;

    public int ProcessId { get; set; }

    public DateTimeOffset ProcessStartUtc { get; set; }

    public DateTimeOffset StartedUtc { get; set; }

    public string Flavor { get; set; } = nameof(RcloneFlavor.Standard);

    public string? VolumeName { get; set; }

    public bool IsPool { get; set; }

    public List<string> PoolAccountIds { get; set; } = new();

    public long CacheBudgetBytes { get; set; }

    public long CacheMinFreeSpaceBytes { get; set; }

    public List<string> ExtraFlags { get; set; } = new();

    public RcloneFlavor FlavorValue =>
        Enum.TryParse(Flavor, ignoreCase: true, out RcloneFlavor flavor) ? flavor : RcloneFlavor.Standard;

    /// <summary>Builds a record from a live session.</summary>
    public static MountRecord FromSession(MountSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new MountRecord
        {
            AccountId = session.AccountId,
            DisplayName = session.Spec.DisplayName,
            ProviderId = session.Spec.ProviderId,
            RemoteName = session.Spec.RemoteName,
            RemotePath = session.Spec.RemotePath,
            DriveLetter = session.DriveLetter.Display,
            CacheDirectory = session.Spec.CacheDirectory,
            LogFilePath = session.Spec.LogFilePath,
            ConfigPath = session.Spec.ConfigPath,
            RcPort = session.Spec.RcPort,
            RcUser = session.Spec.RcUser,
            RcPasswordSecretKey = SecretKeys.RcPassword(session.AccountId),
            ProcessId = session.Process?.ProcessId ?? 0,
            ProcessStartUtc = session.Process?.ProcessStartUtc ?? default,
            StartedUtc = session.Process?.StartedUtc ?? DateTimeOffset.UtcNow,
            Flavor = session.Spec.Flavor.ToString(),
            VolumeName = session.Spec.VolumeName,
            IsPool = session.Spec.IsPool,
            PoolAccountIds = new List<string>(session.Spec.PoolAccountIds),
            CacheBudgetBytes = session.Spec.CacheBudgetBytes,
            CacheMinFreeSpaceBytes = session.Spec.CacheMinFreeSpaceBytes,
            ExtraFlags = new List<string>(session.Spec.ExtraFlags)
        };
    }
}
