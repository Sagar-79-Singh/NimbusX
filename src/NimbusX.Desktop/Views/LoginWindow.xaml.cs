using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace NimbusX.Desktop.Views;

/// <summary>
/// The in-app browser window used for sign-ins whose session NimbusX must read (TeraBox).
/// </summary>
/// <remarks>
/// The user signs in on the provider's real site inside this window; nothing is injected into the page, no script
/// is run in it, and the cookie is read from WebView2's own cookie store after the user presses Done.
/// </remarks>
public partial class LoginWindow : Window
{
    private TaskCompletionSource<bool>? _completion;

    public LoginWindow()
    {
        InitializeComponent();
    }

    /// <summary>Current address, for progress messages (never logged in full).</summary>
    public Uri? CurrentAddress { get; private set; }

    /// <summary>True when the WebView2 runtime is present.</summary>
    public static bool IsRuntimeAvailable
    {
        get
        {
            try
            {
                string? version = CoreWebView2Environment.GetAvailableBrowserVersionString();
                return !string.IsNullOrWhiteSpace(version);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public Task<bool> ShowAsync(Uri startUrl, string title, string userDataFolder)
    {
        ArgumentNullException.ThrowIfNull(startUrl);

        Title = title;
        _completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Loaded += async (_, _) => await InitializeAsync(startUrl, userDataFolder).ConfigureAwait(true);
        return _completion.Task;
    }

    private async Task InitializeAsync(Uri startUrl, string userDataFolder)
    {
        try
        {
            CoreWebView2Environment environment = await CoreWebView2Environment
                .CreateAsync(browserExecutableFolder: null, userDataFolder: userDataFolder)
                .ConfigureAwait(true);

            await Browser.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            Browser.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            Browser.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;

            Browser.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                CurrentAddress = Browser.CoreWebView2.Source is null ? null : new Uri(Browser.CoreWebView2.Source);
                AddressText.Text = CurrentAddress?.GetLeftPart(UriPartial.Authority) ?? string.Empty;
                if (!args.IsSuccess)
                {
                    AddressText.Text = "Could not load the page: " + args.WebErrorStatus;
                }
            };

            Browser.CoreWebView2.Navigate(startUrl.ToString());
        }
        catch (Exception exception)
        {
            _completion?.TrySetException(new InvalidOperationException(
                "The embedded browser could not start: " + exception.Message, exception));
        }
    }

    /// <summary>Reads the cookies for one host from the WebView2 cookie store.</summary>
    public async Task<IReadOnlyList<CoreWebView2Cookie>> GetCookiesAsync(string host)
    {
        if (Browser.CoreWebView2 is null || string.IsNullOrWhiteSpace(host))
        {
            return Array.Empty<CoreWebView2Cookie>();
        }

        string url = host.Contains("://", StringComparison.Ordinal) ? host : "https://" + host + "/";
        return await Browser.CoreWebView2.CookieManager.GetCookiesAsync(url).ConfigureAwait(true);
    }

    private void OnDone(object sender, RoutedEventArgs e)
    {
        _completion?.TrySetResult(true);
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _completion?.TrySetResult(false);
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _completion?.TrySetResult(false);
        base.OnClosed(e);
    }
}
