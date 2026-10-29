using System;
using System.IO;
using NimbusX.Core.Io;
using NimbusX.Core.Json;
using NimbusX.Core.Logging;

namespace NimbusX.Core.Config;

/// <summary>
/// Loads/saves <see cref="AppSettings"/> atomically and notifies listeners of changes.
/// </summary>
public sealed class SettingsService
{
    private readonly AppPaths _paths;
    private readonly IAppLogger _logger;
    private readonly object _gate = new();
    private AppSettings _current = new();

    public SettingsService(AppPaths paths, IAppLogger logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Raised after settings have been loaded or changed.</summary>
    public event EventHandler<AppSettings>? Changed;

    /// <summary>Settings that are safe to point at for read-only binding.</summary>
    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public string FilePath => _paths.SettingsFile;

    /// <summary>Loads settings from disk, creating defaults on first run.</summary>
    public AppSettings Load()
    {
        lock (_gate)
        {
            AppSettings settings = AtomicFile.ReadJson(_paths.SettingsFile, new AppSettings()) ?? new AppSettings();
            if (settings.SchemaVersion < AppSettings.CurrentSchemaVersion)
            {
                _logger.Info(
                    "Migrating settings from schema " + settings.SchemaVersion + " to " + AppSettings.CurrentSchemaVersion + ".",
                    "Settings");
                settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
            }

            _current = Normalize(settings);
            return _current;
        }
    }

    /// <summary>Saves the current settings.</summary>
    public void Save()
    {
        AppSettings snapshot;
        lock (_gate)
        {
            snapshot = _current;
        }

        try
        {
            AtomicFile.WriteJson(_paths.SettingsFile, snapshot);
        }
        catch (Exception exception)
        {
            _logger.Error("Could not save settings to " + _paths.SettingsFile + ".", "Settings", exception);
            throw;
        }
    }

    /// <summary>Mutates the settings, saves them and raises <see cref="Changed"/>.</summary>
    public AppSettings Update(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        AppSettings snapshot;
        lock (_gate)
        {
            mutate(_current);
            _current = Normalize(_current);
            snapshot = _current;
        }

        Save();
        Changed?.Invoke(this, snapshot);
        return snapshot;
    }

    private AppSettings Normalize(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.CacheRoot))
        {
            settings.CacheRoot = SuggestCacheRoot();
        }

        if (settings.CacheBudgetBytes <= 0)
        {
            settings.CacheBudgetBytes = 20L * 1024 * 1024 * 1024;
            settings.CacheBudgetPresetGb = 20;
        }

        if (settings.HealthCheckSeconds < 1)
        {
            settings.HealthCheckSeconds = 5;
        }

        if (settings.MaxRestartsPerTenMinutes < 1)
        {
            settings.MaxRestartsPerTenMinutes = 5;
        }

        if (settings.UnmountUploadGraceSeconds < 0)
        {
            settings.UnmountUploadGraceSeconds = 60;
        }

        if (settings.VerifyLaunchSeconds < 5)
        {
            settings.VerifyLaunchSeconds = 60;
        }

        settings.PoolAccountIds ??= new();
        settings.DismissedWarnings ??= new();
        settings.CacheShareOverride ??= new();
        return settings;
    }

    /// <summary>
    /// First run helper: the NTFS fixed drive with the most free space, which is what the wizard suggests.
    /// Returns null when no fixed drive could be read, in which case the wizard asks the user to pick.
    /// </summary>
    public static string? SuggestCacheRoot()
    {
        string? best = null;
        long bestFree = -1;
        foreach (LocalVolume volume in DiskSpace.GetVolumes())
        {
            if (!volume.IsNtfs || volume.FreeBytes <= bestFree)
            {
                continue;
            }

            best = Path.Combine(volume.Root, "NimbusXCache");
            bestFree = volume.FreeBytes;
        }

        return best;
    }
}
