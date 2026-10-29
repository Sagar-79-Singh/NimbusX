using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NimbusX.Core.Logging;
using NimbusX.Mount;

namespace NimbusX.Providers;

/// <summary>
/// Connects the providers that take a form instead of a browser sign-in (MEGA, WebDAV, SFTP, Koofr).
/// </summary>
public sealed class CredentialConnectFlow
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMinutes(5);

    private readonly RcloneConfigStateMachine _stateMachine;
    private readonly IAppLogger _logger;

    public CredentialConnectFlow(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _stateMachine = new RcloneConfigStateMachine(_logger);
    }

    /// <summary>Creates the remote from form values and verifies that rclone stored it.</summary>
    public Task<StateMachineResult> ConnectAsync(
        RcloneRcClient rc,
        ProviderDefinition provider,
        string remoteName,
        IReadOnlyDictionary<string, string> values,
        IProgress<ConnectProgress>? progress,
        CancellationToken cancellationToken) =>
        ConnectAsync(rc, provider, remoteName, values, null, progress, cancellationToken);

    /// <summary>
    /// Creates the remote from form values plus optional extra rclone parameters (for example the TeraBox session
    /// cookie, which is passed here only long enough to create the remote and is then removed from the config).
    /// </summary>
    public async Task<StateMachineResult> ConnectAsync(
        RcloneRcClient rc,
        ProviderDefinition provider,
        string remoteName,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string>? extraParameters,
        IProgress<ConnectProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rc);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(values);

        JsonObject parameters = new();
        foreach (KeyValuePair<string, string> pair in provider.FixedParameters)
        {
            parameters[pair.Key] = pair.Value;
        }

        if (extraParameters is not null)
        {
            foreach (KeyValuePair<string, string> pair in extraParameters)
            {
                parameters[pair.Key] = pair.Value;
            }
        }

        foreach (ProviderField field in provider.Fields)
        {
            if (!values.TryGetValue(field.Name, out string? value))
            {
                if (field.Required && field.DefaultValue is null)
                {
                    return StateMachineResult.Fail(
                        "'" + field.Label + "' is required for " + provider.DisplayName + ".",
                        Array.Empty<string>(),
                        Array.Empty<ConfigQuestion>());
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                if (field.Required)
                {
                    return StateMachineResult.Fail(
                        "'" + field.Label + "' is required for " + provider.DisplayName + ".",
                        Array.Empty<string>(),
                        Array.Empty<ConfigQuestion>());
                }

                continue;
            }

            parameters[field.Name] = value.Trim();
        }

        progress?.Report(new ConnectProgress(ConnectPhase.SavingCredentials, "Saving the " + provider.DisplayName + " login..."));

        string Answer(ConfigQuestion question)
        {
            string key = question.Name.StartsWith("config_", StringComparison.OrdinalIgnoreCase)
                ? question.Name[7..]
                : question.Name;

            if (parameters.TryGetPropertyValue(key, out JsonNode? node) && node is JsonValue value &&
                value.TryGetValue(out string? text) && !string.IsNullOrEmpty(text))
            {
                return text!;
            }

            return RcloneConfigStateMachine.DefaultAnswer(question);
        }

        StateMachineResult result = await _stateMachine
            .RunAsync(rc, remoteName, provider.RcloneType, parameters, Answer, null, ConnectTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            progress?.Report(new ConnectProgress(ConnectPhase.Failed, result.Error ?? "The provider rejected the login."));
            return result;
        }

        try
        {
            JsonObject? stored = await rc.ConfigGetAsync(remoteName, cancellationToken).ConfigureAwait(false);
            if (stored is null)
            {
                return StateMachineResult.Fail(
                    "rclone did not store the remote. Nothing was saved.",
                    result.Steps,
                    result.Questions);
            }
        }
        catch (RcloneRcException exception)
        {
            return StateMachineResult.Fail(
                "The remote could not be verified: " + exception.Message,
                result.Steps,
                result.Questions);
        }

        progress?.Report(new ConnectProgress(ConnectPhase.Done, provider.DisplayName + " connected."));
        return new StateMachineResult(true, null, result.Steps, result.Questions, null);
    }
}
