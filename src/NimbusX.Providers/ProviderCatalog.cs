using System;
using System.Collections.Generic;
using System.Linq;

namespace NimbusX.Providers;

/// <summary>
/// The providers NimbusX can connect with one click, with the exact rclone backend and options each one uses.
/// </summary>
/// <remarks>
/// All OAuth entries below are verified against rclone 1.75.2 source: Google Drive, OneDrive, Dropbox, pCloud, Box
/// and Yandex Disk ship a built-in rclone client id, so a browser sign-in works without the user registering an
/// application. Koofr is deliberately <em>not</em> OAuth in rclone 1.75.2 - its backend takes a user name and an
/// app password - so NimbusX shows an app-password form with a direct link to Koofr's password page instead of
/// pretending a browser flow exists. MEGA uses email + password, WebDAV/SFTP use connection details, and TeraBox
/// needs the community rclone build plus a captured session cookie.
/// </remarks>
public static class ProviderCatalog
{
    private const string KoofrPasswordPage = "https://app.koofr.net/app/admin/preferences/password";
    private const string MegaWebLogin = "https://mega.nz/login";
    private const string TeraboxLogin = "https://www.terabox.com/login";

    private static readonly ProviderDefinition[] Providers =
    {
        new("drive", "Google Drive", "The most common cloud drive for game folders. 15 GB free per account.",
            "drive", ProviderConnectKind.OAuth, "\uE8F1")
        {
            HasBuiltInOAuthClient = true,
            HelpUrl = "https://rclone.org/drive/",
            FixedParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["scope"] = "drive" }
        },
        new("onedrive", "OneDrive", "Microsoft OneDrive, including personal and work accounts.",
            "onedrive", ProviderConnectKind.OAuth, "\uE753")
        {
            HasBuiltInOAuthClient = true,
            HelpUrl = "https://rclone.org/onedrive/",
            FixedParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["region"] = "global" }
        },
        new("dropbox", "Dropbox", "Dropbox with its own drive letter and cache folder.",
            "dropbox", ProviderConnectKind.OAuth, "\uE7C3")
        {
            HasBuiltInOAuthClient = true,
            HelpUrl = "https://rclone.org/dropbox/"
        },
        new("pcloud", "pCloud", "pCloud (EU and US regions are detected by pCloud itself).",
            "pcloud", ProviderConnectKind.OAuth, "\uE753")
        {
            HasBuiltInOAuthClient = true,
            HelpUrl = "https://rclone.org/pcloud/"
        },
        new("box", "Box", "Box cloud storage.",
            "box", ProviderConnectKind.OAuth, "\uE7B8")
        {
            HasBuiltInOAuthClient = true,
            HelpUrl = "https://rclone.org/box/"
        },
        new("yandex", "Yandex Disk", "Yandex Disk, popular in Eastern Europe and Central Asia.",
            "yandex", ProviderConnectKind.OAuth, "\uE774")
        {
            HasBuiltInOAuthClient = true,
            HelpUrl = "https://rclone.org/yandex/"
        },
        new("koofr", "Koofr", "Koofr and Digi Storage. Uses an app password, not a browser sign-in.",
            "koofr", ProviderConnectKind.Credentials, "\uE72E")
        {
            Fields = new[]
            {
                new ProviderField("user", "Email", ProviderFieldKind.Email, required: true,
                    placeholder: "you@example.com"),
                new ProviderField("password", "App password", ProviderFieldKind.Password, required: true,
                    help: "Create an app password in Koofr (Preferences -> Password), then paste it here. " +
                          "Your normal Koofr password is not accepted by the API.",
                    opensUrl: KoofrPasswordPage)
            },
            FixedParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["provider"] = "koofr" },
            HelpUrl = KoofrPasswordPage,
            SignInUrl = KoofrPasswordPage,
            ConnectNote = "rclone 1.75.2 has no OAuth flow for Koofr, so NimbusX uses Koofr's official app password."
        },
        new("mega", "MEGA", "MEGA with email + password (no browser window needed).",
            "mega", ProviderConnectKind.Credentials, "\uE8D7")
        {
            Fields = new[]
            {
                new ProviderField("user", "MEGA email", ProviderFieldKind.Email, required: true,
                    placeholder: "you@example.com"),
                new ProviderField("pass", "Password", ProviderFieldKind.Password, required: true,
                    help: "Stored encrypted with Windows DPAPI and given to rclone through its environment only."),
                new ProviderField("2fa", "Two-factor code", ProviderFieldKind.Text, required: false,
                    help: "Only needed when your MEGA account uses 2FA. Leave empty otherwise.")
            },
            HelpUrl = "https://rclone.org/mega/",
            SignInUrl = MegaWebLogin,
            ConnectNote = "MEGA's API takes your password directly, so NimbusX asks for it once and stores it with DPAPI."
        },
        new("webdav", "WebDAV", "Nextcloud, ownCloud, HiDrive, Synology or any WebDAV server.",
            "webdav", ProviderConnectKind.Credentials, "\uE968")
        {
            Fields = new[]
            {
                new ProviderField("url", "Server URL", ProviderFieldKind.Text, required: true,
                    placeholder: "https://cloud.example.com/remote.php/dav/files/you"),
                new ProviderField("vendor", "Vendor", ProviderFieldKind.Choice, required: false,
                    defaultValue: "other",
                    choices: new[] { "other", "nextcloud", "owncloud", "sharepoint", "rclone", "cyberduck", "hidrive", "putio" }),
                new ProviderField("user", "User name", ProviderFieldKind.Text, required: true),
                new ProviderField("pass", "Password", ProviderFieldKind.Password, required: true,
                    help: "Use an app password where the server supports one.")
            },
            HelpUrl = "https://rclone.org/webdav/"
        },
        new("sftp", "SFTP", "Any SSH server, including a home server or a seedbox.",
            "sftp", ProviderConnectKind.Credentials, "\uE72E")
        {
            Fields = new[]
            {
                new ProviderField("host", "Host", ProviderFieldKind.Text, required: true,
                    placeholder: "storage.example.com"),
                new ProviderField("port", "Port", ProviderFieldKind.Number, required: false, defaultValue: "22"),
                new ProviderField("user", "User name", ProviderFieldKind.Text, required: true),
                new ProviderField("pass", "Password", ProviderFieldKind.Password, required: true,
                    help: "Key-based login is not supported from the one-click form yet; use a password or an SSH agent key later.")
            },
            HelpUrl = "https://rclone.org/sftp/"
        },
        new("terabox", "TeraBox", "Up to 1 TB free, but downloads are slow and the API is unofficial.",
            "terabox", ProviderConnectKind.CookieCapture, "\uE8F1")
        {
            Fields = new[]
            {
                new ProviderField("domain", "Domain", ProviderFieldKind.Choice, required: false,
                    defaultValue: "https://www.terabox.com",
                    choices: new[]
                    {
                        "https://www.terabox.com",
                        "https://www.terabox.app",
                        "https://freeterabox.com",
                        "https://www.1024tera.com"
                    })
            },
            IsExperimental = true,
            RequiresTeraBoxBuild = true,
            HelpUrl = "https://github.com/gulp79/rclone-extra",
            SignInUrl = TeraboxLogin,
            ConnectNote = "NimbusX opens terabox.com in an in-app window, you sign in normally, and NimbusX reads the " +
                          "session cookie from the browser window itself.",
            ExperimentalWarning =
                "Experimental: TeraBox has no official API. This uses an unofficial community rclone build that can " +
                "break at any time, free-tier downloads are slow, and it is best used as a big storage drive rather " +
                "than a drive you play games from directly."
        }
    };

    /// <summary>All providers, in the order they appear in the UI.</summary>
    public static IReadOnlyList<ProviderDefinition> All => Providers;

    /// <summary>Providers that work with the official rclone build.</summary>
    public static IReadOnlyList<ProviderDefinition> StandardBuild =>
        Providers.Where(p => !p.RequiresTeraBoxBuild).ToArray();

    public static bool TryGet(string? providerId, out ProviderDefinition definition)
    {
        ProviderDefinition? found = Providers.FirstOrDefault(
            p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));
        definition = found!;
        return found is not null;
    }

    public static ProviderDefinition? Get(string? providerId) =>
        Providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));

    public static ProviderDefinition GetRequired(string providerId) =>
        Get(providerId) ?? throw new ArgumentException("Unknown provider '" + providerId + "'.", nameof(providerId));

    /// <summary>True when the provider needs the community TeraBox build.</summary>
    public static bool RequiresTeraBox(string? providerId) =>
        Get(providerId)?.RequiresTeraBoxBuild ?? false;

    /// <summary>Provider that owns an rclone backend name (used when reading existing configs).</summary>
    public static ProviderDefinition? GetByRcloneType(string? rcloneType) =>
        Providers.FirstOrDefault(p => string.Equals(p.RcloneType, rcloneType, StringComparison.OrdinalIgnoreCase));
}
