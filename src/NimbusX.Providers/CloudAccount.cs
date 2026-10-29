using System;
using System.Collections.Generic;
using NimbusX.Core.Text;

namespace NimbusX.Providers;

/// <summary>
/// One connected cloud account: a provider login plus its own drive letter and cache folder.
/// </summary>
/// <remarks>
/// Three Google accounts are three <see cref="CloudAccount"/> records with different ids and rclone remotes, which
/// is what gives them different drive letters and different cache folders. No secret is stored here: provider
/// credentials live in rclone.conf (encrypted) and in DPAPI protected secret files.
/// </remarks>
public sealed class CloudAccount
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Provider id from <see cref="ProviderCatalog"/>.</summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Name shown on the card, e.g. "Google Drive 2".</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>rclone remote name, e.g. <c>nx_9f3ac1b2</c>.</summary>
    public string RemoteName { get; set; } = string.Empty;

    /// <summary>Optional folder inside the remote to mount.</summary>
    public string RemotePath { get; set; } = string.Empty;

    /// <summary>Drive letter the account prefers. NimbusX moves it when the letter is taken.</summary>
    public string? PreferredDriveLetter { get; set; }

    /// <summary>Mount automatically when NimbusX starts.</summary>
    public bool AutoMount { get; set; } = true;

    /// <summary>Label returned by the provider for this login (email/user name), when known.</summary>
    public string? AccountLabel { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastConnectedUtc { get; set; }

    public DateTimeOffset? LastQuotaUtc { get; set; }

    public long? QuotaUsedBytes { get; set; }

    public long? QuotaTotalBytes { get; set; }

    /// <summary>Set when the login needs the user to sign in again (expired cookie, revoked token).</summary>
    public bool NeedsReconnect { get; set; }

    /// <summary>Last real error from the provider, shown on the card.</summary>
    public string? LastError { get; set; }

    /// <summary>True when the account used the user's own OAuth client id/secret.</summary>
    public bool UsesCustomClient { get; set; }

    /// <summary>The user's own OAuth client id when <see cref="UsesCustomClient"/> is set (not a secret).</summary>
    public string? CustomClientId { get; set; }

    /// <summary>Domain chosen for TeraBox (or another provider that has a region/domain option).</summary>
    public string? Domain { get; set; }

    /// <summary>Free space according to the last quota query, or null when the provider cannot report it.</summary>
    public long? QuotaFreeBytes =>
        QuotaTotalBytes.HasValue && QuotaUsedBytes.HasValue
            ? Math.Max(0, QuotaTotalBytes.Value - QuotaUsedBytes.Value)
            : null;

    /// <summary>0..1 usage, or null when the provider does not report quota.</summary>
    public double? UsageFraction =>
        QuotaTotalBytes is > 0 && QuotaUsedBytes.HasValue
            ? Math.Clamp((double)QuotaUsedBytes.Value / QuotaTotalBytes.Value, 0d, 1d)
            : null;

    /// <summary>Quota text for the card, e.g. "42.1 GB of 1.0 TB used".</summary>
    public string QuotaText
    {
        get
        {
            if (!QuotaTotalBytes.HasValue || !QuotaUsedBytes.HasValue)
            {
                return "Space not reported yet";
            }

            return ByteSize.Format(QuotaUsedBytes.Value) + " of " + ByteSize.Format(QuotaTotalBytes.Value) + " used";
        }
    }

    /// <summary>Free space text for the card, or "Unavailable".</summary>
    public string FreeText => QuotaFreeBytes.HasValue ? ByteSize.Format(QuotaFreeBytes.Value) + " free" : "Space unavailable";

    public ProviderDefinition? Definition => ProviderCatalog.Get(ProviderId);

    public string ProviderName => Definition?.DisplayName ?? ProviderId;

    public bool IsExperimental => Definition?.IsExperimental ?? false;

    public string RcloneTarget => string.IsNullOrWhiteSpace(RemotePath) ? RemoteName + ":" : RemoteName + ":" + RemotePath;

    public CloudAccount Clone() => new()
    {
        Id = Id,
        ProviderId = ProviderId,
        DisplayName = DisplayName,
        RemoteName = RemoteName,
        RemotePath = RemotePath,
        PreferredDriveLetter = PreferredDriveLetter,
        AutoMount = AutoMount,
        AccountLabel = AccountLabel,
        CreatedUtc = CreatedUtc,
        LastConnectedUtc = LastConnectedUtc,
        LastQuotaUtc = LastQuotaUtc,
        QuotaUsedBytes = QuotaUsedBytes,
        QuotaTotalBytes = QuotaTotalBytes,
        NeedsReconnect = NeedsReconnect,
        LastError = LastError,
        UsesCustomClient = UsesCustomClient,
        CustomClientId = CustomClientId,
        Domain = Domain
    };

    public override string ToString() => DisplayName + " (" + ProviderId + ", " + RcloneTarget + ")";

    /// <summary>Sanity check used when loading accounts.json.</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Id) &&
                           !string.IsNullOrWhiteSpace(RemoteName) &&
                           ProviderCatalog.Get(ProviderId) is not null;
}
