using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NimbusX.Core;
using NimbusX.Core.Logging;
using NimbusX.Mount;

namespace NimbusX.Providers;

/// <summary>
/// Connects an OAuth provider with a single button.
/// </summary>
/// <remarks>
/// The flow is: rclone's config state machine is started with <c>nonInteractive</c>; when NimbusX answers
/// "use a local browser" rclone opens its own callback server on 127.0.0.1 and blocks. NimbusX polls
/// <c>config/oauthstatus</c> for the authorization URL, opens it in the user's default browser and waits for rclone
/// to finish exchanging the code. Nothing is pasted by the user and no token ever passes through NimbusX code
/// beyond the rclone config file, which is itself encrypted.
/// </remarks>
public sealed class OAuthConnectFlow
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMinutes(10);

    private readonly RcloneConfigStateMachine _stateMachine;
    private readonly IBrowserLauncher _browser;
    private readonly IAppLogger _logger;

    public OAuthConnectFlow(IBrowserLauncher? browser = null, IAppLogger? logger = null)
    {
        _browser = browser ?? new ShellBrowserLauncher();
        _logger = logger ?? NullLogger.Instance;
        _stateMachine = new RcloneConfigStateMachine(_logger);
    }

    /// <summary>Runs the OAuth connect for one provider.</summary>
    public async Task<StateMachineResult> ConnectAsync(
        RcloneRcClient rc,
        ProviderDefinition provider,
        string remoteName,
        bool useCustomClient,
        string? clientId,
        string? clientSecret,
        IProgress<ConnectProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rc);
        ArgumentNullException.ThrowIfNull(provider);
        Guard.NotNullOrWhiteSpace(remoteName, nameof(remoteName));

        JsonObject parameters = new();
        foreach (KeyValuePair<string, string> pair in provider.FixedParameters)
        {
            parameters[pair.Key] = pair.Value;
        }

        if (useCustomClient)
        {
            if (string.IsNullOrWhiteSpace(clientId))
            {
                return StateMachineResult.Fail("A custom OAuth client id is required when the Advanced toggle is on.", Array.Empty<string>(), Array.Empty<ConfigQuestion>());
            }

            parameters[provider.ClientIdOptionName] = clientId!.Trim();
            if (!string.IsNullOrWhiteSpace(clientSecret))
            {
                parameters[provider.ClientSecretOptionName] = clientSecret!.Trim();
            }
        }

        string? authUrl = null;
        bool browserOpened = false;
        progress?.Report(new ConnectProgress(ConnectPhase.Preparing, "Starting the " + provider.DisplayName + " sign-in..."));

        async Task OnTick(CancellationToken token)
        {
            if (browserOpened)
            {
                return;
            }

            RcOAuthStatus status = await rc.OAuthStatusAsync(token).ConfigureAwait(false);
            if (!status.Running || string.IsNullOrWhiteSpace(status.AuthUrl))
            {
                return;
            }

            browserOpened = true;
            authUrl = status.AuthUrl;
            progress?.Report(new ConnectProgress(
                ConnectPhase.WaitingForBrowser,
                "Approve NimbusX in your browser, then come back here.",
                status.AuthUrl));

            if (!_browser.Open(status.AuthUrl!))
            {
                _logger.Warn("Could not open the default browser for the OAuth flow.", "Providers");
            }
        }

        string Answer(ConfigQuestion question)
        {
            if (question.Name is "config_is_local" or "config_refresh_token")
            {
                return "true";
            }

            if (question.Name is "config_client_id")
            {
                return clientId?.Trim() ?? question.DefaultValue ?? string.Empty;
            }

            if (question.Name is "config_client_secret")
            {
                return clientSecret?.Trim() ?? question.DefaultValue ?? string.Empty;
            }

            if (question.Name.StartsWith("config_", StringComparison.OrdinalIgnoreCase) &&
                parameters.TryGetPropertyValue(question.Name[7..], out JsonNode? node) && node is JsonValue value &&
                value.TryGetValue(out string? text))
            {
                return text ?? string.Empty;
            }

            return RcloneConfigStateMachine.DefaultAnswer(question);
        }

        StateMachineResult result = await _stateMachine
            .RunAsync(rc, remoteName, provider.RcloneType, parameters, Answer, OnTick, ConnectTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            await rc.StopOAuthAsync(cancellationToken).ConfigureAwait(false);
            progress?.Report(new ConnectProgress(ConnectPhase.Failed, result.Error ?? "Sign-in failed."));
            return result;
        }

        progress?.Report(new ConnectProgress(ConnectPhase.VerifyingAccess, "Checking that the login really works..."));

        // Verify that rclone really stored a token; a flow that "succeeded" without one must never be reported as
        // a connected account.
        JsonObject? stored = null;
        try
        {
            stored = await rc.ConfigGetAsync(remoteName, cancellationToken).ConfigureAwait(false);
        }
        catch (RcloneRcException exception)
        {
            return StateMachineResult.Fail(
                "The provider did not save the login: " + exception.Message,
                result.Steps,
                result.Questions);
        }

        string? token = stored is null ? null : RcJson.String(stored, "token");
        if (string.IsNullOrWhiteSpace(token))
        {
            return StateMachineResult.Fail(
                "The sign-in finished but no access token was stored. The provider did not grant access.",
                result.Steps,
                result.Questions);
        }

        progress?.Report(new ConnectProgress(ConnectPhase.Done, provider.DisplayName + " connected."));
        return new StateMachineResult(true, null, result.Steps, result.Questions, authUrl);
    }
}
