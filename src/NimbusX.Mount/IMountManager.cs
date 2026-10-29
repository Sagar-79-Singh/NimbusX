using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NimbusX.Core.Windows;

namespace NimbusX.Mount;

/// <summary>The mount engine as the rest of the application sees it.</summary>
public interface IMountManager
{
    /// <summary>Raised whenever a mount changes phase, letter or error state.</summary>
    event EventHandler<MountSnapshot>? SessionChanged;

    /// <summary>Current mounts.</summary>
    IReadOnlyList<MountSnapshot> Snapshots { get; }

    /// <summary>Path of the encrypted rclone config.</summary>
    string ConfigPath { get; }

    /// <summary>Starts (or reuses) the mount for an account.</summary>
    Task<MountResult> MountAsync(MountRequest request, CancellationToken cancellationToken);

    /// <summary>Gracefully unmounts one account. Returns false when it was not mounted.</summary>
    Task<bool> UnmountAsync(string accountId, CancellationToken cancellationToken);

    /// <summary>Gracefully unmounts everything (app exit).</summary>
    Task UnmountAllAsync(CancellationToken cancellationToken);

    MountSnapshot? GetSnapshot(string accountId);

    RcloneRcClient? GetRcClient(string accountId);

    bool IsReady(string accountId);

    /// <summary>The 5 second health loop.</summary>
    Task<HealthCheckOutcome> HealthCheckAsync(CancellationToken cancellationToken);

    /// <summary>Adopts mounts that are still running from a previous app session.</summary>
    Task<ReattachOutcome> ReattachAsync(CancellationToken cancellationToken);

    /// <summary>Quota for a mounted account, from <c>operations/about</c>.</summary>
    Task<RemoteUsage?> GetQuotaAsync(string accountId, CancellationToken cancellationToken);

    /// <summary>Last lines of an account's rclone log.</summary>
    IReadOnlyList<string> GetLogTail(string accountId, int lines);

    /// <summary>Letters currently promised to mounts (plus the pool letter).</summary>
    IReadOnlyList<DriveLetter> ReservedLetters();

    Task ShutdownAsync(CancellationToken cancellationToken);
}
