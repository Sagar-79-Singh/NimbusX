using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NimbusX.Core;
using NimbusX.Core.Config;
using NimbusX.Core.Logging;
using NimbusX.Core.Security;
using NimbusX.Mount;

namespace NimbusX.Providers;

/// <summary>
/// Owns the rclone configuration file and its encryption key.
/// </summary>
/// <remarks>
/// The config holds every provider token, so it is encrypted with rclone's own config encryption. The random key
/// is generated once, stored with Windows DPAPI (CurrentUser) and handed to rclone only through
/// <c>RCLONE_CONFIG_PASS</c>. NimbusX never writes the key to a file, a command line or a log.
/// </remarks>
public sealed class RcloneConfigManager
{
    private readonly AppPaths _paths;
    private readonly ISecretStore _secrets;
    private readonly IAppLogger _logger;

    public RcloneConfigManager(AppPaths paths, ISecretStore secrets, IAppLogger? logger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _logger = logger ?? NullLogger.Instance;
    }

    public string ConfigPath => _paths.RcloneConfigFile;

    /// <summary>True when a config key exists in DPAPI protected storage.</summary>
    public bool HasConfigPassword => _secrets.Contains(SecretKeys.RcloneConfigPassword);

    /// <summary>Loads the config key, creating one on first use.</summary>
    public string EnsureConfigPassword()
    {
        string? existing = _secrets.TryLoad(SecretKeys.RcloneConfigPassword);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing!;
        }

        string generated = RandomSecrets.Password(48);
        _secrets.Save(SecretKeys.RcloneConfigPassword, generated);
        _logger.Info("Created a new rclone configuration key (stored with Windows DPAPI).", "Config");
        return generated;
    }

    /// <summary>True when the config file exists and looks encrypted (rclone writes base64, not INI text).</summary>
    public bool IsConfigEncrypted()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return false;
            }

            using StreamReader reader = new(ConfigPath);
            char[] buffer = new char[64];
            int read = reader.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                return false;
            }

            string head = new string(buffer, 0, read).TrimStart();
            return !head.StartsWith("[", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Makes sure the config file exists and is encrypted, creating it through rclone so the key is applied.
    /// </summary>
    public async Task EnsureInitialisedAsync(RcloneRcClient rc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rc);
        EnsureConfigPassword();

        if (!File.Exists(ConfigPath))
        {
            Directory.CreateDirectory(_paths.Root);
            // Create and delete a throwaway remote: rclone writes the file on save, which is what applies the
            // encryption key. A local (notebook) backend needs no network access.
            const string probe = "nx_probe_local";
            await rc.ConfigCreateAsync(
                probe,
                "local",
                new JsonObject(),
                new JsonObject { ["nonInteractive"] = true, ["noOutput"] = true },
                cancellationToken).ConfigureAwait(false);
            await rc.DeleteRemoteAsync(probe, cancellationToken).ConfigureAwait(false);
            _logger.Info("Created the rclone configuration file.", "Config");
        }

        if (!IsConfigEncrypted())
        {
            await ReencryptAsync(rc, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Re-writes every remote so rclone saves the file encrypted. Values from <c>config/dump</c> are already
    /// obscured for password options, so they are written back with <c>noObscure</c> to avoid double obscuring.
    /// </summary>
    public async Task ReencryptAsync(RcloneRcClient rc, CancellationToken cancellationToken)
    {
        JsonObject dump = await rc.CallAsync("config/dump", null, TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);

        List<(string Name, string Type, JsonObject Parameters)> remotes = new();
        foreach (KeyValuePair<string, JsonNode?> entry in dump)
        {
            if (entry.Value is not JsonObject values)
            {
                continue;
            }

            string? type = RcJson.String(values, "type");
            if (string.IsNullOrWhiteSpace(type))
            {
                continue;
            }

            JsonObject parameters = new();
            foreach (KeyValuePair<string, JsonNode?> value in values)
            {
                if (value.Key.Equals("type", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                parameters[value.Key] = value.Value?.DeepClone();
            }

            remotes.Add((entry.Key, type!, parameters));
        }

        foreach ((string name, string type, JsonObject parameters) in remotes)
        {
            await rc.ConfigCreateAsync(
                name,
                type,
                parameters,
                new JsonObject { ["nonInteractive"] = true, ["noObscure"] = true, ["noOutput"] = true },
                cancellationToken).ConfigureAwait(false);
        }

        if (!IsConfigEncrypted())
        {
            throw new InvalidOperationException(
                "rclone did not encrypt the configuration file. Check that RCLONE_CONFIG_PASS reaches the rclone process.");
        }

        _logger.Info("Encrypted the rclone configuration file (" + remotes.Count + " remote(s)).", "Config");
    }

    /// <summary>Deletes a remote from the config.</summary>
    public async Task DeleteRemoteAsync(RcloneRcClient rc, string remoteName, CancellationToken cancellationToken)
    {
        try
        {
            await rc.DeleteRemoteAsync(remoteName, cancellationToken).ConfigureAwait(false);
        }
        catch (RcloneRcException exception)
        {
            _logger.Debug("Removing remote '" + remoteName + "' failed: " + exception.Message, "Config");
        }
    }

    /// <summary>Reads <c>config userinfo</c> for a remote so the card can show the account e-mail.</summary>
    public static string? ParseUserInfo(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        foreach (string rawLine in output!.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("Username:", StringComparison.OrdinalIgnoreCase))
            {
                string value = line["Username:".Length..].Trim();
                if (value.Length > 0)
                {
                    return value;
                }
            }
        }

        return null;
    }
}
