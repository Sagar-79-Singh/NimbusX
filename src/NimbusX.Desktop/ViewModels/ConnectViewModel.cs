using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NimbusX.App;
using NimbusX.Providers;

namespace NimbusX.Desktop.ViewModels;

/// <summary>One provider card in the "add account" dialog.</summary>
public sealed partial class ProviderCardViewModel : ObservableObject
{
    public ProviderCardViewModel(ProviderDefinition definition)
    {
        Definition = definition;
    }

    public ProviderDefinition Definition { get; }

    public string Id => Definition.Id;

    public string DisplayName => Definition.DisplayName;

    public string Description => Definition.Description;

    public string Glyph => Definition.IconGlyph;

    public bool IsExperimental => Definition.IsExperimental;

    public string? Warning => Definition.ExperimentalWarning;

    public string ConnectKindText => Definition.ConnectKind switch
    {
        ProviderConnectKind.OAuth => "Browser sign-in",
        ProviderConnectKind.Credentials => "Email and password",
        ProviderConnectKind.CookieCapture => "In-app sign-in window",
        _ => string.Empty
    };

    public string? ConnectNote => Definition.ConnectNote;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>One input of the provider form.</summary>
public sealed partial class FieldRowViewModel : ObservableObject
{
    public FieldRowViewModel(ProviderField field)
    {
        Field = field;
        Value = field.DefaultValue ?? string.Empty;
    }

    public ProviderField Field { get; }

    public string Name => Field.Name;

    public string Label => Field.Label;

    public string? Help => Field.Help;

    public bool HasHelp => !string.IsNullOrWhiteSpace(Field.Help);

    public string Placeholder => Field.Placeholder ?? Field.Label;

    public bool IsSecret => Field.IsSecret;

    public bool IsPlain => !Field.IsSecret && !IsChoice;

    public bool IsChoice => Field.Choices.Count > 0;

    public IReadOnlyList<string> Choices => Field.Choices;

    public bool IsRequired => Field.Required;

    public string? OpensUrl => Field.OpensUrl;

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private string? _selectedChoice;

    partial void OnSelectedChoiceChanged(string? value) => Value = value ?? string.Empty;
}

/// <summary>
/// The connect flow: pick a provider, fill its form and watch the real progress rclone reports.
/// </summary>
/// <remarks>
/// Nothing is assumed to work: the dialog stays open until the account has been verified against the provider, and a
/// failure keeps rclone's own message plus the fix hint. Passwords typed here go straight into the DPAPI store and are
/// never written to a log.
/// </remarks>
public sealed partial class ConnectViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private CancellationTokenSource? _cts;

    public ConnectViewModel(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        Providers = new ObservableCollection<ProviderCardViewModel>();
        Fields = new ObservableCollection<FieldRowViewModel>();
        Steps = new ObservableCollection<string>();

        foreach (ProviderDefinition provider in ProviderCatalog.All)
        {
            Providers.Add(new ProviderCardViewModel(provider));
        }

        _selectedProvider = Providers.FirstOrDefault();
        if (_selectedProvider is not null)
        {
            _selectedProvider.IsSelected = true;
            RebuildFields();
        }
    }

    public ObservableCollection<ProviderCardViewModel> Providers { get; }

    public ObservableCollection<FieldRowViewModel> Fields { get; }

    public ObservableCollection<string> Steps { get; }

    [ObservableProperty]
    private ProviderCardViewModel? _selectedProvider;

    [ObservableProperty]
    private bool _isConnecting;

    [ObservableProperty]
    private string _statusText = "Choose a provider to begin.";

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string? _fixText;

    [ObservableProperty]
    private bool _hasFix;

    [ObservableProperty]
    private string? _authUrl;

    [ObservableProperty]
    private bool _hasAuthUrl;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private bool _autoMount = true;

    [ObservableProperty]
    private bool _finished;

    [ObservableProperty]
    private bool _succeeded;

    /// <summary>Raised when the dialog should close; the argument says whether an account was added.</summary>
    public event EventHandler<bool>? Closed;

    partial void OnSelectedProviderChanged(ProviderCardViewModel? value)
    {
        foreach (ProviderCardViewModel provider in Providers)
        {
            provider.IsSelected = ReferenceEquals(provider, value);
        }

        RebuildFields();
        StatusText = value is null ? "Choose a provider to begin." : value.ConnectKindText + ": " + value.Description;
        ClearError();
    }

