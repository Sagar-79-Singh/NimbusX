using System;
using System.Windows;
using NimbusX.App;
using NimbusX.Desktop.ViewModels;

namespace NimbusX.Desktop.Views;

/// <summary>The first run wizard.</summary>
public partial class WizardWindow : Window
{
    private readonly WizardViewModel _viewModel;

    public WizardWindow(AppServices services)
    {
        InitializeComponent();

        _viewModel = new WizardViewModel(services ?? throw new ArgumentNullException(nameof(services)));
        DataContext = _viewModel;
        _viewModel.Closed += OnClosed;
    }

    private void OnClosed(object? sender, bool completed)
    {
        _viewModel.Closed -= OnClosed;
        DialogResult = completed;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Closed -= OnClosed;
        base.OnClosed(e);
    }
}
