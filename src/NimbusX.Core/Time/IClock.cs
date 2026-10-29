using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimbusX.Core.Time;

/// <summary>Injectable clock so timers, TTLs and health checks are deterministic in tests.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    DateTimeOffset Now { get; }

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>Real wall clock.</summary>
public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public DateTimeOffset Now => DateTimeOffset.Now;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}
