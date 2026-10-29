using System;
using System.Buffers.Binary;
using System.IO;

namespace NimbusX.Games;

/// <summary>Machine type from a PE header.</summary>
public enum PeMachine
{
    Unknown = 0,
    X86,
    X64,
    Arm64
}

/// <summary>What a Windows executable header says about the binary.</summary>
public sealed class PeInfo
{
    public PeInfo(bool isPe, PeMachine machine, int subsystem, string detail)
    {
        IsPe = isPe;
        Machine = machine;
        Subsystem = subsystem;
        Detail = detail;
    }

    public bool IsPe { get; }

    public PeMachine Machine { get; }

    /// <summary>3 = console, 2 = graphical Windows.</summary>
    public int Subsystem { get; }

    public bool IsGui => IsPe && Subsystem == 2;

    public bool IsConsole => IsPe && Subsystem == 3;

    public string Detail { get; }

    public string MachineText => Machine switch
    {
        PeMachine.X86 => "x86",
        PeMachine.X64 => "x64",
        PeMachine.Arm64 => "ARM64",
        _ => "unknown"
    };

    public static PeInfo NotPe(string why) => new(false, PeMachine.Unknown, 0, why);
}

/// <summary>
/// Reads just enough of a PE header to tell a real game executable from a console helper or an installer.
/// </summary>
/// <remarks>
/// Nothing is executed and nothing is written: the file is opened for read with full sharing so a running game is
/// never disturbed, and only the first few kilobytes are read.
/// </remarks>
public static class PeInspector
{
    public const int HeaderBytes = 4096;

    public static PeInfo Inspect(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8, FileOptions.RandomAccess);
            byte[] header = new byte[HeaderBytes];
            int read = stream.Read(header, 0, header.Length);
            if (read < 64)
            {
                return PeInfo.NotPe("The file is smaller than a DOS header.");
            }

            if (header[0] != (byte)'M' || header[1] != (byte)'Z')
            {
                return PeInfo.NotPe("No 'MZ' signature.");
            }

            int lfanew = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0x3C, 4));
            if (lfanew <= 0 || lfanew + 24 > read)
            {
                return PeInfo.NotPe("The PE header offset is outside the first bytes of the file.");
            }

            if (header[lfanew] != (byte)'P' || header[lfanew + 1] != (byte)'E' ||
                header[lfanew + 2] != 0 || header[lfanew + 3] != 0)
            {
                return PeInfo.NotPe("No 'PE\\0\\0' signature.");
            }

            int machineCode = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(lfanew + 4, 2));
            int optionalHeaderOffset = lfanew + 24;
            if (optionalHeaderOffset + 70 > read)
            {
                return PeInfo.NotPe("The optional header is truncated.");
            }

            int subsystem = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(optionalHeaderOffset + 68, 2));
            PeMachine machine = machineCode switch
            {
                0x014c => PeMachine.X86,
                0x8664 => PeMachine.X64,
                0xAA64 => PeMachine.Arm64,
                _ => PeMachine.Unknown
            };

            string kind = subsystem switch
            {
                2 => "graphical Windows",
                3 => "console",
                _ => "subsystem " + subsystem
            };

            return new PeInfo(true, machine, subsystem, "PE32" + (machine == PeMachine.X64 || machine == PeMachine.Arm64 ? "+" : string.Empty) + " " + kind + " executable (" + machine + ")");
        }
        catch (Exception exception)
        {
            return PeInfo.NotPe("The header could not be read: " + exception.Message);
        }
    }
}
