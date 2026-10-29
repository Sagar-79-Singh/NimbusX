using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NimbusX.Core;
using NimbusX.Core.Logging;
using NimbusX.Core.Processes;

namespace NimbusX.Mount;

/// <summary>State of the WinFsp installation on this machine.</summary>
public sealed class WinFspStatus
{
    public WinFspStatus(bool installed, string? version, string? installDirectory, string? detail)
    {
        Installed = installed;
        Version = version;
        InstallDirectory = installDirectory;
        Detail = detail;
    }

    public bool Installed { get; }

    /// <summary>Installed version as reported by the WinFsp registry key, e.g. <c>2.1.25156</c>.</summary>
    public string? Version { get; }

    public string? InstallDirectory { get; }

    public string? Detail { get; }

    public string InstallerUrl => DependencyVersions.WinFsp.DownloadUrl;

    public string InstallerSha256 => DependencyVersions.WinFsp.Sha256;

    public string Describe()
    {
        if (!Installed)
        {
            return "WinFsp is not installed. rclone cannot create drives without it.";
        }

        return "WinFsp " + (Version ?? "?") + " installed" +
               (string.IsNullOrWhiteSpace(InstallDirectory) ? string.Empty : " in " + InstallDirectory) + ".";
    }
}

/// <summary>
/// Finds WinFsp and, when it is missing, tells the user exactly what to install.
/// </summary>
/// <remarks>
/// NimbusX never installs WinFsp silently and never touches Windows security settings: it opens the official
/// signed MSI from winfsp.dev / GitHub and lets the user decide. Detection reads the WinFsp registry key that the
/// official installer writes, then falls back to the well-known installation paths.
/// </remarks>
public sealed class WinFspDetector
{
    private static readonly string[] RegistryKeys =
    {
        @"HKLM\SOFTWARE\WOW6432Node\WinFsp",
        @"HKLM\SOFTWARE\WinFsp"
    };

    private readonly IProcessRunner _runner;
    private readonly IAppLogger _logger;

    public WinFspDetector(IProcessRunner runner, IAppLogger logger)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Detects WinFsp.</summary>
    public async Task<WinFspStatus> DetectAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WinFspStatus(false, null, null, "WinFsp detection is only supported on Windows.");
        }

        foreach (string key in RegistryKeys)
        {
            (string? installDir, string? version) = await ReadRegistryKeyAsync(key, cancellationToken).ConfigureAwait(false);
            if (installDir is not null)
            {
                return new WinFspStatus(true, version, installDir, "Detected from the WinFsp registry key.");
            }
        }

        // Fallback: the installer's well known file layout.
        foreach (string? programFiles in new[]
                 {
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                     Environment.GetEnvironmentVariable("ProgramFiles")
                 })
        {
            if (string.IsNullOrWhiteSpace(programFiles))
            {
                continue;
            }

            string candidate = Path.Combine(programFiles!, "WinFsp");
            string dll = Path.Combine(candidate, "bin", "winfsp-x64.dll");
            if (File.Exists(dll))
            {
                return new WinFspStatus(true, ReadAssemblyVersion(dll), candidate, "Detected from the WinFsp installation folder.");
            }
        }

        return new WinFspStatus(
            false,
            null,
            null,
            "WinFsp was not found. Install it once with the official installer, then press Re-check.");
    }

    /// <summary>Opens the official WinFsp installer download page in the default browser.</summary>
    public bool OpenInstallerPage(IAppLogger? logger = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(DependencyVersions.WinFsp.DownloadUrl) { UseShellExecute = true });
            (logger ?? _logger).Info("Opened the official WinFsp installer page.", "WinFsp");
            return true;
        }
        catch (Exception exception)
        {
            (logger ?? _logger).Error("Could not open the WinFsp download page.", "WinFsp", exception);
            return false;
        }
    }

    /// <summary>Human readable instruction block shown in the mount error card.</summary>
    public static string InstallInstructions()
    {
        return "NimbusX needs WinFsp (the user mode file system driver rclone uses)." + Environment.NewLine +
               "1. Download the official installer: " + DependencyVersions.WinFsp.DownloadUrl + Environment.NewLine +
               "2. SHA-256: " + DependencyVersions.WinFsp.Sha256 + Environment.NewLine +
               "3. Run the MSI and accept its defaults (a reboot is usually not needed)." + Environment.NewLine +
               "4. Return to NimbusX and press Re-check.";
    }

    private async Task<(string? InstallDir, string? Version)> ReadRegistryKeyAsync(string key, CancellationToken cancellationToken)
    {
        ProcessSpec spec = ProcessSpec.Create("reg.exe", "query", key, "/v", "InstallDir");
        ProcessResult result = await _runner.RunAsync(spec, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return (null, null);
        }

        string? installDir = ParseRegValue(result.StandardOutput, "InstallDir");
        if (string.IsNullOrWhiteSpace(installDir))
        {
            return (null, null);
        }

        ProcessResult versionResult = await _runner
            .RunAsync(ProcessSpec.Create("reg.exe", "query", key, "/v", "Version"), TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);

        string? version = versionResult.Succeeded ? ParseRegValue(versionResult.StandardOutput, "Version") : null;
        return (installDir, version);
    }

    /// <summary>Parses <c>    InstallDir    REG_SZ    C:\Program Files (x86)\WinFsp\</c>.</summary>
    public static string? ParseRegValue(string? regOutput, string valueName)
    {
        if (string.IsNullOrWhiteSpace(regOutput))
        {
            return null;
        }

        foreach (string rawLine in regOutput!.Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith(valueName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] parts = line.Split(new[] { "REG_SZ", "REG_EXPAND_SZ", "REG_DWORD" }, StringSplitOptions.None);
            if (parts.Length >= 2)
            {
                return parts[^1].Trim();
            }
        }

        return null;
    }

    private static string? ReadAssemblyVersion(string dllPath)
    {
        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(dllPath);
            if (!string.IsNullOrWhiteSpace(info.FileVersion))
            {
                return info.FileVersion!.Trim();
            }

            return info.ProductVersion?.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Compares two dotted versions, returning true when <paramref name="installed"/> is new enough.</summary>
    public static bool IsAtLeast(string? installed, string minimum)
    {
        if (string.IsNullOrWhiteSpace(installed))
        {
            return false;
        }

        string[] left = installed!.Split('.', '-', '+');
        string[] right = minimum.Split('.');
        int count = Math.Max(left.Length, right.Length);
        for (int i = 0; i < count; i++)
        {
            int a = i < left.Length && int.TryParse(left[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedA) ? parsedA : 0;
            int b = i < right.Length && int.TryParse(right[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedB) ? parsedB : 0;
            if (a != b)
            {
                return a > b;
            }
        }

        return true;
    }

    /// <summary>Detailed diagnostic lines about the WinFsp installation.</summary>
    public static IReadOnlyDictionary<string, string> DescribeForDiagnostics(WinFspStatus status)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Installed"] = status.Installed ? "yes" : "no",
            ["Version"] = status.Version ?? "unknown",
            ["InstallDirectory"] = status.InstallDirectory ?? "unknown",
            ["Detail"] = status.Detail ?? string.Empty,
            ["RequiredMinimum"] = DependencyVersions.MinimumWinFspVersion
        };
        return values;
    }
}
