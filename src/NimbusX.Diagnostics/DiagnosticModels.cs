using System;
using System.Collections.Generic;
using System.Linq;
using NimbusX.Core.Text;

namespace NimbusX.Diagnostics;

/// <summary>How serious a finding is.</summary>
public enum DiagnosticSeverity
{
    Info = 0,
    Warning,
    Error
}

/// <summary>One environment check with a real message and a concrete fix.</summary>
public sealed class DiagnosticCheck
{
    public DiagnosticCheck(string area, string title, DiagnosticSeverity severity, string detail, string? fix = null)
    {
        Area = area;
        Title = title;
        Severity = severity;
        Detail = detail;
        Fix = fix;
    }

    public string Area { get; }

    public string Title { get; }

    public DiagnosticSeverity Severity { get; }

    public string Detail { get; }

    /// <summary>What the user can do about it; null when there is nothing to do.</summary>
    public string? Fix { get; }

    public bool Ok => Severity == DiagnosticSeverity.Info;

    public override string ToString() => "[" + Severity + "] " + Area + ": " + Title + " - " + Detail;
}

/// <summary>Result of one external dependency check.</summary>
public sealed class DependencyReport
{
    public DependencyReport(string name, string expected, string? installed, bool usable, string detail, string source)
    {
        Name = name;
        Expected = expected;
        Installed = installed;
        Usable = usable;
        Detail = detail;
        Source = source;
    }

    public string Name { get; }

    public string Expected { get; }

    public string? Installed { get; }

    public bool Usable { get; }

    public string Detail { get; }

    /// <summary>Where the binary/installer came from, with its pinned digest.</summary>
    public string Source { get; }

    public string StatusText => Usable ? "Ready" : "Not ready";

    public override string ToString() => Name + ": " + (Installed ?? "not found") + " (" + StatusText + ")";
}

/// <summary>Everything the Diagnostics page and the export bundle show.</summary>
public sealed class DiagnosticsSnapshot
{
    public DiagnosticsSnapshot()
    {
        GeneratedUtc = DateTimeOffset.UtcNow;
    }

    public DateTimeOffset GeneratedUtc { get; set; }

    public string AppVersion { get; set; } = string.Empty;

    public string AppDataFolder { get; set; } = string.Empty;

    public string OperatingSystem { get; set; } = string.Empty;

    public string Architecture { get; set; } = string.Empty;

    public string DotNetVersion { get; set; } = string.Empty;

    public int ProcessorCount { get; set; }

    public long TotalMemoryBytes { get; set; }

    public string CacheRoot { get; set; } = string.Empty;

    public long CacheBudgetBytes { get; set; }

    public long? CacheVolumeFreeBytes { get; set; }

    public string ConfigPath { get; set; } = string.Empty;

    public bool ConfigEncrypted { get; set; }

    public bool ConfigKeyPresent { get; set; }

    public int AccountCount { get; set; }

    public int MountedCount { get; set; }

    public List<DependencyReport> Dependencies { get; set; } = new();

    public List<DiagnosticCheck> Checks { get; set; } = new();

    public List<string> MountSummaries { get; set; } = new();

    public List<string> AccountSummaries { get; set; } = new();

    public string CacheSummary { get; set; } = "Unavailable";

    public int ErrorCount => Checks.Count(c => c.Severity == DiagnosticSeverity.Error);

    public int WarningCount => Checks.Count(c => c.Severity == DiagnosticSeverity.Warning);

    public bool Healthy => ErrorCount == 0;

    public string Headline =>
        Healthy
            ? (WarningCount == 0 ? "Everything NimbusX needs is ready." : WarningCount + " warning(s) need attention.")
            : ErrorCount + " problem(s) stop NimbusX from working.";

    public string MemoryText => TotalMemoryBytes <= 0 ? "Unavailable" : ByteSize.Format(TotalMemoryBytes);

    public void AddCheck(string area, string title, DiagnosticSeverity severity, string detail, string? fix = null) =>
        Checks.Add(new DiagnosticCheck(area, title, severity, detail, fix));

    public override string ToString() => Headline;
}

/// <summary>One exported diagnostics bundle.</summary>
public sealed class ExportResult
{
    public ExportResult(string path, long bytes, IReadOnlyList<string> files, IReadOnlyList<string> warnings)
    {
        Path = path;
        Bytes = bytes;
        Files = files ?? Array.Empty<string>();
        Warnings = warnings ?? Array.Empty<string>();
    }

    public string Path { get; }

    public long Bytes { get; }

    public IReadOnlyList<string> Files { get; }

    /// <summary>Things that could not be included (missing files, unreadable logs).</summary>
    public IReadOnlyList<string> Warnings { get; }

    public string Describe() =>
        "Exported " + ByteSize.Format(Bytes) + " in " + Files.Count + " file(s) to " + Path;
}
