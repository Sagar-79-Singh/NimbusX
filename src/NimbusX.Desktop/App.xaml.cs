using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using NimbusX.App;
using NimbusX.Core.Config;
using NimbusX.Desktop.Services;
using NimbusX.Desktop.ViewModels;
using NimbusX.Desktop.Views;
using NimbusX.Providers;

namespace NimbusX.Desktop;

/// <summary>
/// Application entry point: one instance, one service graph, one window.
/// </summary>
/// <remarks>
/// Startup failures are shown to the user with the real error, and the window still opens so the Diagnostics page can
/// explain the fix. Nothing here swallows an exception silently: anything unhandled is logged and reported.
/// </remarks>
public partial class App : Application
{
    private AppHost? _host;
    private MainViewModel? _mainViewModel;
    private CancellationTokenSource? _lifetime;
    private bool _shuttingDown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        AppPaths paths = AppPaths.CreateDefault();
        paths.EnsureCreated();

        WebView2LoginSessionFactory browserSessions = new(paths);

        _host = AppHost.TryCreate(out int? ownerPid, paths.Root, browserSessions, new ShellBrowserLauncher(), logger: null);
        if (_host is null)
        {
            MessageBox.Show(
                "NimbusX is already running" + (ownerPid is null ? "." : " (process " + ownerPid.Value + ").") + Environment.NewLine +
                "Use that window or close it first.",
                "NimbusX",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(2);
            return;
        }

        _lifetime = new CancellationTokenSource();
        _mainViewModel = new MainViewModel(_host);

        // Fully qualified: Application.MainWindow is a property, so the bare name would be a compile error.
        NimbusX.Desktop.MainWindow window = new(_mainViewModel);
        MainWindow = window;
        window.Show();

        StartupReport report;
        try
        {
            report = await _host.StartAsync(autoMount: true, _lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            _host.Services.Logger.Error("Startup failed.", "Startup", exception);
            MessageBox.Show(
                "NimbusX could not finish starting: " + exception.Message + Environment.NewLine +
                "Open Diagnostics for details.",
                "NimbusX",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _mainViewModel.OnStartupCompleted(report);
        _mainViewModel.Dashboard.ApplyStartup(report);
        await _mainViewModel.Dashboard.RefreshAsync(CancellationToken.None).ConfigureAwait(true);

        if (_host.Services.Settings.Current.NeedsWizard)
        {
            WizardWindow wizard = new(_host.Services) { Owner = window };
            bool completed = wizard.ShowDialog() == true;
            if (completed)
            {
                await _mainViewModel.RefreshAllAsync().ConfigureAwait(true);
            }
        }
    }

    /// <summary>Unmounts everything and shuts the services down. Safe to call twice and safe to await.</summary>
    public async Task ShutdownHostAsync()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;

        try
        {
            _lifetime?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already disposed
        }

        if (_host is not null)
        {
            try
            {
                await _host.ShutdownAsync().ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                _host.Services.Logger.Error("Shutdown failed.", "Shutdown", exception);
            }
            finally
            {
                await _host.DisposeAsync().ConfigureAwait(true);
                _host = null;
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_mainViewModel is not null)
        {
            _mainViewModel.Dispose();
        }

        _lifetime?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _host?.Services.Logger.Error("Unhandled UI error: " + e.Exception.Message, "Ui", e.Exception);
        MessageBox.Show(
            "Something went wrong: " + e.Exception.Message + Environment.NewLine +
            "The error was written to the NimbusX log. Diagnostics can export it.",
            "NimbusX",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            _host?.Services.Logger.Error("Unhandled background error: " + exception.Message, "App", exception);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _host?.Services.Logger.Error("Unobserved task error: " + e.Exception?.Message, "App", e.Exception);
        e.SetObserved();
    }
}
