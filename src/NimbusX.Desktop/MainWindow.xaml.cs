using System;
using System.ComponentModel;
using System.Windows;
using NimbusX.Core;
using NimbusX.Desktop.ViewModels;
using NimbusX.Desktop.Views;

namespace NimbusX.Desktop;

/// <summary>The shell window. It owns the two dialogs and the orderly shutdown.</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _shutdownComplete;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
        Title = AppInfo.WindowTitle;

        _viewModel.AddAccountRequested += OnAddAccountRequested;
        _viewModel.WizardRequested += OnWizardRequested;
    }

    private void OnAddAccountRequested(object? sender, EventArgs e)
    {
        AddAccountWindow dialog = new(_viewModel.CloudDrives.Services) { Owner = this };
        bool? result = dialog.ShowDialog();
        if (result == true)
        {
            _ = _viewModel.OnAccountListChangedAsync();
        }
    }

    private void OnWizardRequested(object? sender, EventArgs e)
    {
        WizardWindow wizard = new(_viewModel.CloudDrives.Services) { Owner = this };
        if (wizard.ShowDialog() == true)
        {
            _viewModel.OnSettingsChanged();
        }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_shutdownComplete)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        base.OnClosing(e);

        try
        {
            IsEnabled = false;
            if (App.Current is App app)
            {
                await app.ShutdownHostAsync().ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            _viewModel.CloudDrives.Services.Logger.Warn("Shutdown reported: " + exception.Message, "Shutdown");
        }
        finally
        {
            _shutdownComplete = true;
            Close();
        }
    }
}
