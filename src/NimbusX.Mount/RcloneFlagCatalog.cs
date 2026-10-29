using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NimbusX.Mount;

/// <summary>Result of validating user supplied extra mount flags.</summary>
public sealed class FlagValidationResult
{
    public FlagValidationResult(IReadOnlyList<string> accepted, IReadOnlyList<string> rejected, IReadOnlyList<string> reasons)
    {
        Accepted = accepted;
        Rejected = rejected;
        Reasons = reasons;
    }

    public IReadOnlyList<string> Accepted { get; }

    public IReadOnlyList<string> Rejected { get; }

    public IReadOnlyList<string> Reasons { get; }

    public bool IsValid => Rejected.Count == 0;

    public string Explain() => IsValid
        ? "All extra flags are supported by the installed rclone."
        : string.Join(Environment.NewLine, Reasons);
}

/// <summary>
/// Knows which flags the installed rclone actually supports (parsed from <c>rclone mount --help</c>) and
/// refuses flags that would break NimbusX's security model.
/// </summary>
/// <remarks>
/// NimbusX builds every mount command line itself. A user may add extra flags in the mount advanced panel, so
/// the list is filtered twice: it must exist in the installed rclone, and it must not touch rc settings, the
/// config file, cache/log paths or run sub-commands. Secrets never go on a command line, so anything that takes
/// a password is refused as well.
/// </remarks>
public sealed class RcloneFlagCatalog
{
    /// <summary>Prefixes that are always refused, whatever the user asks for.</summary>
    private static readonly string[] ForbiddenPrefixes =
    {
        "--rc",
        "--config",
        "--cache-dir",
        "--cache-db",
        "--log-file",
        "--log-format",
        "--password-command",
        "--ask-password",
        "--command",
        "--daemon",
        "--vfs-cache-dir",
        "--dump",
        "--use-json-log",
        "--stats-log-level",
        "--files-from",
        "--filter-from",
        "--exclude-from",
        "--include-from"
    };

    private static readonly HashSet<string> ForbiddenExact = new(StringComparer.OrdinalIgnoreCase)
    {
        "--rc",
        "--config",
        "--config-password",
        "--daemon",
        "--daemon-wait"
    };

    private static readonly Regex FlagPattern = new(@"^\s*--(?<name>[a-z0-9][a-z0-9-]*)(?<rest>.*)$", RegexOptions.Compiled);

    private readonly HashSet<string> _supported;

    private RcloneFlagCatalog(HashSet<string> supported)
    {
        _supported = supported;
    }

    /// <summary>An empty catalog that accepts nothing (used when rclone has not been installed yet).</summary>
    public static RcloneFlagCatalog Empty => new(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public IReadOnlyCollection<string> SupportedFlags => _supported;

    public bool Supports(string flag)
    {
        string name = NormalizeName(flag);
        return name.Length > 0 && _supported.Contains(name);
    }

    /// <summary>Parses the flag list out of <c>rclone mount --help</c> output.</summary>
    public static RcloneFlagCatalog Parse(string? helpText)
    {
        HashSet<string> flags = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(helpText))
        {
            return new RcloneFlagCatalog(flags);
        }

        foreach (string rawLine in helpText!.Split('\n'))
        {
            Match match = FlagPattern.Match(rawLine);
            if (!match.Success)
            {
                continue;
            }

            flags.Add(match.Groups["name"].Value);
        }

        return new RcloneFlagCatalog(flags);
    }

    /// <summary>Validates extra flags the user typed.</summary>
    public FlagValidationResult Validate(IEnumerable<string>? extraFlags)
    {
        List<string> accepted = new();
        List<string> rejected = new();
        List<string> reasons = new();

        if (extraFlags is null)
        {
            return new FlagValidationResult(accepted, rejected, reasons);
        }

        foreach (string raw in extraFlags)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string trimmed = raw.Trim();
            string name = NormalizeName(trimmed);
            if (!trimmed.StartsWith("--", StringComparison.Ordinal) || name.Length == 0)
            {
                rejected.Add(trimmed);
                reasons.Add("'" + trimmed + "' is not a long-form rclone flag (--name or --name=value).");
                continue;
            }

            if (ForbiddenExact.Contains("--" + name) || ForbiddenPrefixes.Any(prefix => name.StartsWith(prefix.TrimStart('-'), StringComparison.OrdinalIgnoreCase)))
            {
                rejected.Add(trimmed);
                reasons.Add("'" + trimmed + "' is refused: it would change NimbusX's rc, config, cache or log settings.");
                continue;
            }

            if (_supported.Count > 0 && !_supported.Contains(name))
            {
                rejected.Add(trimmed);
                reasons.Add("'" + trimmed + "' is not supported by the installed rclone (checked against 'rclone mount --help').");
                continue;
            }

            accepted.Add(trimmed);
        }

        return new FlagValidationResult(accepted, rejected, reasons);
    }

    /// <summary>Extracts the flag name without dashes and without a value.</summary>
    public static string NormalizeName(string flag)
    {
        if (string.IsNullOrWhiteSpace(flag))
        {
            return string.Empty;
        }

        string trimmed = flag.Trim();
        if (trimmed.StartsWith("--", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }

        int equals = trimmed.IndexOf('=');
        if (equals >= 0)
        {
            trimmed = trimmed[..equals];
        }

        int space = trimmed.IndexOf(' ');
        if (space >= 0)
        {
            trimmed = trimmed[..space];
        }

        return trimmed.Trim().ToLowerInvariant();
    }

    /// <summary>The flags NimbusX always passes, in a stable order (used by tests and diagnostics).</summary>
    public static IReadOnlyList<string> CoreMountFlags() => new[]
    {
        "--vfs-cache-mode", "full",
        "--buffer-size", "0",
        "--vfs-read-chunk-size", "4M",
        "--vfs-read-chunk-size-limit", "off",
        "--file-perms", "0777",
        "--dir-perms", "0777",
        "--attr-timeout", "1s",
        "--dir-cache-time", "1h",
        "--vfs-cache-poll-interval", "1m",
        "--vfs-cache-max-age", "168h",
        "--timeout", "10m",
        "--contimeout", "30s",
        "--low-level-retries", "20",
        "--log-format", "date,time,microseconds",
        "-vv"
    };
}
