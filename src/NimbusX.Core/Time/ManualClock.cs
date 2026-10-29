using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimbusX.Core.Time;

/// <summary>Clock used by unit tests: time only moves when the test says so.</summary>
public sealed class ManualClock : IClock
{
    private readonly object _gate = new();
    private DateTimeOffset _utcNow;

    public ManualClock(DateTimeOffset? start = null)
    {
        _utcNow = start ?? new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    }

    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_gate)
            {
                return _utcNow;
            }
        }
    }

    public DateTimeOffset Now => UtcNow.ToLocalTime();

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Advance(delay);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public void Advance(TimeSpan by)
    {
        lock (_gate)
        {
            _utcNow = _utcNow.Add(by);
        }
    }
}
