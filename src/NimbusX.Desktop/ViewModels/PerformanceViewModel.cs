using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NimbusX.App;
using NimbusX.Cache;
using NimbusX.Core.Text;
using NimbusX.Streaming;

namespace NimbusX.Desktop.ViewModels;

/// <summary>Live view of prefetch work and stream sessions.</summary>
public sealed partial class PerformanceViewModel : PageViewModel
{
    private const int SeriesLength = 60;

    private readonly Queue<double> _growth = new();
    private long _lastCacheBytes = -1;
    private DateTimeOffset _lastSample = DateTimeOffset.UtcNow;

    public PerformanceViewModel(AppServices services)
        : base(services, "Performance", "What the cache is doing right now, measured from disk and rclone", "\uE9D9")
    {
        Jobs = new ObservableCollection<PrefetchJob>();
        Streams = new ObservableCollection<StreamSession>();
        Series = Array.Empty<double>();

        StartTimer(TimeSpan.FromSeconds(1), TickAsync);
    }

    public ObservableCollection<PrefetchJob> Jobs { get; }

    public ObservableCollection<StreamSession> Streams { get; }

    [ObservableProperty]
    private IReadOnlyList<double> _series;

    [ObservableProperty]
    private string _growthText = "Unavailable";

    [ObservableProperty]
    private string _peakText = "Unavailable";

    [ObservableProperty]
    private string _cacheText = "Unavailable";

    [ObservableProperty]
    private string _prefetchText = "Idle";

    [ObservableProperty]
    private string _prefetchBudget = "Unavailable";

    [ObservableProperty]
    private bool _prefetchPaused;

    [ObservableProperty]
    private string _pauseReason = string.Empty;

    [ObservableProperty]
    private string _streamText = "No active streams";

    [ObservableProperty]
    private bool _hasJobs;

    [ObservableProperty]
    private bool _hasStreams;

    [ObservableProperty]
    private string _sampleNote = "Sampling every second; the chart shows cache growth on disk.";

    public override async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await GuardedRefreshAsync(cancellationToken, () => TickAsync()).ConfigureAwait(true);
    }

    private async Task TickAsync()
    {
        CacheStatus status = Services.CacheService.Current;

        long current = status.TotalPhysicalBytes;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (_lastCacheBytes >= 0)
        {
            double seconds = Math.Max(0.001, (now - _lastSample).TotalSeconds);
            double bytesPerSecond = Math.Max(0, current - _lastCacheBytes) / seconds;
            _growth.Enqueue(bytesPerSecond);
            while (_growth.Count > SeriesLength)
            {
                _growth.Dequeue();
            }

            Series = _growth.ToArray();
            double latest = _growth.Count > 0 ? _growth.Last() : 0;
            double peak = _growth.Count > 0 ? _growth.Max() : 0;
            GrowthText = latest <= 0 ? "0 B/s" : ByteSize.FormatRate(latest);
            PeakText = peak <= 0 ? "0 B/s" : ByteSize.FormatRate(peak);
        }

        _lastCacheBytes = current;
        _lastSample = now;

        CacheText = status.Usages.Count == 0
            ? "Unavailable"
            : ByteSize.Format(status.TotalPhysicalBytes) + " of " + ByteSize.Format(status.Plan.TotalBudgetBytes) + " budget";
        PrefetchPaused = status.PrefetchPaused;
        PauseReason = status.Safety.Explanation;
        PrefetchBudget = ByteSize.Format(Services.Prefetch.RemainingBudgetBytes);

        IReadOnlyList<PrefetchJob> jobs = Services.Prefetch.Jobs;
        Jobs.Clear();
        foreach (PrefetchJob job in jobs.OrderByDescending(j => j.State == PrefetchState.Running).ThenByDescending(j => j.QueuedUtc))
        {
            Jobs.Add(job);
        }

        HasJobs = Jobs.Count > 0;

        int running = jobs.Count(j => j.State == PrefetchState.Running);
        PrefetchText = Services.Prefetch.IsPaused
            ? "Paused"
            : running == 0
                ? (jobs.Count == 0 ? "Idle" : "Queued: " + jobs.Count)
                : "Running: " + running + " of " + jobs.Count;

        IReadOnlyList<StreamSession> streams = Services.Streams.Sessions;
        Streams.Clear();
        foreach (StreamSession session in streams)
        {
            Streams.Add(session);
        }

        HasStreams = Streams.Count > 0;
        StreamText = Streams.Count == 0
            ? "No active streams"
            : Streams.Count + " active stream(s), " + ByteSize.Format(Streams.Sum(s => s.BytesServed)) + " served";

        await Task.CompletedTask.ConfigureAwait(true);
    }

    [RelayCommand]
    private void PausePrefetch()
    {
        Services.Prefetch.Pause();
        PrefetchText = "Paused";
    }

    [RelayCommand]
    private void ResumePrefetch()
    {
        Services.Prefetch.Resume();
        PrefetchText = "Resumed";
    }

    [RelayCommand]
    private void CancelJobs()
    {
        Services.Prefetch.CancelAll();
        PrefetchText = "Cancelled";
    }

    [RelayCommand]
    private void StopStream(StreamSession? session)
    {
        if (session is null)
        {
            return;
        }

        Services.Streams.Close(session.Id);
    }

    /// <summary>Formats a prefetch job for the list: state, progress and the measured rate.</summary>
    public static string DescribeJob(PrefetchJob job) =>
        job.State + " — " + job.ProgressText + " — " + job.RateText;
}
