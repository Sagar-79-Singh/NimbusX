using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NimbusX.Cache;
using NimbusX.Core;
using NimbusX.Core.Config;
using NimbusX.Core.Logging;
using NimbusX.Core.Processes;
using NimbusX.Core.Security;
using NimbusX.Core.Time;
using NimbusX.Core.Windows;
using NimbusX.Diagnostics;
using NimbusX.Games;
using NimbusX.Mount;
using NimbusX.Providers;
using NimbusX.Streaming;

namespace NimbusX.App;

/// <summary>
/// Builds every NimbusX service once and owns their lifetime, so the WPF shell and the <c>cpx</c> CLI share exactly
/// the same behaviour.
/// </summary>
/// <remarks>
/// There is no dependency injection container here on purpose: the graph is small, and explicit construction makes
/// it obvious which component touches secrets, which one starts processes and which one deletes files.
/// </remarks>
public sealed class AppServices : IAsyncDisposable
{
    private bool _disposed;

    private AppServices(
        AppPaths paths,
        AppLogger logger,
        SettingsService settings,
        ISecretStore secrets,
        IProcessRunner runner,
        IClock clock,
        RcloneProvisioner provisioner,
        RcloneControlDaemon standardDaemon,
        RcloneControlDaemon teraboxDaemon,
        RcloneConfigManager config,
        RcloneOneShot oneShot,
        AccountStore accountStore,
        MountManager mounts,
        AccountService accounts,
        CacheManager cache,
        PrefetchEngine prefetch,
        GameStore gameStore,
        GameLibraryScanner scanner,
        GameLauncher launcher,
        GamesService games,
        StreamService streams,
        DiagnosticsService diagnostics,
        DiagnosticsExporter exporter,
        ProfileDatabase profiles,
        AcceleratorService accelerator,
        GameLaunchCoordinator launches)
    {
        Paths = paths;
        Logger = logger;
        Settings = settings;
        Secrets = secrets;
        Runner = runner;
        Clock = clock;
        Provisioner = provisioner;
        StandardDaemon = standardDaemon;
        TeraBoxDaemon = teraboxDaemon;
        Config = config;
        OneShot = oneShot;
        AccountStore = accountStore;
        Mounts = mounts;
        Accounts = accounts;
        CacheService = cache;
        Prefetch = prefetch;
        GameStore = gameStore;
        Scanner = scanner;
        Launcher = launcher;
        Games = games;
        Streams = streams;
        Diagnostics = diagnostics;
        Exporter = exporter;
        Profiles = profiles;
        Accelerator = accelerator;
        Launches = launches;
    }

    public AppPaths Paths { get; }

    public AppLogger Logger { get; }

    public SettingsService Settings { get; }

    public ISecretStore Secrets { get; }

    public IProcessRunner Runner { get; }

    public IClock Clock { get; }

    public RcloneProvisioner Provisioner { get; }

    public RcloneControlDaemon StandardDaemon { get; }

    public RcloneControlDaemon TeraBoxDaemon { get; }

    public RcloneConfigManager Config { get; }

    public RcloneOneShot OneShot { get; }

    public AccountStore AccountStore { get; }

    public MountManager Mounts { get; }

    public AccountService Accounts { get; }

    public CacheManager CacheService { get; }

    public ICacheManager Cache => CacheService;

    public PrefetchEngine Prefetch { get; }

    public GameStore GameStore { get; }

    public GameLibraryScanner Scanner { get; }

    public GameLauncher Launcher { get; }

    public GamesService Games { get; }

    public StreamService Streams { get; }

    public DiagnosticsService Diagnostics { get; }

    public DiagnosticsExporter Exporter { get; }

    public ProfileDatabase Profiles { get; }

    /// <summary>Prepare / warm start for a game, using only measured launch history.</summary>
    public AcceleratorService Accelerator { get; }

    /// <summary>Launches a game and keeps the SQLite launch history up to date.</summary>
    public GameLaunchCoordinator Launches { get; }

