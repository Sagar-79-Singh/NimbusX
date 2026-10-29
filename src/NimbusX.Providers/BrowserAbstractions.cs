using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace NimbusX.Providers;

/// <summary>A cookie read from the embedded login window.</summary>
public sealed class BrowserCookie
{
    public BrowserCookie(string name, string value, string domain, string path, DateTimeOffset? expiresUtc)
    {
        Name = name;
        Value = value;
        Domain = domain;
        Path = path;
        ExpiresUtc = expiresUtc;
    }

    public string Name { get; }

    public string Value { get; }

    public string Domain { get; }

    public string Path { get; }

    public DateTimeOffset? ExpiresUtc { get; }

    public bool IsExpired(DateTimeOffset now) => ExpiresUtc.HasValue && ExpiresUtc.Value <= now;

    public override string ToString() => Name + " @ " + Domain;
}

/// <summary>Opens a URL in the user's default browser.</summary>
public interface IBrowserLauncher
{
    bool Open(string url);
}

/// <summary>Default launcher used by the desktop app and the CLI.</summary>
public sealed class ShellBrowserLauncher : IBrowserLauncher
{
    public bool Open(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// An embedded browser window (WebView2 in the desktop app) used for logins whose session NimbusX must read.
/// </summary>
/// <remarks>
/// The user signs in on the provider's real site; NimbusX then reads the provider's own cookies from the WebView2
/// cookie store. Nobody copies or pastes a cookie, and the cookie never appears in a log or on a command line.
/// </remarks>
public interface IBrowserLoginSession : IAsyncDisposable
{
    /// <summary>Shows the window and completes when the user presses "Done" (or the window closes).</summary>
    Task<bool> ShowAsync(Uri startUrl, string title, CancellationToken cancellationToken);

    /// <summary>Reads cookies for a host from the browser's cookie store.</summary>
    Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(string host, CancellationToken cancellationToken);

    /// <summary>Current address of the window, for progress messages.</summary>
    Uri? CurrentAddress { get; }
}

/// <summary>Creates <see cref="IBrowserLoginSession"/> windows (WebView2 lives in the WPF project).</summary>
public interface IBrowserLoginSessionFactory
{
    /// <summary>True when the WebView2 runtime and control are available.</summary>
    bool IsAvailable { get; }

    Task<IBrowserLoginSession> CreateAsync(string title, CancellationToken cancellationToken);
}

/// <summary>Factory used when no embedded browser is available (CLI, headless tests).</summary>
public sealed class UnavailableBrowserLoginSessionFactory : IBrowserLoginSessionFactory
{
    public bool IsAvailable => false;

    public Task<IBrowserLoginSession> CreateAsync(string title, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "The embedded login window is only available in the NimbusX desktop app (WebView2 runtime required).");
}
