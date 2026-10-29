using System;

namespace NimbusX.Mount;

/// <summary>An rc call that rclone answered with an error (or not at all).</summary>
public sealed class RcloneRcException : Exception
{
    public RcloneRcException(string method, int statusCode, string message)
        : base(message)
    {
        Method = method;
        StatusCode = statusCode;
    }

    public RcloneRcException(string method, int statusCode, string message, Exception inner)
        : base(message, inner)
    {
        Method = method;
        StatusCode = statusCode;
    }

    /// <summary>rc method, e.g. <c>mount/mount</c>.</summary>
    public string Method { get; }

    public int StatusCode { get; }

    /// <summary>True when rclone is simply not reachable (process gone / port closed).</summary>
    public bool IsUnreachable => StatusCode == 0;
}
