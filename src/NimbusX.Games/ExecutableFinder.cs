using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NimbusX.Games;

/// <summary>One executable that might be a game's entry point, with the evidence behind its score.</summary>
public sealed class ExecutableCandidate
{
    public ExecutableCandidate(string relativePath, long bytes, PeInfo pe, int score, IReadOnlyList<string> reasons)
    {
        RelativePath = relativePath;
        Bytes = Math.Max(0, bytes);
        Pe = pe;
        Score = score;
        Reasons = reasons;
    }

    public string RelativePath { get; }

    public long Bytes { get; }

    public PeInfo Pe { get; }

    public int Score { get; }

    public IReadOnlyList<string> Reasons { get; }

    public string FileName => RelativePath.Split('/').Last();

    public string Describe() =>
        FileName + " (score " + Score + ": " + string.Join("; ", Reasons) + ")";

    public override string ToString() => Describe();
}

/// <summary>
/// Picks the executable a game should be launched with.
/// </summary>
/// <remarks>
/// The ranking is based only on things that can be observed: the PE subsystem (a console tool is almost never the
/// game), the file size, the name (installers, crash handlers and redistributables are filtered out) and engine
/// marker files next to it. The user's manual choice always wins over the ranking.
/// </remarks>
public static class ExecutableFinder
{
    private static readonly string[] IgnoredNameFragments =
    {
        "unins", "setup", "install", "update", "redist", "vcredist", "dxsetup", "dxwebsetup", "dotnet",
        "crashhandler", "crashreport", "crashpad", "bugreport", "eac_", "easyanticheat", "battleye",
        "unityeditor", "unrealeditor", "editor.exe", "server.exe", "dedicated", "benchmark", "config",
        "launcher_helper", "report", "helper"
    };

    public static IReadOnlyList<ExecutableCandidate> Find(string folder, int maxFiles = 64)
    {
        if (!Directory.Exists(folder))
        {
            return Array.Empty<ExecutableCandidate>();
        }

        List<string> engineMarkers = DetectEngineMarkers(folder);
        List<ExecutableCandidate> candidates = new();

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*.exe", SearchOption.TopDirectoryOnly);
        }
        catch (Exception)
        {
            return Array.Empty<ExecutableCandidate>();
        }

        foreach (string file in files.Take(maxFiles))
        {
            string name = Path.GetFileName(file);
            string lower = name.ToLowerInvariant();
            long bytes;
            try
            {
                bytes = new FileInfo(file).Length;
            }
            catch (Exception)
            {
                continue;
            }

            PeInfo pe = PeInspector.Inspect(file);
            List<string> reasons = new();
            int score = 0;

            if (pe.IsGui)
            {
                score += 40;
                reasons.Add("graphical Windows subsystem");
            }
            else if (pe.IsConsole)
            {
                score -= 20;
                reasons.Add("console subsystem");
            }
            else if (!pe.IsPe)
            {
                score -= 30;
                reasons.Add(pe.Detail);
            }

            if (IgnoredNameFragments.Any(fragment => lower.Contains(fragment, StringComparison.Ordinal)))
            {
                score -= 60;
                reasons.Add("name looks like an installer, helper or crash handler");
            }

            if (bytes >= 8L * 1024 * 1024)
            {
                score += 20;
                reasons.Add("8 MiB or larger");
            }
            else if (bytes >= 1024 * 1024)
            {
                score += 10;
                reasons.Add("1 MiB or larger");
            }
            else if (bytes < 128 * 1024)
            {
                score -= 10;
                reasons.Add("smaller than 128 KiB");
            }

            if (engineMarkers.Count > 0)
            {
                score += 15;
                reasons.Add("engine markers: " + string.Join(", ", engineMarkers));
            }

            candidates.Add(new ExecutableCandidate(
                name,
                bytes,
                pe,
                score,
                reasons.Count == 0 ? new[] { "no positive or negative signals" } : reasons));
        }

        return candidates
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.Bytes)
            .ToArray();
    }

    /// <summary>Best guess, or null when nothing looks like a game executable.</summary>
    public static ExecutableCandidate? Best(string folder)
    {
        IReadOnlyList<ExecutableCandidate> candidates = Find(folder);
        return candidates.Count > 0 && candidates[0].Score > 0 ? candidates[0] : null;
    }

    /// <summary>Engine marker files/folders that identify a game even without a manifest.</summary>
    public static List<string> DetectEngineMarkers(string folder)
    {
        List<string> markers = new();
        void Check(string path, string label)
        {
            try
            {
                if (File.Exists(path) || Directory.Exists(path))
                {
                    markers.Add(label);
                }
            }
            catch (Exception)
            {
                // an unreadable marker simply does not count
            }
        }

        void CheckPattern(string directory, string pattern, string label)
        {
            try
            {
                if (Directory.Exists(directory) &&
                    Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Any())
                {
                    markers.Add(label);
                }
            }
            catch (Exception)
            {
                // unreadable folder: the marker simply does not count
            }
        }

        Check(Path.Combine(folder, "UnityPlayer.dll"), "Unity");
        Check(Path.Combine(folder, "GameAssembly.dll"), "IL2CPP");
        Check(Path.Combine(folder, "Engine"), "Unreal");
        Check(Path.Combine(folder, "Binaries"), "Unreal");
        Check(Path.Combine(folder, "godot.exe"), "Godot");
        Check(Path.Combine(folder, "resources", "app"), "Electron");
        Check(Path.Combine(folder, "x64"), "Game folder layout");
        Check(Path.Combine(folder, "steam_api64.dll"), "Steam API");
        Check(Path.Combine(folder, "steam_api.dll"), "Steam API");
        Check(Path.Combine(folder, "GOG.com"), "GOG");
        Check(Path.Combine(folder, "data"), "Game data");
        CheckPattern(folder, "*.pak", "Unreal PAK");
        CheckPattern(folder, "*.pck", "Godot PCK");
        return markers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
