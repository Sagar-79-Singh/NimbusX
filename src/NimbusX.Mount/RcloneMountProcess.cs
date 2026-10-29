using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NimbusX.Core;
using NimbusX.Core.Logging;
using NimbusX.Core.Processes;
using NimbusX.Core.Windows;

namespace NimbusX.Mount;

/// <summary>
/// One rclone mount child process, its rc endpoint and its log tailer.
/// </summary>
/// <remarks>
/// NimbusX runs one rclone process per cloud account. That gives every account its own drive letter, its own
/// cache folder, its own rc endpoint (so a health check is per mount) and its own <c>-vv</c> log (so the
/// prediction engine sees exactly that account's reads). A mount is "ready" only when the drive letter is
/// visible in Windows <em>and</em> rc answers.
/// </remarks>
public sealed class RcloneMountProcess : IDisposable
{
    private static readonly Regex ReadActivityPattern = new(@"(_readAt:|vfs cache: looking for range)", RegexOptions.Compiled);

    private readonly MountSpec _spec;
    private readonly RcloneBinaryInfo _binary;
    private readonly IProcessRunner _runner;
    private readonly IAppLogger _logger;
    private readonly string? _configPassword;
    private readonly string? _teraboxCookie;
    private readonly string? _rcPassword;
    private readonly List<string> _stderrTail = new();

    private Process? _process;
    private ProcessOutputPump? _pump;
    private bool _disposed;
    private bool _adopted;

    public RcloneMountProcess(
        MountSpec spec,
        RcloneBinaryInfo binary,
        IProcessRunner runner,
        IAppLogger logger,
        string? rcPassword,
        string? configPassword,
        string? teraboxCookie)
    {
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _binary = binary ?? throw new ArgumentNullException(nameof(binary));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _logger = logger ?? NullLogger.Instance;
        _rcPassword = rcPassword;
        _configPassword = configPassword;
        _teraboxCookie = teraboxCookie;

        Rc = new RcloneRcClient(spec.RcPort, spec.RcUser, rcPassword ?? string.Empty, logger);
    }

    /// <summary>rc client for this mount.</summary>
    public RcloneRcClient Rc { get; }

    public MountSpec Spec => _spec;

