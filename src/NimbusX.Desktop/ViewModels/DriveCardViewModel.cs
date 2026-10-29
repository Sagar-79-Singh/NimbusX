using System;
using CommunityToolkit.Mvvm.ComponentModel;
using NimbusX.Core.Text;
using NimbusX.Mount;
using NimbusX.Providers;

namespace NimbusX.Desktop.ViewModels;

/// <summary>One drive tile on the dashboard: an account plus its live mount state.</summary>
public sealed partial class DriveCardViewModel : ObservableObject
{
    public DriveCardViewModel(string accountId, string displayName)
    {
        AccountId = accountId;
        DisplayName = displayName;
    }

    public string AccountId { get; }

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private string _providerName = string.Empty;

    [ObservableProperty]
    private string _driveLetter = string.Empty;

    [ObservableProperty]
    private string _phaseText = "Not mounted";

    [ObservableProperty]
    private string _phase = "Stopped";

    [ObservableProperty]
    private bool _isMounted;

    [ObservableProperty]
    private bool _isReady;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _quotaText = "Quota unavailable";

    [ObservableProperty]
    private double _quotaFraction;

    [ObservableProperty]
    private string _freeText = "Space unavailable";

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string? _fix;

    [ObservableProperty]
    private bool _isExperimental;

    [ObservableProperty]
    private string _usageText = "Unavailable";

    [ObservableProperty]
    private bool _needsReconnect;

    public string MountButtonText => IsMounted ? "Unmount" : "Mount";

    public string StatusColorKey => IsReady ? "SuccessBrush" : (HasError ? "DangerBrush" : "MutedTextBrush");

    partial void OnIsMountedChanged(bool value) => OnPropertyChanged(nameof(MountButtonText));

    partial void OnIsReadyChanged(bool value) => OnPropertyChanged(nameof(StatusColorKey));

    partial void OnHasErrorChanged(bool value) => OnPropertyChanged(nameof(StatusColorKey));

    public void Apply(CloudAccount account)
    {
        DisplayName = account.DisplayName;
        ProviderName = account.ProviderName;
        IsExperimental = account.IsExperimental;
        NeedsReconnect = account.NeedsReconnect;
        QuotaText = account.QuotaText;
        FreeText = account.FreeText;
        QuotaFraction = account.UsageFraction ?? 0d;

        if (!IsMounted)
        {
            DriveLetter = string.IsNullOrWhiteSpace(account.PreferredDriveLetter)
                ? "—"
                : account.PreferredDriveLetter + ":";
            Error = account.LastError;
            HasError = !string.IsNullOrWhiteSpace(account.LastError);
        }
    }

    public void Apply(MountSnapshot? snapshot, NimbusX.Cache.CacheUsage? usage)
    {
        if (snapshot is null)
        {
            IsMounted = false;
            IsReady = false;
            Phase = nameof(MountPhase.Stopped);
            PhaseText = "Not mounted";
            DriveLetter = "—";
            UsageText = usage is null ? "Unavailable" : usage.Summary;
            return;
        }

        IsMounted = snapshot.Phase is not MountPhase.Stopped;
        IsReady = snapshot.Phase == MountPhase.Ready;
        Phase = snapshot.Phase.ToString();
        PhaseText = snapshot.Phase switch
        {
            MountPhase.Ready => "Ready",
            MountPhase.Starting => "Starting…",
            MountPhase.Degraded => "Degraded",
            MountPhase.Stopping => "Unmounting…",
            MountPhase.Failed => "Failed",
            _ => "Not mounted"
        };
        DriveLetter = snapshot.DriveLetter + ":";
        Error = snapshot.LastError;
        HasError = !string.IsNullOrWhiteSpace(snapshot.LastError);
        Fix = HasError ? "Press Repair to restart this mount; the rclone log for the account is in Diagnostics." : null;
        UsageText = usage is null ? "Unavailable" : usage.Summary;
    }

    public override string ToString() => DisplayName + " " + DriveLetter + " " + PhaseText;
}
