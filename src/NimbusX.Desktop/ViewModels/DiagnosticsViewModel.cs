using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NimbusX.App;
using NimbusX.Core.Text;
using NimbusX.Diagnostics;

namespace NimbusX.Desktop.ViewModels;

/// <summary>One dependency row on the Diagnostics page.</summary>
public sealed class DependencyRow
{
    public DependencyRow(DependencyReport report)
    {
        Name = report.Name;
        Expected = report.Expected;
        Installed = report.Installed ?? "not found";
        Detail = report.Detail;
        Source = report.Source;
        IsReady = report.Usable;
        StatusText = report.StatusText;
    }

    public string Name { get; }

    public string Expected { get; }

    public string Installed { get; }

    public string Detail { get; }

    public string Source { get; }

    public bool IsReady { get; }

    public string StatusText { get; }
}

/// <summary>One check result on the Diagnostics page.</summary>
public sealed class CheckRow
{
    public CheckRow(DiagnosticCheck check)
    {
        Area = check.Area;
        Title = check.Title;
        Severity = check.Severity.ToString();
        Detail = check.Detail;
        Fix = check.Fix ?? string.Empty;
        HasFix = !string.IsNullOrWhiteSpace(check.Fix);
        IsOk = check.Ok;
    }

    public string Area { get; }

    public string Title { get; }

    public string Severity { get; }

    public string Detail { get; }

    public string Fix { get; }

    public bool HasFix { get; }

    public bool IsOk { get; }
}

/// <summary>Diagnostics: environment checks, dependencies, log tail and the redacted export.</summary>
public sealed partial class DiagnosticsViewModel : PageViewModel
{
    public DiagnosticsViewModel(AppServices services)
        : base(services, "Diagnostics", "What NimbusX checked, what it found and how to fix it", "\uE9D9")
    {
        Checks = new ObservableCollection<CheckRow>();
        Dependencies = new ObservableCollection<DependencyRow>();
        LogTail = new ObservableCollection<string>();
    }

    public ObservableCollection<CheckRow> Checks { get; }

    public ObservableCollection<DependencyRow> Dependencies { get; }

    public ObservableCollection<string> LogTail { get; }

    [ObservableProperty]
    private string _headline = "Not checked yet";

    [ObservableProperty]
    private string _environment = string.Empty;

    [ObservableProperty]
    private string _note = string.Empty;

    [ObservableProperty]
    private string _exportPath = string.Empty;

    [ObservableProperty]
    private string _exportStatus = string.Empty;

    [ObservableProperty]
    private bool _isExporting;

    [ObservableProperty]
    private int _errorCount;

    [ObservableProperty]
    private int _warningCount;

    [ObservableProperty]
    private bool _isHealthy;

    public override async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await GuardedRefreshAsync(cancellationToken, async () =>
        {
            DiagnosticsSnapshot snapshot = await Services.Diagnostics.CollectAsync(cancellationToken).ConfigureAwait(true);

            Headline = snapshot.Headline;
            Environment = snapshot.OperatingSystem + " · " + snapshot.Architecture + " · " + snapshot.DotNetVersion +
                          " · " + snapshot.ProcessorCount + " CPU · " + snapshot.MemoryText + " RAM";
            ErrorCount = snapshot.ErrorCount;
            WarningCount = snapshot.WarningCount;
            IsHealthy = snapshot.Healthy;

            Checks.Clear();
            foreach (DiagnosticCheck check in snapshot.Checks
                         .OrderBy(c => c.Severity)
                         .ThenBy(c => c.Area, StringComparer.CurrentCultureIgnoreCase))
            {
                Checks.Add(new CheckRow(check));
            }

            Dependencies.Clear();
            foreach (DependencyReport dependency in snapshot.Dependencies)
            {
                Dependencies.Add(new DependencyRow(dependency));
            }

            LoadLogTail();
            await Task.CompletedTask.ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private void LoadLogTail()
    {
        LogTail.Clear();
        foreach (NimbusX.Core.Logging.LogEntry entry in Services.Logger.RecentEntries(40))
        {
            LogTail.Add(entry.ToLogLine());
        }

        if (LogTail.Count == 0)
        {
            LogTail.Add("No log entries yet.");
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (IsExporting)
        {
            return;
        }

        IsExporting = true;
        ExportStatus = "Collecting and writing the bundle…";
        try
        {
            DiagnosticsSnapshot snapshot = await Services.Diagnostics.CollectAsync(CancellationToken.None).ConfigureAwait(true);
            ExportResult result = await Services.Exporter.ExportAsync(snapshot, Note, CancellationToken.None).ConfigureAwait(true);
            ExportPath = result.Path;
            ExportStatus = result.Describe() + (result.Warnings.Count > 0 ? " " + result.Warnings.Count + " note(s)." : string.Empty);
        }
        catch (Exception exception)
        {
            ExportStatus = "Export failed: " + exception.Message;
        }
        finally
        {
            IsExporting = false;
        }
    }

    [RelayCommand]
    private void OpenExportFolder()
    {
        try
        {
            string folder = string.IsNullOrWhiteSpace(ExportPath)
                ? Services.Paths.ExportsDirectory
                : Path.GetDirectoryName(ExportPath) ?? Services.Paths.ExportsDirectory;

            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ExportStatus = "Could not open the export folder: " + exception.Message;
        }
    }

    [RelayCommand]
    private async Task CopySummaryAsync()
    {
        // `Environment` is a property of this view model, so System.Environment must be spelled out.
        string text = Headline + System.Environment.NewLine + Environment +
                      System.Environment.NewLine +
                      string.Join(System.Environment.NewLine,
                          Checks.Select(c => "[" + c.Severity + "] " + c.Area + ": " + c.Title + " — " + c.Detail));
        try
        {
            System.Windows.Clipboard.SetText(text);
            ExportStatus = "Summary copied to the clipboard.";
        }
        catch (Exception exception)
        {
            ExportStatus = "Could not copy: " + exception.Message;
        }

        await Task.CompletedTask.ConfigureAwait(true);
    }
}
