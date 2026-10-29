using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace NimbusX.Core.Logging;

/// <summary>
/// Removes credentials from any text before it is logged, shown in the UI or written to a diagnostics ZIP.
/// </summary>
/// <remarks>
/// This is a defence-in-depth layer: NimbusX never puts secrets on a command line and keeps them in DPAPI
/// protected storage, but rclone log files, provider error messages and user-visible strings can still carry
/// tokens or cookies. Everything that leaves the process goes through here first.
/// </remarks>
public static class SecretRedactor
{
    /// <summary>Replacement text used for redacted values.</summary>
    public const string Placeholder = "***redacted***";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>key=value / "key": "value" pairs whose value is always a secret.</summary>
    private static readonly Regex KeyValuePattern = new(
        @"(?<key>\b(?:password|passwd|pass|pwd|secret|client_secret|clientsecret|token|access_token|refresh_token|id_token|session_token|sessionid|session_id|api_key|apikey|api_token|auth_token|cookie|set-cookie|authorization|bearer|ndus|rc-pass|rc_pass|rcpass|config_pass|config-password|private_key|key_pem)\b\s*[""']?\s*[:=]\s*[""']?)(?<val>[^""'\s,;&}\]]+)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled,
        Timeout);

    /// <summary>Windows "DOMAIN\user:password" and URL embedded credentials.</summary>
    private static readonly Regex UrlCredentialPattern = new(
        @"(?<scheme>[a-z][a-z0-9+.\-]*://)(?<user>[^/\s:@]+):(?<pass>[^/\s@]+)@",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        Timeout);

    /// <summary>OAuth bearer headers even when the word "bearer" is not followed by a separator.</summary>
    private static readonly Regex BearerHeaderPattern = new(
        @"(?<prefix>\bBearer\s+)(?<val>[A-Za-z0-9\-._~+/]+=*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        Timeout);

    /// <summary>Well known token shapes (Google access tokens, TeraBox ndus cookies, rclone obscured secrets).</summary>
    private static readonly Regex KnownTokenShapePattern = new(
        @"(?<val>(?:ya29\.[A-Za-z0-9_\-]{10,})|(?:1//[A-Za-z0-9_\-]{20,})|(?:sl\.[A-Za-z0-9_\-]{20,})|(?:EAA[A-Za-z0-9]{40,}))",
        RegexOptions.Compiled,
        Timeout);

    /// <summary>rclone config "token = {...}" blobs that arrive without a quoted key.</summary>
    private static readonly Regex RcloneTokenBlobPattern = new(
        @"(?<key>\btoken\s*=\s*)(?<val>\{[^\r\n]*\})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        Timeout);

    /// <summary>Redacts every recognised secret in <paramref name="text"/>.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string result = text;

        result = RcloneTokenBlobPattern.Replace(result, m => m.Groups["key"].Value + Placeholder);

        // Bearer headers must be handled before the key/value pass: that pass stops at the first space, so it would
        // replace the word "Bearer" and leave the token itself in place.
        result = BearerHeaderPattern.Replace(result, m => m.Groups["prefix"].Value + Placeholder);

        result = KeyValuePattern.Replace(
            result,
            m => string.Equals(m.Groups["val"].Value, Placeholder, StringComparison.Ordinal)
                ? m.Value
                : m.Groups["key"].Value + Placeholder);
        result = UrlCredentialPattern.Replace(
            result,
            m => m.Groups["scheme"].Value + m.Groups["user"].Value + ":" + Placeholder + "@");
        result = KnownTokenShapePattern.Replace(
            result,
            m => Placeholder);

        return result;
    }

    /// <summary>Redacts a single key/value pair, keeping the key visible for support purposes.</summary>
    public static string RedactPair(string key, string? value) => key + "=" + Placeholder;

    /// <summary>True when <paramref name="text"/> still appears to contain a secret (used by tests and the exporter).</summary>
    public static bool LooksLikeItContainsSecret(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return !string.Equals(Redact(text), text, StringComparison.Ordinal);
    }

    /// <summary>Keys whose values are secrets. Shared with settings and account serialisation code.</summary>
    public static readonly IReadOnlySet<string> SecretKeyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "token",
        "access_token",
        "refresh_token",
        "id_token",
        "client_secret",
        "clientsecret",
        "password",
        "pass",
        "passwd",
        "pwd",
        "secret",
        "cookie",
        "ndus",
        "sessionid",
        "session_id",
        "api_key",
        "apikey",
        "auth_token",
        "config_pass",
        "rc_pass",
        "private_key",
        "key_pem"
    };

    /// <summary>True when a config/rclone option name holds a secret.</summary>
    public static bool IsSecretKey(string? key) => key is not null && SecretKeyNames.Contains(key);
}
