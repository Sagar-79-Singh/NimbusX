using System;
using System.IO;
using System.Linq;
using NimbusX.Games;
using Xunit;

namespace NimbusX.Tests;

public sealed class PeInspectorTests
{
    [Fact]
    public void A_minimal_graphical_pe_is_recognised()
    {
        using TempFolder folder = new("pe");
        string path = folder.WriteBytes("game.exe", PeBuilder.Executable(subsystem: 2, machine: 0x8664));

        PeInfo info = PeInspector.Inspect(path);

        Assert.True(info.IsPe);
        Assert.Equal(PeMachine.X64, info.Machine);
        Assert.Equal(2, info.Subsystem);
        Assert.Contains("graphical", info.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_console_subsystem_is_reported_as_such()
    {
        using TempFolder folder = new("pe");
        string path = folder.WriteBytes("tool.exe", PeBuilder.Executable(subsystem: 3, machine: 0x014c));

        PeInfo info = PeInspector.Inspect(path);

        Assert.True(info.IsPe);
        Assert.Equal(PeMachine.X86, info.Machine);
        Assert.Contains("console", info.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_text_file_is_not_an_executable()
    {
        using TempFolder folder = new("pe");
        string path = folder.WriteBytes("readme.exe", PeBuilder.NotAnExecutable());

        PeInfo info = PeInspector.Inspect(path);

        Assert.False(info.IsPe);
        Assert.Contains("MZ", info.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_file_is_reported_instead_of_throwing()
    {
        PeInfo info = PeInspector.Inspect(Path.Combine(Path.GetTempPath(), "nimbusx-does-not-exist.exe"));
        Assert.False(info.IsPe);
    }
}

public sealed class ExecutableFinderTests
{
    [Fact]
    public void An_installer_next_to_the_game_never_wins()
    {
        using TempFolder folder = new("finder");
        folder.WriteBytes("Game.exe", PeBuilder.Executable(subsystem: 2, padding: 200_000));
        folder.WriteBytes("unins000.exe", PeBuilder.Executable(subsystem: 2, padding: 200_000));

        ExecutableCandidate? best = ExecutableFinder.Best(folder.Path);

        Assert.NotNull(best);
        Assert.Equal("Game.exe", best!.FileName);
        Assert.DoesNotContain("unins000", best.RelativePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_console_helper_scores_below_the_gui_game()
    {
        using TempFolder folder = new("finder");
        folder.WriteBytes("Game.exe", PeBuilder.Executable(subsystem: 2, padding: 100_000));
        folder.WriteBytes("dedicated.exe", PeBuilder.Executable(subsystem: 3, padding: 100_000));

        var candidates = ExecutableFinder.Find(folder.Path);
        ExecutableCandidate game = candidates.Single(c => c.FileName == "Game.exe");
        ExecutableCandidate tool = candidates.Single(c => c.FileName == "dedicated.exe");

        Assert.True(game.Score > tool.Score);
    }

    [Fact]
    public void Engine_markers_next_to_the_executable_are_listed()
    {
        using TempFolder folder = new("finder");
        folder.WriteBytes("Game.exe", PeBuilder.Executable());
        folder.Write("UnityPlayer.dll", "not really a dll");

        var markers = ExecutableFinder.DetectEngineMarkers(folder.Path);

        Assert.NotEmpty(markers);
        Assert.Contains(markers, marker => marker.Contains("Unity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_empty_folder_has_no_candidates()
    {
        using TempFolder folder = new("finder-empty");
        Assert.Empty(ExecutableFinder.Find(folder.Path));
        Assert.Null(ExecutableFinder.Best(folder.Path));
    }
}

public sealed class GameConfidenceTests
{
    [Fact]
    public void Confidence_levels_are_ordered_from_unknown_to_confirmed()
    {
        // Enums do not support relational operators in C#, so the order is compared by value.
        Assert.True((int)GameConfidence.Unknown < (int)GameConfidence.Possible);
        Assert.True((int)GameConfidence.Possible < (int)GameConfidence.Likely);
        Assert.True((int)GameConfidence.Likely < (int)GameConfidence.Confirmed);
    }

    [Fact]
    public void A_new_entry_starts_unverified_and_honest_about_missing_data()
    {
        GameEntry entry = new()
        {
            Id = "abc",
            Name = "Example",
            AccountId = "account-1",
            RelativeFolder = "Games/Example",
            ExecutableRelativePath = "Games/Example/Example.exe"
        };

        Assert.False(entry.VerifiedLaunch);
        Assert.Equal(0, entry.LaunchCount);
        Assert.Equal("Example.exe", entry.ExecutableName);
        Assert.Equal("Not launched yet", entry.StatusText);
        // No measurement yet means the UI says "Unavailable" instead of inventing a size.
        Assert.Equal("Unavailable", entry.SizeText);
        Assert.Equal("Unavailable", entry.StartupText);
    }

    [Fact]
    public void A_verified_entry_reports_its_real_status()
    {
        GameEntry entry = new()
        {
            Name = "Example",
            VerifiedLaunch = true,
            LastStartupSeconds = 12.5,
            MeasuredFolderBytes = 4L * 1024 * 1024 * 1024
        };

        Assert.Equal("Launch verified", entry.StatusText);
        Assert.Equal("12.5 s", entry.StartupText);
        Assert.NotEqual("Unavailable", entry.SizeText);
    }
}