    public int? ProcessId
    {
        get
        {
            try
            {
                return _process?.Id;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public DateTimeOffset? ProcessStartUtc { get; private set; }

    public DateTimeOffset StartedUtc { get; private set; }

    /// <summary>Last time this mount served a read (from the -vv log). Used to settle before unmounting.</summary>
    public DateTimeOffset? LastReadActivityUtc { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Tail of the log file, for error cards.</summary>
    public RcloneLogTailer? LogTailer { get; private set; }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process is null || _process.HasExited;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            try
            {
                return _process is null ? null : _process.HasExited ? _process.ExitCode : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>Builds the rclone command line for a mount (deterministic, unit tested).</summary>
    public static IReadOnlyList<string> BuildArguments(MountSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        List<string> args = new()
        {
            "mount",
            spec.RemoteSpec,
            spec.MountPoint,
            "--config", spec.ConfigPath,
            "--cache-dir", spec.CacheDirectory,
            "--vfs-cache-mode", "full",
            "--buffer-size", "0",
            "--vfs-read-chunk-size", "4M",
            "--vfs-read-chunk-size-limit", "off",
            "--file-perms", "0777",
            "--dir-perms", "0777",
            "--rc",
            "--rc-addr", "127.0.0.1:" + spec.RcPort,
            "--rc-job-expire-duration", "1h",
            "--log-file", spec.LogFilePath,
            "--log-format", "date,time,microseconds",
            "--dir-cache-time", "1h",
            "--attr-timeout", "1s",
            "--vfs-cache-poll-interval", "1m",
            "--vfs-cache-max-age", "168h",
            "--contimeout", "30s",
            "--timeout", "10m",
            "--low-level-retries", "20",
            "-vv"
        };

        if (!string.IsNullOrWhiteSpace(spec.VolumeName))
        {
            args.Add("--volname");
            args.Add(spec.VolumeName!);
        }

        if (spec.CacheBudgetBytes > 0)
        {
            args.Add("--vfs-cache-max-size");
            args.Add(FormatSize(spec.CacheBudgetBytes));
        }

        if (spec.CacheMinFreeSpaceBytes > 0)
        {
            args.Add("--vfs-cache-min-free-space");
            args.Add(FormatSize(spec.CacheMinFreeSpaceBytes));
        }

        foreach (string extra in spec.ExtraFlags)
        {
            if (!string.IsNullOrWhiteSpace(extra))
            {
                args.Add(extra);
            }
        }

        return args;
    }

    /// <summary>Formats a byte count the way rclone's SizeSuffix parser expects (KiB/MiB/GiB units).</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0";
        }

        const long gib = 1024L * 1024L * 1024L;
        const long mib = 1024L * 1024L;
        if (bytes % gib == 0)
        {
            return (bytes / gib) + "G";
        }

        if (bytes % mib == 0)
        {
            return (bytes / mib) + "M";
        }

        return bytes + "B";
    }

    /// <summary>
    /// Adopts an rclone process that NimbusX started in a previous app session.
    /// </summary>
    /// <remarks>
    /// The child's stdout pipe died with the old parent, so only the log file and the rc endpoint are used.
    /// The caller has already checked pid + process start time, which is what makes this safe against pid reuse.
    /// </remarks>
    public static RcloneMountProcess Adopt(
        MountSpec spec,
        RcloneBinaryInfo binary,
        IProcessRunner runner,
        IAppLogger logger,
        int processId,
        DateTimeOffset processStartUtc,
        DateTimeOffset startedUtc,
        string? rcPassword,
        string? configPassword,
        string? teraboxCookie)
    {
        RcloneMountProcess mount = new(spec, binary, runner, logger, rcPassword, configPassword, teraboxCookie)
        {
            _adopted = true,
            ProcessStartUtc = processStartUtc,
            StartedUtc = startedUtc
        };

        mount._process = Process.GetProcessById(processId);
        mount.LogTailer = new RcloneLogTailer(spec.LogFilePath, logger);
        mount.LogTailer.LineReceived += mount.OnLogLine;
        mount.LogTailer.Start(TimeSpan.FromMilliseconds(300), CancellationToken.None);
        logger.Info("Re-attached to running rclone for " + spec.DisplayName + " (" + spec.MountPoint + ", pid " + processId + ").", "Mount");
        return mount;
    }

    /// <summary>Starts the process.</summary>
    public void Start()
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("The mount process has already been started.");
        }

        Directory.CreateDirectory(_spec.CacheDirectory);
        string? logDirectory = Path.GetDirectoryName(_spec.LogFilePath);
        if (!string.IsNullOrEmpty(logDirectory))
        {
            Directory.CreateDirectory(logDirectory!);
        }

        Dictionary<string, string> environment = RcloneEnvironment.ForMount(_spec, _configPassword, _teraboxCookie, _rcPassword);
        ProcessSpec processSpec = new(_binary.Path)
        {
            Arguments = BuildArguments(_spec),
            Environment = environment,
            CreateNoWindow = true
        };

        _logger.Info("Starting mount: " + _binary.Path + " " + string.Join(" ", RedactArguments(processSpec.Arguments)), "Mount");
        _process = _runner.Start(processSpec);
        StartedUtc = DateTimeOffset.UtcNow;
        try
        {
            ProcessStartUtc = new DateTimeOffset(_process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception)
        {
            ProcessStartUtc = StartedUtc;
        }

        _pump = ProcessOutputPump.Attach(
            _process,
            line => _logger.Debug("rclone stdout: " + line, "Mount"),
            line =>
            {
                lock (_stderrTail)
                {
                    _stderrTail.Add(line);
                    while (_stderrTail.Count > 40)
                    {
                        _stderrTail.RemoveAt(0);
                    }
                }

                if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Fatal", StringComparison.OrdinalIgnoreCase))
                {
                    LastError = line.Trim();
                }

                _logger.Debug("rclone stderr: " + line, "Mount");
            });

        LogTailer = new RcloneLogTailer(_spec.LogFilePath, _logger);
        LogTailer.LineReceived += OnLogLine;
        LogTailer.Start(TimeSpan.FromMilliseconds(300), CancellationToken.None);
    }

    private void OnLogLine(object? sender, string line)
    {
        if (ReadActivityPattern.IsMatch(line))
        {
            LastReadActivityUtc = DateTimeOffset.UtcNow;
        }

        if (line.Contains("ERROR", StringComparison.Ordinal) || line.Contains("Failed to", StringComparison.Ordinal))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                LastError = trimmed;
            }
        }
    }

    /// <summary>
    /// Waits until the drive letter is visible and rc answers, or until the timeout expires.
    /// </summary>
    public async Task<bool> WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (HasExited)
            {
                LastError ??= "rclone exited with code " + (ExitCode?.ToString() ?? "?") + " before the drive became ready.";
                return false;
            }

            bool driveVisible = DriveLetters.IsVisible(_spec.DriveLetter);
            if (driveVisible)
            {
                bool rcAlive = await Rc.PingAsync(cancellationToken).ConfigureAwait(false);
                if (rcAlive)
                {
                    LastError = null;
                    return true;
                }
            }

