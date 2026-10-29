using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using NimbusX.Core.Io;

namespace NimbusX.Core.Windows;

/// <summary>
/// Per-user single instance guard for one NimbusX data folder.
/// </summary>
/// <remarks>
/// Uses a named mutex (local session namespace) so a second launch can focus the existing window, plus a small
/// lock file holding the owning process id and start time for diagnostics. The mutex is released by the OS if
/// the process dies, so a crash never blocks the next launch.
/// </remarks>
public sealed class SingleInstanceLock : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _lockFile;
    private readonly int _processId;
    private bool _disposed;

    private SingleInstanceLock(Mutex mutex, string lockFile, int processId)
    {
        _mutex = mutex;
        _lockFile = lockFile;
        _processId = processId;
    }

    /// <summary>Name of the mutex for a data folder (stable across versions).</summary>
    public static string MutexNameFor(string dataRoot)
    {
        string hash = Hashing.Sha256Text(PathGuard.Normalize(dataRoot).ToLowerInvariant())[..16];
        return "Local\\NimbusX-" + hash;
    }

    /// <summary>
    /// Tries to become the single running instance. When another instance owns the lock,
    /// <paramref name="ownerProcessId"/> contains its process id when it can be determined.
    /// </summary>
    public static bool TryAcquire(string dataRoot, out SingleInstanceLock? handle, out int? ownerProcessId)
    {
        Guard.NotNullOrWhiteSpace(dataRoot, nameof(dataRoot));
        handle = null;
        ownerProcessId = ReadOwnerProcessId(dataRoot);

        Mutex mutex = new(initiallyOwned: false, MutexNameFor(dataRoot));
        try
        {
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // Previous owner died without releasing: we own it now.
                acquired = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                return false;
            }

            handle = new SingleInstanceLock(mutex, Path.Combine(dataRoot, "nimbusx.lock"), Environment.ProcessId);
            handle.WriteLockFile();
            return true;
        }
        catch (Exception)
        {
            try
            {
                mutex.Dispose();
            }
            catch (Exception)
            {
                // ignore
            }

            return false;
        }
    }

    /// <summary>Process id recorded by the running instance, if the lock file is fresh and valid.</summary>
    public static int? ReadOwnerProcessId(string dataRoot)
    {
        try
        {
            string file = Path.Combine(dataRoot, "nimbusx.lock");
            if (!File.Exists(file))
            {
                return null;
            }

            string text = File.ReadAllText(file).Trim();
            string firstLine = text.Split('\n')[0].Trim();
            return int.TryParse(firstLine, out int pid) && pid > 0 ? pid : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>True when the recorded owner is still alive (used to decide between "already running" and "stale").</summary>
    public static bool IsOwnerAlive(int? processId)
    {
        if (processId is null or <= 0)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(processId.Value);
            return !process.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void WriteLockFile()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_lockFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory!);
            }

            StringBuilder builder = new();
            builder.AppendLine(_processId.ToString());
            builder.AppendLine(DateTimeOffset.UtcNow.ToString("O"));
            builder.AppendLine(AppInfo.Version);
            File.WriteAllText(_lockFile, builder.ToString(), new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // the mutex is the real guard; the lock file is best effort diagnostics
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            AtomicFile.TryDelete(_lockFile);
        }
        catch (Exception)
        {
            // ignore
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (Exception)
        {
            // not owned
        }

        _mutex.Dispose();
    }
}
