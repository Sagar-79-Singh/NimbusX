using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NimbusX.App;
using NimbusX.Core.Logging;

namespace NimbusX.Desktop.ViewModels;

/// <summary>
/// Base class for a page: title, subtitle, navigation glyph, a busy flag, an error slot and a refresh tick.
/// </summary>
/// <remarks>
/// Pages own their timers so each one refreshes at the rate its data actually changes (a mount phase every two
/// seconds, diagnostics only on demand). Every refresh is wrapped so a failing service shows an error string
/// instead of taking the window down.
/// </remarks>
public abstract partial class PageViewModel : ObservableObject, IDisposable
{
    private DispatcherTimer? _timer;

    protected PageViewModel(AppServices services, string title, string subtitle, string glyph)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Logger = services.Logger;
        Title = title;
        Subtitle = subtitle;
        Glyph = glyph;
    }

    public AppServices Services { get; }

    protected IAppLogger Logger { get; }

    public string Title { get; }

    public string Subtitle { get; }

    /// <summary>Segoe Fluent Icons glyph shown in the navigation rail.</summary>
    public string Glyph { get; }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _hasError;

    /// <summary>Loads or refreshes the page's data. Never throws: failures land in <see cref="Error"/>.</summary>
    public abstract Task RefreshAsync(CancellationToken cancellationToken);

    /// <summary>Refresh command every page shares, for the "re-check" buttons.</summary>
    [RelayCommand]
    private Task RefreshNowAsync() => RefreshAsync(CancellationToken.None);

    protected void SetError(string? message, Exception? exception = null)
    {
        Error = message;
        HasError = !string.IsNullOrWhiteSpace(message);
        if (exception is not null)
        {
            Logger.Warn("Page '" + Title + "' failed: " + exception.Message, "Ui");
        }
    }

    protected async Task GuardedRefreshAsync(CancellationToken cancellationToken, Func<Task> work)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await work().ConfigureAwait(true);
            SetError(null);
        }
        catch (OperationCanceledException)
        {
            // navigating away or shutting down
        }
        catch (Exception exception)
        {
            SetError(exception.Message, exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Starts a periodic refresh; the interval is chosen by the page.</summary>
    protected void StartTimer(TimeSpan interval, Func<Task> tick)
    {
        StopTimer();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
        _timer.Tick += async (_, _) =>
        {
            try
            {
                await tick().ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                Logger.Debug("Timer tick for '" + Title + "' failed: " + exception.Message, "Ui");
            }
        };

        _timer.Start();
    }

    protected void StopTimer()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer = null;
    }

    public virtual void Dispose()
    {
        StopTimer();
        GC.SuppressFinalize(this);
    }
}
