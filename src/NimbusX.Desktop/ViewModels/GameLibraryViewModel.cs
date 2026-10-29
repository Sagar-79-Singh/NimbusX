using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NimbusX.App;
using NimbusX.Core.Text;
using NimbusX.Games;
using NimbusX.Mount;

namespace NimbusX.Desktop.ViewModels;

/// <summary>A game row in the library.</summary>
public sealed partial class GameRowViewModel : ObservableObject
{
    public GameRowViewModel(GameEntry entry)
    {
        Id = entry.Id;
        Name = entry.Name;
        AccountId = entry.AccountId;
        Folder = entry.RelativeFolder;
        ExecutableName = entry.ExecutableName;
        Confidence = entry.Confidence.ToString();
        SizeText = entry.SizeText;
        StartupText = entry.StartupText;
        StatusText = entry.StatusText;
        IsVerified = entry.VerifiedLaunch;
        IsFavorite = entry.IsFavorite;
        LaunchCount = entry.LaunchCount;
        LastError = entry.LastError;
        HasError = !string.IsNullOrWhiteSpace(entry.LastError);
    }

    public string Id { get; }

    public string Name { get; }

    public string AccountId { get; }

    public string Folder { get; }

    public string ExecutableName { get; }

    public string Confidence { get; }

    public string SizeText { get; }

    public string StartupText { get; }

    public string StatusText { get; }

    public bool IsVerified { get; }

    public bool IsFavorite { get; }

    public int LaunchCount { get; }

    public string? LastError { get; }

    public bool HasError { get; }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isRunning;

    public string PlayButtonText => IsRunning ? "Running" : "Play";

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(PlayButtonText));
}

/// <summary>Game Library: scan a mounted drive, keep the library, launch and verify.</summary>
public sealed partial class GameLibraryViewModel : PageViewModel
{
    public GameLibraryViewModel(AppServices services)
        : base(services, "Game Library", "Find the games on your cloud drives and launch them", "\uE7FC")
    {
        Games = new ObservableCollection<GameRowViewModel>();
        Mounts = new ObservableCollection<MountSnapshot>();
        StartTimer(TimeSpan.FromSeconds(5), () => RefreshLiveStateAsync());
    }

    public ObservableCollection<GameRowViewModel> Games { get; }

    public ObservableCollection<MountSnapshot> Mounts { get; }

    [ObservableProperty]
    private MountSnapshot? _selectedMount;

    [ObservableProperty]
    private string _scanRoot = string.Empty;

    [ObservableProperty]
    private bool _measureSizes;

    [ObservableProperty]
    private string _statusMessage = "Choose a mounted drive and scan it for games.";

    [ObservableProperty]
    private string? _launchResult;

    [ObservableProperty]
    private bool _hasGames;

    [ObservableProperty]
    private string _summary = string.Empty;

    public override async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await GuardedRefreshAsync(cancellationToken, async () =>
        {
            IReadOnlyList<MountSnapshot> snapshots = Services.Mounts.Snapshots;

            string? previous = SelectedMount?.AccountId;
            Mounts.Clear();
            foreach (MountSnapshot snapshot in snapshots)
            {
                Mounts.Add(snapshot);
            }

            SelectedMount = Mounts.FirstOrDefault(m => m.AccountId == previous) ?? Mounts.FirstOrDefault();

            ReloadGames();
            await Task.CompletedTask.ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private void ReloadGames()
    {
        IReadOnlyList<GameEntry> library = Services.GameStore.Games;
        Games.Clear();
        foreach (GameEntry entry in library.OrderByDescending(g => g.IsFavorite).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Games.Add(new GameRowViewModel(entry));
        }

        HasGames = Games.Count > 0;
        Summary = Games.Count == 0
            ? "No games yet."
            : Games.Count + " " + TextFormat.Pluralize(Games.Count, "game") + ", " +
              Games.Count(g => g.IsVerified) + " with a verified launch.";
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (SelectedMount is null)
        {
            StatusMessage = "Mount a cloud drive first, then scan it.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Scanning " + SelectedMount.DriveLetter + ": …";
        try
        {
            ScanOptions options = new() { MeasureSizes = MeasureSizes };
            GameScanResult result = await Services.Games
                .ScanAsync(SelectedMount.AccountId, ScanRoot, options, CancellationToken.None)
                .ConfigureAwait(true);

            int added = 0;
            foreach (GameCandidate candidate in result.Candidates)
            {
                if (Services.Games.AddCandidate(SelectedMount.AccountId, candidate) is not null)
                {
                    added++;
                }
            }

            ReloadGames();
            StatusMessage = result.Describe() + " Added " + added + " to the library." +
                            (result.Warnings.Count > 0 ? " " + result.Warnings.Count + " folder(s) could not be read." : string.Empty);
        }
        catch (Exception exception)
        {
            StatusMessage = "Scan failed: " + exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PlayAsync(GameRowViewModel? row)
    {
        if (row is null || row.IsBusy)
        {
            return;
        }

        row.IsBusy = true;
        LaunchResult = "Launching " + row.Name + " and watching it for 60 seconds…";
        try
        {
            LaunchResult result = await Services.Launches.PlayAsync(row.Id, CancellationToken.None).ConfigureAwait(true);
            LaunchResult =
                row.Name + ": " + (result.Verified ? "verified" : "not verified") +
                ", window " + (result.WindowAppeared ? "after " + result.StartupText : "not detected") +
                (result.Error is null ? "." : ". Error: " + result.Error + " Fix: " + result.Fix);
            ReloadGames();
        }
        catch (Exception exception)
        {
            LaunchResult = "Launch failed: " + exception.Message;
        }
        finally
        {
            row.IsBusy = false;
            await RefreshLiveStateAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void ToggleFavorite(GameRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        Services.Games.SetFavorite(row.Id, !row.IsFavorite);
        ReloadGames();
    }

    [RelayCommand]
    private void Remove(GameRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        Services.Games.Remove(row.Id);
        ReloadGames();
        StatusMessage = row.Name + " removed from the library. Nothing was deleted in the cloud.";
    }

    [RelayCommand]
    private void OpenFolder(GameRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        MountSnapshot? mount = Services.Mounts.Snapshots.FirstOrDefault(m => m.AccountId == row.AccountId);
        if (mount is null)
        {
            StatusMessage = "That drive is not mounted.";
            return;
        }

        try
        {
            string path = string.IsNullOrWhiteSpace(row.Folder)
                ? mount.DriveRoot
                : Path.Combine(mount.DriveRoot, row.Folder.Replace('/', Path.DirectorySeparatorChar));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception exception)
        {
            StatusMessage = "Could not open the folder: " + exception.Message;
        }
    }

    private Task RefreshLiveStateAsync()
    {
        HashSet<string> running = Services.Launcher.Running.Select(g => g.GameId).ToHashSet(StringComparer.Ordinal);
        foreach (GameRowViewModel row in Games)
        {
            row.IsRunning = running.Contains(row.Id);
        }

        return Task.CompletedTask;
    }
}
