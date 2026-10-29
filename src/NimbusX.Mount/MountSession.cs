using System;
using NimbusX.Core.Text;
using NimbusX.Core.Windows;

namespace NimbusX.Mount;

/// <summary>Mutable runtime state of one mount, owned by <see cref="MountManager"/>.</summary>
public sealed class MountSession
{
    internal MountSession(MountSpec spec)
    {
        Spec = spec ?? throw new ArgumentNullException(nameof(spec));
        CreatedUtc = DateTimeOffset.UtcNow;
    }

    public MountSpec Spec { get; internal set; }

    public string AccountId => Spec.AccountId;

    public MountPhase Phase { get; internal set; } = MountPhase.Stopped;

    public RcloneRcClient? Rc { get; internal set; }

    public RcloneMountProcess? Process { get; internal set; }

    public DateTimeOffset CreatedUtc { get; }

    public DateTimeOffset? ReadySinceUtc { get; internal set; }

    public DateTimeOffset? LastHealthCheckUtc { get; internal set; }

    public string? LastError { get; internal set; }

    public int RestartCount { get; internal set; }

    /// <summary>True when this session was adopted from a still-running rclone after an app restart.</summary>
    public bool Reattached { get; internal set; }

    public DriveLetter DriveLetter => Spec.DriveLetter;

    public string CacheDirectory => Spec.CacheDirectory;

    public string LogFilePath => Spec.LogFilePath;

    public bool IsPool => Spec.IsPool;

    public MountSnapshot ToSnapshot() => new(this);

    public override string ToString() => Spec.DisplayName + " " + Spec.DriveLetter.Display + " " + Phase;
}

/// <summary>Immutable view of a mount for the UI and diagnostics.</summary>
public sealed class MountSnapshot
{
    public MountSnapshot(MountSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        AccountId = session.AccountId;
        DisplayName = session.Spec.DisplayName;
        ProviderId = session.Spec.ProviderId;
        RemoteName = session.Spec.RemoteName;
        RemoteSpec = session.Spec.RemoteSpec;
        DriveLetter = session.DriveLetter.Display;
        DriveRoot = session.DriveLetter.Root;
        CacheDirectory = session.Spec.CacheDirectory;
        Phase = session.Phase;
        IsPool = session.Spec.IsPool;
        VolumeName = session.Spec.VolumeName;
        RcPort = session.Spec.RcPort;
        StartedUtc = session.Process?.StartedUtc;
        ReadySinceUtc = session.ReadySinceUtc;
        ProcessId = session.Process?.ProcessId;
        LastError = session.LastError ?? session.Process?.LastError;
        RestartCount = session.RestartCount;
        LogFilePath = session.Spec.LogFilePath;
        Reattached = session.Reattached;
        IsExperimental = session.Spec.ProviderId.Contains("terabox", StringComparison.OrdinalIgnoreCase);
    }

    public string AccountId { get; }

    public string DisplayName { get; }

    public string ProviderId { get; }

    public string RemoteName { get; }

    public string RemoteSpec { get; }

    public string DriveLetter { get; }

    public string DriveRoot { get; }

    public string CacheDirectory { get; }

    public MountPhase Phase { get; }

    public bool IsPool { get; }

    public bool IsExperimental { get; }

    public string? VolumeName { get; }

    public int RcPort { get; }

    public DateTimeOffset? StartedUtc { get; }

    public DateTimeOffset? ReadySinceUtc { get; }

    public int? ProcessId { get; }

    public string? LastError { get; }

    public int RestartCount { get; }

    public string LogFilePath { get; }

    public bool Reattached { get; }

    public bool IsReady => Phase == MountPhase.Ready;

    public string PhaseText => Phase switch
    {
        MountPhase.Ready => "Mounted",
        MountPhase.Starting => "Starting",
        MountPhase.Degraded => "Unstable",
        MountPhase.Stopping => "Unmounting",
        MountPhase.Failed => "Failed",
        _ => "Not mounted"
    };

    public string UptimeText
    {
        get
        {
            if (ReadySinceUtc is null)
            {
                return "\u2014";
            }

            return TextFormat.Duration(DateTimeOffset.UtcNow - ReadySinceUtc.Value);
        }
    }
}
