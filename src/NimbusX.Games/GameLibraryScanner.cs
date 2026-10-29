using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NimbusX.Core.Io;
using NimbusX.Core.Logging;
using NimbusX.Mount;

namespace NimbusX.Games;

/// <summary>One folder that looks like a game, produced by a scan.</summary>
public sealed class GameCandidate
{
    public GameCandidate(
        string relativeFolder,
        string executableRelativePath,
        string suggestedName,
        GameConfidence confidence,
        IReadOnlyList<string> engineMarkers,
        long folderBytes,
        ExecutableCandidate executable)
    {
        RelativeFolder = relativeFolder;
        ExecutableRelativePath = executableRelativePath;
        SuggestedName = suggestedName;
        Confidence = confidence;
        EngineMarkers = engineMarkers;
        FolderBytes = folderBytes;
        Executable = executable;
    }

    public string RelativeFolder { get; }

    public string ExecutableRelativePath { get; }

    public string SuggestedName { get; }

    public GameConfidence Confidence { get; }

    public IReadOnlyList<string> EngineMarkers { get; }

    public long FolderBytes { get; }

    public ExecutableCandidate Executable { get; }

    public override string ToString() =>
        SuggestedName + " (" + Confidence + ", " + RelativeFolder + ", " + Executable.FileName + ")";
}

/// <summary>Outcome of scanning one mounted drive for games.</summary>
public sealed class GameScanResult
{
    public GameScanResult(
        string accountId,
        string relativeRoot,
        IReadOnlyList<GameCandidate> candidates,
        int foldersVisited,
        TimeSpan elapsed,
        IReadOnlyList<string> warnings)
    {
        AccountId = accountId;
        RelativeRoot = relativeRoot;
        Candidates = candidates ?? Array.Empty<GameCandidate>();
        FoldersVisited = foldersVisited;
        Elapsed = elapsed;
        Warnings = warnings ?? Array.Empty<string>();
    }

    public string AccountId { get; }

    public string RelativeRoot { get; }

    public IReadOnlyList<GameCandidate> Candidates { get; }

    public int FoldersVisited { get; }

    public TimeSpan Elapsed { get; }

    /// <summary>Real problems encountered while scanning (unreadable folders, timeouts).</summary>
    public IReadOnlyList<string> Warnings { get; }

    public string Describe() =>
        Candidates.Count + " game folder(s) found in " + FoldersVisited + " folder(s) in " +
        Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
}

/// <summary>
/// Finds game folders on a mounted drive without downloading their contents.
/// </summary>
/// <remarks>
/// A scan only lists directories and reads executable headers, so it costs metadata requests rather than file data.
/// Deep scans are bounded by <see cref="ScanOptions.MaxFolders"/> and by a cancellation token, because a cold
/// directory cache over the network can be slow; the scan reports what it actually visited.
/// </remarks>
public sealed class GameLibraryScanner
{
    private readonly IMountManager _mounts;
    private readonly IAppLogger _logger;

    public GameLibraryScanner(IMountManager mounts, IAppLogger? logger = null)
    {
        _mounts = mounts ?? throw new ArgumentNullException(nameof(mounts));
        _logger = logger ?? NullLogger.Instance;
    }

    public Task<GameScanResult> ScanAsync(
        string accountId,
        string relativeRoot,
        ScanOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(accountId, relativeRoot, options, cancellationToken), cancellationToken);

    public GameScanResult Scan(
        string accountId,
        string relativeRoot,
        ScanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ScanOptions();
        DateTimeOffset started = DateTimeOffset.UtcNow;
        List<string> warnings = new();
        List<GameCandidate> candidates = new();
        int foldersVisited = 0;

        MountSnapshot? snapshot = _mounts.GetSnapshot(accountId);
        if (snapshot is null)
        {
            throw new InvalidOperationException("That cloud drive is not mounted, so it cannot be scanned.");
        }

        string root;
        try
        {
            root = PathGuard.CombineUnder(snapshot.DriveRoot, relativeRoot ?? string.Empty);
        }
        catch (Exception exception)
        {
            throw new ArgumentException("The scan folder is not inside " + snapshot.DriveLetter + ": (" + exception.Message + ").", nameof(relativeRoot));
        }

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("The folder '" + (string.IsNullOrWhiteSpace(relativeRoot) ? "(drive root)" : relativeRoot) + "' does not exist on " + snapshot.DriveLetter + ":.");
        }

