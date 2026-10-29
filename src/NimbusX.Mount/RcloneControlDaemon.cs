using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NimbusX.Core;
using NimbusX.Core.Config;
using NimbusX.Core.Logging;
using NimbusX.Core.Ports;
using NimbusX.Core.Processes;
using NimbusX.Core.Security;

namespace NimbusX.Mount;

/// <summary>
/// A long lived <c>rclone rcd</c> process used for configuration work: creating remotes, running OAuth flows,
/// reading quotas and managing the pool remote.
/// </summary>
/// <remarks>
/// Mounts never share this process: each mount has its own rclone and its own rc port, so a config operation can
/// never disturb a mounted drive. The daemon exists because rclone's rc API is the only supported way to write an
/// encrypted config, and because the OAuth state machine lives there.
/// </remarks>
public sealed class RcloneControlDaemon : IAsyncDisposable
{
    private readonly AppPaths _paths;
    private readonly RcloneProvisioner _provisioner;
    private readonly IProcessRunner _runner;
    private readonly IAppLogger _logger;
    private readonly ISecretStore _secrets;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Process? _process;
    private ProcessOutputPump? _pump;
    private RcloneRcClient? _client;
    private int _port;
    private string? _rcUser;
    private string? _rcPassword;
    private bool _disposed;

    public RcloneControlDaemon(
        AppPaths paths,
        RcloneProvisioner provisioner,
        IProcessRunner runner,
        IAppLogger logger,
        ISecretStore secrets,
        RcloneFlavor flavor = RcloneFlavor.Standard)
    {
        Flavor = flavor;
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _provisioner = provisioner ?? throw new ArgumentNullException(nameof(provisioner));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _logger = logger ?? NullLogger.Instance;
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
    }

    /// <summary>Which rclone build this daemon runs (TeraBox needs the community build).</summary>
    public RcloneFlavor Flavor { get; }

    public bool IsRunning => _process is { HasExited: false } && _client is not null;

    public int? Port => IsRunning ? _port : null;

    /// <summary>Log file of the control daemon (config/OAuth diagnostics).</summary>
    public string LogFilePath => Path.Combine(
        _paths.RcloneLogsDirectory,
        Flavor == RcloneFlavor.TeraBox ? "control-terabox.log" : "control-rclone.log");

    /// <summary>Starts the daemon if needed and returns its rc client.</summary>
    public async Task<RcloneRcClient> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning && _client is not null && await _client.PingAsync(cancellationToken).ConfigureAwait(false))
            {
                return _client;
            }

            await StopInternalAsync(cancellationToken).ConfigureAwait(false);

            RcloneBinaryInfo binary = await _provisioner.InspectAsync(Flavor, cancellationToken).ConfigureAwait(false);
            if (!binary.IsUsable)
            {
                throw new InvalidOperationException(binary.Describe());
            }

            Directory.CreateDirectory(_paths.RcloneLogsDirectory);
            _port = PortAllocator.GetFreePort();
            _rcUser = AppInfo.Name.ToLowerInvariant() + "_ctl";
            _rcPassword = RandomSecrets.Password(32);

            string? configPassword = _secrets.TryLoad(SecretKeys.RcloneConfigPassword);
            Dictionary<string, string> environment = RcloneEnvironment.ForControl(_paths.RcloneConfigFile, configPassword);
            environment[RcloneEnvironment.RcUserVar] = _rcUser;
            environment[RcloneEnvironment.RcPassVar] = _rcPassword;

            ProcessSpec spec = new(binary.Path)
            {
                Arguments = new[]
                {
                    "rcd",
                    "--config", _paths.RcloneConfigFile,
                    "--rc-addr", "127.0.0.1:" + _port,
                    "--rc-job-expire-duration", "10m",
                    "--rc-web-gui=false",
                    "--log-file", LogFilePath,
                    "--log-format", "date,time,microseconds",
                    "--stats", "0",
                    "-vv"
                },
                Environment = environment,
                CreateNoWindow = true
            };

            _logger.Info("Starting the " + DependencyVersions.DisplayName(Flavor) + " control daemon on 127.0.0.1:" + _port + ".", "Control");
            _process = _runner.Start(spec);
            _pump = ProcessOutputPump.Attach(
                _process,
                line => _logger.Debug("control stdout: " + line, "Control"),
                line => _logger.Debug("control stderr: " + line, "Control"));

            RcloneRcClient client = new(_port, _rcUser!, _rcPassword!, _logger);
            DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_process.HasExited)
                {
                    throw new InvalidOperationException(
                        "The rclone control daemon exited with code " + _process.ExitCode + ". See " + LogFilePath + ".");
                }

                if (await client.PingAsync(cancellationToken).ConfigureAwait(false))
                {
                    _client = client;
                    _logger.Info("rclone control daemon ready (rc port " + _port + ").", "Control");
                    return client;
                }

                await Task.Delay(300, cancellationToken).ConfigureAwait(false);
            }

            client.Dispose();
            throw new TimeoutException("The rclone control daemon did not become ready within 30 seconds.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops the daemon.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopInternalAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopInternalAsync(CancellationToken cancellationToken)
    {
        RcloneRcClient? client = _client;
        _client = null;

        if (client is not null)
        {
            try
            {
                await client.QuitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // ignore
            }
        }

        if (_process is not null)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8);
            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    if (_process.HasExited)
                    {
                        break;
                    }
                }
                catch (Exception)
                {
                    break;
                }

                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
                // ignore
            }

            _process.Dispose();
            _process = null;
        }

        _pump?.Dispose();
        _pump = null;
        client?.Dispose();
    }

    /// <summary>Reads the tail of the control daemon's log.</summary>
    public IReadOnlyList<string> ReadLogTail(int lines)
    {
        try
        {
            if (!File.Exists(LogFilePath) || lines <= 0)
            {
                return Array.Empty<string>();
            }

            using FileStream stream = new(LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream);
            List<string> all = new();
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                all.Add(SecretRedactor.Redact(line));
                if (all.Count > lines * 4)
                {
                    all.RemoveRange(0, all.Count - lines);
                }
            }

            return all.Count <= lines ? all : all.GetRange(all.Count - lines, lines);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
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
            await StopInternalAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // ignore
        }

        _gate.Dispose();
    }
}
