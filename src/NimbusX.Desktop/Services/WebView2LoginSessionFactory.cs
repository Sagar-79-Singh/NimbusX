using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using NimbusX.Core.Config;
using NimbusX.Desktop.Views;
using NimbusX.Providers;

namespace NimbusX.Desktop.Services;

/// <summary>
/// Creates the WebView2 sign-in windows and hands the cookies to the provider layer.
/// </summary>
/// <remarks>
/// The browser profile lives inside NimbusX's own data folder, so the sign-in never touches the user's normal
/// browser. Cookies are converted to plain <see cref="BrowserCookie"/> values only after the user presses Done, and
/// the provider layer is the only thing that ever sees them.
/// </remarks>
public sealed class WebView2LoginSessionFactory : IBrowserLoginSessionFactory
{
    private readonly AppPaths _paths;

    public WebView2LoginSessionFactory(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public bool IsAvailable => LoginWindow.IsRuntimeAvailable;

    public string UserDataFolder => Path.Combine(_paths.Root, "webview2");

    public Task<IBrowserLoginSession> CreateAsync(string title, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            throw new NotSupportedException(
                "The embedded login window needs the Microsoft Edge WebView2 runtime, which is not installed on this PC.");
        }

        Directory.CreateDirectory(UserDataFolder);
        return Task.FromResult<IBrowserLoginSession>(new Session(title, UserDataFolder));
    }

    private sealed class Session : IBrowserLoginSession
    {
        private readonly string _title;
        private readonly string _userDataFolder;
        private LoginWindow? _window;

        public Session(string title, string userDataFolder)
        {
            _title = title;
            _userDataFolder = userDataFolder;
        }

        public Uri? CurrentAddress => _window?.CurrentAddress;

        public async Task<bool> ShowAsync(Uri startUrl, string title, CancellationToken cancellationToken)
        {
            LoginWindow window = new();
            _window = window;

            // Register the Loaded hook before the window is shown so a fast first paint cannot miss it.
            Task<bool> shown = window.ShowAsync(
                startUrl,
                string.IsNullOrWhiteSpace(title) ? _title : title,
                _userDataFolder);

            await window.Dispatcher.InvokeAsync(() =>
            {
                window.Owner = System.Windows.Application.Current?.MainWindow;
                window.Show();
            });

            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                try
                {
                    window.Dispatcher.Invoke(() => window.Close());
                }
                catch (Exception)
                {
                    // window already gone
                }
            });

            return await shown.ConfigureAwait(true);
        }

        public async Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(string host, CancellationToken cancellationToken)
        {
            if (_window is null)
            {
                return Array.Empty<BrowserCookie>();
            }

            IReadOnlyList<CoreWebView2Cookie> cookies = await _window.GetCookiesAsync(host).ConfigureAwait(true);
            DateTimeOffset now = DateTimeOffset.UtcNow;

            return cookies
                .Where(c => !c.IsSession || new DateTimeOffset(c.Expires.ToUniversalTime()) > now)
                .Select(c => new BrowserCookie(
                    c.Name,
                    c.Value,
                    c.Domain,
                    c.Path,
                    c.IsSession ? null : new DateTimeOffset(c.Expires.ToUniversalTime())))
                .ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            LoginWindow? window = _window;
            _window = null;
            if (window is null)
            {
                return;
            }

            await window.Dispatcher.InvokeAsync(() =>
            {
                window.Close();
            });
        }
    }
}
