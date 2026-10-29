using System;
using System.Diagnostics;

namespace NimbusX.Core.Processes;

/// <summary>Queries about other processes (used to re-attach to rclone mounts after an app restart).</summary>
public static class ProcessProbe
{
    /// <summary>True when a process with this id exists and has not exited.</summary>
    public static bool IsRunning(int? processId)
    {
        if (processId is null or <= 0)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(processId.Value);
            return !process.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Start time in UTC, or null when the process is gone.</summary>
    public static DateTimeOffset? TryGetStartTimeUtc(int? processId)
    {
        if (processId is null or <= 0)
        {
            return null;
        }

        try
        {
            using Process process = Process.GetProcessById(processId.Value);
            return process.HasExited ? null : new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Process name for a pid, or null.</summary>
    public static string? TryGetName(int? processId)
    {
        if (processId is null or <= 0)
        {
            return null;
        }

        try
        {
            using Process process = Process.GetProcessById(processId.Value);
            return process.HasExited ? null : process.ProcessName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the recorded pid is still the very same process (guards against pid reuse after a reboot).
    /// </summary>
    public static bool MatchesRecordedStart(int? processId, DateTimeOffset? recordedStartUtc)
    {
        if (recordedStartUtc is null)
        {
            return false;
        }

        DateTimeOffset? actual = TryGetStartTimeUtc(processId);
        if (actual is null)
        {
            return false;
        }

        return Math.Abs((actual.Value - recordedStartUtc.Value).TotalSeconds) <= 3d;
    }
}
