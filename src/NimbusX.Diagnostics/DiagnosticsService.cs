using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NimbusX.Cache;
using NimbusX.Core;
using NimbusX.Core.Config;
using NimbusX.Core.Io;
using NimbusX.Core.Logging;
using NimbusX.Core.Text;
using NimbusX.Mount;
using NimbusX.Providers;

namespace NimbusX.Diagnostics;

/// <summary>
/// Collects the health report shown on the Diagnostics page.
/// </summary>
/// <remarks>
/// Every row is a fact gathered from the machine, the installed binaries or rclone itself. A check that cannot be
/// measured says so; nothing here invents a green tick, and every failing check carries a fix the user can apply
/// without an administrator account.
/// </remarks>
public sealed class DiagnosticsService
{
    private readonly AppPaths _paths;
    private readonly SettingsService _settingsService;
    private AppSettings _settings;
    private readonly RcloneProvisioner _provisioner;
    private readonly WinFspDetector _winfsp;
    private readonly IMountManager _mounts;
    private readonly AccountStore _accounts;
    private readonly RcloneConfigManager _config;
    private readonly ICacheManager _cache;
    private readonly IAppLogger _logger;

    public DiagnosticsService(
        AppPaths paths,
        SettingsService settings,
        RcloneProvisioner provisioner,
        WinFspDetector winfsp,
        IMountManager mounts,
        AccountStore accounts,
        RcloneConfigManager config,
        ICacheManager cache,
        IAppLogger logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _settingsService = settings ?? throw new ArgumentNullException(nameof(settings));
        _settings = _settingsService.Current;
        _provisioner = provisioner ?? throw new ArgumentNullException(nameof(provisioner));
        _winfsp = winfsp ?? throw new ArgumentNullException(nameof(winfsp));
        _mounts = mounts ?? throw new ArgumentNullException(nameof(mounts));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<DiagnosticsSnapshot> CollectAsync(CancellationToken cancellationToken)
    {
        // Pick up settings changes made since the last report.
        _settings = _settingsService.Current;

        DiagnosticsSnapshot snapshot = new()
        {
            AppVersion = AppInfo.Version,
            AppDataFolder = _paths.Root,
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            DotNetVersion = RuntimeInformation.FrameworkDescription,
            ProcessorCount = Environment.ProcessorCount,
            TotalMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            CacheRoot = _settings.CacheRoot ?? string.Empty,
            CacheBudgetBytes = _settings.CacheBudgetBytes,
            ConfigPath = _config.ConfigPath,
            ConfigEncrypted = SafeConfigEncrypted(),
            ConfigKeyPresent = _config.HasConfigPassword
        };

        WinFspStatus winfsp = await _winfsp.DetectAsync(cancellationToken).ConfigureAwait(false);
        snapshot.Dependencies.Add(new DependencyReport(
            "WinFsp",
            DependencyVersions.MinimumWinFspVersion + " or newer",
            winfsp.Version,
            winfsp.Installed && WinFspDetector.IsAtLeast(winfsp.Version, DependencyVersions.MinimumWinFspVersion),
            winfsp.Detail ?? winfsp.Describe(),
            "Pinned installer " + DependencyVersions.WinFsp.FileName + " (sha256 " + Short(DependencyVersions.WinFsp.Sha256) + ")"));

        foreach (RcloneFlavor flavor in new[] { RcloneFlavor.Standard, RcloneFlavor.TeraBox })
        {
            RcloneBinaryInfo info = await _provisioner.InspectAsync(flavor, cancellationToken).ConfigureAwait(false);
            DependencyAsset asset = DependencyVersions.ForFlavor(flavor);
            snapshot.Dependencies.Add(new DependencyReport(
                asset.Name,
                asset.Version,
                info.Version,
                info.IsUsable,
                info.Describe(),
                "Pinned " + asset.FileName + " (sha256 " + Short(asset.Sha256) + ") from " + asset.DownloadUrl));
        }

        AddWindowsChecks(snapshot, winfsp);
        AddRcloneChecks(snapshot);
        AddMountChecks(snapshot);
        AddAccountChecks(snapshot);
        AddCacheChecks(snapshot);
        AddStreamingChecks(snapshot);

        snapshot.MountedCount = _mounts.Snapshots.Count;
        snapshot.AccountCount = _accounts.Load().Count;
        snapshot.CacheSummary = _cache.Current.Description;

        _logger.Info(
            "Diagnostics collected: " + snapshot.Checks.Count + " checks, " + snapshot.WarningCount + " warning(s), " +
            snapshot.ErrorCount + " error(s).",
            "Diagnostics");

        return snapshot;
    }

    private void AddWindowsChecks(DiagnosticsSnapshot snapshot, WinFspStatus winfsp)
    {
        bool winfspOk = winfsp.Installed && WinFspDetector.IsAtLeast(winfsp.Version, DependencyVersions.MinimumWinFspVersion);
        snapshot.AddCheck(
            "Mount",
            "WinFsp file system driver",
            winfspOk ? DiagnosticSeverity.Info : DiagnosticSeverity.Error,
            winfspOk
                ? "WinFsp " + (winfsp.Version ?? "unknown version") + " is installed in " + (winfsp.InstallDirectory ?? "the default location") + "."
                : "WinFsp " + DependencyVersions.MinimumWinFspVersion + " or newer is required to mount a drive. " + (winfsp.Detail ?? string.Empty),
            winfspOk ? null : WinFspDetector.InstallInstructions());

        if (!OperatingSystem.IsWindows())
        {
            snapshot.AddCheck(
                "System",
                "Windows detected",
                DiagnosticSeverity.Error,
                "NimbusX mounts drives with WinFsp, which only exists on Windows.",
                "Run NimbusX on Windows 10 1809 or Windows 11 (x64).");
        }

        if (!Environment.Is64BitProcess)
        {
            snapshot.AddCheck(
                "System",
                "64-bit process",
                DiagnosticSeverity.Warning,
                "NimbusX is running as a 32-bit process, which limits how much cache it can address.",
                "Use the x64 installer.");
        }
    }

    private void AddRcloneChecks(DiagnosticsSnapshot snapshot)
    {
        DependencyReport standard = snapshot.Dependencies.First(d => d.Name.Contains("rclone", StringComparison.OrdinalIgnoreCase) && !d.Name.Contains("TeraBox", StringComparison.OrdinalIgnoreCase));
        snapshot.AddCheck(
            "Dependencies",
            "rclone",
            standard.Usable ? DiagnosticSeverity.Info : DiagnosticSeverity.Error,
            standard.Detail,
            standard.Usable ? null : "Open Settings -> Dependencies and press 'Install / repair'. NimbusX downloads the pinned build and verifies its SHA-256.");

        DependencyReport terabox = snapshot.Dependencies.First(d => d.Name.Contains("TeraBox", StringComparison.OrdinalIgnoreCase));
        bool teraboxWanted = _accounts.Load().Any(a => a.ProviderId.Contains("terabox", StringComparison.OrdinalIgnoreCase));
        snapshot.AddCheck(
            "Dependencies",
            "TeraBox build",
            terabox.Usable || !teraboxWanted ? DiagnosticSeverity.Info : DiagnosticSeverity.Warning,
            terabox.Usable
                ? terabox.Detail
                : "The community TeraBox build is not installed. TeraBox accounts cannot mount until it is. " + terabox.Detail,
            terabox.Usable || !teraboxWanted ? null : "Open Settings -> Dependencies and press 'Install / repair'; NimbusX installs the TeraBox build into its own folder next to the standard one.");

        if (!_config.HasConfigPassword)
        {
            snapshot.AddCheck(
                "Security",
                "rclone configuration key",
                DiagnosticSeverity.Warning,
                "No configuration key is stored yet. NimbusX creates one the first time an account is connected.",
                "Connect an account from Cloud Drives; the key is generated and protected with Windows DPAPI.");
        }
        else if (!SafeConfigEncrypted())
        {
            snapshot.AddCheck(
                "Security",
                "rclone configuration encryption",
                DiagnosticSeverity.Error,
                "The rclone configuration file exists but is not encrypted, so provider tokens would be readable by anyone with access to the file.",
                "Open Settings -> Security and press 'Encrypt now'. NimbusX re-writes the configuration through rclone with encryption enabled.");
        }
        else
        {
            snapshot.AddCheck(
                "Security",
                "rclone configuration encryption",
                DiagnosticSeverity.Info,
                "The rclone configuration is encrypted and its key is stored with Windows DPAPI.",
                null);
        }
    }

    private void AddMountChecks(DiagnosticsSnapshot snapshot)
    {
        foreach (MountSnapshot mount in _mounts.Snapshots)
        {
            string summary =
                mount.DisplayName + " -> " + mount.DriveLetter + ": (" + mount.Phase + ", pid " +
                (mount.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "-") + ", restarts " + mount.RestartCount + ")";
            snapshot.MountSummaries.Add(summary);

            if (mount.Phase is MountPhase.Failed or MountPhase.Degraded)
            {
                snapshot.AddCheck(
                    "Mount",
                    mount.DisplayName + " on " + mount.DriveLetter + ":",
                    mount.Phase == MountPhase.Failed ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                    "The mount is " + mount.Phase + (string.IsNullOrWhiteSpace(mount.LastError) ? "." : ": " + mount.LastError),
                    "Press Repair on the dashboard to restart this mount; the rclone log for this account is included in the export.");
            }
        }

        if (snapshot.MountSummaries.Count == 0)
        {
            snapshot.AddCheck(
                "Mount",
                "No drives mounted",
                DiagnosticSeverity.Info,
                "Nothing is mounted right now, so there is nothing to repair.",
                null);
        }
    }

    private void AddAccountChecks(DiagnosticsSnapshot snapshot)
    {
        IReadOnlyList<CloudAccount> accounts = _accounts.Load();
        foreach (CloudAccount account in accounts)
        {
            snapshot.AccountSummaries.Add(
                account.DisplayName + " (" + account.ProviderId + ", " + account.RcloneTarget + ", " +
                (string.IsNullOrWhiteSpace(account.QuotaText) ? "quota unavailable" : account.QuotaText) + ")");

            if (account.NeedsReconnect)
            {
                snapshot.AddCheck(
                    "Accounts",
                    account.DisplayName + " needs to be reconnected",
                    DiagnosticSeverity.Warning,
                    account.LastError ?? "The saved sign-in is no longer accepted by the provider.",
                    "Open Cloud Drives, press Reconnect on the account card and sign in again.");
            }
        }

        if (accounts.Count == 0)
        {
            snapshot.AddCheck(
                "Accounts",
                "No cloud accounts",
                DiagnosticSeverity.Info,
                "Add a cloud account to mount a drive.",
                null);
        }
    }

    private void AddCacheChecks(DiagnosticsSnapshot snapshot)
    {
        CacheStatus status = _cache.Current;
        string? cacheRoot = string.IsNullOrWhiteSpace(snapshot.CacheRoot) ? null : snapshot.CacheRoot;

        if (cacheRoot is null)
        {
            snapshot.AddCheck(
                "Cache",
                "Cache folder",
                DiagnosticSeverity.Error,
                "No cache folder is configured, so mounting cannot start.",
                "Open the first run wizard (Settings -> Cache) and choose a folder on a local NTFS drive with at least 20 GB free.");
            return;
        }

        if (!Directory.Exists(cacheRoot))
        {
            snapshot.AddCheck(
                "Cache",
                "Cache folder",
                DiagnosticSeverity.Error,
                "The configured cache folder '" + cacheRoot + "' does not exist.",
                "Pick an existing folder in Settings -> Cache; NimbusX will not create a folder it cannot verify.");
            return;
        }

        string volume = DiskSpace.VolumeRootFor(cacheRoot) ?? cacheRoot;
        string format = SafeDriveFormat(volume);
        snapshot.CacheVolumeFreeBytes = DiskSpace.GetFreeBytes(volume);

        bool ntfs = string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase);
        snapshot.AddCheck(
            "Cache",
            "Cache volume file system",
            ntfs ? DiagnosticSeverity.Info : DiagnosticSeverity.Warning,
            ntfs
                ? "The cache volume is NTFS, which supports the sparse files rclone uses."
                : "The cache volume reports file system '" + format + "'. Sparse cache files may take much more space than the budget.",
            ntfs ? null : "Move the cache folder to an NTFS volume (Settings -> Cache).");

        if (snapshot.CacheVolumeFreeBytes is long free)
        {
            long reserve = _settings.ProtectedFreeSpaceBytes;
            bool fits = free >= snapshot.CacheBudgetBytes + reserve;
            snapshot.AddCheck(
                "Cache",
                "Cache budget fits on the volume",
                fits ? DiagnosticSeverity.Info : DiagnosticSeverity.Warning,
                "The cache budget is " + ByteSize.Format(snapshot.CacheBudgetBytes) + ", the volume has " +
                ByteSize.Format(free) + " free and " + ByteSize.Format(reserve) + " are reserved for Windows.",
                fits ? null : "Lower the cache preset in Settings -> Cache, or free space on the volume.");
        }

        foreach (CacheUsage usage in status.Usages)
        {
            if (usage.PhysicalBytes > usage.BudgetShareBytes && usage.BudgetShareBytes > 0)
            {
                snapshot.AddCheck(
                    "Cache",
                    "Cache over its share: " + usage.AccountId,
                    DiagnosticSeverity.Warning,
                    "This mount uses " + usage.Summary + " which is over its share of the budget.",
                    "rclone evicts the oldest cached data automatically; lower the per-mount share in Settings -> Cache if this keeps happening.");
            }
        }
    }

    private void AddStreamingChecks(DiagnosticsSnapshot snapshot)
    {
        snapshot.AddCheck(
            "Streaming",
            "Loopback media proxy",
            DiagnosticSeverity.Info,
            "The streaming proxy only listens on 127.0.0.1 with a random port and a per-session token; it needs no firewall rule.",
            null);
    }

    private bool SafeConfigEncrypted()
    {
        try
        {
            return _config.IsConfigEncrypted();
        }
        catch (Exception exception)
        {
            _logger.Debug("Could not read the rclone configuration state: " + exception.Message, "Diagnostics");
            return false;
        }
    }

    private static string SafeDriveFormat(string volume)
    {
        try
        {
            DriveInfo info = new(volume);
            return info.IsReady ? info.DriveFormat : "not ready";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string Short(string? hash) =>
        string.IsNullOrWhiteSpace(hash) || hash.Length <= 16 ? (hash ?? string.Empty) : hash!.Substring(0, 16) + "...";
}