        Queue<(string Path, int Depth)> pending = new();
        pending.Enqueue((root, 0));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (foldersVisited >= options.MaxFolders)
            {
                warnings.Add("Stopped after " + options.MaxFolders + " folders; raise the limit in Settings to scan deeper.");
                break;
            }

            (string folder, int depth) = pending.Dequeue();
            foldersVisited++;

            string[] subdirectories;
            try
            {
                subdirectories = Directory.GetDirectories(folder);
            }
            catch (Exception exception)
            {
                warnings.Add("Could not list '" + RelativeOf(root, folder) + "': " + exception.Message);
                continue;
            }

            IReadOnlyList<ExecutableCandidate> executables = ExecutableFinder.Find(folder);
            ExecutableCandidate? best = executables.FirstOrDefault(e => e.Score > 0);
            if (best is not null)
            {
                List<string> markers = ExecutableFinder.DetectEngineMarkers(folder);
                GameConfidence confidence = markers.Count > 0 ? GameConfidence.Likely : GameConfidence.Possible;
                long bytes = 0;
                if (options.MeasureSizes)
                {
                    bytes = MeasureFolder(folder, warnings);
                }

                string relativeFolder = RelativeOf(root, folder);
                string driveFolder = CombineRelative(relativeRoot, relativeFolder);
                candidates.Add(new GameCandidate(
                    driveFolder,
                    string.IsNullOrWhiteSpace(driveFolder) ? best.RelativePath : driveFolder + "/" + best.RelativePath,
                    SuggestName(relativeFolder, best),
                    confidence,
                    markers,
                    bytes,
                    best));
            }

            if (depth < options.MaxDepth)
            {
                foreach (string subdirectory in subdirectories)
                {
                    string name = Path.GetFileName(subdirectory);
                    if (ShouldSkip(name, options))
                    {
                        continue;
                    }

                    pending.Enqueue((subdirectory, depth + 1));
                }
            }
        }

        TimeSpan elapsed = DateTimeOffset.UtcNow - started;
        _logger.Info(
            "Game scan of " + snapshot.DisplayName + " visited " + foldersVisited + " folder(s) and found " +
            candidates.Count + " candidate(s) in " + elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s.",
            "Games");

        return new GameScanResult(accountId, relativeRoot ?? string.Empty, candidates, foldersVisited, elapsed, warnings);
    }

    private static bool ShouldSkip(string name, ScanOptions options)
    {
        foreach (string fragment in options.SkipFolderFragments)
        {
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string SuggestName(string relativeFolder, ExecutableCandidate executable)
    {
        if (!string.IsNullOrWhiteSpace(relativeFolder))
        {
            string last = relativeFolder.Split('/').Last();
            if (!string.IsNullOrWhiteSpace(last))
            {
                return last.Replace('_', ' ').Trim();
            }
        }

        return Path.GetFileNameWithoutExtension(executable.FileName);
    }

    /// <summary>Joins the scan root and a folder relative to it, always with forward slashes.</summary>
    private static string CombineRelative(string? root, string relative)
    {
        string left = string.IsNullOrWhiteSpace(root) ? string.Empty : root!.Replace('\\', '/').Trim('/');
        string right = relative.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(left))
        {
            return right;
        }

        return string.IsNullOrWhiteSpace(right) ? left : left + "/" + right;
    }

    private static string RelativeOf(string root, string folder)
    {
        string relative = Path.GetRelativePath(root, folder).Replace('\\', '/');
        return relative == "." ? string.Empty : relative.Trim('/');
    }

    private static long MeasureFolder(string folder, List<string> warnings)
    {
        try
        {
            return DiskSpace.MeasureTree(folder).Physical;
        }
        catch (Exception exception)
        {
            warnings.Add("Could not measure '" + folder + "': " + exception.Message);
            return 0;
        }
    }
}

/// <summary>How far and how carefully a scan looks.</summary>
public sealed class ScanOptions
{
    public int MaxDepth { get; set; } = 2;

    public int MaxFolders { get; set; } = 400;

    /// <summary>Measuring sizes reads every file's metadata, which is slow over the network; off by default.</summary>
    public bool MeasureSizes { get; set; }

    /// <summary>Folder names that are never games.</summary>
    public List<string> SkipFolderFragments { get; set; } = new()
    {
        "$RECYCLE.BIN", "System Volume Information", "Windows", "Program Files", "ProgramData",
        "AppData", "node_modules", ".git", "__MACOSX", "Redistributables", "_CommonRedist", "DirectX",
        "dotnet", "vc_redist"
    };
}
