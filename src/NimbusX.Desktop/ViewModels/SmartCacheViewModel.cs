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
using NimbusX.Core.Text;

namespace NimbusX.Desktop.ViewModels;

/// <summary>A cache budget preset button.</summary>
public sealed partial class CachePresetViewModel : ObservableObject
{
    public CachePresetViewModel(int gigabytes, long bytes)
    {
        Gigabytes = gigabytes;
        Bytes = bytes;
        Label = gigabytes.ToString(CultureInfo.InvariantCulture) + " GB";
    }

    public int Gigabytes { get; }

    public long Bytes { get; }

    public string Label { get; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isAvailable = true;
}

/// <summary>One mount's cache folder as the Smart Cache page shows it.</summary>
public sealed class CacheUsageRow
{
    public CacheUsageRow(CacheUsage usage, string accountId, string displayName, string driveLetter)
    {
        AccountId = accountId;
        DisplayName = displayName;
        DriveLetter = driveLetter;
        Folder = usage.CacheDirectory;
        PhysicalText = usage.PhysicalText;
        LogicalText = usage.LogicalText;
        ShareText = usage.BudgetShareBytes > 0 ? ByteSize.Format(usage.BudgetShareBytes) : "Unavailable";
        UsageFraction = Math.Clamp(usage.BudgetUse, 0d, 1d);
        Summary = usage.Summary;
        FileCountText = usage.FileCount.ToString(CultureInfo.InvariantCulture) + " file(s)";
        MarkerOk = usage.MarkerPresent;
    }

    public string AccountId { get; }

    public string DisplayName { get; }

    public string DriveLetter { get; }

    public string Folder { get; }

    public string PhysicalText { get; }

    public string LogicalText { get; }

    public string ShareText { get; }

    public double UsageFraction { get; }

    public string Summary { get; }

    public string FileCountText { get; }

    public bool MarkerOk { get; }
}

/// <summary>Smart Cache: budget, real usage, disk pressure, pins and clearing.</summary>
public sealed partial class SmartCacheViewModel : PageViewModel
{
    public SmartCacheViewModel(AppServices services)
        : base(services, "Smart Cache", "How much of each drive is kept on disk, and when to stop", "\uE74E")
    {
        Presets = new ObservableCollection<CachePresetViewModel>();
        Usage = new ObservableCollection<CacheUsageRow>();
        Pins = new ObservableCollection<PinnedEntry>();

        foreach (int gigabytes in CachePresets.Gigabytes)
        {
            Presets.Add(new CachePresetViewModel(gigabytes, CachePresets.ToBytes(gigabytes)));
        }

        StartTimer(TimeSpan.FromSeconds(10), () => RefreshAsync(CancellationToken.None));
    }

    public ObservableCollection<CachePresetViewModel> Presets { get; }

    public ObservableCollection<CacheUsageRow> Usage { get; }

    public ObservableCollection<PinnedEntry> Pins { get; }

    [ObservableProperty]
    private string _budgetText = "Unavailable";

    [ObservableProperty]
    private string _usedText = "Unavailable";

    [ObservableProperty]
    private string _freeText = "Unavailable";

    [ObservableProperty]
    private string _clearableText = "Unavailable";

    [ObservableProperty]
    private double _usageFraction;

    [ObservableProperty]
    private string _safetyText = string.Empty;

    [ObservableProperty]
    private string _safetyLevel = "Info";

    [ObservableProperty]
    private bool _prefetchPaused;

    [ObservableProperty]
    private string _planText = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _customBudgetText = string.Empty;

    [ObservableProperty]
    private string _pinPath = string.Empty;

    [ObservableProperty]
    private bool _hasUsage;

    [ObservableProperty]
    private bool _hasPins;

    [ObservableProperty]
    private bool _isCustom;

