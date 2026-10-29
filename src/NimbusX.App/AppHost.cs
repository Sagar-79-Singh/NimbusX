using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NimbusX.Cache;
using NimbusX.Core;
using NimbusX.Core.Config;
using NimbusX.Core.Logging;
using NimbusX.Core.Windows;
using NimbusX.Games;
using NimbusX.Mount;
using NimbusX.Providers;

namespace NimbusX.App;

/// <summary>
/// Runs the application: single instance lock, startup sequence, health/cache loops and an orderly shutdown.
/// </summary>
/// <remarks>
/// <para>
/// The health loop is what keeps mounts honest. It asks rclone whether the process is alive and the rc endpoint
/// answer, restarts a mount that died, and gives up after the configured restart budget instead of looping forever.
/// </para>
/// <para>
/// Only one NimbusX process may own the data folder, because two processes starting rclone on the same cache folder
/// would corrupt it. A second instance gets <c>ownerProcessId</c> back so it can activate the first window.
/// </para>
/// </remarks>
public sealed class AppHost : IAsyncDisposable
{
    private readonly AppServices _services;
    private readonly CancellationTokenSource _cts = new();
    private SingleInstanceLock? _lock;
    private Task? _healthLoop;
    private Task? _cacheLoop;
    private bool _disposed;

    private AppHost(AppServices services)
    {
        _services = services;
        Startup = new StartupReport();

        _services.Mounts.SessionChanged += (_, snapshot) => MountChanged?.Invoke(this, snapshot);
        _services.CacheService.StatusChanged += (_, status) => CacheChanged?.Invoke(this, status);
        _services.Games.GameStarted += (_, game) => GameStarted?.Invoke(this, game);
        _services.Games.GameExited += (_, game) => GameExited?.Invoke(this, game);
        _services.Accounts.AccountChanged += (_, account) => AccountChanged?.Invoke(this, account);
        _services.Accounts.AccountRemoved += (_, id) => AccountRemoved?.Invoke(this, id);
    }

    public event EventHandler<MountSnapshot>? MountChanged;

    public event EventHandler<CacheStatus>? CacheChanged;

    public event EventHandler<RunningGame>? GameStarted;

    public event EventHandler<RunningGame>? GameExited;

    public event EventHandler<NimbusX.Providers.CloudAccount>? AccountChanged;

    public event EventHandler<string>? AccountRemoved;

    public AppServices Services => _services;

    public StartupReport Startup { get; private set; }

    public bool IsRunning { get; private set; }

    /// <summary>
    /// Takes the single instance lock and builds the service graph.
    /// </summary>
    /// <param name="ownerProcessId">Set when another NimbusX already owns the data folder.</param>
    public static AppHost? TryCreate(
        out int? ownerProcessId,
        string? dataRoot = null,
        IBrowserLoginSessionFactory? browserSessions = null,
        IBrowserLauncher? browser = null,
        IAppLogger? logger = null)
    {
        AppPaths paths = string.IsNullOrWhiteSpace(dataRoot) ? AppPaths.CreateDefault() : new AppPaths(dataRoot!);
        paths.EnsureCreated();

        if (!SingleInstanceLock.TryAcquire(paths.Root, out SingleInstanceLock? held, out ownerProcessId))
        {
            ownerProcessId ??= SingleInstanceLock.ReadOwnerProcessId(paths.Root);
            return null;
        }

        AppServices services = AppServices.Create(paths.Root, browserSessions, browser, logger: logger);
        AppHost host = new(services);
        host._lock = held;
        return host;
    }

    public async Task<StartupReport> StartAsync(bool autoMount, CancellationToken cancellationToken)
    {
        if (IsRunning)
        {
            return Startup;
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);

        Startup = await _services.InitializeAsync(linked.Token).ConfigureAwait(false);

        if (autoMount && Startup.CanMount)
        {
            (int mounted, IReadOnlyList<string> failures) =
                await _services.AutoMountAsync(linked.Token).ConfigureAwait(false);
            Startup.AutoMountedCount = mounted;
            Startup.AutoMountFailures = failures;
            Startup.Add(
                "Auto-mount",
                failures.Count == 0,
                mounted + " drive(s) mounted automatically" + (failures.Count == 0 ? "." : "; " + string.Join("; ", failures)),
                failures.Count == 0 ? null : "Open the dashboard and press Mount for the accounts listed above.");
        }
        else if (autoMount)
        {
            Startup.Add(
                "Auto-mount",
                false,
                "Skipped because WinFsp, rclone or the cache folder is not ready.",
                "Fix the failing checks above, then mount from the dashboard.");
        }

        _services.Prefetch.ResetBudget(_services.Settings.Current.PrefetchSessionBudgetBytes);
        IsRunning = true;

        _healthLoop = Task.Run(() => HealthLoopAsync(_cts.Token));
        _cacheLoop = Task.Run(() => CacheLoopAsync(_cts.Token));

        _services.Logger.Info(
            "NimbusX started. " + Startup.Headline + " (" + Startup.Steps.Count + " startup step(s), " + Startup.FailedCount + " failed).",
            "Startup");

        return Startup;
    }

