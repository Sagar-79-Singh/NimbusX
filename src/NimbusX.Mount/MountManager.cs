using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NimbusX.Core;
using NimbusX.Core.Config;
using NimbusX.Core.Logging;
using NimbusX.Core.Ports;
using NimbusX.Core.Processes;
using NimbusX.Core.Security;
using NimbusX.Core.Text;
using NimbusX.Core.Time;
using NimbusX.Core.Windows;

namespace NimbusX.Mount;

/// <summary>
/// Owns every rclone mount: allocation of drive letters and cache folders, start, health checks, crash restarts,
/// graceful unmount and re-attach after an app restart.
/// </summary>
/// <remarks>
/// <para>
/// One cloud account equals one rclone process, one drive letter, one cache folder and one rc endpoint, so
/// three Google accounts behave like three independent drives. The manager never mounts two accounts on the
/// same letter: the allocator is asked for a free letter while every other live mount is reserved.
/// </para>
/// <para>
/// Secrets are resolved from <see cref="ISecretStore"/> at start time and handed to rclone through the child
/// process environment only. The rc password is regenerated for every start (and for every restart) and stored
/// with DPAPI so a crash-restarted app can talk to a mount it did not spawn.
/// </para>
/// </remarks>
public sealed class MountManager : IMountManager, IDisposable
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ResumeGap = TimeSpan.FromSeconds(30);

    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly ISecretStore _secrets;
    private readonly RcloneProvisioner _provisioner;
    private readonly IProcessRunner _runner;
    private readonly IClock _clock;
    private readonly IAppLogger _logger;
    private readonly DriveLetterAllocator _allocator;
    private readonly MountStore _store;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, MountSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DateTimeOffset>> _restartHistory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _consecutiveFailures = new(StringComparer.OrdinalIgnoreCase);

    private DateTimeOffset? _lastHealthCheckUtc;
    private bool _disposed;

    public MountManager(
        AppPaths paths,
        SettingsService settings,
        ISecretStore secrets,
        RcloneProvisioner provisioner,
        IProcessRunner runner,
        IClock clock,
        IAppLogger logger,
        DriveLetterAllocator? allocator = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _provisioner = provisioner ?? throw new ArgumentNullException(nameof(provisioner));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? NullLogger.Instance;
        _allocator = allocator ?? new DriveLetterAllocator();
        _store = new MountStore(paths.MountsFile, _logger);
    }

    public event EventHandler<MountSnapshot>? SessionChanged;

    public string ConfigPath => _paths.RcloneConfigFile;

    public IReadOnlyList<MountSnapshot> Snapshots
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Values
                    .OrderBy(s => s.DriveLetter.Letter)
                    .Select(s => s.ToSnapshot())
                    .ToArray();
            }
        }
    }

    /// <summary>Number of live mounts (diagnostics).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    public MountSnapshot? GetSnapshot(string accountId)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(accountId ?? string.Empty, out MountSession? session) ? session.ToSnapshot() : null;
        }
    }

    public RcloneRcClient? GetRcClient(string accountId)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(accountId ?? string.Empty, out MountSession? session) ? session.Rc : null;
        }
    }

    public bool IsReady(string accountId)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(accountId ?? string.Empty, out MountSession? session) &&
                   session.Phase == MountPhase.Ready;
        }
    }

    public IReadOnlyList<DriveLetter> ReservedLetters()
    {
        List<DriveLetter> letters = new();
        lock (_gate)
        {
            letters.AddRange(_sessions.Values.Select(s => s.DriveLetter));
        }

        if (_settings.Current.PoolModeEnabled && DriveLetter.TryParse(_settings.Current.PoolDriveLetter, out DriveLetter pool))
        {
            letters.Add(pool);
        }

        return letters;
    }

    public async Task<MountResult> MountAsync(MountRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_sessions.TryGetValue(request.AccountId, out MountSession? existing))
                {
                    if (existing.Phase == MountPhase.Ready)
                    {
                        return MountResult.Ok(existing.ToSnapshot());
                    }

                    return MountResult.Fail(
                        "This account is already mounted (" + existing.DriveLetter.Display + ") but not ready yet.",
                        "Wait a few seconds, or unmount it first.");
                }
            }

            RcloneBinaryInfo binary = await _provisioner.InspectAsync(request.Flavor, cancellationToken).ConfigureAwait(false);
            if (!binary.IsUsable)
            {
                return MountResult.Fail(
                    binary.Describe(),
                    "Open Settings -> Dependencies and press 'Install / repair', then mount again.");
            }

            string? cacheRoot = _settings.Current.CacheRoot;
            if (string.IsNullOrWhiteSpace(cacheRoot))
            {
                return MountResult.Fail(
                    "No cache folder is configured.",
                    "Run the first run wizard (Settings -> Cache) to choose a folder on an NTFS drive.");
            }

            DriveLetter? preferred = request.FixedDriveLetter;
            if (preferred is null && DriveLetter.TryParse(request.PreferredDriveLetter?.ToString(), out DriveLetter configured))
            {
                preferred = configured;
            }

            DriveLetter letter = preferred ?? _allocator.Allocate(null, ReservedLetters());
            if (!letter.IsValid)
            {
                return MountResult.Fail("The chosen drive letter is not usable.", "Pick a letter between C and Z.");
            }

            if (!request.FixedDriveLetter.HasValue)
            {
                letter = _allocator.Allocate(letter, ReservedLetters());
            }
            else if (_allocator.IsFree(request.FixedDriveLetter.Value, ReservedLetters().Where(l => l != request.FixedDriveLetter.Value)))
            {
                letter = request.FixedDriveLetter.Value;
            }
            else
            {
                return MountResult.Fail(
                    "Drive letter " + request.FixedDriveLetter.Value.Display + " is already in use.",
                    "Unmount the drive using that letter, or let NimbusX choose the next free letter.");
            }

            MountCacheLayout.EnsureCacheDirectory(cacheRoot!, letter, request.AccountId, _logger);

            string? configPassword = _secrets.TryLoad(SecretKeys.RcloneConfigPassword);
            if (string.IsNullOrEmpty(configPassword))
            {
                return MountResult.Fail(
                    "The rclone configuration key is missing from Windows credential storage.",
                    "Open Cloud Drives and connect an account again; NimbusX will recreate the configuration.");
            }

            string? teraboxCookie = null;
            if (request.Flavor == RcloneFlavor.TeraBox)
            {
                teraboxCookie = _secrets.TryLoad(SecretKeys.TeraboxCookie(request.AccountId));
                if (string.IsNullOrWhiteSpace(teraboxCookie))
                {
                    return MountResult.Fail(
                        "The TeraBox session has expired.",
                        "Press 'Reconnect TeraBox' on the account card and sign in again.");
                }
            }

            int rcPort = PortAllocator.GetFreePort();
            string rcUser = AppInfo.Name.ToLowerInvariant() + "_" + RandomSecrets.Hex(4);
            string rcPassword = RandomSecrets.Password();
            _secrets.Save(SecretKeys.RcPassword(request.AccountId), rcPassword);

            long budget = request.CacheBudgetBytes > 0 ? request.CacheBudgetBytes : DefaultBudgetShare();
            long reserve = request.CacheMinFreeSpaceBytes > 0 ? request.CacheMinFreeSpaceBytes : _settings.Current.ProtectedFreeSpaceBytes;

            MountSpec spec = new(
                request.AccountId,
                request.DisplayName,
                request.ProviderId,
                request.RemoteName,
                request.RemotePath,
                letter,
                MountCacheLayout.CacheDirectoryFor(cacheRoot!, letter),
                LogFilePathFor(request.AccountId),
                _paths.RcloneConfigFile,
                rcPort,
                rcUser)
            {
                Flavor = request.Flavor,
                VolumeName = request.VolumeName ?? request.DisplayName,
                CacheBudgetBytes = budget,
                CacheMinFreeSpaceBytes = reserve,
                ExtraFlags = request.ExtraFlags,
                IsPool = request.IsPool,
                PoolAccountIds = request.PoolAccountIds
            };

            MountSession session = new(spec) { Phase = MountPhase.Starting };
            RcloneMountProcess process = new(spec, binary, _runner, _logger, rcPassword, configPassword, teraboxCookie);
            session.Process = process;
            session.Rc = process.Rc;

            lock (_gate)
            {
                _sessions[request.AccountId] = session;
            }

            RaiseChanged(session);

            try
            {
                process.Start();
            }
            catch (Exception exception)
            {
                _logger.Error("Could not start rclone for " + request.DisplayName + ".", "Mount", exception);
                RemoveSession(request.AccountId);
                process.Dispose();
                return MountResult.Fail(
                    "rclone could not be started: " + exception.Message,
                    "Check that Windows did not block the binary (SmartScreen) and that WinFsp is installed.");
            }

            bool ready = await process.WaitForReadyAsync(ReadyTimeout, cancellationToken).ConfigureAwait(false);
            if (!ready)
            {
                IReadOnlyList<string> tail = process.ReadLogTail(40);
                string? error = process.LastError ?? LastMeaningfulLine(tail) ?? "The mount did not become ready.";
                string fix = BuildReadinessFix(error);
                await process.StopAsync(TimeSpan.FromSeconds(5), TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false);
                RemoveSession(request.AccountId);
                process.Dispose();
                _logger.Warn("Mount failed for " + request.DisplayName + ": " + error, "Mount");
                return MountResult.Fail(error, fix, tail);
            }

            session.Phase = MountPhase.Ready;
            session.ReadySinceUtc = _clock.UtcNow;
            session.LastHealthCheckUtc = _clock.UtcNow;
            session.LastError = null;
            MountCacheLayout.UpdateMarker(spec.CacheDirectory, request.AccountId, letter, cacheRoot!);
            SaveRecords();
            RaiseChanged(session);

            _logger.Info(
                "Mounted " + request.DisplayName + " on " + letter.Display + " (cache " + spec.CacheDirectory + ", rc port " + rcPort + ").",
                "Mount");

            return MountResult.Ok(session.ToSnapshot());
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<bool> UnmountAsync(string accountId, CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(accountId, nameof(accountId));
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MountSession? session;
            lock (_gate)
            {
                if (!_sessions.TryGetValue(accountId, out session))
                {
                    return false;
                }

                _sessions.Remove(accountId);
            }

            session.Phase = MountPhase.Stopping;
            RaiseChanged(session);

            TimeSpan uploadGrace = TimeSpan.FromSeconds(Math.Max(0, _settings.Current.UnmountUploadGraceSeconds));
            if (session.Process is not null)
            {
                await session.Process.StopAsync(uploadGrace, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                session.Process.Dispose();
            }

            session.Phase = MountPhase.Stopped;
            session.Rc?.Dispose();
            session.Rc = null;

            lock (_gate)
            {
                _restartHistory.Remove(accountId);
                _consecutiveFailures.Remove(accountId);
            }

            _secrets.Delete(SecretKeys.RcPassword(accountId));
            SaveRecords();
            RaiseChanged(session);
            _logger.Info("Unmounted " + session.Spec.DisplayName + " (" + session.DriveLetter.Display + ").", "Mount");
            return true;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task UnmountAllAsync(CancellationToken cancellationToken)
    {
        List<string> accountIds;
        lock (_gate)
        {
            accountIds = _sessions.Keys.ToList();
        }

        foreach (string accountId in accountIds)
        {
            try
            {
                await UnmountAsync(accountId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.Warn("Unmounting " + accountId + " failed: " + exception.Message, "Mount");
            }
        }
    }

    public async Task<HealthCheckOutcome> HealthCheckAsync(CancellationToken cancellationToken)
    {
        bool resume = DetectResume();
        List<MountSession> snapshot;
        lock (_gate)
        {
            snapshot = _sessions.Values.ToList();
        }

        int restarted = 0;
        int failed = 0;

        foreach (MountSession session in snapshot)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            session.LastHealthCheckUtc = _clock.UtcNow;

            if (session.Phase is MountPhase.Stopping or MountPhase.Stopped or MountPhase.Failed)
            {
                continue;
            }

            RcloneMountProcess? process = session.Process;
            if (process is null || process.HasExited)
            {
                string error = process is null
                    ? "The rclone process is gone."
                    : "rclone exited with code " + (process.ExitCode?.ToString() ?? "?") + ".";
                session.LastError = error;
                _logger.Warn("Mount " + session.Spec.DisplayName + " is down: " + error, "Mount");

                if (await RestartAsync(session, cancellationToken).ConfigureAwait(false))
                {
                    restarted++;
                }
                else
                {
                    failed++;
                }

                continue;
            }

            bool driveVisible = DriveLetters.IsVisible(session.DriveLetter);
            bool rcAlive = await process.Rc.PingAsync(cancellationToken).ConfigureAwait(false);

            if (driveVisible && rcAlive)
            {
                if (session.Phase != MountPhase.Ready)
                {
                    session.Phase = MountPhase.Ready;
                    session.ReadySinceUtc ??= _clock.UtcNow;
                    session.LastError = null;
                    RaiseChanged(session);
                }

                lock (_gate)
                {
                    _consecutiveFailures[session.AccountId] = 0;
                }

                continue;
            }

            string reason = !driveVisible
                ? "The drive letter " + session.DriveLetter.Display + " is no longer visible."
                : "The rclone remote control endpoint stopped answering.";
            session.LastError = reason;

            int failures = 1;
            lock (_gate)
            {
                failures = _consecutiveFailures.TryGetValue(session.AccountId, out int current) ? current + 1 : 1;
                _consecutiveFailures[session.AccountId] = failures;
            }

            if (session.Phase == MountPhase.Ready)
            {
                session.Phase = MountPhase.Degraded;
                RaiseChanged(session);
            }

            bool shouldRestart = resume || failures >= 3;
            if (!shouldRestart)
            {
                _logger.Debug("Health check warning for " + session.Spec.DisplayName + ": " + reason + " (attempt " + failures + ").", "Mount");
                continue;
            }

            _logger.Warn("Restarting " + session.Spec.DisplayName + " because " + reason, "Mount");
            if (await RestartAsync(session, cancellationToken).ConfigureAwait(false))
            {
                restarted++;
            }
            else
            {
                failed++;
            }
        }

        return new HealthCheckOutcome(snapshot.Count, restarted, failed, resume);
    }

    public async Task<ReattachOutcome> ReattachAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MountRecord> records = _store.Load();
        int adopted = 0;
        int dropped = 0;
        List<string> notes = new();

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (MountRecord record in records)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                bool sameProcess = record.ProcessId > 0 &&
                                   ProcessProbe.MatchesRecordedStart(record.ProcessId, record.ProcessStartUtc);

                if (!sameProcess)
                {
                    dropped++;
                    notes.Add(record.DisplayName + ": rclone is no longer running; the mount was cleared.");
                    _logger.Info("Dropping stale mount record for " + record.DisplayName + ".", "Mount");
                    continue;
                }

                if (!DriveLetter.TryParse(record.DriveLetter, out DriveLetter letter))
                {
                    dropped++;
                    continue;
                }

                string? rcPassword = _secrets.TryLoad(record.RcPasswordSecretKey);
                if (string.IsNullOrEmpty(rcPassword))
                {
                    dropped++;
                    notes.Add(record.DisplayName + ": the rc password could not be read; the mount was cleared.");
                    continue;
                }

                string? configPassword = _secrets.TryLoad(SecretKeys.RcloneConfigPassword);
                string? cookie = record.FlavorValue == RcloneFlavor.TeraBox
                    ? _secrets.TryLoad(SecretKeys.TeraboxCookie(record.AccountId))
                    : null;

                RcloneBinaryInfo binary = await _provisioner.InspectAsync(record.FlavorValue, cancellationToken).ConfigureAwait(false);
                if (!binary.IsUsable)
                {
                    dropped++;
                    notes.Add(record.DisplayName + ": the rclone binary failed verification; mount cleared.");
                    continue;
                }

                MountSpec spec = new(
                    record.AccountId,
                    record.DisplayName,
                    record.ProviderId,
                    record.RemoteName,
                    record.RemotePath,
                    letter,
                    record.CacheDirectory,
                    record.LogFilePath,
                    string.IsNullOrWhiteSpace(record.ConfigPath) ? _paths.RcloneConfigFile : record.ConfigPath,
                    record.RcPort,
                    record.RcUser)
                {
                    Flavor = record.FlavorValue,
                    VolumeName = record.VolumeName,
                    CacheBudgetBytes = record.CacheBudgetBytes,
                    CacheMinFreeSpaceBytes = record.CacheMinFreeSpaceBytes,
                    ExtraFlags = record.ExtraFlags,
                    IsPool = record.IsPool,
                    PoolAccountIds = record.PoolAccountIds
                };

                bool reattached = false;
                for (int attempt = 0; attempt < 5 && !reattached; attempt++)
                {
                    if (!PortAllocator.IsListening(record.RcPort, TimeSpan.FromMilliseconds(300)) &&
                        !DriveLetters.IsVisible(letter))
                    {
                        await Task.Delay(300, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        RcloneMountProcess process = RcloneMountProcess.Adopt(
                            spec,
                            binary,
                            _runner,
                            _logger,
                            record.ProcessId,
                            record.ProcessStartUtc,
                            record.StartedUtc,
                            rcPassword,
                            configPassword,
                            cookie);

                        if (!await process.Rc.PingAsync(cancellationToken).ConfigureAwait(false))
                        {
                            process.Dispose();
                            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        MountSession session = new(spec)
                        {
                            Phase = DriveLetters.IsVisible(letter) ? MountPhase.Ready : MountPhase.Degraded,
                            Process = process,
                            Rc = process.Rc,
                            Reattached = true,
                            ReadySinceUtc = DriveLetters.IsVisible(letter) ? _clock.UtcNow : null,
                            LastHealthCheckUtc = _clock.UtcNow
                        };

                        lock (_gate)
                        {
                            _sessions[record.AccountId] = session;
                        }

                        RaiseChanged(session);
                        adopted++;
                        reattached = true;
                        notes.Add(record.DisplayName + ": re-attached on " + letter.Display + ".");
                    }
                    catch (Exception exception)
                    {
                        _logger.Warn("Re-attach attempt failed for " + record.DisplayName + ": " + exception.Message, "Mount");
                        await Task.Delay(300, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (!reattached)
                {
                    dropped++;
                    notes.Add(record.DisplayName + ": rclone is running but its rc endpoint did not answer; mount cleared.");
                }
            }

            SaveRecords();
        }
        finally
        {
            _operationLock.Release();
        }

        if (adopted > 0 || dropped > 0)
        {
            _logger.Info("Re-attach finished: " + adopted + " adopted, " + dropped + " cleared.", "Mount");
        }

        return new ReattachOutcome(adopted, dropped, notes);
    }

    public async Task<RemoteUsage?> GetQuotaAsync(string accountId, CancellationToken cancellationToken)
    {
        RcloneRcClient? rc = GetRcClient(accountId);
        MountSnapshot? snapshot = GetSnapshot(accountId);
        if (rc is null || snapshot is null)
        {
            return null;
        }

        try
        {
            return await rc.AboutAsync(snapshot.RemoteSpec, cancellationToken).ConfigureAwait(false);
        }
        catch (RcloneRcException exception)
        {
            _logger.Debug("Quota query failed for " + snapshot.DisplayName + ": " + exception.Message, "Mount");
            return null;
        }
    }

    public IReadOnlyList<string> GetLogTail(string accountId, int lines)
    {
        MountSession? session;
        lock (_gate)
        {
            _sessions.TryGetValue(accountId ?? string.Empty, out session);
        }

        return session?.Process?.ReadLogTail(lines) ?? Array.Empty<string>();
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        _logger.Info("Shutting down the mount engine.", "Mount");
        await UnmountAllAsync(cancellationToken).ConfigureAwait(false);
        SaveRecords();
    }

    private async Task<bool> RestartAsync(MountSession session, CancellationToken cancellationToken)
    {
        string accountId = session.AccountId;
        List<DateTimeOffset> history;
        lock (_gate)
        {
            if (!_restartHistory.TryGetValue(accountId, out List<DateTimeOffset>? list))
            {
                list = new List<DateTimeOffset>();
                _restartHistory[accountId] = list;
            }

            history = list;
        }

        DateTimeOffset now = _clock.UtcNow;
        int attempts;
        lock (history)
        {
            history.RemoveAll(t => now - t > RestartWindow);
            int max = Math.Max(1, _settings.Current.MaxRestartsPerTenMinutes);
            if (history.Count >= max)
            {
                session.Phase = MountPhase.Failed;
                session.LastError =
                    "rclone crashed " + history.Count + " times in 10 minutes; NimbusX stopped restarting it.";
                RaiseChanged(session);
                _logger.Error("Giving up on " + session.Spec.DisplayName + ": " + session.LastError, "Mount");
                return false;
            }

            attempts = history.Count;
            history.Add(now);
        }

        TimeSpan delay = TimeSpan.FromSeconds(Math.Min(30d, Math.Pow(2d, Math.Min(attempts, 5))));
        _logger.Info(
            "Restarting " + session.Spec.DisplayName + " in " + (int)delay.TotalSeconds + " s (attempt " + (attempts + 1) + ").",
            "Mount");

        await _clock.DelayAsync(delay, cancellationToken).ConfigureAwait(false);

        session.Phase = MountPhase.Starting;
        session.RestartCount++;
        RaiseChanged(session);

        if (session.Process is not null)
        {
            try
            {
                await session.Process.StopAsync(TimeSpan.Zero, TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // ignored: we are replacing it anyway
            }

            session.Process.Dispose();
            session.Process = null;
        }

        session.Rc?.Dispose();
        session.Rc = null;

        RcloneBinaryInfo binary = await _provisioner.InspectAsync(session.Spec.Flavor, cancellationToken).ConfigureAwait(false);
        if (!binary.IsUsable)
        {
            session.Phase = MountPhase.Failed;
            session.LastError = binary.Describe();
            RaiseChanged(session);
            return false;
        }

        string? configPassword = _secrets.TryLoad(SecretKeys.RcloneConfigPassword);
        string? cookie = session.Spec.Flavor == RcloneFlavor.TeraBox
            ? _secrets.TryLoad(SecretKeys.TeraboxCookie(session.AccountId))
            : null;
        if (session.Spec.Flavor == RcloneFlavor.TeraBox && string.IsNullOrWhiteSpace(cookie))
        {
            session.Phase = MountPhase.Failed;
            session.LastError = "The TeraBox session expired; reconnect the account.";
            RaiseChanged(session);
            return false;
        }

        int port = PortAllocator.GetFreePort();
        string rcUser = AppInfo.Name.ToLowerInvariant() + "_" + RandomSecrets.Hex(4);
        string rcPassword = RandomSecrets.Password();
        _secrets.Save(SecretKeys.RcPassword(session.AccountId), rcPassword);

        session.Spec = session.Spec.WithRc(port, rcUser);
        RcloneMountProcess process = new(session.Spec, binary, _runner, _logger, rcPassword, configPassword, cookie);
        session.Process = process;
        session.Rc = process.Rc;

        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            session.Phase = MountPhase.Failed;
            session.LastError = "rclone could not be restarted: " + exception.Message;
            RaiseChanged(session);
            return false;
        }

        bool ready = await process.WaitForReadyAsync(ReadyTimeout, cancellationToken).ConfigureAwait(false);
        if (!ready)
        {
            session.Phase = MountPhase.Failed;
            session.LastError = process.LastError ?? "The restart did not become ready.";
            RaiseChanged(session);
            SaveRecords();
            return false;
        }

        session.Phase = MountPhase.Ready;
        session.ReadySinceUtc = _clock.UtcNow;
        session.LastError = null;
        lock (_gate)
        {
            _consecutiveFailures[session.AccountId] = 0;
        }

        SaveRecords();
        RaiseChanged(session);
        return true;
    }

    private bool DetectResume()
    {
        DateTimeOffset now = _clock.UtcNow;
        bool resume = _lastHealthCheckUtc is not null && now - _lastHealthCheckUtc.Value > ResumeGap;
        _lastHealthCheckUtc = now;
        return resume;
    }

    private void RemoveSession(string accountId)
    {
        MountSession? removed = null;
        lock (_gate)
        {
            if (_sessions.TryGetValue(accountId, out MountSession? session))
            {
                removed = session;
                _sessions.Remove(accountId);
            }
        }

        if (removed is not null)
        {
            removed.Phase = MountPhase.Failed;
            RaiseChanged(removed);
        }

        SaveRecords();
    }

    private void SaveRecords()
    {
        List<MountRecord> records;
        lock (_gate)
        {
            records = _sessions.Values.Select(MountRecord.FromSession).ToList();
        }

        _store.Save(records);
    }

    private string LogFilePathFor(string accountId)
    {
        Directory.CreateDirectory(_paths.RcloneLogsDirectory);
        return Path.Combine(_paths.RcloneLogsDirectory, "mount-" + TextFormat.Slug(accountId) + ".log");
    }

    private long DefaultBudgetShare()
    {
        long total = _settings.Current.CacheBudgetBytes;
        int mounts = Math.Max(1, Snapshots.Count + 1);
        return Math.Max(512L * 1024L * 1024L, total / mounts);
    }

    private static string? LastMeaningfulLine(IReadOnlyList<string> lines)
    {
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            string line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("FATAL", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Failed", StringComparison.OrdinalIgnoreCase))
            {
                return line;
            }
        }

        return lines.Count > 0 ? lines[^1] : null;
    }

    /// <summary>Turns rclone's own error text into something the user can act on.</summary>
    public static string BuildReadinessFix(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "Open Diagnostics, copy the rclone log tail and check WinFsp is installed.";
        }

        string text = error!;
        if (text.Contains("fuse", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("winfsp", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("mountpoint", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("device", StringComparison.OrdinalIgnoreCase))
        {
            return "Install WinFsp (official MSI) and press Re-check, then mount again.";
        }

        if (text.Contains("401", StringComparison.Ordinal) ||
            text.Contains("403", StringComparison.Ordinal) ||
            text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
        {
            return "The provider rejected the saved login. Press Reconnect on the account card.";
        }

        if (text.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ndus", StringComparison.OrdinalIgnoreCase))
        {
            return "Reconnect TeraBox: the saved session cookie is no longer valid.";
        }

        if (text.Contains("already in use", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("in use", StringComparison.OrdinalIgnoreCase))
        {
            return "Another program is using that drive letter. Unmount it or let NimbusX pick the next free letter.";
        }

        return "Open Diagnostics -> Mount log for rclone's full message, then try mounting again.";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            foreach (MountSession session in _sessions.Values)
            {
                session.Process?.Dispose();
                session.Rc?.Dispose();
            }

            _sessions.Clear();
        }

        _operationLock.Dispose();
    }
}
