using System;
using System.Collections.Generic;

namespace NimbusX.Providers;

/// <summary>How a provider authenticates.</summary>
public enum ProviderConnectKind
{
    /// <summary>Browser sign-in driven through rclone's OAuth state machine.</summary>
    OAuth = 0,

    /// <summary>A form (email/password, host/user/password or an app password).</summary>
    Credentials = 1,

    /// <summary>A login window whose session cookie NimbusX reads automatically (TeraBox).</summary>
    CookieCapture = 2
}

/// <summary>Input type for a provider form field.</summary>
public enum ProviderFieldKind
{
    Text = 0,
    Password = 1,
    Number = 2,
    Choice = 3,
    Email = 4
}

/// <summary>One field of a provider connect form.</summary>
public sealed class ProviderField
{
    public ProviderField(
        string name,
        string label,
        ProviderFieldKind kind = ProviderFieldKind.Text,
        bool required = true,
        string? placeholder = null,
        string? help = null,
        string? defaultValue = null,
        IReadOnlyList<string>? choices = null,
        string? opensUrl = null)
    {
        Name = name;
        Label = label;
        Kind = kind;
        Required = required;
        Placeholder = placeholder;
        Help = help;
        DefaultValue = defaultValue;
        Choices = choices ?? Array.Empty<string>();
        OpensUrl = opensUrl;
    }

    /// <summary>rclone option name this field maps to.</summary>
    public string Name { get; }

    public string Label { get; }

    public ProviderFieldKind Kind { get; }

    public bool Required { get; }

    public string? Placeholder { get; }

    public string? Help { get; }

    public string? DefaultValue { get; }

    public IReadOnlyList<string> Choices { get; }

    /// <summary>When set, the UI offers a "Open" button for this URL (e.g. generate an app password).</summary>
    public string? OpensUrl { get; }

    public bool IsSecret => Kind == ProviderFieldKind.Password;
}

/// <summary>Everything the UI and the connect engine need to know about a provider.</summary>
public sealed class ProviderDefinition
{
    public ProviderDefinition(
        string id,
        string displayName,
        string description,
        string rcloneType,
        ProviderConnectKind connectKind,
        string iconGlyph)
    {
        Id = id;
        DisplayName = displayName;
        Description = description;
        RcloneType = rcloneType;
        ConnectKind = connectKind;
        IconGlyph = iconGlyph;
        Fields = Array.Empty<ProviderField>();
    }

    /// <summary>Stable identifier used in accounts.json and the UI (e.g. <c>drive</c>).</summary>
    public string Id { get; }

    public string DisplayName { get; }

    public string Description { get; }

    /// <summary>rclone backend name (e.g. <c>drive</c>, <c>webdav</c>, <c>terabox</c>).</summary>
    public string RcloneType { get; }

    public ProviderConnectKind ConnectKind { get; }

    /// <summary>Segoe MDL2 glyph used by the WPF cards.</summary>
    public string IconGlyph { get; }

    public IReadOnlyList<ProviderField> Fields { get; init; }

    /// <summary>True when rclone ships a built-in OAuth client for this backend (one-click, no app registration).</summary>
    public bool HasBuiltInOAuthClient { get; init; }

    /// <summary>Option names used when the user supplies their own OAuth client.</summary>
    public string ClientIdOptionName { get; init; } = "client_id";

    public string ClientSecretOptionName { get; init; } = "client_secret";

    /// <summary>Extra rclone config parameters always sent with this provider.</summary>
    public IReadOnlyDictionary<string, string> FixedParameters { get; init; } = new Dictionary<string, string>();

    public bool IsExperimental { get; init; }

    /// <summary>Honest warning shown next to the Experimental badge.</summary>
    public string? ExperimentalWarning { get; init; }

    /// <summary>Which rclone build the provider needs.</summary>
    public bool RequiresTeraBoxBuild { get; init; }

    /// <summary>Page the user should open to create credentials (app password, WebDAV URL, ...).</summary>
    public string? HelpUrl { get; init; }

    public string? HelpText { get; init; }

    /// <summary>Deep link that starts the provider's own sign-in page for credential providers.</summary>
    public string? SignInUrl { get; init; }

    /// <summary>Why a provider does not use OAuth (shown in Advanced / docs).</summary>
    public string? ConnectNote { get; init; }

    public bool SupportsCustomClient => ConnectKind == ProviderConnectKind.OAuth;

    public override string ToString() => DisplayName + " (" + Id + ")";
}