    /// <summary>Runs one health pass immediately (the dashboard Repair button uses this).</summary>
    public Task<HealthCheckOutcome> RunHealthCheckAsync(CancellationToken cancellationToken) =>
        _services.Mounts.HealthCheckAsync(cancellationToken);

    /// <summary>Restarts one mount by unmounting and mounting it again with the same settings.</summary>
    public async Task<MountResult> RepairMountAsync(string accountId, CancellationToken cancellationToken)
    {
        CloudAccount? account = _services.Accounts.Find(accountId);
        if (account is null)
        {
            return MountResult.Fail("That account no longer exists.", "Refresh the account list on the Cloud Drives page.");
        }

        await _services.Mounts.UnmountAsync(accountId, cancellationToken).ConfigureAwait(false);

        ProviderCatalog.TryGet(account.ProviderId, out ProviderDefinition definition);
        RcloneFlavor flavor = definition is not null && definition.IsExperimental ? RcloneFlavor.TeraBox : RcloneFlavor.Standard;

        MountRequest request = new(account.Id, account.DisplayName, account.ProviderId, account.RemoteName)
        {
            RemotePath = account.RemotePath,
            PreferredDriveLetter = string.IsNullOrWhiteSpace(account.PreferredDriveLetter) ? null : account.PreferredDriveLetter![0],
            Flavor = flavor,
            VolumeName = AppInfo.Name + " - " + account.DisplayName,
            CacheBudgetBytes = _services.Cache.BudgetFor(account.Id),
            CacheMinFreeSpaceBytes = _services.Settings.Current.ProtectedFreeSpaceBytes
        };

        MountResult result = await _services.Mounts.MountAsync(request, cancellationToken).ConfigureAwait(false);
        _services.Logger.Info(
            "Repair of " + account.DisplayName + ": " + (result.Success ? "mounted on " + result.Snapshot?.DriveLetter + ":" : "failed - " + result.Error),
            "Mount");

        return result;
    }

    public async Task ShutdownAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;

        try
        {
            _cts.Cancel();
        }
        catch (Exception)
        {
            // already cancelled
        }

        foreach (Task? loop in new[] { _healthLoop, _cacheLoop })
        {
            if (loop is null)
            {
                continue;
            }

            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
            catch (Exception exception)
            {
                _services.Logger.Warn("A background loop ended with an error: " + exception.Message, "Shutdown");
            }
        }

        _services.Logger.Info("Shutting down: unmounting drives (uploads are given time to finish).", "Shutdown");

        await _services.DisposeAsync().ConfigureAwait(false);
        _lock?.Dispose();
        _lock = null;
    }

    private async Task HealthLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            int seconds = Math.Clamp(_services.Settings.Current.HealthCheckSeconds, 2, 60);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                HealthCheckOutcome outcome = await _services.Mounts.HealthCheckAsync(cancellationToken).ConfigureAwait(false);
                if (outcome.RestartedCount > 0 || outcome.FailedCount > 0)
                {
                    _services.Logger.Warn("Health check: " + outcome + ".", "Mount");
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                _services.Logger.Warn("Health check failed: " + exception.Message, "Mount");
            }
        }
    }

    private async Task CacheLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                CacheStatus status = _services.CacheService.Refresh();
                if (status.PrefetchPaused && !_services.Prefetch.IsPaused)
                {
                    _services.Prefetch.Pause();
                }
                else if (!status.PrefetchPaused && _services.Prefetch.IsPaused)
                {
                    // Only resume automatically when no game is running; game-aware mode owns the pause otherwise.
                    if (!_services.Launcher.AnyGameRunning)
                    {
                        _services.Prefetch.Resume();
                    }
                }
            }
            catch (Exception exception)
            {
                _services.Logger.Debug("Cache refresh failed: " + exception.Message, "Cache");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await ShutdownAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
