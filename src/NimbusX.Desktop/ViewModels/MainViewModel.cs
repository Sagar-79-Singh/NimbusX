using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NimbusX.App;
using NimbusX.Cache;
using NimbusX.Core.Text;

namespace NimbusX.Desktop.ViewModels;

/// <summary>
/// The shell: navigation, the status strip and the wiring between pages and dialogs.
/// </summary>
/// <remarks>
/// The view model never invents a status. The strip at the bottom shows the mount count, the measured cache usage
/// and the startup result, all read from the running services.
/// </remarks>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppHost _host;
    private readonly List<PageViewModel> _pages;
    private DispatcherTimer? _statusTimer;

    public MainViewModel(AppHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));

        Dashboard = new DashboardViewModel(host.Services);
        CloudDrives = new CloudDrivesViewModel(host.Services);
        GameLibrary = new GameLibraryViewModel(host.Services);
        Accelerator = new AcceleratorViewModel(host.Services);
        SmartCache = new SmartCacheViewModel(host.Services);
        Performance = new PerformanceViewModel(host.Services);
        Diagnostics = new DiagnosticsViewModel(host.Services);
        Settings = new SettingsViewModel(host.Services);

        _pages = new List<PageViewModel>
        {
            Dashboard,
            CloudDrives,
            GameLibrary,
            Accelerator,
            SmartCache,
            Performance,
            Diagnostics,
            Settings
        };

        Pages = new ReadOnlyCollection<PageViewModel>(_pages);
        _selectedPage = Dashboard;

        CloudDrives.AddAccountRequested += (_, _) => AddAccountRequested?.Invoke(this, EventArgs.Empty);
        Settings.FirstRunRequested += (_, _) => WizardRequested?.Invoke(this, EventArgs.Empty);
        Settings.SettingsSaved += (_, _) => OnSettingsChanged();

        Version = NimbusX.Core.AppInfo.Version;
        StartupHeadline = host.Startup.Headline;
        StartupHasProblems = host.Startup.AnyFailed;

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
        RefreshStatus();
    }

    /// <summary>Raised when the user asks for the "add account" dialog.</summary>
    public event EventHandler? AddAccountRequested;

    /// <summary>Raised when the user re-opens the first run wizard.</summary>
    public event EventHandler? WizardRequested;

    public DashboardViewModel Dashboard { get; }

    public CloudDrivesViewModel CloudDrives { get; }

    public GameLibraryViewModel GameLibrary { get; }

    public AcceleratorViewModel Accelerator { get; }

    public SmartCacheViewModel SmartCache { get; }

    public PerformanceViewModel Performance { get; }

    public DiagnosticsViewModel Diagnostics { get; }

    public SettingsViewModel Settings { get; }

    public IReadOnlyList<PageViewModel> Pages { get; }

    [ObservableProperty]
    private PageViewModel _selectedPage;

    [ObservableProperty]
    private string _version = string.Empty;

    [ObservableProperty]
    private string _statusLeft = "Starting…";

    [ObservableProperty]
    private string _statusMiddle = string.Empty;

    [ObservableProperty]
    private string _statusRight = string.Empty;

    [ObservableProperty]
    private bool _statusHasWarning;

    [ObservableProperty]
    private string _startupHeadline = string.Empty;

    [ObservableProperty]
    private bool _startupHasProblems;

    [ObservableProperty]
    private string _dataFolder = string.Empty;

    partial void OnSelectedPageChanged(PageViewModel value)
    {
        if (value is null)
        {
            return;
        }

        _ = value.RefreshAsync(CancellationToken.None);
    }

    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        foreach (PageViewModel page in _pages)
        {
            await page.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        }

        RefreshStatus();
    }

    [RelayCommand]
    private async Task CheckHealthAsync()
    {
        try
        {
            var outcome = await _host.RunHealthCheckAsync(CancellationToken.None).ConfigureAwait(true);
            StatusRight = "Health: " + outcome.ToString();
            StatusHasWarning = outcome.FailedCount > 0 || outcome.RestartedCount > 0;
            await Dashboard.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            StatusRight = "Health check failed: " + exception.Message;
            StatusHasWarning = true;
        }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        DataFolder = _host.Services.Paths.Root;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _host.Services.Paths.Root,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            StatusRight = "Could not open the data folder: " + exception.Message;
        }
    }

    [RelayCommand]
    private void RequestAddAccount() => AddAccountRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void RequestWizard() => WizardRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Called after the add-account dialog closed so the pages pick up the new account.</summary>
    public async Task OnAccountListChangedAsync()
    {
        await CloudDrives.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        await Dashboard.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        RefreshStatus();
    }

    public void OnSettingsChanged()
    {
        _ = SmartCache.RefreshAsync(CancellationToken.None);
        _ = Settings.RefreshAsync(CancellationToken.None);
        _ = Dashboard.RefreshAsync(CancellationToken.None);
    }

    public void OnStartupCompleted(StartupReport report)
    {
        StartupHeadline = report.Headline;
        StartupHasProblems = report.AnyFailed;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        DataFolder = _host.Services.Paths.Root;

        IReadOnlyList<NimbusX.Mount.MountSnapshot> mounts = _host.Services.Mounts.Snapshots;
        int ready = mounts.Count(m => m.Phase == NimbusX.Mount.MountPhase.Ready);
        int failed = mounts.Count(m => m.Phase is NimbusX.Mount.MountPhase.Failed or NimbusX.Mount.MountPhase.Degraded);

        StatusLeft = mounts.Count == 0
            ? "No drives mounted"
            : ready + " of " + mounts.Count + " " + (mounts.Count == 1 ? "drive" : "drives") + " ready";

        CacheStatus cache = _host.Services.CacheService.Current;
        StatusMiddle = cache.Usages.Count == 0
            ? "Cache: " + cache.Description
            : "Cache: " + ByteSize.Format(cache.TotalPhysicalBytes) + " of " + ByteSize.Format(cache.Plan.TotalBudgetBytes) +
              (cache.PrefetchPaused ? " (prefetch paused)" : string.Empty);

        StatusHasWarning = failed > 0 || cache.PrefetchPaused || StartupHasProblems;
        StatusRight = _host.Services.Launcher.AnyGameRunning
            ? "Game running: " + _host.Services.Launcher.Running[0].Name
            : "Idle";
    }

    public void Dispose()
    {
        if (_statusTimer is not null)
        {
            _statusTimer.Stop();
            _statusTimer = null;
        }

        foreach (PageViewModel page in _pages)
        {
            page.Dispose();
        }
    }
}
