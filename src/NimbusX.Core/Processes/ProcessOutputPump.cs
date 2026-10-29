using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace NimbusX.Core.Processes;

/// <summary>
/// Drains stdout/stderr of a long lived child process line by line so the OS pipe buffer can never block it.
/// </summary>
public sealed class ProcessOutputPump : IDisposable
{
    private readonly Task _stdoutTask;
    private readonly Task _stderrTask;

    private ProcessOutputPump(Process process, Action<string>? onStandardOutput, Action<string>? onStandardError)
    {
        _stdoutTask = PumpAsync(process.StandardOutput, onStandardOutput);
        _stderrTask = PumpAsync(process.StandardError, onStandardError);
    }

    public static ProcessOutputPump Attach(Process process, Action<string>? onStandardOutput = null, Action<string>? onStandardError = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        return new ProcessOutputPump(process, onStandardOutput, onStandardError);
    }

    private static async Task PumpAsync(System.IO.StreamReader reader, Action<string>? handler)
    {
        try
        {
            while (true)
            {
                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                if (handler is null)
                {
                    continue;
                }

                try
                {
                    handler(Logging.SecretRedactor.Redact(line));
                }
                catch (Exception)
                {
                    // ignore handler failures
                }
            }
        }
        catch (Exception)
        {
            // pipe closed
        }
    }

    public async Task DrainAsync(TimeSpan timeout)
    {
        await Task.WhenAny(Task.WhenAll(_stdoutTask, _stderrTask), Task.Delay(timeout)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        // Pumping stops by itself when the process closes its pipes; nothing to release here.
    }
}