    /// <summary>
    /// Creates the whole graph. Browser abstractions are injected so the WPF app can supply WebView2 while the CLI
    /// gets the shell-based fallback.
    /// </summary>
    public static AppServices Create(
        string? dataRoot = null,
        IBrowserLoginSessionFactory? browserSessions = null,
        IBrowserLauncher? browser = null,
        IProcessRunner? runner = null,
        ISecretStore? secrets = null,
        IAppLogger? logger = null,
        IClock? clock = null,
        Func<HttpClient>? httpFactory = null)
    {
        AppPaths paths = string.IsNullOrWhiteSpace(dataRoot)
            ? AppPaths.CreateDefault()
            : new AppPaths(dataRoot!);
        paths.EnsureCreated();

        AppLogger appLogger = logger as AppLogger ?? new AppLogger(paths.LogsDirectory);
        SettingsService settings = new(paths, appLogger);
        settings.Load();

        ISecretStore secretStore = secrets ?? new FileSecretStore(
            new DpapiSecretProtector(),
            paths.SecretsDirectory,
            appLogger);
        IProcessRunner processRunner = runner ?? new ProcessRunner();
        IClock appClock = clock ?? new SystemClock();

        RcloneProvisioner provisioner = new(paths, appLogger, processRunner, httpFactory);
        RcloneControlDaemon standardDaemon = new(paths, provisioner, processRunner, appLogger, secretStore, RcloneFlavor.Standard);
        RcloneControlDaemon teraboxDaemon = new(paths, provisioner, processRunner, appLogger, secretStore, RcloneFlavor.TeraBox);
        RcloneConfigManager config = new(paths, secretStore, appLogger);
        RcloneOneShot oneShot = new(paths, provisioner, processRunner, secretStore, appLogger);
        AccountStore accountStore = new(paths.AccountsFile, appLogger);

        MountManager mounts = new(paths, settings, secretStore, provisioner, processRunner, appClock, appLogger);
        AccountService accounts = new(
            paths,
            secretStore,
            accountStore,
            config,
            standardDaemon,
            teraboxDaemon,
            provisioner,
            oneShot,
            browserSessions ?? new UnavailableBrowserLoginSessionFactory(),
            mounts,
            browser ?? new ShellBrowserLauncher(),
            appLogger);

        PinnedPathStore pins = new(paths.CachePinsFile, appLogger);
        CacheManager cache = new(settings, mounts, appLogger, pins);
        PrefetchEngine prefetch = new(
            mounts,
            appLogger,
            () => !cache.Current.PrefetchPaused && cache.CanPrefetch,
            sessionBudgetBytes: settings.Current.PrefetchSessionBudgetBytes);

        GameStore gameStore = new(paths.GamesFile, appLogger);
        GameLibraryScanner scanner = new(mounts, appLogger);
        GameLauncher launcher = new(mounts, prefetch, appLogger);
        GamesService games = new(gameStore, scanner, launcher, appLogger);

        StreamService streams = new(mounts, appLogger);
        DiagnosticsService diagnostics = new(
            paths,
            settings,
            provisioner,
            new WinFspDetector(processRunner, appLogger),
            mounts,
            accountStore,
            config,
            cache,
            appLogger);
        DiagnosticsExporter exporter = new(paths, appLogger);
        ProfileDatabase profiles = new(paths.DatabaseFile, appLogger);
        AcceleratorService accelerator = new(gameStore, prefetch, profiles, mounts, appLogger);
        GameLaunchCoordinator launches = new(games, profiles, appLogger);

        return new AppServices(
            paths,
            appLogger,
            settings,
            secretStore,
            processRunner,
            appClock,
            provisioner,
            standardDaemon,
            teraboxDaemon,
            config,
            oneShot,
            accountStore,
            mounts,
            accounts,
            cache,
            prefetch,
            gameStore,
            scanner,
            launcher,
            games,
            streams,
            diagnostics,
            exporter,
            profiles,
            accelerator,
            launches);
    }

