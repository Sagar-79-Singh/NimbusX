using System;
using System.Collections.Generic;
using System.Linq;

namespace NimbusX.Core.Processes;

/// <summary>
/// Describes a child process to start. Arguments are a list (never a single string) so quoting is handled
/// by the runtime and secrets can be passed through <see cref="Environment"/> only.
/// </summary>
public sealed class ProcessSpec
{
    public ProcessSpec(string fileName)
    {
        FileName = Guard.NotNullOrWhiteSpace(fileName, nameof(fileName));
        Arguments = Array.Empty<string>();
        Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Executable path or name.</summary>
    public string FileName { get; }

    /// <summary>Arguments in order, without quoting.</summary>
    public IReadOnlyList<string> Arguments { get; init; }

    public string? WorkingDirectory { get; init; }

    /// <summary>Environment variables to set/override on top of the inherited environment.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; }

    /// <summary>When true the child only sees <see cref="Environment"/> (plus SYSTEMROOT/PATH essentials).</summary>
    public bool ClearInheritedEnvironment { get; init; }

    /// <summary>Capture stdout/stderr (default true).</summary>
    public bool CaptureOutput { get; init; } = true;

    public bool CreateNoWindow { get; init; } = true;

    public static ProcessSpec Create(string fileName, params string[] arguments)
    {
        return new ProcessSpec(fileName) { Arguments = arguments ?? Array.Empty<string>() };
    }

    public ProcessSpec WithArguments(params string[] arguments) => new(FileName)
    {
        Arguments = arguments ?? Array.Empty<string>(),
        WorkingDirectory = WorkingDirectory,
        Environment = Environment,
        ClearInheritedEnvironment = ClearInheritedEnvironment,
        CaptureOutput = CaptureOutput,
        CreateNoWindow = CreateNoWindow
    };

    public ProcessSpec WithEnvironment(IReadOnlyDictionary<string, string> environment)
    {
        Dictionary<string, string> merged = new(Environment, StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> pair in environment)
        {
            merged[pair.Key] = pair.Value;
        }

        return new ProcessSpec(FileName)
        {
            Arguments = Arguments,
            WorkingDirectory = WorkingDirectory,
            Environment = merged,
            ClearInheritedEnvironment = ClearInheritedEnvironment,
            CaptureOutput = CaptureOutput,
            CreateNoWindow = CreateNoWindow
        };
    }

    public ProcessSpec WithWorkingDirectory(string? workingDirectory) => new(FileName)
    {
        Arguments = Arguments,
        WorkingDirectory = workingDirectory,
        Environment = Environment,
        ClearInheritedEnvironment = ClearInheritedEnvironment,
        CaptureOutput = CaptureOutput,
        CreateNoWindow = CreateNoWindow
    };

    /// <summary>Renders the command line for logs. Values that look like secrets are shown as arguments only when safe.</summary>
    public override string ToString()
    {
        string args = string.Join(" ", (Arguments ?? Array.Empty<string>()).Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));
        return string.IsNullOrEmpty(args) ? FileName : FileName + " " + args;
    }
}
