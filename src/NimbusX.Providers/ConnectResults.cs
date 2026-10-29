using System;
using System.Collections.Generic;
using NimbusX.Core;
using NimbusX.Core.Logging;

namespace NimbusX.Providers;

/// <summary>Where a connect flow currently is.</summary>
public enum ConnectPhase
{
    Preparing = 0,
    WaitingForBrowser = 1,
    WaitingForUser = 2,
    SavingCredentials = 3,
    VerifyingAccess = 4,
    Done = 5,
    Failed = 6
}

/// <summary>Progress reported to the UI while connecting.</summary>
public sealed class ConnectProgress
{
    public ConnectProgress(ConnectPhase phase, string message, string? authUrl = null)
    {
        Phase = phase;
        Message = SecretRedactor.Redact(message);
        AuthUrl = authUrl;
    }

    public ConnectPhase Phase { get; }

    public string Message { get; }

    /// <summary>URL the user must approve, when the flow is waiting for a browser.</summary>
    public string? AuthUrl { get; }

    public override string ToString() => Phase + ": " + Message;
}

/// <summary>Outcome of a connect attempt.</summary>
public sealed class ConnectResult
{
    private ConnectResult(
        bool success,
        CloudAccount? account,
        string? error,
        string? fix,
        string? authUrl,
        IReadOnlyList<string> steps,
        string? logTail)
    {
        Success = success;
        Account = account;
        Error = error;
        Fix = fix;
        AuthUrl = authUrl;
        Steps = steps;
        LogTail = logTail;
    }

    public bool Success { get; }

    public CloudAccount? Account { get; }

    /// <summary>The provider's or rclone's real error text (redacted).</summary>
    public string? Error { get; }

    public string? Fix { get; }

    public string? AuthUrl { get; }

    /// <summary>Human readable trace of the state machine, shown under "details".</summary>
    public IReadOnlyList<string> Steps { get; }

    public string? LogTail { get; }

    public static ConnectResult Ok(CloudAccount account, IReadOnlyList<string>? steps = null, string? authUrl = null) =>
        new(true, account, null, null, authUrl, steps ?? Array.Empty<string>(), null);

    public static ConnectResult Fail(string error, string? fix = null, IReadOnlyList<string>? steps = null, string? logTail = null) =>
        new(false, null, SecretRedactor.Redact(error), fix, null, steps ?? Array.Empty<string>(), logTail);
}

/// <summary>Values collected by a provider form.</summary>
public sealed class ConnectInputs
{
    public ConnectInputs(string providerId)
    {
        ProviderId = Guard.NotNullOrWhiteSpace(providerId, nameof(providerId));
    }

    public string ProviderId { get; }

    /// <summary>Field values keyed by <see cref="ProviderField.Name"/>.</summary>
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional display name; NimbusX generates a numbered one when empty.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Preferred drive letter, e.g. "H".</summary>
    public string? PreferredDriveLetter { get; set; }

    public bool AutoMount { get; set; } = true;

    /// <summary>Use the user's own OAuth client instead of rclone's built-in one.</summary>
    public bool UseCustomClient { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string? Get(string name) => Values.TryGetValue(name, out string? value) ? value : null;

    public ConnectInputs With(string name, string? value)
    {
        if (value is not null)
        {
            Values[name] = value;
        }

        return this;
    }
}

/// <summary>Outcome of a quota refresh.</summary>
public sealed class QuotaRefreshResult
{
    public QuotaRefreshResult(bool success, long? used, long? total, string? error)
    {
        Success = success;
        Used = used;
        Total = total;
        Error = error;
    }

    public bool Success { get; }

    public long? Used { get; }

    public long? Total { get; }

    public string? Error { get; }

    public static QuotaRefreshResult Ok(long? used, long? total) => new(true, used, total, null);

    public static QuotaRefreshResult Fail(string error) => new(false, null, null, error);
}
