using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NimbusX.Core;
using NimbusX.Core.Config;
using NimbusX.Core.Io;
using NimbusX.Core.Json;
using NimbusX.Core.Logging;
using NimbusX.Core.Text;

namespace NimbusX.Diagnostics;

/// <summary>
/// Writes a diagnostics bundle the user can attach to a bug report.
/// </summary>
/// <remarks>
/// <para>
/// The bundle contains the health report, a redacted copy of the settings and account list, and the last lines of
/// every log. It never contains <c>rclone.conf</c>, tokens, cookies or passwords: log files pass through
/// <see cref="SecretRedactor"/> before they are written, and directories are never included.
/// </para>
/// <para>
/// Large logs are truncated from the front (newest lines are kept) instead of being skipped, so a bundle always
/// has the part that matters and never silently drops a file it claimed to include.
/// </para>
/// </remarks>
public sealed class DiagnosticsExporter
{
    public const int MaxLogLinesPerFile = 2000;
    public const int MaxLogBytesPerFile = 2 * 1024 * 1024;

    private readonly AppPaths _paths;
    private readonly IAppLogger _logger;

    public DiagnosticsExporter(AppPaths paths, IAppLogger? logger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<ExportResult> ExportAsync(
        DiagnosticsSnapshot snapshot,
        string? note,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Directory.CreateDirectory(_paths.ExportsDirectory);
        string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string zipPath = Path.Combine(_paths.ExportsDirectory, "nimbusx-diagnostics-" + stamp + ".zip");
        List<string> files = new();
        List<string> warnings = new();

        await using (FileStream file = new(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (ZipArchive archive = new(file, ZipArchiveMode.Create))
        {
            cancellationToken.ThrowIfCancellationRequested();

            WriteEntry(archive, "report.json", JsonDefaults.Serialize(snapshot), files);
            WriteEntry(archive, "report.txt", BuildTextReport(snapshot, note), files);
            WriteEntry(archive, "dependencies.txt", BuildDependencyReport(snapshot), files);

            if (File.Exists(_paths.SettingsFile))
            {
                string settings = AtomicFile.ReadAllTextOrNull(_paths.SettingsFile) ?? string.Empty;
                WriteEntry(archive, "settings.json", SecretRedactor.Redact(settings), files);
            }
            else
            {
                warnings.Add("settings.json was not found.");
            }

            if (File.Exists(_paths.AccountsFile))
            {
                string accounts = AtomicFile.ReadAllTextOrNull(_paths.AccountsFile) ?? string.Empty;
                WriteEntry(archive, "accounts.json", SecretRedactor.Redact(accounts), files);
            }

            if (File.Exists(_paths.MountsFile))
            {
                string mounts = AtomicFile.ReadAllTextOrNull(_paths.MountsFile) ?? string.Empty;
                WriteEntry(archive, "mounts.json", SecretRedactor.Redact(mounts), files);
            }

            AddLogFolder(archive, _paths.LogsDirectory, "logs", files, warnings);
            AddLogFolder(archive, _paths.RcloneLogsDirectory, "rclone-logs", files, warnings);

            // The configuration holds provider tokens; it is deliberately never included.
            WriteEntry(
                archive,
                "NOT_INCLUDED.txt",
                "rclone.conf is not included in this bundle because it contains provider tokens.\r\n" +
                "The health report records only whether the file is encrypted and whether its key exists.\r\n",
                files);
        }

        FileInfo info = new(zipPath);
        ExportResult result = new(zipPath, info.Exists ? info.Length : 0, files, warnings);

        _logger.Info(
            "Diagnostics exported to " + zipPath + " (" + ByteSize.Format(result.Bytes) + ", " + files.Count + " file(s)).",
            "Diagnostics");

        return result;
    }

    public static string BuildTextReport(DiagnosticsSnapshot snapshot, string? note)
    {
        StringBuilder builder = new();
        builder.AppendLine("NimbusX diagnostics report");
        builder.AppendLine("==========================");
        builder.AppendLine("Generated (UTC): " + snapshot.GeneratedUtc.ToString("u", CultureInfo.InvariantCulture));
        builder.AppendLine("Version:         " + snapshot.AppVersion);
        builder.AppendLine("Data folder:     " + snapshot.AppDataFolder);
        builder.AppendLine("Operating system:" + snapshot.OperatingSystem);
        builder.AppendLine("Architecture:    " + snapshot.Architecture);
        builder.AppendLine("Runtime:         " + snapshot.DotNetVersion);
        builder.AppendLine("Processors:      " + snapshot.ProcessorCount.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("Memory:          " + snapshot.MemoryText);
        builder.AppendLine();
        builder.AppendLine("Headline: " + snapshot.Headline);
        builder.AppendLine();

        builder.AppendLine("Dependencies");
        builder.AppendLine("------------");
        foreach (DependencyReport dependency in snapshot.Dependencies)
        {
            builder.AppendLine("- " + dependency.Name + ": expected " + dependency.Expected +
                               ", found " + (dependency.Installed ?? "nothing") + " (" + dependency.StatusText + ")");
            builder.AppendLine("    " + dependency.Detail);
            builder.AppendLine("    source: " + dependency.Source);
        }

        builder.AppendLine();
        builder.AppendLine("Checks");
        builder.AppendLine("------");
        foreach (DiagnosticCheck check in snapshot.Checks)
        {
            builder.AppendLine("[" + check.Severity + "] " + check.Area + " / " + check.Title);
            builder.AppendLine("    " + check.Detail);
            if (!string.IsNullOrWhiteSpace(check.Fix))
            {
                builder.AppendLine("    fix: " + check.Fix);
            }
        }

        builder.AppendLine();
        builder.AppendLine("Mounts");
        builder.AppendLine("------");
        if (snapshot.MountSummaries.Count == 0)
        {
            builder.AppendLine("(none)");
        }

        foreach (string mount in snapshot.MountSummaries)
        {
            builder.AppendLine("- " + mount);
        }

        builder.AppendLine();
        builder.AppendLine("Accounts");
        builder.AppendLine("--------");
        if (snapshot.AccountSummaries.Count == 0)
        {
            builder.AppendLine("(none)");
        }

        foreach (string account in snapshot.AccountSummaries)
        {
            builder.AppendLine("- " + account);
        }

        builder.AppendLine();
        builder.AppendLine("Cache");
        builder.AppendLine("-----");
        builder.AppendLine("Root:   " + (string.IsNullOrWhiteSpace(snapshot.CacheRoot) ? "(not configured)" : snapshot.CacheRoot));
        builder.AppendLine("Budget: " + ByteSize.Format(snapshot.CacheBudgetBytes));
        builder.AppendLine("Free:   " + (snapshot.CacheVolumeFreeBytes is long free ? ByteSize.Format(free) : "Unavailable"));
        builder.AppendLine("Status: " + snapshot.CacheSummary);

        if (!string.IsNullOrWhiteSpace(note))
        {
            builder.AppendLine();
            builder.AppendLine("User note");
            builder.AppendLine("---------");
            builder.AppendLine(note);
        }

        // Every line leaves through the redactor, not just the user's note: provider error messages and mount
        // summaries can carry tokens or cookies too, and this report is meant to be shareable.
        return SecretRedactor.Redact(builder.ToString());
    }

    private static string BuildDependencyReport(DiagnosticsSnapshot snapshot)
    {
        StringBuilder builder = new();
        foreach (DependencyReport dependency in snapshot.Dependencies)
        {
            builder.AppendLine(dependency.Name + " " + dependency.Expected + " -> " + (dependency.Installed ?? "not found"));
            builder.AppendLine("  " + dependency.Detail);
            builder.AppendLine("  " + dependency.Source);
            builder.AppendLine();
        }

        return SecretRedactor.Redact(builder.ToString());
    }

    private void AddLogFolder(ZipArchive archive, string folder, string entryPrefix, List<string> files, List<string> warnings)
    {
        if (!Directory.Exists(folder))
        {
            warnings.Add("The " + entryPrefix + " folder does not exist yet.");
            return;
        }

        IEnumerable<string> logs;
        try
        {
            logs = Directory.EnumerateFiles(folder, "*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(6)
                .ToArray();
        }
        catch (Exception exception)
        {
            warnings.Add("Could not list " + entryPrefix + ": " + exception.Message);
            return;
        }

        foreach (string log in logs)
        {
            try
            {
                string text = ReadTail(log, MaxLogLinesPerFile, MaxLogBytesPerFile);
                bool truncated = new FileInfo(log).Length > MaxLogBytesPerFile;
                if (truncated)
                {
                    warnings.Add(Path.GetFileName(log) + " was truncated to the newest " + ByteSize.Format(MaxLogBytesPerFile) + ".");
                }

                string body = SecretRedactor.Redact(text);
                WriteEntry(archive, entryPrefix + "/" + Path.GetFileName(log), body, files);
            }
            catch (Exception exception)
            {
                warnings.Add("Could not read " + Path.GetFileName(log) + ": " + exception.Message);
            }
        }
    }

    private static string ReadTail(string path, int maxLines, int maxBytes)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, stream.Length - maxBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        string content = reader.ReadToEnd();
        string[] lines = content.Split('\n');
        if (lines.Length > maxLines)
        {
            return string.Join("\n", lines.Skip(lines.Length - maxLines));
        }

        return content;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content, List<string> files)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using Stream entryStream = entry.Open();
        using StreamWriter writer = new(entryStream, new UTF8Encoding(false));
        writer.Write(content);
        writer.Flush();
        files.Add(name);
    }
}
