using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NimbusX.Core.Processes;

/// <summary>Default <see cref="IProcessRunner"/> implementation.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <inheritdoc />
    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken) =>
        RunAsync(spec, Timeout.InfiniteTimeSpan, cancellationToken);

    /// <inheritdoc />
    public async Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);

        ProcessStartInfo startInfo = BuildStartInfo(spec);
        using Process process = new() { StartInfo = startInfo };

        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            if (!process.Start())
            {
                return new ProcessResult(-1, string.Empty, "Failed to start " + spec.FileName + ".", false, stopwatch.Elapsed);
            }
        }
        catch (Exception exception)
        {
            return new ProcessResult(-1, string.Empty, SecretRedact(exception.Message), false, stopwatch.Elapsed);
        }

        Task<string> stdoutTask = spec.CaptureOutput ? process.StandardOutput.ReadToEndAsync(cancellationToken) : Task.FromResult(string.Empty);
        Task<string> stderrTask = spec.CaptureOutput ? process.StandardError.ReadToEndAsync(cancellationToken) : Task.FromResult(string.Empty);

        bool timedOut = false;
        using (CancellationTokenSource? timeoutCts = timeout == Timeout.InfiniteTimeSpan ? null : new CancellationTokenSource(timeout))
        using (CancellationTokenSource linked = timeoutCts is null
                   ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                   : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token))
        {
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                timedOut = timeoutCts is not null && timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
                TryKill(process);
            }
        }

        string stdout = await SafeAwait(stdoutTask).ConfigureAwait(false);
        string stderr = await SafeAwait(stderrTask).ConfigureAwait(false);

        int exitCode;
        try
        {
            exitCode = process.HasExited ? process.ExitCode : -1;
        }
        catch (InvalidOperationException)
        {
            exitCode = -1;
        }

        stopwatch.Stop();
        return new ProcessResult(exitCode, SecretRedact(stdout), SecretRedact(stderr), timedOut, stopwatch.Elapsed);
    }

    /// <inheritdoc />
    public Process Start(ProcessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ProcessStartInfo startInfo = BuildStartInfo(spec);
        Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Failed to start " + spec.FileName + ".");
        }

        return process;
    }

    /// <summary>Kills a process and its children, ignoring races.</summary>
    public static void TryKill(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // process already gone
        }
    }

    private static ProcessStartInfo BuildStartInfo(ProcessSpec spec)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = spec.FileName,
            UseShellExecute = false,
            CreateNoWindow = spec.CreateNoWindow,
            RedirectStandardOutput = spec.CaptureOutput,
            RedirectStandardError = spec.CaptureOutput,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrWhiteSpace(spec.WorkingDirectory))
        {
            startInfo.WorkingDirectory = spec.WorkingDirectory!;
        }

        if (spec.Arguments is not null)
        {
            foreach (string argument in spec.Arguments)
            {
                startInfo.ArgumentList.Add(argument ?? string.Empty);
            }
        }

        if (spec.ClearInheritedEnvironment)
        {
            startInfo.Environment.Clear();
            foreach (string essential in new[] { "SystemRoot", "windir", "SystemDrive", "PATH", "TEMP", "TMP", "USERPROFILE", "USERNAME", "COMSPEC", "PATHEXT" })
            {
                string? value = Environment.GetEnvironmentVariable(essential);
                if (!string.IsNullOrEmpty(value))
                {
                    startInfo.Environment[essential] = value;
                }
            }
        }

        if (spec.Environment is not null)
        {
            foreach (KeyValuePair<string, string> pair in spec.Environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        return startInfo;
    }

    private static async Task<string> SafeAwait(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string SecretRedact(string text) => Logging.SecretRedactor.Redact(text);
}
