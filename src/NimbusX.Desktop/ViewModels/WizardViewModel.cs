using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NimbusX.Core.Io;
using NimbusX.Mount;
using NimbusX.App;
using NimbusX.Cache;
using NimbusX.Core.Text;

namespace NimbusX.Desktop.ViewModels;

/// <summary>
/// First run wizard: choose where the cache lives and how big it may get.
/// </summary>
/// <remarks>
/// The wizard refuses a folder that does not exist or that sits on a mounted cloud drive, because a cache on the
/// network would defeat its purpose and clearing it could be dangerous.
/// </remarks>
public sealed partial class WizardViewModel : ObservableObject
{
    private readonly AppServices _services;

    public WizardViewModel(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        Presets = new ObservableCollection<CachePresetViewModel>();

        foreach (int gigabytes in CachePresets.Gigabytes)
        {
            Presets.Add(new CachePresetViewModel(gigabytes, CachePresets.ToBytes(gigabytes)));
        }

        string? suggested = NimbusX.Core.Config.SettingsService.SuggestCacheRoot();
        CacheRoot = services.Settings.Current.CacheRoot ?? suggested ?? string.Empty;

        CachePresetViewModel? selected = Presets.FirstOrDefault(p => p.Bytes == services.Settings.Current.CacheBudgetBytes);
        (selected ?? Presets.FirstOrDefault(p => p.Gigabytes == 20) ?? Presets[0]).IsSelected = true;
        SelectedPreset = selected ?? Presets.FirstOrDefault(p => p.Gigabytes == 20) ?? Presets[0];

        FreeSpaceText = ByteSize.Format(_services.Settings.Current.ProtectedFreeSpaceBytes) + " is kept free for Windows.";
    }

    public ObservableCollection<CachePresetViewModel> Presets { get; }

    [ObservableProperty]
    private string _cacheRoot = string.Empty;

    [ObservableProperty]
    private CachePresetViewModel? _selectedPreset;

    [ObservableProperty]
    private string _freeSpaceText = string.Empty;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private int _step;

    /// <summary>Raised when the wizard is done so the window can close.</summary>
    public event EventHandler<bool>? Closed;

    public bool IsFirstStep => Step == 0;

    public bool IsSecondStep => Step == 1;

    public bool IsThirdStep => Step == 2;

    partial void OnStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsFirstStep));
        OnPropertyChanged(nameof(IsSecondStep));
        OnPropertyChanged(nameof(IsThirdStep));
    }

    partial void OnSelectedPresetChanged(CachePresetViewModel? value)
    {
        foreach (CachePresetViewModel preset in Presets)
        {
            preset.IsSelected = ReferenceEquals(preset, value);
        }
    }

    [RelayCommand]
    private void SelectPreset(CachePresetViewModel? preset)
    {
        if (preset is null)
        {
            return;
        }

        SelectedPreset = preset;
        StatusText = preset.Label + " of cached data, split between the drives you mount.";
    }

    [RelayCommand]
    private void Browse()
    {
        OpenFolderDialog dialog = new()
        {
            Title = "Choose a cache folder on a local drive",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            CacheRoot = dialog.FolderName;
        }
    }

    [RelayCommand]
    private void Next()
    {
        ClearError();

        if (Step == 0)
        {
            Step = 1;
            return;
        }

        if (Step == 1)
        {
            Step = 2;
            StatusText = "NimbusX will create " + CacheRoot + " and reserve " + (SelectedPreset?.Label ?? "20 GB") + ".";
            return;
        }

        Complete();
    }

    [RelayCommand]
    private void Back()
    {
        if (Step > 0)
        {
            Step--;
        }
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke(this, false);

    [RelayCommand]
    private async Task FinishAsync()
    {
        Complete();
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private void Complete()
    {
        if (string.IsNullOrWhiteSpace(CacheRoot))
        {
            SetError("Choose a folder for the cache.");
            return;
        }

        try
        {
            if (!Directory.Exists(CacheRoot))
            {
                Directory.CreateDirectory(CacheRoot);
            }

            foreach (MountSnapshot mount in _services.Mounts.Snapshots)
            {
                if (PathGuard.IsUnder(CacheRoot, mount.DriveRoot))
                {
                    SetError("That folder is on the mounted cloud drive " + mount.DriveLetter + ":. Choose a local drive.");
                    return;
                }
            }

            _services.Settings.Update(settings =>
            {
                settings.CacheRoot = CacheRoot;
                settings.FirstRunCompleted = true;
            });

            if (SelectedPreset is not null)
            {
                _services.CacheService.SetBudgetPreset(SelectedPreset.Gigabytes);
            }

            _services.CacheService.Refresh();
            StatusText = "Cache ready at " + CacheRoot + ".";
            Closed?.Invoke(this, true);
        }
        catch (Exception exception)
        {
            SetError("Could not prepare the cache folder: " + exception.Message);
        }
    }

    private void SetError(string message)
    {
        ErrorText = message;
        HasError = true;
    }

    private void ClearError()
    {
        ErrorText = null;
        HasError = false;
    }
}
