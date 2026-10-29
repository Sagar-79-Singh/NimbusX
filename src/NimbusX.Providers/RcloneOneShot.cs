using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NimbusX.Core;
using NimbusX.Core.Config;
using NimbusX.Core.Logging;
using NimbusX.Core.Processes;
using NimbusX.Core.Security;
using NimbusX.Mount;

namespace NimbusX.Providers;

/// <summary>Result of a one-shot rclone command.</summary>
public sealed class OneShotResult
{
    public OneShotResult(bool success, string standardOutput, string standardError, int exitCode)
    {
        Success = success;
        StandardOutput = standardOutput;
        StandardError = standardError;
        ExitCode = exitCode;
    }

    public bool Success { get; }

    public string StandardOutput { get; }

    public string StandardError { get; }

    public int ExitCode { get; }

    /// <summary>Best available error text (stderr, else stdout, else exit code).</summary>
    public string ErrorText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(StandardError))
            {
                return SecretRedactor.Redact(StandardError).Trim();
            }

            if (!string.IsNullOrWhiteSpace(StandardOutput))
            {
                return SecretRedactor.Redact(StandardOutput).Trim();
            }

            return "rclone exited with code " + ExitCode + ".";
        }
    }

    public JsonObject? TryParseJson()
    {
        try
        {
            string text = StandardOutput.Trim();
            if (text.Length == 0)
            {
                return null;
            }

            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                text = text[start..(end + 1)];
            }

            return JsonNode.Parse(text) as JsonObject;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// Runs short rclone commands (about, lsd, userinfo) as separate processes.
/// </summary>
/// <remarks>
/// One-shot commands are the honest way to verify a login: rclone actually talks to the provider, and its exit
/// code plus stderr are the truth. They also avoid needing a mount just to read a quota. Secrets (the config key
/// and the TeraBox cookie) are passed through the environment only.
/// </remarks>
public sealed class RcloneOneShot
{
    private readonly AppPaths _paths;
    private readonly RcloneProvisioner _provisioner;
    private readonly IProcessRunner _runner;
    private readonly ISecretStore _secrets;
    private readonly IAppLogger _logger;

    public RcloneOneShot(
        AppPaths paths,
        RcloneProvisioner provisioner,
        IProcessRunner runner,
        ISecretStore secrets,
        IAppLogger? logger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _provisioner = provisioner ?? throw new ArgumentNullException(nameof(provisioner));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Runs rclone with the NimbusX config and (optionally) a TeraBox cookie in the environment.</summary>
    public async Task<OneShotResult> RunAsync(
        RcloneFlavor flavor,
        IReadOnlyList<string> arguments,
        string? teraboxRemoteName,
        string? teraboxCookie,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        string executable = _provisioner.ExecutablePathFor(flavor);
        Dictionary<string, string> environment = RcloneEnvironment.ForControl(
            _paths.RcloneConfigFile,
            _secrets.TryLoad(SecretKeys.RcloneConfigPassword));

        if (!string.IsNullOrWhiteSpace(teraboxRemoteName) && !string.IsNullOrWhiteSpace(teraboxCookie))
        {
            environment[RcloneEnvironment.RemoteOptionVar(teraboxRemoteName!, "cookie")] = teraboxCookie!;
        }

        ProcessSpec spec = new(executable)
        {
            Arguments = arguments,
            Environment = environment,
            CreateNoWindow = true
        };

        ProcessResult result = await _runner.RunAsync(spec, timeout, cancellationToken).ConfigureAwait(false);
        OneShotResult oneShot = new(result.Succeeded, result.StandardOutput, result.StandardError, result.ExitCode);

        if (!oneShot.Success)
        {
            _logger.Debug("rclone " + string.Join(" ", arguments) + " failed: " + oneShot.ErrorText, "Providers");
        }

        return oneShot;
    }

    /// <summary>Reads quota with <c>rclone about --json</c>, returning null when the provider cannot report it.</summary>
    public async Task<RemoteUsage?> ReadQuotaAsync(
        RcloneFlavor flavor,
        string target,
        string? teraboxRemoteName,
        string? teraboxCookie,
        CancellationToken cancellationToken)
    {
        OneShotResult result = await RunAsync(
            flavor,
            new[] { "about", target, "--json" },
            teraboxRemoteName,
            teraboxCookie,
            TimeSpan.FromSeconds(60),
            cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            return null;
        }

        JsonObject? json = result.TryParseJson();
        if (json is null)
        {
            return null;
        }

        long? total = ReadLong(json, "total") ?? ReadLong(json, "Total");
        long? used = ReadLong(json, "used") ?? ReadLong(json, "Used");
        long? free = ReadLong(json, "free") ?? ReadLong(json, "Free");
        if (total is null && used is null && free is null)
        {
            return null;
        }

        if (free is not null && used is null && total is not null)
        {
            used = Math.Max(0, total.Value - free.Value);
        }

        if (free is not null && total is null && used is not null)
        {
            total = used.Value + free.Value;
        }

        return new RemoteUsage(target, used, total, free, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Verifies that a remote really works: quota first, then a directory listing (some servers have no quota
    /// support but do authenticate). Returns the provider's real error when both fail.
    /// </summary>
    public async Task<(bool Ok, string? Error, RemoteUsage? Usage)> VerifyAccessAsync(
        RcloneFlavor flavor,
        string target,
        string? teraboxRemoteName,
        string? teraboxCookie,
        CancellationToken cancellationToken)
    {
        RemoteUsage? usage = await ReadQuotaAsync(flavor, target, teraboxRemoteName, teraboxCookie, cancellationToken)
            .ConfigureAwait(false);
        if (usage is not null)
        {
            return (true, null, usage);
        }

        OneShotResult listing = await RunAsync(
            flavor,
            new[] { "lsd", target, "--max-depth", "1", "--low-level-retries", "1", "--retries", "1" },
            teraboxRemoteName,
            teraboxCookie,
            TimeSpan.FromSeconds(90),
            cancellationToken).ConfigureAwait(false);

        if (listing.Success)
        {
            return (true, null, null);
        }

        return (false, listing.ErrorText, null);
    }

    /// <summary>Reads the provider account label (e-mail / user name) with <c>rclone config userinfo</c>.</summary>
    public async Task<string?> ReadUserInfoAsync(
        RcloneFlavor flavor,
        string target,
        string? teraboxRemoteName,
        string? teraboxCookie,
        CancellationToken cancellationToken)
    {
        OneShotResult result = await RunAsync(
            flavor,
            new[] { "config", "userinfo", target },
            teraboxRemoteName,
            teraboxCookie,
            TimeSpan.FromSeconds(45),
            cancellationToken).ConfigureAwait(false);

        return result.Success ? RcloneConfigManager.ParseUserInfo(result.StandardOutput) : null;
    }

    private static long? ReadLong(JsonObject json, string name)
    {
        if (!json.TryGetPropertyValue(name, out JsonNode? node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue(out long number))
            {
                return number;
            }

            if (value.TryGetValue(out double doubleNumber))
            {
                return (long)doubleNumber;
            }
        }

        return null;
    }
}
