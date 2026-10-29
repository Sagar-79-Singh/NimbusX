using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NimbusX.Diagnostics;
using NimbusX.Core.Logging;
using Xunit;

namespace NimbusX.Tests;

public sealed class DiagnosticsModelTests
{
    [Fact]
    public void A_snapshot_without_errors_is_healthy()
    {
        DiagnosticsSnapshot snapshot = new();
        snapshot.AddCheck("Mount", "WinFsp detected", DiagnosticSeverity.Info, "WinFsp 2.1.25156 is installed.");

        Assert.True(snapshot.Healthy);
        Assert.Equal("Everything NimbusX needs is ready.", snapshot.Headline);
    }

    [Fact]
    public void Errors_change_the_headline_and_the_counts()
    {
        DiagnosticsSnapshot snapshot = new();
        snapshot.AddCheck("Mount", "WinFsp missing", DiagnosticSeverity.Error, "WinFsp was not found.", "Install WinFsp from the pinned MSI.");
        snapshot.AddCheck("Cache", "Slow volume", DiagnosticSeverity.Warning, "The cache volume is a network share.");

        Assert.False(snapshot.Healthy);
        Assert.Equal(1, snapshot.ErrorCount);
        Assert.Equal(1, snapshot.WarningCount);
        Assert.Contains("problem", snapshot.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Memory_text_says_unavailable_instead_of_zero()
    {
        DiagnosticsSnapshot snapshot = new();
        Assert.Equal("Unavailable", snapshot.MemoryText);

        snapshot.TotalMemoryBytes = 16L * 1024 * 1024 * 1024;
        Assert.NotEqual("Unavailable", snapshot.MemoryText);
    }

    [Fact]
    public void A_dependency_reports_not_ready_only_when_it_is_unusable()
    {
        DependencyReport missing = new("WinFsp", "2.1.25156", null, false, "Not installed.", "Pinned MSI, SHA-256 073a70e0...");
        DependencyReport ready = new("rclone", "1.75.2", "1.75.2", true, "Matched the pinned digest.", "Pinned zip, SHA-256 8a9db2ba...");

        Assert.Equal("Not ready", missing.StatusText);
        Assert.Equal("Ready", ready.StatusText);
        Assert.False(missing.Usable);
        Assert.True(ready.Usable);
    }

    [Fact]
    public void The_text_report_lists_dependencies_and_checks()
    {
        DiagnosticsSnapshot snapshot = new()
        {
            AppVersion = "1.0.0",
            OperatingSystem = "Windows 11",
            Architecture = "X64",
            DotNetVersion = "10.0.0"
        };
        snapshot.Dependencies.Add(new DependencyReport("rclone", "1.75.2", "1.75.2", true, "Digest matched.", "pinned"));
        snapshot.AddCheck("Mount", "WinFsp missing", DiagnosticSeverity.Error, "WinFsp was not found.", "Install WinFsp.");

        string report = DiagnosticsExporter.BuildTextReport(snapshot, note: "user note");

        Assert.Contains("NimbusX diagnostics report", report, StringComparison.Ordinal);
        Assert.Contains("rclone: expected 1.75.2", report, StringComparison.Ordinal);
        Assert.Contains("Install WinFsp.", report, StringComparison.Ordinal);
        Assert.Contains("(none)", report, StringComparison.Ordinal);
    }

    [Fact]
    public void The_text_report_never_contains_a_secret_that_was_put_into_it()
    {
        DiagnosticsSnapshot snapshot = new();
        snapshot.AddCheck(
            "TeraBox",
            "Login failed",
            DiagnosticSeverity.Error,
            "The server replied: cookie: ndus=SeCrEtCoOkIeValue",
            "Sign in again.");

        string report = DiagnosticsExporter.BuildTextReport(snapshot, note: null);

        Assert.DoesNotContain("SeCrEtCoOkIeValue", report, StringComparison.Ordinal);
    }
}

public sealed class DiagnosticsRedactionTests
{
    [Fact]
    public void A_log_line_with_a_token_is_redacted_before_it_is_shown()
    {
        RecordingLogger logger = new();
        logger.Warn("rclone said: token=sl.abcdefghijklmnopqrstuvwxyz0123456789", "Mount");

        string shown = logger.RecentEntries(1)[0].Message;

        Assert.Contains(SecretRedactor.Placeholder, shown, StringComparison.Ordinal);
        Assert.DoesNotContain("sl.abcdefghijklmnopqrstuvwxyz", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cookie_in_a_terabox_error_is_redacted()
    {
        RecordingLogger logger = new();
        logger.Error("login failed: cookie: ndus=AnBcDeFgHiJkLmNoP", "TeraBox");

        Assert.DoesNotContain("AnBcDeFgHiJkLmNoP", logger.RecentEntries(1)[0].Message, StringComparison.Ordinal);
    }
}
