using System;
using System.Collections.Generic;
using System.Linq;
using NimbusX.Diagnostics;

namespace NimbusX.App;

/// <summary>One step of the startup sequence.</summary>
public sealed class StartupStep
{
    public StartupStep(string name, bool success, string detail, string? fix = null, TimeSpan duration = default)
    {
        Name = name;
        Success = success;
        Detail = detail;
        Fix = fix;
        Duration = duration;
    }

    public string Name { get; }

    public bool Success { get; }

    public string Detail { get; }

    public string? Fix { get; }

    public TimeSpan Duration { get; }

    public override string ToString() => (Success ? "ok   " : "FAIL ") + Name + ": " + Detail;
}

/// <summary>
/// What happened while NimbusX started.
/// </summary>
/// <remarks>
/// Starting is allowed to partially fail: a missing WinFsp stops mounting but must not stop the app, so the UI can
/// still show the fix. Each failed step keeps the real error and the action that resolves it.
/// </remarks>
public sealed class StartupReport
{
    private readonly List<StartupStep> _steps = new();

    public IReadOnlyList<StartupStep> Steps => _steps;

    public bool ReattachAttempted { get; set; }

    public int ReattachedCount { get; set; }

    public int AutoMountedCount { get; set; }

    public IReadOnlyList<string> AutoMountFailures { get; set; } = Array.Empty<string>();

    public bool AnyFailed => _steps.Any(s => !s.Success);

    public int FailedCount => _steps.Count(s => !s.Success);

    public bool CanMount
    {
        get
        {
            StartupStep? winfsp = _steps.FirstOrDefault(s => s.Name.Contains("WinFsp", StringComparison.OrdinalIgnoreCase));
            StartupStep? rclone = _steps.FirstOrDefault(s => s.Name.Contains("rclone", StringComparison.OrdinalIgnoreCase));
            StartupStep? cache = _steps.FirstOrDefault(s => s.Name.Contains("Cache", StringComparison.OrdinalIgnoreCase));
            return (winfsp?.Success ?? false) && (rclone?.Success ?? false) && (cache?.Success ?? false);
        }
    }

    public void Add(string name, bool success, string detail, string? fix = null, TimeSpan duration = default) =>
        _steps.Add(new StartupStep(name, success, detail, fix, duration));

    public string Headline =>
        _steps.Count == 0
            ? "Not started yet."
            : CanMount
                ? "NimbusX is ready." + (AnyFailed ? " " + FailedCount + " optional check(s) failed." : string.Empty)
                : "NimbusX cannot mount drives yet; see the failing checks below.";

    public override string ToString() => string.Join(Environment.NewLine, _steps.Select(s => s.ToString()));
}