    private void RebuildFields()
    {
        Fields.Clear();
        if (SelectedProvider is null)
        {
            return;
        }

        foreach (ProviderField field in SelectedProvider.Definition.Fields)
        {
            FieldRowViewModel row = new(field);
            if (row.IsChoice && row.Choices.Count > 0)
            {
                row.SelectedChoice = string.IsNullOrWhiteSpace(row.Value) ? row.Choices[0] : row.Value;
                row.Value = row.SelectedChoice!;
            }

            Fields.Add(row);
        }

        DisplayName = SelectedProvider.DisplayName;
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (SelectedProvider is null || IsConnecting)
        {
            return;
        }

        ClearError();
        Steps.Clear();
        AuthUrl = null;
        HasAuthUrl = false;

        if (!Validate(out string? validationError))
        {
            SetError(validationError!, "Fill in every required field and try again.");
            return;
        }

        IsConnecting = true;
        StatusText = "Connecting to " + SelectedProvider.DisplayName + "…";
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        Progress<ConnectProgress> progress = new(item =>
        {
            StatusText = item.Message;
            Steps.Add(item.Phase + ": " + item.Message);
            if (!string.IsNullOrWhiteSpace(item.AuthUrl))
            {
                AuthUrl = item.AuthUrl;
                HasAuthUrl = true;
            }
        });

        try
        {
            ConnectInputs inputs = BuildInputs();
            ConnectResult result = await _services.Accounts
                .ConnectAsync(inputs, progress, _cts.Token)
                .ConfigureAwait(true);

            foreach (string step in result.Steps)
            {
                Steps.Add(step);
            }

            if (result.Success && result.Account is not null)
            {
                Succeeded = true;
                StatusText = result.Account.DisplayName + " connected (" + result.Account.QuotaText + ").";
                Finished = true;
                Closed?.Invoke(this, true);
                return;
            }

            SetError(result.Error ?? "The provider did not accept the login.", result.Fix);
            StatusText = "Not connected.";
        }
        catch (OperationCanceledException)
        {
            SetError("The sign-in timed out.", "Try again; keep the sign-in window open until your files are listed.");
            StatusText = "Cancelled.";
        }
        catch (Exception exception)
        {
            SetError(exception.Message, "Export diagnostics from the Diagnostics page if this keeps happening.");
            StatusText = "Not connected.";
        }
        finally
        {
            IsConnecting = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already finished
        }

        Closed?.Invoke(this, false);
    }

    [RelayCommand]
    private void OpenHelp()
    {
        string? url = SelectedProvider?.Definition.HelpUrl ?? SelectedProvider?.Definition.SignInUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception exception)
        {
            SetError("Could not open the browser: " + exception.Message, null);
        }
    }

    [RelayCommand]
    private void OpenAuthUrl()
    {
        if (string.IsNullOrWhiteSpace(AuthUrl))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = AuthUrl, UseShellExecute = true });
        }
        catch (Exception exception)
        {
            SetError("Could not open the sign-in page: " + exception.Message, null);
        }
    }

    private bool Validate(out string? error)
    {
        error = null;
        if (SelectedProvider is null)
        {
            error = "Choose a provider first.";
            return false;
        }

        foreach (FieldRowViewModel field in Fields)
        {
            if (field.IsRequired && string.IsNullOrWhiteSpace(field.Value))
            {
                error = field.Label + " is required.";
                return false;
            }
        }

        return true;
    }

    private ConnectInputs BuildInputs()
    {
        ProviderDefinition provider = SelectedProvider!.Definition;
        ConnectInputs inputs = new(provider.Id)
        {
            DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? provider.DisplayName : DisplayName.Trim(),
            AutoMount = AutoMount
        };

        foreach (FieldRowViewModel field in Fields)
        {
            if (!string.IsNullOrWhiteSpace(field.Value))
            {
                inputs.Values[field.Name] = field.Value;
            }
        }

        return inputs;
    }

    private void SetError(string message, string? fix)
    {
        ErrorText = message;
        HasError = true;
        FixText = fix;
        HasFix = !string.IsNullOrWhiteSpace(fix);
    }

    private void ClearError()
    {
        ErrorText = null;
        HasError = false;
        FixText = null;
        HasFix = false;
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _cts = null;
    }
}
