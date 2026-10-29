using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace NimbusX.Core.Processes;

/// <summary>Starts child processes. Injected so mount logic can be unit tested without spawning rclone.</summary>
public interface IProcessRunner
{
    /// <summary>Runs a process to completion, capturing output.</summary>
    Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Runs a process to completion with no timeout of its own.</summary>
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken);

    /// <summary>Starts a long lived process (an rclone mount) and hands ownership of the handle to the caller.</summary>
    Process Start(ProcessSpec spec);
}
