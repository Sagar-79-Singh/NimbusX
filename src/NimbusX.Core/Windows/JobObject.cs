using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NimbusX.Core.Windows;

/// <summary>
/// A Windows job object that keeps a launched game and every child process in one group and terminates the
/// whole group when NimbusX closes. This is how a game is launched "under supervision" without any injection.
/// </summary>
public sealed class JobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private IntPtr _handle;
    private bool _disposed;

    private JobObject(IntPtr handle)
    {
        _handle = handle;
    }

    /// <summary>Creates a job object, or returns null with a message when the OS refuses.</summary>
    public static JobObject? TryCreate(out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows())
        {
            error = "Job objects are only available on Windows.";
            return null;
        }

        IntPtr handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            error = "CreateJobObject failed with Win32 error " + Marshal.GetLastWin32Error() + ".";
            return null;
        }

        JobObjectInfo info = new()
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };

        int size = Marshal.SizeOf<JobObjectInfo>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                error = "SetInformationJobObject failed with Win32 error " + Marshal.GetLastWin32Error() + ".";
                CloseHandle(handle);
                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return new JobObject(handle);
    }

    /// <summary>Adds a started process to the job.</summary>
    public bool Assign(Process process, out string? error)
    {
        error = null;
        ArgumentNullException.ThrowIfNull(process);
        if (_handle == IntPtr.Zero)
        {
            error = "The job object has been closed.";
            return false;
        }

        if (!AssignProcessToJobObject(_handle, process.Handle))
        {
            error = "AssignProcessToJobObject failed with Win32 error " + Marshal.GetLastWin32Error() + ".";
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectInfo
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateJobObjectW")]
    private static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
