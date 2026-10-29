using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NimbusX.App;
using NimbusX.Core;
using NimbusX.Core.Text;
using NimbusX.Mount;
using NimbusX.Providers;

namespace NimbusX.Desktop.ViewModels;

/// <summary>Cloud Drives: connected accounts, quotas and mount actions.</summary>
public sealed partial class CloudDrivesViewModel : PageViewModel
{
    public CloudDrivesViewModel(AppServices services)
        : base(services, "Cloud Drives", "Sign in once per provider and mount it as a drive letter", "\uE8B7")
    {
        Accounts = new ObservableCollection<AccountRowViewModel>();
        StartTimer(TimeSpan.FromSeconds(5), () => RefreshAsync(CancellationToken.None));
    }

    /// <summary>Raised when the user presses "Add account".</summary>
    public event EventHandler? AddAccountRequested;

    public ObservableCollection<AccountRowViewModel> Accounts { get; }

    [ObservableProperty]
    private bool _hasAccounts;

    [ObservableProperty]
    private string _connectNote = string.Empty;

    [ObservableProperty]
    private string? _statusMessage;

    public override async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await GuardedRefreshAsync(cancellationToken, async () =>
        {
            IReadOnlyList<CloudAccount> accounts = Services.Accounts.Accounts;
            IReadOnlyList<MountSnapshot> snapshots = Services.Mounts.Snapshots;

            foreach (CloudAccount account in accounts)
            {
                AccountRowViewModel? row = Accounts.FirstOrDefault(r => r.AccountId == account.Id);
                if (row is null)
                {
                    row = new AccountRowViewModel(account);
                    Accounts.Add(row);
                }

                row.Apply(account);
                row.Apply(snapshots.FirstOrDefault(s => s.AccountId == account.Id));
            }

            for (int i = Accounts.Count - 1; i >= 0; i--)
            {
                if (accounts.All(a => a.Id != Accounts[i].AccountId))
                {
                    Accounts.RemoveAt(i);
                }
            }

            HasAccounts = Accounts.Count > 0;
            ConnectNote = accounts.Count == 0
                ? "No accounts yet. NimbusX needs one cloud account per drive letter."
                : string.Empty;

            await Task.CompletedTask.ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private void AddAccount() => AddAccountRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task MountAsync(AccountRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        await RunRowActionAsync(row, async () =>
        {
            MountResult result = await Services.Mounts.MountAsync(BuildRequest(row), CancellationToken.None).ConfigureAwait(true);
            StatusMessage = result.Success
                ? row.DisplayName + " mounted on " + (result.Snapshot?.DriveLetter ?? "?") + ":"
                : "Mount failed: " + result.Error + (string.IsNullOrWhiteSpace(result.Fix) ? string.Empty : " — " + result.Fix);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task UnmountAsync(AccountRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        await RunRowActionAsync(row, async () =>
        {
            bool unmounted = await Services.Mounts.UnmountAsync(row.AccountId, CancellationToken.None).ConfigureAwait(true);
            StatusMessage = unmounted
                ? row.DisplayName + " unmounted."
                : row.DisplayName + " was not mounted.";
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RepairAsync(AccountRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        await RunRowActionAsync(row, async () =>
        {
            bool unmounted = await Services.Mounts.UnmountAsync(row.AccountId, CancellationToken.None).ConfigureAwait(true);
            if (!unmounted)
            {
                Logger.Info(row.DisplayName + " was not mounted; Repair mounts it fresh.", "Ui");
            }

            MountResult result = await Services.Mounts.MountAsync(BuildRequest(row), CancellationToken.None).ConfigureAwait(true);

            StatusMessage = result.Success
                ? "Repaired " + row.DisplayName + " on " + (result.Snapshot?.DriveLetter ?? "?") + ":"
                : "Repair failed: " + result.Error;
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RefreshQuotaAsync(AccountRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        await RunRowActionAsync(row, async () =>
        {
            QuotaRefreshResult result = await Services.Accounts.RefreshQuotaAsync(row.AccountId, CancellationToken.None).ConfigureAwait(true);
            CloudAccount? account = Services.Accounts.Find(row.AccountId);
            if (account is not null)
            {
                row.Apply(account);
            }

            StatusMessage = result.Success
                ? row.DisplayName + ": " + row.QuotaText
                : "Quota refresh failed: " + result.Error;
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ReconnectAsync(AccountRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        await RunRowActionAsync(row, async () =>
        {
            ConnectResult result = await Services.Accounts
                .ReconnectAsync(row.AccountId, null, null, CancellationToken.None)
                .ConfigureAwait(true);

            StatusMessage = result.Success
                ? row.DisplayName + " signed in again."
                : "Reconnect failed: " + result.Error + (string.IsNullOrWhiteSpace(result.Fix) ? string.Empty : " — " + result.Fix);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RemoveAsync(AccountRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        await RunRowActionAsync(row, async () =>
        {
            bool removed = await Services.Accounts.DisconnectAsync(row.AccountId, CancellationToken.None).ConfigureAwait(true);
            if (removed)
            {
                Accounts.Remove(row);
                HasAccounts = Accounts.Count > 0;
                StatusMessage = row.DisplayName + " removed. Nothing was deleted in the cloud.";
            }
            else
            {
                StatusMessage = "Could not remove " + row.DisplayName + ". Check Diagnostics for the rclone error.";
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenDrive(AccountRowViewModel? row)
    {
        if (row is null || !row.IsReady)
        {
            StatusMessage = row is null ? null : row.DisplayName + " is not ready yet.";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = row.DriveLetter + "\\",
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            StatusMessage = "Could not open " + row.DriveLetter + ": " + exception.Message;
        }
    }

    [RelayCommand]
    private async Task ToggleAutoMountAsync(AccountRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        bool value = !row.AutoMount;
        Services.Accounts.SetAutoMount(row.AccountId, value);
        row.AutoMount = value;
        StatusMessage = row.DisplayName + (value ? " will mount at startup." : " will not mount automatically.");
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private async Task RunRowActionAsync(AccountRowViewModel row, Func<Task> action)
    {
        if (row.IsBusy)
        {
            return;
        }

        row.IsBusy = true;
        StatusMessage = null;
        try
        {
            await action().ConfigureAwait(true);
            await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            StatusMessage = row.DisplayName + ": " + exception.Message;
            Logger.Warn("Account action failed for " + row.DisplayName + ": " + exception.Message, "Ui");
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    private MountRequest BuildRequest(AccountRowViewModel row)
    {
        CloudAccount? account = Services.Accounts.Find(row.AccountId)
            ?? throw new InvalidOperationException("The account no longer exists.");

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
}
