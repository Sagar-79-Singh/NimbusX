using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NimbusX.Core.Text;

namespace NimbusX.Games;

/// <summary>How confident NimbusX is that a folder really is a game.</summary>
public enum GameConfidence
{
    Unknown = 0,

    /// <summary>An executable was found but nothing proves it is a game.</summary>
    Possible,

    /// <summary>An executable plus engine markers (Unity, Unreal, Godot...) were found.</summary>
    Likely,

    /// <summary>The user picked the folder or executable explicitly.</summary>
    Confirmed
}

/// <summary>One game that lives on a NimbusX cloud drive.</summary>
public sealed class GameEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("d");

    public string AccountId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Folder inside the mounted drive, forward slashes, empty for the drive root.</summary>
    public string RelativeFolder { get; set; } = string.Empty;

    /// <summary>Executable inside <see cref="RelativeFolder"/>, forward slashes.</summary>
    public string ExecutableRelativePath { get; set; } = string.Empty;

    /// <summary>Command line arguments the user configured (never secrets).</summary>
    public string Arguments { get; set; } = string.Empty;

    public GameConfidence Confidence { get; set; } = GameConfidence.Possible;

    /// <summary>Engine markers that were detected, e.g. "Unity", "Unreal", "Godot".</summary>
    public List<string> EngineMarkers { get; set; } = new();

    /// <summary>Physical size of the folder when it was scanned (0 when not measured).</summary>
    public long MeasuredFolderBytes { get; set; }

    public bool IsFavorite { get; set; }

    public DateTimeOffset AddedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastScanUtc { get; set; }

    public int LaunchCount { get; set; }

    public DateTimeOffset? LastLaunchUtc { get; set; }

    /// <summary>Seconds from process start until the window appeared, measured by the launcher.</summary>
    public double? LastStartupSeconds { get; set; }

    public double? BestStartupSeconds { get; set; }

    public double? LastExitCode { get; set; }

    public string? LastError { get; set; }

    /// <summary>True only when a launch actually stayed up for the configured verification window.</summary>
    public bool VerifiedLaunch { get; set; }

    public DateTimeOffset? VerifiedUtc { get; set; }

    /// <summary>Files the launcher observed early, used as prefetch hints.</summary>
    public List<string> HotFiles { get; set; } = new();

    public string ExecutableName =>
        string.IsNullOrWhiteSpace(ExecutableRelativePath)
            ? string.Empty
            : ExecutableRelativePath.Split('/').Last();

    public string DisplayFolder =>
        string.IsNullOrWhiteSpace(RelativeFolder) ? "(drive root)" : RelativeFolder;

    public string SizeText => MeasuredFolderBytes <= 0 ? "Unavailable" : ByteSize.Format(MeasuredFolderBytes);

    public string StartupText =>
        LastStartupSeconds is null
            ? "Unavailable"
            : LastStartupSeconds.Value.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    public string StatusText
    {
        get
        {
            if (VerifiedLaunch)
            {
                return "Launch verified";
            }

            if (!string.IsNullOrWhiteSpace(LastError))
            {
                return "Last launch failed";
            }

            return LaunchCount == 0 ? "Not launched yet" : "Launched, not verified";
        }
    }

    public override string ToString() => Name + " (" + DisplayFolder + ")";
}
