using System;
using System.Collections.Generic;
using System.Text;
using NimbusX.Core;

namespace NimbusX.Mount;

/// <summary>
/// Builds the environment that carries secrets into rclone.
/// </summary>
/// <remarks>
/// NimbusX never puts a password, token or cookie on a command line (where any process could read it from the
/// command line or a crash dump). Everything secret travels in the child process environment:
/// <c>RCLONE_CONFIG_PASS</c> for the encrypted config, <c>RCLONE_RC_USER</c>/<c>RCLONE_RC_PASS</c> for the rc
/// endpoint, and <c>RCLONE_CONFIG_&lt;REMOTE&gt;_&lt;OPTION&gt;</c> for per-provider secrets such as the TeraBox cookie.
/// </remarks>
public static class RcloneEnvironment
{
    public const string ConfigVar = "RCLONE_CONFIG";
    public const string ConfigPassVar = "RCLONE_CONFIG_PASS";
    public const string RcUserVar = "RCLONE_RC_USER";
    public const string RcPassVar = "RCLONE_RC_PASS";
    public const string AskPasswordVar = "RCLONE_ASK_PASSWORD";
    public const string LogLevelVar = "RCLONE_LOG_LEVEL";
    public const string StatsVar = "RCLONE_STATS";

    /// <summary>Environment for one mount process.</summary>
    public static Dictionary<string, string> ForMount(
        MountSpec spec,
        string? configPassword,
        string? teraboxCookie,
        string? rcPassword)
    {
        ArgumentNullException.ThrowIfNull(spec);
        Dictionary<string, string> environment = new(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigVar] = spec.ConfigPath,
            [RcUserVar] = spec.RcUser,
            [AskPasswordVar] = "false",
            // Keep rclone's own stats quiet; NimbusX reads the VFS/metadata state directly.
            [StatsVar] = "0"
        };

        if (!string.IsNullOrEmpty(configPassword))
        {
            environment[ConfigPassVar] = configPassword!;
        }

        if (!string.IsNullOrEmpty(rcPassword))
        {
            environment[RcPassVar] = rcPassword!;
        }

        if (spec.Flavor == RcloneFlavor.TeraBox)
        {
            if (string.IsNullOrWhiteSpace(teraboxCookie))
            {
                throw new InvalidOperationException(
                    "A TeraBox cookie is required to mount " + spec.DisplayName + ". Reconnect the account in Cloud Drives.");
            }

            // The TeraBox backend reads the 'cookie' option; passing it here keeps it out of rclone.conf.
            environment[RemoteOptionVar(spec.RemoteName, "cookie")] = teraboxCookie!;
        }

        return environment;
    }

    /// <summary>Environment for the short lived control daemon that edits the config.</summary>
    public static Dictionary<string, string> ForControl(string configPath, string? configPassword)
    {
        Dictionary<string, string> environment = new(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigVar] = configPath,
            [AskPasswordVar] = "false"
        };

        if (!string.IsNullOrEmpty(configPassword))
        {
            environment[ConfigPassVar] = configPassword!;
        }

        return environment;
    }

    /// <summary>
    /// Maps <c>(remote, option)</c> to the environment variable rclone reads for a remote option, matching
    /// rclone's own rule: <c>RCLONE_CONFIG_</c> + upper(remote_option) with '-' replaced by '_'.
    /// Remote names created by NimbusX only use letters, digits and '_'.
    /// </summary>
    public static string RemoteOptionVar(string remoteName, string optionName)
    {
        Guard.NotNullOrWhiteSpace(remoteName, nameof(remoteName));
        Guard.NotNullOrWhiteSpace(optionName, nameof(optionName));
        StringBuilder builder = new("RCLONE_CONFIG_");
        builder.Append(remoteName.ToUpperInvariant());
        builder.Append('_');
        builder.Append(optionName.ToUpperInvariant().Replace('-', '_'));
        return builder.ToString();
    }

    /// <summary>Secret values contained in an environment dictionary (used by tests to prove nothing leaks).</summary>
    public static IReadOnlyList<string> SecretVariableNames() => new[] { ConfigPassVar, RcPassVar };
}
