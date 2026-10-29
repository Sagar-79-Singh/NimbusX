using System;

namespace NimbusX.Core.Processes;

/// <summary>Outcome of a finished child process.</summary>
public sealed class ProcessResult
{
    public ProcessResult(int exitCode, string standardOutput, string standardError, bool timedOut, TimeSpan duration)
    {
        ExitCode = exitCode;
        StandardOutput = standardOutput ?? string.Empty;
        StandardError = standardError ?? string.Empty;
        TimedOut = timedOut;
        Duration = duration;
    }

    public int ExitCode { get; }

    public string StandardOutput { get; }

    public string StandardError { get; }

    public bool TimedOut { get; }

    public TimeSpan Duration { get; }

    public bool Succeeded => ExitCode == 0 && !TimedOut;

    /// <summary>Combined output, useful for parsing rclone's help and version text.</summary>
    public string CombinedOutput =>
        string.IsNullOrEmpty(StandardError) ? StandardOutput : StandardOutput + Environment.NewLine + StandardError;

    public override string ToString() =>
        "exit=" + ExitCode + (TimedOut ? " (timed out)" : string.Empty) + " in " + (int)Duration.TotalMilliseconds + "ms";
}
