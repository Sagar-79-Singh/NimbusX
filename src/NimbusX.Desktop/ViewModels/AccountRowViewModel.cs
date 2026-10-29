using System;
using CommunityToolkit.Mvvm.ComponentModel;
using NimbusX.Core.Text;
using NimbusX.Mount;
using NimbusX.Providers;

namespace NimbusX.Desktop.ViewModels;

/// <summary>One row on the Cloud Drives page.</summary>
public sealed partial class AccountRowViewModel : ObservableObject
{
    public AccountRowViewModel(CloudAccount account)
    {
        AccountId = account.Id;
        ProviderId = account.ProviderId;
        DisplayName = account.DisplayName;
        RemoteName = account.RemoteName;
        Apply(account);
    }

    public string AccountId { get; }

    public string ProviderId { get; }

    public string RemoteName { get; }

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _providerName = string.Empty;

    [ObservableProperty]
    private string _remoteTarget = string.Empty;

    [ObservableProperty]
    private string _quotaText = "Quota unavailable";

    [ObservableProperty]
    private double _quotaFraction;

    [ObservableProperty]
    private string _freeText = "Space unavailable";

    [ObservableProperty]
    private bool _autoMount = true;

    [ObservableProperty]
    private bool _isExperimental;

    [ObservableProperty]
    private bool _needsReconnect;

    [ObservableProperty]
    private string _mountStateText = "Not mounted";

    [ObservableProperty]
    private string _driveLetter = "—";

    [ObservableProperty]
    private bool _isMounted;

    [ObservableProperty]
    private bool _isReady;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _preferredDriveLetterText = string.Empty;

    [ObservableProperty]
    private string _lastConnectedText = string.Empty;

    public string MountButtonText => IsMounted ? "Unmount" : "Mount";

    partial void OnIsMountedChanged(bool value) => OnPropertyChanged(nameof(MountButtonText));

    partial void OnDisplayNameChanged(string value) => OnPropertyChanged(nameof(MountButtonText));

    public void Apply(CloudAccount account)
    {
        DisplayName = account.DisplayName;
        ProviderName = account.ProviderName;
        RemoteTarget = account.RcloneTarget;
        QuotaText = account.QuotaText;
        FreeText = account.FreeText;
        QuotaFraction = account.UsageFraction ?? 0d;
        AutoMount = account.AutoMount;
        IsExperimental = account.IsExperimental;
        NeedsReconnect = account.NeedsReconnect;
        LastError = account.LastError;
        HasError = !string.IsNullOrWhiteSpace(account.LastError) && !IsReady;
        PreferredDriveLetterText = string.IsNullOrWhiteSpace(account.PreferredDriveLetter) ? "Automatic" : account.PreferredDriveLetter + ":";
        LastConnectedText = account.LastConnectedUtc is DateTimeOffset connected
            ? "Connected " + TextFormat.Relative(connected)
            : "Never connected";
    }

    public void Apply(MountSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            IsMounted = false;
            IsReady = false;
            DriveLetter = "—";
            MountStateText = NeedsReconnect ? "Reconnect needed" : "Not mounted";
            return;
        }

        IsMounted = snapshot.Phase is not MountPhase.Stopped;
        IsReady = snapshot.Phase == MountPhase.Ready;
        DriveLetter = snapshot.DriveLetter + ":";
        MountStateText = snapshot.Phase switch
        {
            MountPhase.Ready => "Ready",
            MountPhase.Starting => "Starting…",
            MountPhase.Degraded => "Degraded",
            MountPhase.Stopping => "Unmounting…",
            MountPhase.Failed => "Failed",
            _ => "Not mounted"
        };

        if (!string.IsNullOrWhiteSpace(snapshot.LastError))
        {
            LastError = snapshot.LastError;
            HasError = true;
        }
        else if (IsReady)
        {
            HasError = false;
        }
    }

    public override string ToString() => DisplayName + " (" + ProviderName + ")";
}
