using System;
using System.Collections.Generic;

namespace NimbusX.Mount;

/// <summary>Quota information for a remote, as reported by rclone's <c>operations/about</c>.</summary>
public sealed class RemoteUsage
{
    public RemoteUsage(string remote, long? used, long? total, long? free, DateTimeOffset measuredUtc)
    {
        Remote = remote;
        Used = used;
        Total = total;
        Free = free;
        MeasuredUtc = measuredUtc;
    }

    public string Remote { get; }

    public long? Used { get; }

    public long? Total { get; }

    public long? Free { get; }

    public DateTimeOffset MeasuredUtc { get; }

    /// <summary>True when the backend returned usable numbers.</summary>
    public bool HasNumbers => Total is > 0 || Used is > 0;

    /// <summary>Fraction of the quota that is used, or null.</summary>
    public double? UsedFraction =>
        Total is > 0 && Used.HasValue ? Math.Clamp((double)Used.Value / Total.Value, 0d, 1d) : null;

    public override string ToString() =>
        Remote + ": " + (Used.HasValue ? Used.Value.ToString() : "?") + " / " + (Total.HasValue ? Total.Value.ToString() : "?");
}

/// <summary>Status of rclone's local OAuth callback server.</summary>
public sealed class RcOAuthStatus
{
    public RcOAuthStatus(bool running, string? authUrl)
    {
        Running = running;
        AuthUrl = authUrl;
    }

    public bool Running { get; }

    /// <summary>The URL the user must open to approve access, when the server is running.</summary>
    public string? AuthUrl { get; }

    public override string ToString() => Running ? "oauth running: " + AuthUrl : "oauth stopped";
}

/// <summary>State of an rc job started with <c>_async</c>.</summary>
public sealed class RcJobStatus
{
    public RcJobStatus(long id, bool finished, bool success, string? error, double? durationSeconds, IReadOnlyDictionary<string, string> all)
    {
        Id = id;
        Finished = finished;
        Success = success;
        Error = error;
        DurationSeconds = durationSeconds;
        All = all;
    }

    public long Id { get; }

    public bool Finished { get; }

    public bool Success { get; }

    public string? Error { get; }

    public double? DurationSeconds { get; }

    public IReadOnlyDictionary<string, string> All { get; }

    public override string ToString() => "job " + Id + (Finished ? (Success ? " ok" : " failed: " + Error) : " running");
}

/// <summary>One active mount reported by <c>mount/listmounts</c>.</summary>
public sealed class RcMountPoint
{
    public RcMountPoint(string mountPoint, string? fs, string? mountType)
    {
        MountPoint = mountPoint;
        Fs = fs;
        MountType = mountType;
    }

    public string MountPoint { get; }

    public string? Fs { get; }

    public string? MountType { get; }

    public override string ToString() => MountPoint + " <- " + (Fs ?? "?");
}
