using System;
using System.Collections.Generic;
using System.Linq;

namespace NimbusX.Providers;

/// <summary>Result of turning browser cookies into the cookie string the TeraBox backend accepts.</summary>
public sealed class CookieBuildResult
{
    public CookieBuildResult(bool success, string? cookieHeader, string? error, string? fix, DateTimeOffset? expiresUtc)
    {
        Success = success;
        CookieHeader = cookieHeader;
        Error = error;
        Fix = fix;
        ExpiresUtc = expiresUtc;
    }

    public bool Success { get; }

    /// <summary>The value passed to rclone via <c>RCLONE_CONFIG_&lt;REMOTE&gt;_COOKIE</c>.</summary>
    public string? CookieHeader { get; }

    public string? Error { get; }

    public string? Fix { get; }

    public DateTimeOffset? ExpiresUtc { get; }

    public static CookieBuildResult Fail(string error, string fix) => new(false, null, error, fix, null);

    public static CookieBuildResult Ok(string header, DateTimeOffset? expiresUtc) => new(true, header, null, null, expiresUtc);
}

/// <summary>
/// Builds the TeraBox session cookie from what the embedded login window saw.
/// </summary>
/// <remarks>
/// The community TeraBox backend authenticates with the <c>ndus</c> cookie (or a full cookie string). NimbusX reads
/// the cookies from the WebView2 store after the user signs in and sends <c>ndus=...; lang=en</c>, which is the
/// minimum the backend accepts. The value is stored with DPAPI and only ever handed to rclone through its
/// environment.
/// </remarks>
public static class TeraboxCookieBuilder
{
    public const string SessionCookieName = "ndus";

    /// <summary>Domains whose cookies count as a TeraBox session.</summary>
    private static readonly string[] TeraboxDomains =
    {
        "terabox",
        "1024tera",
        "4funbox",
        "freeterabox",
        "dubox",
        "teraboxapp"
    };

    /// <summary>Picks the session cookie out of a set and returns the header rclone needs.</summary>
    public static CookieBuildResult Build(IEnumerable<BrowserCookie> cookies, DateTimeOffset now)
    {
        if (cookies is null)
        {
            return CookieBuildResult.Fail(
                "The login window returned no cookies.",
                "Press Connect again and finish signing in to TeraBox.");
        }

        List<BrowserCookie> list = cookies.Where(c => !string.IsNullOrEmpty(c.Name)).ToList();
        if (list.Count == 0)
        {
            return CookieBuildResult.Fail(
                "No cookies were found after the login window closed.",
                "Sign in fully (enter your email/phone and password) before pressing Done.");
        }

        BrowserCookie? session = list.FirstOrDefault(
            c => c.Name.Equals(SessionCookieName, StringComparison.OrdinalIgnoreCase) && IsTeraboxDomain(c.Domain));

        session ??= list.FirstOrDefault(c => c.Name.Equals(SessionCookieName, StringComparison.OrdinalIgnoreCase));

        if (session is null)
        {
            return CookieBuildResult.Fail(
                "The 'ndus' session cookie was not found, so you are probably not signed in yet.",
                "Sign in to TeraBox in the window NimbusX opened, wait until your files are visible, then press Done.");
        }

        if (string.IsNullOrWhiteSpace(session.Value) || session.Value.Length < 8)
        {
            return CookieBuildResult.Fail(
                "The TeraBox session cookie is empty or incomplete.",
                "Sign out and back in inside the login window, then press Done again.");
        }

        if (session.IsExpired(now))
        {
            return CookieBuildResult.Fail(
                "The TeraBox session cookie has already expired.",
                "Sign in again; the session must be fresh when you press Done.");
        }

        string header = SessionCookieName + "=" + session.Value.Trim() + "; lang=en";
        return CookieBuildResult.Ok(header, session.ExpiresUtc);
    }

    /// <summary>True when a cookie domain belongs to TeraBox.</summary>
    public static bool IsTeraboxDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return false;
        }

        return TeraboxDomains.Any(d => domain!.IndexOf(d, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>Recognises the errors rclone reports when the stored cookie is no longer valid.</summary>
    public static bool IsExpiredSessionError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        string text = message!;
        return text.Contains("ndus", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("login", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("401", StringComparison.Ordinal) ||
               text.Contains("errno", StringComparison.OrdinalIgnoreCase) && text.Contains("2", StringComparison.Ordinal);
    }

    /// <summary>Host to open in the login window for a provider domain option.</summary>
    public static Uri LoginUri(string? domainOption)
    {
        string domain = string.IsNullOrWhiteSpace(domainOption) ? "https://www.terabox.com" : domainOption!.Trim();
        if (!domain.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            domain = "https://" + domain;
        }

        return new Uri(domain.TrimEnd('/') + "/main");
    }

    /// <summary>Cookie host filter for the WebView2 store.</summary>
    public static IReadOnlyList<string> CookieHosts() => TeraboxDomains;
}
