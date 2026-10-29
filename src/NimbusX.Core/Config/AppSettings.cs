using System.Collections.Generic;

namespace NimbusX.Core.Config;

/// <summary>
/// User settings. Persisted as JSON; contains no secrets (those live in DPAPI protected storage).
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public bool FirstRunCompleted { get; set; }

    /// <summary>Root folder that holds every per-mount cache folder.</summary>
    public string? CacheRoot { get; set; }

    /// <summary>Total VFS cache budget in bytes, split across mounts.</summary>
    public long CacheBudgetBytes { get; set; } = 20L * 1024 * 1024 * 1024;

    /// <summary>Selected preset (10/20/30/40 GB) or 0 when the user chose a custom budget.</summary>
    public int CacheBudgetPresetGb { get; set; } = 20;

    public bool CacheBudgetIsCustom { get; set; }

    /// <summary>Free space NimbusX must leave on the cache volume for Windows.</summary>
    public long ProtectedFreeSpaceBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>Measured (not guessed) download speed, Mbps. Zero means "not measured".</summary>
    public double MeasuredDownloadMbps { get; set; }

    public double MeasuredUploadMbps { get; set; }

    /// <summary>When true the selected accounts are merged into one pooled letter with rclone union.</summary>
    public bool PoolModeEnabled { get; set; }

    public List<string> PoolAccountIds { get; set; } = new();

    public string? PoolDriveLetter { get; set; } = "P";

    /// <summary>First letter NimbusX tries for a new mount.</summary>
    public string? PreferredFirstDriveLetter { get; set; } = "G";

    public bool UseCustomOAuthClientId { get; set; }

    public bool StartWithWindows { get; set; }

    public bool MinimizeToTray { get; set; } = true;

    public bool EnablePrediction { get; set; } = true;

    public bool EnableGameAwareMode { get; set; }

    public bool EnableTelemetryParsing { get; set; } = true;

    public int HealthCheckSeconds { get; set; } = 5;

    public int MaxRestartsPerTenMinutes { get; set; } = 5;

    public int UnmountUploadGraceSeconds { get; set; } = 60;

    public string LogMinimumLevel { get; set; } = "Information";

    public long PrefetchSessionBudgetBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>How long a launch must be watched before a game can be marked verified (seconds).</summary>
    public int VerifyLaunchSeconds { get; set; } = 60;

    public bool DeveloperModeConfirmed { get; set; }

    public string? LastSelectedPage { get; set; } = "Dashboard";

    public bool ShowExperimentalTeraBox { get; set; } = true;

    /// <summary>Free-form note the user can attach to a diagnostics export.</summary>
    public string? DiagnosticsNote { get; set; }

    public List<string> DismissedWarnings { get; set; } = new();

    /// <summary>Per-account cache share overrides (account id to bytes). Empty means "split evenly".</summary>
    public Dictionary<string, long> CacheShareOverride { get; set; } = new();

    /// <summary>True when a first run wizard still has to run.</summary>
    public bool NeedsWizard => !FirstRunCompleted || string.IsNullOrWhiteSpace(CacheRoot);
}
