using System.Collections.Generic;

namespace NimbusX.Mount;

/// <summary>Outcome of a mount request, including the real error and a suggested fix.</summary>
public sealed class MountResult
{
    private MountResult(bool success, MountSnapshot? snapshot, string? error, string? fix, IReadOnlyList<string> logTail)
    {
        Success = success;
        Snapshot = snapshot;
        Error = error;
        Fix = fix;
        LogTail = logTail;
    }

    public bool Success { get; }

    public MountSnapshot? Snapshot { get; }

    /// <summary>rclone's own error text (redacted), never a generic "failed".</summary>
    public string? Error { get; }

    /// <summary>What the user can do next.</summary>
    public string? Fix { get; }

    /// <summary>Last rclone log/stderr lines, shown behind "details".</summary>
    public IReadOnlyList<string> LogTail { get; }

    public static MountResult Ok(MountSnapshot snapshot) => new(true, snapshot, null, null, new List<string>());

    public static MountResult Fail(string error, string? fix = null, IReadOnlyList<string>? logTail = null) =>
        new(false, null, error, fix, logTail ?? new List<string>());
}

/// <summary>Summary of one health check pass.</summary>
public sealed class HealthCheckOutcome
{
    public HealthCheckOutcome(int checkedCount, int restartedCount, int failedCount, bool resumeDetected)
    {
        CheckedCount = checkedCount;
        RestartedCount = restartedCount;
        FailedCount = failedCount;
        ResumeDetected = resumeDetected;
    }

    public int CheckedCount { get; }

    public int RestartedCount { get; }

    public int FailedCount { get; }

    /// <summary>True when the gap between checks suggests the machine was asleep.</summary>
    public bool ResumeDetected { get; }

    public override string ToString() =>
        "checked=" + CheckedCount + " restarted=" + RestartedCount + " failed=" + FailedCount + (ResumeDetected ? " resume" : string.Empty);
}

/// <summary>Summary of a re-attach pass after an app restart.</summary>
public sealed class ReattachOutcome
{
    public ReattachOutcome(int adoptedCount, int droppedCount, IReadOnlyList<string> notes)
    {
        AdoptedCount = adoptedCount;
        DroppedCount = droppedCount;
        Notes = notes;
    }

    public int AdoptedCount { get; }

    public int DroppedCount { get; }

    public IReadOnlyList<string> Notes { get; }
}