    public override async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await GuardedRefreshAsync(cancellationToken, async () =>
        {
            CacheStatus status = Services.CacheService.Refresh();

            BudgetText = status.BudgetText;
            UsedText = status.UsedText;
            FreeText = status.FreeText;
            UsageFraction = Math.Clamp(status.BudgetUse, 0d, 1d);
            SafetyText = status.Safety.Explanation;
            SafetyLevel = status.Safety.Level.ToString();
            PrefetchPaused = status.PrefetchPaused;
            PlanText = status.Plan.Describe();
            IsCustom = Services.Settings.Current.CacheBudgetIsCustom;

            Usage.Clear();
            foreach (CacheUsage usage in status.Usages)
            {
                CacheDescriptor? descriptor = status.MountFor(usage.AccountId);
                NimbusX.Mount.MountSnapshot? snapshot = Services.Mounts.GetSnapshot(usage.AccountId);
                Usage.Add(new CacheUsageRow(
                    usage,
                    usage.AccountId,
                    descriptor?.DisplayName ?? snapshot?.DisplayName ?? usage.AccountId,
                    snapshot is null ? "—" : snapshot.DriveLetter + ":"));
            }

            HasUsage = Usage.Count > 0;

            foreach (CachePresetViewModel preset in Presets)
            {
                preset.IsSelected = !IsCustom && preset.Bytes == Services.Settings.Current.CacheBudgetBytes;
                preset.IsAvailable = true;
            }

            try
            {
                ClearableText = ByteSize.Format(Services.CacheService.MeasureClearable());
            }
            catch (Exception exception)
            {
                ClearableText = "Unavailable";
                Logger.Debug("Could not measure clearable cache: " + exception.Message, "Ui");
            }

            ReloadPins();
            await Task.CompletedTask.ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private void ReloadPins()
    {
        Pins.Clear();
        foreach (CacheUsage usage in Services.CacheService.Current.Usages)
        {
            foreach (PinnedEntry pin in Services.CacheService.Pins(usage.AccountId))
            {
                Pins.Add(pin);
            }
        }

        HasPins = Pins.Count > 0;
    }

    [RelayCommand]
    private async Task SelectPresetAsync(CachePresetViewModel? preset)
    {
        if (preset is null)
        {
            return;
        }

        bool applied = Services.CacheService.SetBudgetPreset(preset.Gigabytes);
        StatusMessage = applied
            ? "Budget set to " + preset.Label + ". Each mounted drive gets its share."
            : "That preset is not supported.";
        await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ApplyCustomBudgetAsync()
    {
        if (!ByteSize.TryParse(CustomBudgetText, out long bytes) || bytes <= 0)
        {
            StatusMessage = "Enter a size such as 25 GB or 512 MB.";
            return;
        }

        long clamped = Services.CacheService.ClampBudgetToFreeSpace(bytes);
        bool applied = Services.CacheService.SetCustomBudget(clamped);
        StatusMessage = applied
            ? "Custom budget set to " + ByteSize.Format(clamped) + (clamped != bytes ? " (clamped to the free space)." : ".")
            : "The budget must be greater than zero.";
        await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ClearAllAsync()
    {
        CacheClearResult result = Services.CacheService.ClearEverything(includePinned: false);
        StatusMessage = result.Describe();
        await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ClearAccountAsync(CacheUsageRow? row)
    {
        if (row is null)
        {
            return;
        }

        CacheClearResult result = Services.CacheService.ClearAccount(row.AccountId, includePinned: false);
        StatusMessage = result.Describe();
        await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task AddPinAsync()
    {
        if (string.IsNullOrWhiteSpace(PinPath))
        {
            StatusMessage = "Enter a path inside a mounted drive, for example Games\\Levels\\level01.pak.";
            return;
        }

        // Pin the first mounted drive; the UI asks for the path relative to it.
        string? accountId = Services.Mounts.Snapshots.FirstOrDefault()?.AccountId;
        if (accountId is null)
        {
            StatusMessage = "Mount a drive first.";
            return;
        }

        PinnedEntry? entry = Services.CacheService.PinPath(accountId, PinPath);
        StatusMessage = entry is null
            ? "Could not pin that path."
            : "Pinned " + entry.RelativePath + ". A manual clear will keep it.";
        PinPath = string.Empty;
        await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task UnpinAsync(PinnedEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        Services.CacheService.UnpinPath(entry.AccountId, entry.RelativePath);
        StatusMessage = "Unpinned " + entry.RelativePath + ".";
        await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
    }
}
