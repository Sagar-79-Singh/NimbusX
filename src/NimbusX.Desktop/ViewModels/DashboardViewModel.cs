using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NimbusX.App;
using NimbusX.Cache;
using NimbusX.Core;
using NimbusX.Core.Logging;
using NimbusX.Core.Text;
using NimbusX.Mount;
using NimbusX.Providers;

namespace NimbusX.Desktop.ViewModels;

/// <summary>A startup step rendered on the dashboard.</summary>
public sealed class StartupNote
{
    public StartupNote(string name, bool success, string detail, string? fix)
    {
        Name = name;
        Success = success;
        Detail = detail;
        Fix = fix;
    }

    public string Name { get; }

    public bool Success { get; }

    public string Detail { get; }

    public string? Fix { get; }

    public bool HasFix => !string.IsNullOrWhiteSpace(Fix);
}

/// <summary>The dashboard: drive tiles, cache tile, quick actions and the real log tail.</summary>
public sealed partial class DashboardViewModel : PageViewModel
{
    public DashboardViewModel(AppServices services)
        : base(services, "Dashboard", "Your cloud drives at a glance", "\uE80F")
    {
        StartupNotes = new ObservableCollection<StartupNote>();
        Drives = new ObservableCollection<DriveCardViewModel>();
        Activity = new ObservableCollection<string>();
        StartTimer(TimeSpan.FromSeconds(2), () => RefreshAsync(CancellationToken.None));
    }

    public ObservableCollection<StartupNote> StartupNotes { get; }

    public ObservableCollection<DriveCardViewModel> Drives { get; }

    public ObservableCollection<string> Activity { get; }

    [ObservableProperty]
    private string _headline = "Ready when you are";

    [ObservableProperty]
    private string _subheadline = "Mount a cloud drive to give games and media a real drive letter.";

    [ObservableProperty]
    private bool _hasDrives;

    [ObservableProperty]
    private string _accountCount = "0";

    [ObservableProperty]
    private string _mountedCount = "0";

    [ObservableProperty]
    private string _gameCount = "0";

    [ObservableProperty]
    private string _cacheUsed = "Unavailable";

    [ObservableProperty]
    private string _cacheBudget = "Unavailable";

    [ObservableProperty]
    private double _cacheFraction;

    [ObservableProperty]
    private string _cacheDetail = "Cache has not been measured yet.";

    [ObservableProperty]
    private bool _prefetchPaused;

    [ObservableProperty]
    private string _prefetchDetail = string.Empty;

    [ObservableProperty]
    private string _prefetchBudget = "Unavailable";

    [ObservableProperty]
    private bool _startupHasProblems;

    private StartupReport? _startup;

    public override async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await GuardedRefreshAsync(cancellationToken, async () =>
        {
            IReadOnlyList<CloudAccount> accounts = Services.Accounts.Accounts;
            IReadOnlyList<MountSnapshot> snapshots = Services.Mounts.Snapshots;
            CacheStatus cache = Services.CacheService.Refresh();

            AccountCount = accounts.Count.ToString(CultureInfo.InvariantCulture);
            MountedCount = snapshots.Count(s => s.Phase == MountPhase.Ready).ToString(CultureInfo.InvariantCulture);
            GameCount = Services.GameStore.Games.Count.ToString(CultureInfo.InvariantCulture);
            HasDrives = accounts.Count > 0;

            Reconcile(accounts, snapshots, cache);

            CacheUsed = cache.Usages.Count == 0 ? "Unavailable" : ByteSize.Format(cache.TotalPhysicalBytes);
            CacheBudget = cache.Plan.TotalBudgetBytes <= 0 ? "Unavailable" : ByteSize.Format(cache.Plan.TotalBudgetBytes);
            CacheFraction = Math.Clamp(cache.BudgetUse, 0d, 1d);
            CacheDetail = cache.Description;
            PrefetchPaused = cache.PrefetchPaused;
            PrefetchDetail = cache.Safety.Explanation;
            PrefetchBudget = ByteSize.Format(Services.Prefetch.RemainingBudgetBytes);

            Headline = BuildHeadline(accounts, snapshots);
            Subheadline = BuildSubheadline(accounts, snapshots, cache);

            await Task.CompletedTask.ConfigureAwait(true);
        }).ConfigureAwait(true);

