using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NimbusX.App;
using NimbusX.Core.Text;
using NimbusX.Games;
using NimbusX.Streaming;

namespace NimbusX.Desktop.ViewModels;

/// <summary>A planned file in the warm-start list.</summary>
public sealed class PlannedFileRow
{
    public PlannedFileRow(PlannedFile file)
    {
        Path = file.RelativePath;
        SizeText = ByteSize.Format(file.Bytes);
        Reason = file.Reason;
    }

    public string Path { get; }

    public string SizeText { get; }

    public string Reason { get; }
}

/// <summary>Game Accelerator: warm start, prediction confidence and the measured prepare result.</summary>
public sealed partial class AcceleratorViewModel : PageViewModel
{
    public AcceleratorViewModel(AppServices services)
        : base(services, "Game Accelerator", "Prepare a game so its first minutes come from the local cache", "\uE945")
    {
        Games = new ObservableCollection<GameEntry>();
        Files = new ObservableCollection<PlannedFileRow>();
    }

    public ObservableCollection<GameEntry> Games { get; }

    public ObservableCollection<PlannedFileRow> Files { get; }

    [ObservableProperty]
    private GameEntry? _selectedGame;

    [ObservableProperty]
    private string _predictionText = "Select a game to see its launch history.";

    [ObservableProperty]
    private string _explanation = "The accelerator only warms files NimbusX has actually seen the game read.";

    [ObservableProperty]
    private string _planSize = "Unavailable";

    [ObservableProperty]
    private string _resultText = string.Empty;

    [ObservableProperty]
    private bool _isPreparing;

    [ObservableProperty]
    private bool _hasSelection;

    [ObservableProperty]
    private bool _hasFiles;

    [ObservableProperty]
    private string _prefetchBudget = "Unavailable";

    public override Task RefreshAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<GameEntry> library = Services.GameStore.Games;
        string? previous = SelectedGame?.Id;

        Games.Clear();
        foreach (GameEntry game in library.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Games.Add(game);
        }

        SelectedGame = Games.FirstOrDefault(g => g.Id == previous) ?? Games.FirstOrDefault();
        PrefetchBudget = ByteSize.Format(Services.Prefetch.RemainingBudgetBytes);
        return Task.CompletedTask;
    }

    partial void OnSelectedGameChanged(GameEntry? value)
    {
        HasSelection = value is not null;
        UpdatePlan();
    }

    private void UpdatePlan()
    {
        Files.Clear();
        if (SelectedGame is null)
        {
            HasFiles = false;
            PredictionText = "Select a game to see its launch history.";
            Explanation = "The accelerator only warms files NimbusX has actually seen the game read.";
            PlanSize = "Unavailable";
            return;
        }

        try
        {
            AcceleratorPlan plan = Services.Accelerator.BuildPlan(SelectedGame.Id);
            foreach (PlannedFile planned in plan.Files)
            {
                Files.Add(new PlannedFileRow(planned));
            }

            HasFiles = plan.HasWork;
            PredictionText = plan.PredictedText;
            Explanation = plan.Explanation;
            PlanSize = plan.HasWork ? ByteSize.Format(plan.TotalBytes) : "Nothing to warm yet";
        }
        catch (Exception exception)
        {
            HasFiles = false;
            PredictionText = "Launch history unavailable";
            Explanation = exception.Message;
            PlanSize = "Unavailable";
        }
    }

    [RelayCommand]
    private async Task PrepareAsync()
    {
        if (SelectedGame is null || IsPreparing)
        {
            return;
        }

        IsPreparing = true;
        ResultText = "Warming " + SelectedGame.Name + "…";
        string gameId = SelectedGame.Id;

        try
        {
            AcceleratorResult result = await Services.Accelerator
                .PrepareAsync(gameId, null, CancellationToken.None)
                .ConfigureAwait(true);

            ResultText = result.Summary;
            if (!result.Success)
            {
                ResultText += " " + (result.Fix ?? string.Empty);
            }

            PrefetchBudget = ByteSize.Format(Services.Prefetch.RemainingBudgetBytes);
        }
        catch (Exception exception)
        {
            ResultText = "Prepare failed: " + exception.Message;
        }
        finally
        {
            IsPreparing = false;
            await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void CancelPrepare()
    {
        Services.Prefetch.CancelAll();
        ResultText = "Cancelled. Already cached data is kept.";
        IsPreparing = false;
    }

    [RelayCommand]
    private void RefreshPlan() => UpdatePlan();
}
