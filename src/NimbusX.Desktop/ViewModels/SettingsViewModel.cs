using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NimbusX.App;
using NimbusX.Cache;
using NimbusX.Core.Text;

namespace NimbusX.Desktop.ViewModels;

/// <summary>Settings: cache, behaviour and startup options, all persisted in settings.json.</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    public SettingsViewModel(AppServices services)
        : base(services, "Settings", "Cache, drive letters, health checks and startup behaviour", "\uE713")
    {
        Presets = new ObservableCollection<CachePresetViewModel>();
        DriveLetters = new ObservableCollection<string>();

        foreach (int gigabytes in CachePresets.Gigabytes)
        {
            Presets.Add(new CachePresetViewModel(gigabytes, CachePresets.ToBytes(gigabytes)));
        }

        for (char letter = 'D'; letter <= 'Z'; letter++)
        {
            DriveLetters.Add(letter.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Raised when the user wants the first run wizard again.</summary>
    public event EventHandler? FirstRunRequested;

    /// <summary>Raised after settings were saved so other pages can reload.</summary>
    public event EventHandler? SettingsSaved;

    public ObservableCollection<CachePresetViewModel> Presets { get; }

    public ObservableCollection<string> DriveLetters { get; }

    [ObservableProperty]
    private string _cacheRoot = string.Empty;

    [ObservableProperty]
    private string _preferredDriveLetter = "G";

    [ObservableProperty]
    private int _healthCheckSeconds = 5;

    [ObservableProperty]
    private int _maxRestarts = 5;

    [ObservableProperty]
    private int _verifyLaunchSeconds = 60;

    [ObservableProperty]
    private string _prefetchBudgetText = "2 GB";

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private bool _gameAwareMode = true;

    [ObservableProperty]
    private bool _showExperimentalTerabox = true;

    [ObservableProperty]
    private string _protectedFreeSpaceText = "10 GB";

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string _dataFolder = string.Empty;

    [ObservableProperty]
    private string _logFolder = string.Empty;

    public override Task RefreshAsync(CancellationToken cancellationToken)
    {
        NimbusX.Core.Config.AppSettings settings = Services.Settings.Current;

        CacheRoot = settings.CacheRoot ?? string.Empty;
        PreferredDriveLetter = string.IsNullOrWhiteSpace(settings.PreferredFirstDriveLetter) ? "G" : settings.PreferredFirstDriveLetter!;
        HealthCheckSeconds = settings.HealthCheckSeconds;
        MaxRestarts = settings.MaxRestartsPerTenMinutes;
        VerifyLaunchSeconds = settings.VerifyLaunchSeconds;
        PrefetchBudgetText = ByteSize.Format(settings.PrefetchSessionBudgetBytes);
        ProtectedFreeSpaceText = ByteSize.Format(settings.ProtectedFreeSpaceBytes);
        StartWithWindows = settings.StartWithWindows;
        GameAwareMode = settings.EnableGameAwareMode;
        ShowExperimentalTerabox = settings.ShowExperimentalTeraBox;
        DataFolder = Services.Paths.Root;
        LogFolder = Services.Logger.LogDirectory ?? Services.Paths.LogsDirectory;

        foreach (CachePresetViewModel preset in Presets)
        {
            preset.IsSelected = !settings.CacheBudgetIsCustom && preset.Bytes == settings.CacheBudgetBytes;
        }

        IsDirty = false;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            long prefetchBytes = ByteSize.TryParse(PrefetchBudgetText, out long parsedPrefetch)
                ? parsedPrefetch
                : Services.Settings.Current.PrefetchSessionBudgetBytes;
            long reserveBytes = ByteSize.TryParse(ProtectedFreeSpaceText, out long parsedReserve)
                ? parsedReserve
                : Services.Settings.Current.ProtectedFreeSpaceBytes;

            Services.Settings.Update(settings =>
            {
                settings.PreferredFirstDriveLetter = PreferredDriveLetter.TrimEnd(':');
                settings.HealthCheckSeconds = Math.Clamp(HealthCheckSeconds, 2, 60);
                settings.MaxRestartsPerTenMinutes = Math.Clamp(MaxRestarts, 1, 50);
                settings.VerifyLaunchSeconds = Math.Clamp(VerifyLaunchSeconds, 10, 900);
                settings.PrefetchSessionBudgetBytes = Math.Max(0, prefetchBytes);
                settings.ProtectedFreeSpaceBytes = Math.Max(0, reserveBytes);
                settings.EnableGameAwareMode = GameAwareMode;
                settings.ShowExperimentalTeraBox = ShowExperimentalTerabox;
            });

            bool startupApplied = TryApplyStartWithWindows(StartWithWindows, out string? startupError);
            Services.Settings.Update(settings => settings.StartWithWindows = StartWithWindows && startupApplied);

            StatusMessage = startupApplied
                ? "Saved."
                : "Saved, but the Windows startup entry could not be changed: " + startupError;
            IsDirty = false;
            SettingsSaved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            StatusMessage = "Could not save: " + exception.Message;
        }

        await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ApplyPresetAsync(CachePresetViewModel? preset)
    {
        if (preset is null)
        {
            return;
        }

        bool applied = Services.CacheService.SetBudgetPreset(preset.Gigabytes);
        StatusMessage = applied
            ? "Cache budget set to " + preset.Label + "."
            : "That preset is not supported.";
        await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        SettingsSaved?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ChooseCacheFolder()
    {
        OpenFolderDialog dialog = new()
        {
            Title = "Choose a folder for the NimbusX cache",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        Services.Settings.Update(settings => settings.CacheRoot = dialog.FolderName);
        CacheRoot = dialog.FolderName;
        StatusMessage = "Cache folder set to " + dialog.FolderName + ".";
        SettingsSaved?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void RunWizard() => FirstRunRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            string folder = Services.Logger.LogDirectory ?? Services.Paths.LogsDirectory;
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception exception)
        {
            StatusMessage = "Could not open the log folder: " + exception.Message;
        }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = Services.Paths.Root, UseShellExecute = true });
        }
        catch (Exception exception)
        {
            StatusMessage = "Could not open the data folder: " + exception.Message;
        }
    }

    [RelayCommand]
    private void OpenLicense()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Services.Paths.Root,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            StatusMessage = "Could not open the folder: " + exception.Message;
        }
    }

    /// <summary>
    /// Adds or removes the HKCU Run entry. Only the current user's key is touched, so no administrator rights are
    /// needed and nothing system-wide changes.
    /// </summary>
    private static bool TryApplyStartWithWindows(bool enabled, out string? error)
    {
        error = null;
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run",
                writable: true);

            if (key is null)
            {
                error = "The Windows Run key is not accessible for this user.";
                return false;
            }

            const string valueName = "NimbusX";
            if (enabled)
            {
                string? exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe))
                {
                    error = "The executable path is unknown.";
                    return false;
                }

                key.SetValue(valueName, "\"" + exe + "\" --tray", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }
}