            try
            {
                await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        LastError ??= "The drive did not become ready within " + (int)timeout.TotalSeconds + " seconds.";
        return false;
    }

    /// <summary>
    /// Unmounts gracefully: wait for pending uploads and read activity to settle, ask rclone to quit, and only
    /// kill the process if it refuses to exit.
    /// </summary>
    public async Task StopAsync(
        TimeSpan uploadGrace,
        TimeSpan readSettle,
        CancellationToken cancellationToken)
    {
        if (_process is null || HasExited)
        {
            Cleanup();
            return;
        }

        _logger.Info("Unmounting " + _spec.MountPoint + " (" + _spec.DisplayName + ").", "Mount");

        try
        {
            await WaitForUploadsAsync(uploadGrace, cancellationToken).ConfigureAwait(false);
            await WaitForReadSettleAsync(readSettle, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // fall through and still try to quit cleanly
        }

        try
        {
            await Rc.QuitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Debug("core/quit failed: " + exception.Message, "Mount");
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (HasExited)
            {
                Cleanup();
                return;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        _logger.Warn("rclone did not exit within 15 s; terminating the mount process.", "Mount");
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (Exception exception)
        {
            _logger.Debug("Kill failed: " + exception.Message, "Mount");
        }

        Cleanup();
    }

    private async Task WaitForUploadsAsync(TimeSpan grace, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + grace;
        while (DateTimeOffset.UtcNow < deadline)
        {
            int pending = await CountPendingUploadsAsync(cancellationToken).ConfigureAwait(false);
            if (pending <= 0)
            {
                return;
            }

            _logger.Debug("Waiting for " + pending + " pending upload(s) before unmount.", "Mount");
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }

        _logger.Warn("Pending uploads did not finish within " + (int)grace.TotalSeconds + " s; unmounting anyway.", "Mount");
    }

    /// <summary>Uploads still running or queued according to <c>vfs/stats</c>, or 0 when unknown.</summary>
    public async Task<int> CountPendingUploadsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var stats = await Rc.VfsStatsAsync(_spec.RemoteSpec, cancellationToken).ConfigureAwait(false);
            var diskCache = stats is null ? null : RcJson.Object(stats, "diskCache");
            long inProgress = RcJson.Long(diskCache, "uploadsInProgress") ?? 0;
            long queued = RcJson.Long(diskCache, "uploadsQueued") ?? 0;
            return (int)Math.Min(int.MaxValue, inProgress + queued);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private async Task WaitForReadSettleAsync(TimeSpan settle, CancellationToken cancellationToken)
    {
        if (settle <= TimeSpan.Zero)
        {
            return;
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow + settle;
        while (DateTimeOffset.UtcNow < deadline)
        {
            DateTimeOffset? last = LastReadActivityUtc;
            if (last is null || DateTimeOffset.UtcNow - last.Value > TimeSpan.FromSeconds(2))
            {
                return;
            }

            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Last lines written to the rclone log file (already redacted by the OS by the time we read it).</summary>
    public IReadOnlyList<string> ReadLogTail(int lineCount)
    {
        try
        {
            if (!File.Exists(_spec.LogFilePath) || lineCount <= 0)
            {
                return Array.Empty<string>();
            }

            using FileStream stream = new(_spec.LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            List<string> lines = new();
            using StreamReader reader = new(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                lines.Add(SecretRedactor.Redact(line));
                if (lines.Count > lineCount * 4)
                {
                    lines.RemoveRange(0, lines.Count - lineCount);
                }
            }

            return lines.Count <= lineCount ? lines : lines.Skip(lines.Count - lineCount).ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Recent stderr lines captured from the child process.</summary>
    public IReadOnlyList<string> StderrTail()
    {
        lock (_stderrTail)
        {
            return _stderrTail.ToArray();
        }
    }

    private static IEnumerable<string> RedactArguments(IReadOnlyList<string> arguments)
    {
        for (int i = 0; i < arguments.Count; i++)
        {
            string current = arguments[i];
            if (i > 0 && (arguments[i - 1].Equals("--rc-pass", StringComparison.OrdinalIgnoreCase) ||
                          arguments[i - 1].Equals("--config-password", StringComparison.OrdinalIgnoreCase)))
            {
                yield return SecretRedactor.Placeholder;
                continue;
            }

            yield return current;
        }
    }

    private void Cleanup()
    {
        try
        {
            LogTailer?.StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // ignore
        }

        LogTailer?.Dispose();
        _pump?.Dispose();

        try
        {
            _process?.Dispose();
        }
        catch (Exception)
        {
            // ignore
        }

        _process = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (!HasExited)
            {
                _process?.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // ignore
        }

        Cleanup();
        Rc.Dispose();
    }
}
