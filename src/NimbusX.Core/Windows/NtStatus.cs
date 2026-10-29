using System.Collections.Generic;

namespace NimbusX.Core.Windows;

/// <summary>Decoded process exit code.</summary>
public sealed class NtStatusInfo
{
    public NtStatusInfo(int exitCode, string name, string meaning, string? hint)
    {
        ExitCode = exitCode;
        Name = name;
        Meaning = meaning;
        Hint = hint;
    }

    public int ExitCode { get; }

    /// <summary>Status constant name, e.g. <c>STATUS_DLL_NOT_FOUND</c>.</summary>
    public string Name { get; }

    /// <summary>Human readable meaning shown in the launcher UI.</summary>
    public string Meaning { get; }

    /// <summary>What the user can do about it, or null.</summary>
    public string? Hint { get; }

    /// <summary><c>0xC0000135</c> style rendering.</summary>
    public string Hex => "0x" + unchecked((uint)ExitCode).ToString("X8");

    public override string ToString() =>
        (ExitCode == 0 ? "0" : Hex) + " " + Name + ": " + Meaning + (Hint is null ? string.Empty : " (" + Hint + ")");
}

/// <summary>
/// Turns the raw exit code of a game process into something a user can act on. Games launched through a job
/// object often exit with an NTSTATUS value (negative int) instead of a Win32 error.
/// </summary>
public static class NtStatus
{
    private static readonly Dictionary<uint, (string Name, string Meaning, string? Hint)> Known = new()
    {
        [0x00000000] = ("STATUS_SUCCESS", "The game exited normally.", null),
        [0xC0000005] = ("STATUS_ACCESS_VIOLATION", "The game crashed with an access violation.",
            "Usually a broken mod, an out-of-date GPU driver, or a missing shader cache. Try Full Local mode."),
        [0xC000007B] = ("STATUS_INVALID_IMAGE_FORMAT", "The executable is 32-bit/64-bit mismatched or a DLL is corrupt.",
            "Check that the game and its DLLs are the same architecture, and let NimbusX re-check imports."),
        [0xC0000135] = ("STATUS_DLL_NOT_FOUND", "A required DLL could not be found.",
            "Run 'Check DLLs' on the game. In Cloud Direct mode the DLL may live on a slow cloud path; Hybrid mode copies it locally."),
        [0xC0000142] = ("STATUS_DLL_INIT_FAILED", "A DLL failed to initialise.",
            "Often an anti-cheat or overlays; try Full Local mode and close other overlays."),
        [0xC000013A] = ("STATUS_CONTROL_C_EXIT", "The process was closed by the user (Ctrl+C / window close).", null),
        [0xC0000409] = ("STATUS_STACK_BUFFER_OVERRUN", "The process was terminated by Windows exploit protection.",
            "This is often a security product blocking the launcher; NimbusX never disables protections."),
        [0xC0000018] = ("STATUS_CONFLICTING_ADDRESSES", "The executable could not be mapped into memory.",
            "Another process is likely injecting into the game; restart the PC and try again."),
        [0xC000001D] = ("STATUS_ILLEGAL_INSTRUCTION", "The CPU rejected an instruction.",
            "The build may require a newer CPU feature set (AVX2) or the file is corrupted in the cloud."),
        [0xC0000094] = ("STATUS_INTEGER_DIVIDE_BY_ZERO", "The game crashed with a divide-by-zero.", null),
        [0xC00000FD] = ("STATUS_STACK_OVERFLOW", "The game overflowed its stack.", null),
        [0xC000035A] = ("STATUS_FAIL_FAST_EXCEPTION", "The process hit a fail-fast exception.",
            "Often an integrity check that could not read a file from the cloud in time. Try Hybrid mode."),
        [0x80070005] = ("E_ACCESSDENIED", "Access denied while starting the game.",
            "Check that the cloud mount is connected and that the game folder is readable."),
        [0x80070002] = ("E_FILE_NOT_FOUND", "The executable could not be found.",
            "The file may have been moved in the cloud or the mount dropped offline."),
        [0x8007000E] = ("E_OUTOFMEMORY", "The process ran out of memory.", null),
        [0x80004005] = ("E_FAIL", "The game reported an unspecified failure.", null),
        [0x00000001] = ("1", "The program exited with code 1 (generic error).", null),
        [0x00000002] = ("2", "The program exited with code 2 (usually 'file not found').", null),
    };

    /// <summary>Decodes a signed exit code.</summary>
    public static NtStatusInfo Describe(int exitCode)
    {
        uint code = unchecked((uint)exitCode);
        if (Known.TryGetValue(code, out (string Name, string Meaning, string? Hint) entry))
        {
            return new NtStatusInfo(exitCode, entry.Name, entry.Meaning, entry.Hint);
        }

        if ((code & 0x80000000u) != 0)
        {
            return new NtStatusInfo(exitCode, "NTSTATUS", "The process terminated with an unknown NTSTATUS code.", null);
        }

        return new NtStatusInfo(exitCode, "EXITCODE", "The program exited with code " + code + ".", null);
    }

    /// <summary>True when the code represents a crash rather than a normal exit.</summary>
    public static bool IsCrash(int exitCode)
    {
        uint code = unchecked((uint)exitCode);
        return code >= 0xC0000000u && code != 0xC000013Au;
    }

    /// <summary>True when the failure points at a missing or unreadable dependency.</summary>
    public static bool IsMissingDependency(int exitCode)
    {
        uint code = unchecked((uint)exitCode);
        return code == 0xC0000135u || code == 0xC0000142u || code == 0xC000007Bu || code == 0x80070002u;
    }
}
