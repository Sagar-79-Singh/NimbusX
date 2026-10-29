using System;
using System.Windows;
using System.Windows.Controls;
using NimbusX.App;
using NimbusX.Desktop.ViewModels;

namespace NimbusX.Desktop.Views;

/// <summary>The "add a cloud account" dialog. It closes itself when the connect flow finishes.</summary>
public partial class AddAccountWindow : Window
{
    private readonly ConnectViewModel _viewModel;

    public AddAccountWindow(AppServices services)
    {
        InitializeComponent();

        _viewModel = new ConnectViewModel(services ?? throw new ArgumentNullException(nameof(services)));
        DataContext = _viewModel;
        _viewModel.Closed += OnFlowClosed;
    }

    /// <summary>Moves a typed password into the view model. PasswordBox cannot bind, and secrets stay in memory only.</summary>
    private void OnSecretChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box && box.DataContext is FieldRowViewModel row)
        {
            row.Value = box.Password;
        }
    }

    private void OnFlowClosed(object? sender, bool addedAccount)
    {
        _viewModel.Closed -= OnFlowClosed;
        DialogResult = addedAccount;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Closed -= OnFlowClosed;
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
