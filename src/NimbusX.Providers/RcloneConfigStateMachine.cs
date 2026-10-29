using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NimbusX.Core.Logging;
using NimbusX.Mount;

namespace NimbusX.Providers;

/// <summary>A question rclone asked while creating a remote.</summary>
public sealed class ConfigQuestion
{
    public ConfigQuestion(string state, string name, string? help, string? defaultValue, bool isPassword, bool required, bool advanced)
    {
        State = state;
        Name = name;
        Help = help;
        DefaultValue = defaultValue;
        IsPassword = isPassword;
        Required = required;
        Advanced = advanced;
    }

    /// <summary>Opaque state token that must be echoed back with the answer.</summary>
    public string State { get; }

    /// <summary>Option name, e.g. <c>config_is_local</c>.</summary>
    public string Name { get; }

    public string? Help { get; }

    public string? DefaultValue { get; }

    public bool IsPassword { get; }

    public bool Required { get; }

    public bool Advanced { get; }

    public override string ToString() => Name + (Required ? " (required)" : string.Empty);
}

/// <summary>Result of driving rclone's config state machine to the end.</summary>
public sealed class StateMachineResult
{
    public StateMachineResult(bool success, string? error, IReadOnlyList<string> steps, IReadOnlyList<ConfigQuestion> questions, string? authUrl)
    {
        Success = success;
        Error = error;
        Steps = steps;
        Questions = questions;
        AuthUrl = authUrl;
    }

    public bool Success { get; }

    public string? Error { get; }

    public IReadOnlyList<string> Steps { get; }

    public IReadOnlyList<ConfigQuestion> Questions { get; }

    public string? AuthUrl { get; }

    public static StateMachineResult Fail(string error, IReadOnlyList<string> steps, IReadOnlyList<ConfigQuestion> questions) =>
        new(false, error, steps, questions, null);
}

/// <summary>
/// Drives rclone's <c>config/create</c> state machine to completion.
/// </summary>
/// <remarks>
/// rclone's configuration system is a state machine: every call returns either a finished result or a question
/// plus an opaque state token. NimbusX answers the questions that matter (use a local browser, replace an old
/// token, custom client id/secret) and accepts rclone's defaults for everything else, so a connect never needs a
/// terminal. Calls are made asynchronously because the OAuth step blocks inside rclone until the browser comes
/// back, and <c>config/oauthstatus</c> is polled meanwhile to discover the authorization URL.
/// </remarks>
public sealed class RcloneConfigStateMachine
{
    /// <summary>Guards against a backend that keeps re-asking the same question.</summary>
    public const int MaxSteps = 40;

    private readonly IAppLogger _logger;

    public RcloneConfigStateMachine(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Runs the state machine until rclone reports it is finished.</summary>
    public async Task<StateMachineResult> RunAsync(
        RcloneRcClient rc,
        string remoteName,
        string rcloneType,
        JsonObject parameters,
        Func<ConfigQuestion, string> answer,
        Func<CancellationToken, Task>? onTick,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rc);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(answer);

        List<string> steps = new();
        List<ConfigQuestion> questions = new();
        string? state = null;
        string? result = null;
        bool isContinue = false;
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        for (int step = 0; step < MaxSteps; step++)
        {
            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return StateMachineResult.Fail("The provider did not finish signing in within " +
                                               (int)timeout.TotalMinutes + " minutes.", steps, questions);
            }

            JsonObject body = new()
            {
                ["name"] = remoteName,
                ["type"] = rcloneType,
                ["parameters"] = parameters.DeepClone(),
                ["opt"] = BuildOptions(isContinue, state, result)
            };

            JsonObject response;
            try
            {
                response = await rc.CallAsJobAsync("config/create", body, remaining, onTick, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (RcloneRcException exception)
            {
                return StateMachineResult.Fail(exception.Message, steps, questions);
            }

            string? errorText = RcJson.String(response, "Error");
            if (!string.IsNullOrWhiteSpace(errorText))
            {
                steps.Add("rclone: " + errorText);
                _logger.Debug("config/create reported: " + errorText, "Providers");
            }

            string? nextState = RcJson.String(response, "State");
            JsonObject? option = RcJson.Object(response, "Option");

            if (option is null)
            {
                if (string.IsNullOrEmpty(nextState))
                {
                    steps.Add("rclone finished configuring '" + remoteName + "'.");
                    return new StateMachineResult(true, null, steps, questions, null);
                }

                // No question but a state to jump to: continue immediately with an empty result.
                state = nextState;
                result = null;
                isContinue = true;
                continue;
            }

            ConfigQuestion question = new(
                nextState ?? string.Empty,
                RcJson.String(option, "Name") ?? string.Empty,
                RcJson.String(option, "Help"),
                OptionDefault(option),
                RcJson.Bool(option, "IsPassword"),
                RcJson.Bool(option, "Required"),
                RcJson.Bool(option, "Advanced"));

            questions.Add(question);

            if (string.IsNullOrWhiteSpace(question.Name))
            {
                return StateMachineResult.Fail("rclone asked a question without a name; the backend is not supported.", steps, questions);
            }

            string answerValue;
            try
            {
                answerValue = answer(question) ?? string.Empty;
            }
            catch (Exception exception)
            {
                return StateMachineResult.Fail("Could not answer rclone's question '" + question.Name + "': " + exception.Message, steps, questions);
            }

            steps.Add("answered '" + question.Name + "'" + (question.IsPassword ? " (value hidden)" : " = " + answerValue));
            state = question.State;
            result = answerValue;
            isContinue = true;
        }

        return StateMachineResult.Fail(
            "rclone kept asking configuration questions (more than " + MaxSteps + "); the provider is not supported by this build.",
            steps,
            questions);
    }

    /// <summary>Builds the <c>opt</c> dictionary rclone expects.</summary>
    public static JsonObject BuildOptions(bool isContinue, string? state, string? result)
    {
        JsonObject options = new() { ["nonInteractive"] = true, ["noOutput"] = true };
        if (isContinue)
        {
            options["continue"] = true;
            if (state is not null)
            {
                options["state"] = state;
            }

            if (result is not null)
            {
                options["result"] = result;
            }
        }

        return options;
    }

    /// <summary>Default answer for a question, used when nothing better is known.</summary>
    public static string DefaultAnswer(ConfigQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);

        // rclone's own OAuth questions.
        if (question.Name is "config_is_local" or "config_refresh_token")
        {
            return "true";
        }

        if (question.Name == "config_verification_code")
        {
            return string.Empty;
        }

        return question.DefaultValue ?? string.Empty;
    }

    private static string? OptionDefault(JsonObject option)
    {
        if (!option.TryGetPropertyValue("Default", out JsonNode? value) || value is null)
        {
            return null;
        }

        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue(out string? text))
            {
                return text;
            }

            if (jsonValue.TryGetValue(out bool flag))
            {
                return flag ? "true" : "false";
            }

            if (jsonValue.TryGetValue(out long number))
            {
                return number.ToString();
            }
        }

        return null;
    }
}
