namespace NimbusX.Mount;

/// <summary>Lifecycle of one mount.</summary>
public enum MountPhase
{
    /// <summary>Not mounted.</summary>
    Stopped = 0,

    /// <summary>rclone was started and NimbusX is waiting for the drive to appear and rc to answer.</summary>
    Starting = 1,

    /// <summary>Drive letter visible and rc answering.</summary>
    Ready = 2,

    /// <summary>Mounted but the health check is failing; a restart is pending or in progress.</summary>
    Degraded = 3,

    /// <summary>Unmount in progress (waiting for uploads and pending reads).</summary>
    Stopping = 4,

    /// <summary>Gave up after too many crashes; needs user action.</summary>
    Failed = 5
}