        RefreshActivity();
        RefreshStartupNotes();
    }

    private void Reconcile(
        IReadOnlyList<CloudAccount> accounts,
        IReadOnlyList<MountSnapshot> snapshots,
        CacheStatus cache)
    {
        foreach (CloudAccount account in accounts)
        {
            DriveCardViewModel? card = Drives.FirstOrDefault(d => d.AccountId == account.Id);
            if (card is null)
            {
                card = new DriveCardViewModel(account.Id, account.DisplayName);
                Drives.Add(card);
            }

            card.Apply(account);
            MountSnapshot? snapshot = snapshots.FirstOrDefault(s => s.AccountId == account.Id);
            card.Apply(snapshot, cache.ForAccount(account.Id));
        }

        for (int i = Drives.Count - 1; i >= 0; i--)
        {
            if (accounts.All(a => a.Id != Drives[i].AccountId))
            {
                Drives.RemoveAt(i);
            }
        }
    }

    private static string BuildHeadline(IReadOnlyList<CloudAccount> accounts, IReadOnlyList<MountSnapshot> snapshots)
    {
        if (accounts.Count == 0)
        {
            return "No cloud accounts yet";
        }

        int ready = snapshots.Count(s => s.Phase == MountPhase.Ready);
        if (ready == accounts.Count)
        {
            return "All drives ready";
        }

        return ready == 0 ? "Drives are not mounted" : ready + " of " + accounts.Count + " drives ready";
    }

    private string BuildSubheadline(IReadOnlyList<CloudAccount> accounts, IReadOnlyList<MountSnapshot> snapshots, CacheStatus cache)
    {
        if (accounts.Count == 0)
        {
            return "Add a cloud account, then mount it to get a real drive letter in Explorer.";
        }

        MountSnapshot? failing = snapshots.FirstOrDefault(s => s.Phase is MountPhase.Failed or MountPhase.Degraded);
        if (failing is not null)
        {
            return failing.DisplayName + " needs attention: " + (failing.LastError ?? "the mount stopped responding");
        }

        if (cache.PrefetchPaused)
        {
            return "Prefetch is paused to keep the cache disk healthy. Reads still work normally.";
        }

        return "Everything mounted and healthy. Games launch straight from the drive letter.";
    }

    /// <summary>Shows the startup result, worst first, straight from the host's report.</summary>
    public void ApplyStartup(StartupReport report)
    {
        _startup = report;
        StartupHasProblems = report.AnyFailed;
        StartupNotes.Clear();

        foreach (StartupStep step in report.Steps.OrderBy(s => s.Success).Take(8))
        {
            StartupNotes.Add(new StartupNote(step.Name, step.Success, step.Detail, step.Fix));
        }
    }

    [RelayCommand]
    private async Task ToggleMountAsync(DriveCardViewModel? card)
    {
        if (card is null || card.IsBusy)
        {
            return;
        }

        card.IsBusy = true;
        try
        {
            if (card.IsMounted)
            {
                bool unmounted = await Services.Mounts.UnmountAsync(card.AccountId, CancellationToken.None).ConfigureAwait(true);
                card.IsBusy = false;
                await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
                if (!unmounted)
                {
                    card.Error = "The drive was already stopped.";
                    card.HasError = true;
                }

                return;
            }

            CloudAccount? account = Services.Accounts.Find(card.AccountId);
            if (account is null)
            {
                card.Error = "That account no longer exists.";
                card.HasError = true;
                return;
            }

            MountResult result = await Services.Mounts.MountAsync(BuildRequest(account), CancellationToken.None).ConfigureAwait(true);
            if (!result.Success)
            {
                card.Error = result.Error;
                card.Fix = result.Fix;
                card.HasError = true;
            }
            else
            {
                card.Error = null;
                card.HasError = false;
            }
        }
        catch (Exception exception)
        {
            card.Error = exception.Message;
            card.HasError = true;
        }
        finally
        {
            card.IsBusy = false;
            await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task RepairAsync(DriveCardViewModel? card)
    {
        if (card is null || card.IsBusy)
        {
            return;
        }

        card.IsBusy = true;
        try
        {
            bool unmounted = await Services.Mounts.UnmountAsync(card.AccountId, CancellationToken.None).ConfigureAwait(true);
            if (!unmounted)
            {
                Logger.Info(card.DisplayName + " was not mounted; Repair mounts it fresh.", "Ui");
            }

            MountResult result = await TryMountAsync(card.AccountId).ConfigureAwait(true);
            if (!result.Success)
            {
                card.Error = result.Error;
                card.Fix = result.Fix;
                card.HasError = true;
            }
        }
        catch (Exception exception)
        {
            card.Error = exception.Message;
            card.HasError = true;
        }
        finally
        {
            card.IsBusy = false;
            await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    private async Task<MountResult> TryMountAsync(string accountId)
    {
        CloudAccount? account = Services.Accounts.Find(accountId);
        if (account is null)
        {
            return MountResult.Fail("That account no longer exists.", "Refresh the account list on the Cloud Drives page.");
        }

        return await Services.Mounts.MountAsync(BuildRequest(account), CancellationToken.None).ConfigureAwait(true);
    }

    private MountRequest BuildRequest(CloudAccount account)
    {
        ProviderCatalog.TryGet(account.ProviderId, out ProviderDefinition definition);
        RcloneFlavor flavor = definition is not null && definition.RequiresTeraBoxBuild ? RcloneFlavor.TeraBox : RcloneFlavor.Standard;
        List<string> accountIds = Services.Accounts.Accounts.Select(a => a.Id).ToList();

        return new MountRequest(account.Id, account.DisplayName, account.ProviderId, account.RemoteName)
        {
            RemotePath = account.RemotePath,
            PreferredDriveLetter = string.IsNullOrWhiteSpace(account.PreferredDriveLetter) ? null : account.PreferredDriveLetter![0],
            Flavor = flavor,
            VolumeName = AppInfo.Name + " - " + account.DisplayName,
            CacheBudgetBytes = Services.Cache.BudgetFor(account.Id, accountIds),
            CacheMinFreeSpaceBytes = Services.Settings.Current.ProtectedFreeSpaceBytes
        };
    }

    [RelayCommand]
    private void OpenDrive(DriveCardViewModel? card)
    {
        if (card is null || !card.IsReady)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = card.DriveLetter + ":\\", UseShellExecute = true });
        }
        catch (Exception exception)
        {
            card.Error = "Could not open the drive: " + exception.Message;
            card.HasError = true;
        }
    }

    [RelayCommand]
    private async Task MountAllAsync()
    {
        foreach (DriveCardViewModel card in Drives.Where(d => !d.IsMounted))
        {
            await ToggleMountAsync(card).ConfigureAwait(true);
        }
    }

    private void RefreshStartupNotes()
    {
        if (_startup is null)
        {
            StartupNotes.Clear();
        }
    }

    private void RefreshActivity()
    {
        Activity.Clear();
        foreach (LogEntry entry in Services.Logger.RecentEntries(8))
        {
            Activity.Add(entry.ToLogLine());
        }

        if (Activity.Count == 0)
        {
            Activity.Add("No log entries yet.");
        }
    }
}