    /// <summary>Runs the startup sequence and returns what actually happened.</summary>
    public async Task<StartupReport> InitializeAsync(CancellationToken cancellationToken)
    {
        StartupReport report = new();
        Stopwatch step = Stopwatch.StartNew();

        try
        {
            Paths.EnsureCreated();
            Profiles.Initialize();
            report.Add("Profile database", true, "SQLite profile store ready at " + Profiles.FilePath, null, step.Elapsed);
        }
        catch (Exception exception)
        {
            Logger.Error("The profile database could not be opened.", "Startup", exception);
            report.Add(
                "Profile database",
                false,
                exception.Message,
                "Close any other NimbusX window and check that the data folder is writable.");
        }

        step.Restart();
        try
        {
            string keyState = Config.EnsureConfigPassword();
            report.Add(
                "rclone configuration key",
                true,
                keyState,
                null,
                step.Elapsed);
        }
        catch (Exception exception)
        {
            Logger.Error("Could not prepare the rclone configuration key.", "Startup", exception);
            report.Add(
                "rclone configuration key",
                false,
                exception.Message,
                "Windows DPAPI may be unavailable in this session. Sign in to Windows interactively and start NimbusX again.");
        }

        step.Restart();
        RcloneBinaryInfo standard = await Provisioner.InspectAsync(RcloneFlavor.Standard, cancellationToken).ConfigureAwait(false);
        report.Add(
            "rclone (standard)",
            standard.IsUsable,
            standard.Describe(),
            standard.IsUsable ? null : "Open Settings -> Dependencies and press 'Install / repair'.",
            step.Elapsed);

        step.Restart();
        RcloneBinaryInfo terabox = await Provisioner.InspectAsync(RcloneFlavor.TeraBox, cancellationToken).ConfigureAwait(false);
        bool teraboxNeeded = Accounts.Accounts.Any(a => a.ProviderId.Contains("terabox", StringComparison.OrdinalIgnoreCase));
        report.Add(
            "rclone (TeraBox build)",
            terabox.IsUsable || !teraboxNeeded,
            terabox.IsUsable ? terabox.Describe() : terabox.Describe() + " (not needed until a TeraBox account is added)",
            terabox.IsUsable || teraboxNeeded ? "Open Settings -> Dependencies and press 'Install / repair'." : null,
            step.Elapsed);

        step.Restart();
        WinFspStatus winfsp = await new WinFspDetector(Runner, Logger).DetectAsync(cancellationToken).ConfigureAwait(false);
        bool winfspOk = winfsp.Installed && WinFspDetector.IsAtLeast(winfsp.Version, DependencyVersions.MinimumWinFspVersion);
        report.Add(
            "WinFsp",
            winfspOk,
            winfsp.Describe(),
            winfspOk ? null : WinFspDetector.InstallInstructions(),
            step.Elapsed);

        step.Restart();
        try
        {
            ReattachOutcome reattach = await Mounts.ReattachAsync(cancellationToken).ConfigureAwait(false);
            report.ReattachAttempted = true;
            report.ReattachedCount = reattach.AdoptedCount;
            string reattachDetail =
                reattach.AdoptedCount + " mount(s) adopted, " + reattach.DroppedCount + " stale record(s) removed" +
                (reattach.Notes.Count == 0 ? "." : ": " + string.Join("; ", reattach.Notes));
            report.Add(
                "Re-attach existing mounts",
                true,
                reattachDetail,
                null,
                step.Elapsed);
        }
        catch (Exception exception)
        {
            Logger.Error("Re-attaching previous mounts failed.", "Startup", exception);
            report.Add(
                "Re-attach existing mounts",
                false,
                exception.Message,
                "Unmount and mount the drive again from the dashboard.");
        }

        step.Restart();
        CacheStatus cacheStatus = CacheService.Refresh();
        bool cacheOk = !string.IsNullOrWhiteSpace(cacheStatus.CacheRoot);
        report.Add(
            "Cache",
            cacheOk,
            cacheOk ? cacheStatus.Description : (cacheStatus.CacheRoot is null ? "No cache folder configured." : cacheStatus.Description),
            cacheOk ? null : "Run the first run wizard (Settings -> Cache) and choose a folder on a local NTFS drive.",
            step.Elapsed);

        return report;
    }

    /// <summary>Mounts every account that is marked auto-mount and not already mounted.</summary>
    public async Task<(int Mounted, IReadOnlyList<string> Failures)> AutoMountAsync(CancellationToken cancellationToken)
    {
        List<string> failures = new();
        int mounted = 0;
        List<string> accountIds = Accounts.Accounts.Select(a => a.Id).ToList();

        foreach (CloudAccount account in Accounts.Accounts.Where(a => a.AutoMount))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Mounts.GetSnapshot(account.Id) is not null)
            {
                continue;
            }

            ProviderCatalog.TryGet(account.ProviderId, out ProviderDefinition? provider);
            RcloneFlavor flavor = provider is not null && provider.IsExperimental ? RcloneFlavor.TeraBox : RcloneFlavor.Standard;

            MountRequest request = new(account.Id, account.DisplayName, account.ProviderId, account.RemoteName)
            {
                RemotePath = account.RemotePath,
                PreferredDriveLetter = string.IsNullOrWhiteSpace(account.PreferredDriveLetter)
                    ? null
                    : account.PreferredDriveLetter![0],
                Flavor = flavor,
                VolumeName = AppInfo.Name + " - " + account.DisplayName,
                CacheBudgetBytes = CacheService.BudgetFor(account.Id, accountIds),
                CacheMinFreeSpaceBytes = Settings.Current.ProtectedFreeSpaceBytes
            };

            MountResult result = await Mounts.MountAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                mounted++;
            }
            else
            {
                failures.Add(account.DisplayName + ": " + result.Error);
            }
        }

        if (mounted > 0 || failures.Count > 0)
        {
            Logger.Info(
                "Auto-mount finished: " + mounted + " mounted, " + failures.Count + " failed.",
                "Startup");
        }

        return (mounted, failures);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            await Mounts.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Logger.Warn("Shutting down mounts failed: " + exception.Message, "Shutdown");
        }

        Streams.Dispose();
        Games.Dispose();
        Prefetch.Dispose();
        CacheService.Dispose();
        Profiles.Dispose();
        Accounts.Dispose();

        await StandardDaemon.DisposeAsync().ConfigureAwait(false);
        await TeraBoxDaemon.DisposeAsync().ConfigureAwait(false);

        Logger.Info("NimbusX services stopped.", "Shutdown");
        Logger.Dispose();
    }
}
