using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NimbusX.Core;
using NimbusX.Core.Config;
using NimbusX.Core.Logging;
using NimbusX.Core.Security;
using NimbusX.Core.Text;
using NimbusX.Mount;

namespace NimbusX.Providers;

/// <summary>
/// Connects, reconnects, verifies and removes cloud accounts.
/// </summary>
/// <remarks>
/// Every connect follows the same honest path: create the remote in the encrypted rclone config, then <em>prove</em>
/// the login works by asking the provider for its quota and, if that backend has no quota support, by listing the
/// root folder. Only when that real call succeeds is the account saved. If anything fails the remote is removed
/// again so the user never sees a card for a login that does not work, and rclone's own error text is returned.
/// </remarks>
public sealed class AccountService
{
    private readonly AppPaths _paths;
    private readonly ISecretStore _secrets;
    private readonly AccountStore _store;
    private readonly RcloneConfigManager _config;
    private readonly RcloneControlDaemon _standardDaemon;
    private readonly RcloneControlDaemon _teraboxDaemon;
    private readonly RcloneProvisioner _provisioner;
    private readonly RcloneOneShot _oneShot;
    private readonly IBrowserLoginSessionFactory _browserSessions;
    private readonly IMountManager? _mounts;
    private readonly IAppLogger _logger;
    private readonly OAuthConnectFlow _oauthFlow;
    private readonly CredentialConnectFlow _credentialFlow;

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly object _gate = new();
    private readonly List<CloudAccount> _accounts = new();

    public AccountService(
        AppPaths paths,
        ISecretStore secrets,
        AccountStore store,
        RcloneConfigManager config,
        RcloneControlDaemon standardDaemon,
        RcloneControlDaemon teraboxDaemon,
        RcloneProvisioner provisioner,
        RcloneOneShot oneShot,
        IBrowserLoginSessionFactory browserSessions,
        IMountManager? mounts,
        IBrowserLauncher? browser,
        IAppLogger? logger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _standardDaemon = standardDaemon ?? throw new ArgumentNullException(nameof(standardDaemon));
        _teraboxDaemon = teraboxDaemon ?? throw new ArgumentNullException(nameof(teraboxDaemon));
        _provisioner = provisioner ?? throw new ArgumentNullException(nameof(provisioner));
        _oneShot = oneShot ?? throw new ArgumentNullException(nameof(oneShot));
        _browserSessions = browserSessions ?? throw new ArgumentNullException(nameof(browserSessions));
        _mounts = mounts;
        _logger = logger ?? NullLogger.Instance;
        _oauthFlow = new OAuthConnectFlow(browser, _logger);
        _credentialFlow = new CredentialConnectFlow(_logger);

        foreach (CloudAccount account in _store.Load())
        {
            _accounts.Add(account);
        }
    }

    /// <summary>Raised when an account is added or its stored state changed.</summary>
    public event EventHandler<CloudAccount>? AccountChanged;

    /// <summary>Raised after an account has been removed.</summary>
    public event EventHandler<string>? AccountRemoved;

    public IReadOnlyList<CloudAccount> Accounts
    {
        get
        {
            lock (_gate)
            {
                return _accounts.Select(a => a.Clone()).ToArray();
            }
        }
    }

    public CloudAccount? Find(string accountId)
    {
        lock (_gate)
        {
            return _accounts.FirstOrDefault(a => string.Equals(a.Id, accountId, StringComparison.OrdinalIgnoreCase))?.Clone();
        }
    }

    /// <summary>Connects a new account from the values collected by the provider form.</summary>
    public async Task<ConnectResult> ConnectAsync(
        ConnectInputs inputs,
        IProgress<ConnectProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProviderDefinition provider = ProviderCatalog.GetRequired(inputs.ProviderId);
            List<string> steps = new();
            RcloneFlavor flavor = provider.RequiresTeraBoxBuild ? RcloneFlavor.TeraBox : RcloneFlavor.Standard;

            progress?.Report(new ConnectProgress(ConnectPhase.Preparing, "Checking " + provider.DisplayName + " support files..."));

            RcloneBinaryInfo binary = await _provisioner.InspectAsync(flavor, cancellationToken).ConfigureAwait(false);
            if (!binary.IsUsable)
            {
                return ConnectResult.Fail(
                    binary.Describe(),
                    "Open Settings -> Dependencies and press 'Install / repair' for " +
                    DependencyVersions.DisplayName(flavor) + ", then connect again.",
                    steps);
            }

            RcloneControlDaemon daemon = flavor == RcloneFlavor.TeraBox ? _teraboxDaemon : _standardDaemon;
            RcloneRcClient rc;
            try
            {
                rc = await daemon.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return ConnectResult.Fail(
                    "The rclone control daemon did not start: " + exception.Message,
                    "Open Settings -> Dependencies, repair rclone, then retry. Log: " + daemon.LogFilePath,
                    steps);
            }

            await _config.EnsureInitialisedAsync(rc, cancellationToken).ConfigureAwait(false);
            steps.Add("rclone control daemon ready, configuration is encrypted: " + (_config.IsConfigEncrypted() ? "yes" : "no"));

            CloudAccount account = CreateAccount(provider, inputs);
            string? customClientSecret = null;
            if (inputs.UseCustomClient && !string.IsNullOrWhiteSpace(inputs.ClientSecret))
            {
                customClientSecret = inputs.ClientSecret!;
            }

            try
            {
                bool connected = provider.ConnectKind == ProviderConnectKind.CookieCapture
                    ? await ConnectTeraboxAsync(rc, provider, account, inputs, steps, progress, cancellationToken).ConfigureAwait(false)
                    : await ConnectStandardAsync(rc, provider, account, inputs, steps, progress, cancellationToken).ConfigureAwait(false);

                if (!connected)
                {
                    await SafeDeleteRemoteAsync(rc, account.RemoteName, cancellationToken).ConfigureAwait(false);
                    return ConnectResult.Fail(
                        steps.Count > 0 ? steps[^1] : "The provider did not accept the login.",
                        FixFor(provider, steps.LastOrDefault()),
                        steps);
                }

                if (customClientSecret is not null)
                {
                    _secrets.Save(SecretKeys.OAuthClientSecret(account.Id), customClientSecret);
                    account.UsesCustomClient = true;
                }

                if (inputs.UseCustomClient && !string.IsNullOrWhiteSpace(inputs.ClientId))
                {
                    account.CustomClientId = inputs.ClientId!.Trim();
                }

                account.LastConnectedUtc = DateTimeOffset.UtcNow;
                account.NeedsReconnect = false;
                account.LastError = null;

                progress?.Report(new ConnectProgress(ConnectPhase.VerifyingAccess, "Reading the account's free space..."));

                string? cookie = provider.ConnectKind == ProviderConnectKind.CookieCapture
                    ? _secrets.TryLoad(SecretKeys.TeraboxCookie(account.Id))
                    : null;

                (bool ok, string? error, RemoteUsage? usage) verify = await _oneShot
                    .VerifyAccessAsync(flavor, account.RcloneTarget, cookie is null ? null : account.RemoteName, cookie, cancellationToken)
                    .ConfigureAwait(false);

                if (!verify.ok)
                {
                    await SafeDeleteRemoteAsync(rc, account.RemoteName, cancellationToken).ConfigureAwait(false);
                    _secrets.Delete(SecretKeys.TeraboxCookie(account.Id));
                    steps.Add("verification failed: " + verify.error);
                    return ConnectResult.Fail(
                        "The provider login was saved but NimbusX could not read the account: " + verify.error,
                        FixFor(provider, verify.error),
                        steps);
                }

                ApplyUsage(account, verify.usage);
                if (verify.usage is null)
                {
                    steps.Add("the provider does not report free space (quota unavailable)");
                }
                else
                {
                    steps.Add("quota: " + account.QuotaText);
                }

                account.AccountLabel = await _oneShot
                    .ReadUserInfoAsync(flavor, account.RcloneTarget, cookie is null ? null : account.RemoteName, cookie, cancellationToken)
                    .ConfigureAwait(false);

                AddAccount(account);
                progress?.Report(new ConnectProgress(ConnectPhase.Done, account.DisplayName + " connected."));
                _logger.Info("Connected " + account.DisplayName + " (" + provider.DisplayName + ").", "Accounts");
                return ConnectResult.Ok(account, steps);
            }
            catch (Exception exception)
            {
                _logger.Error("Connecting " + provider.DisplayName + " failed.", "Accounts", exception);
                await SafeDeleteRemoteAsync(rc, account.RemoteName, cancellationToken).ConfigureAwait(false);
                _secrets.Delete(SecretKeys.TeraboxCookie(account.Id));
                progress?.Report(new ConnectProgress(ConnectPhase.Failed, exception.Message));
                return ConnectResult.Fail(
                    exception.Message,
                    "Try again. If it keeps failing, export diagnostics from the Diagnostics page.",
                    steps);
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>Re-runs the sign-in for an existing account (expired token or expired TeraBox cookie).</summary>
    public async Task<ConnectResult> ReconnectAsync(
        string accountId,
        ConnectInputs? inputs,
        IProgress<ConnectProgress>? progress,
        CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(accountId, nameof(accountId));
        CloudAccount account = Find(accountId)
            ?? throw new InvalidOperationException("Account '" + accountId + "' does not exist.");
        ProviderDefinition provider = ProviderCatalog.GetRequired(account.ProviderId);
        RcloneFlavor flavor = provider.RequiresTeraBoxBuild ? RcloneFlavor.TeraBox : RcloneFlavor.Standard;
        List<string> steps = new() { "reconnecting " + account.DisplayName };

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RcloneControlDaemon daemon = flavor == RcloneFlavor.TeraBox ? _teraboxDaemon : _standardDaemon;
            RcloneRcClient rc = await daemon.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            await _config.EnsureInitialisedAsync(rc, cancellationToken).ConfigureAwait(false);

            if (provider.ConnectKind == ProviderConnectKind.CookieCapture)
            {
                if (_mounts is not null && _mounts.IsReady(accountId))
                {
                    await _mounts.UnmountAsync(accountId, cancellationToken).ConfigureAwait(false);
                }

                string? header = await CaptureTeraboxCookieAsync(account, inputs, progress, cancellationToken).ConfigureAwait(false);
                if (header is null)
                {
                    return ConnectResult.Fail("The TeraBox sign-in did not produce a session cookie.", "Sign in again and wait for your files to appear before pressing Done.", steps);
                }

                _secrets.Save(SecretKeys.TeraboxCookie(accountId), header);
                (bool ok, string? error, RemoteUsage? usage) verify = await _oneShot
                    .VerifyAccessAsync(flavor, account.RcloneTarget, account.RemoteName, header, cancellationToken)
                    .ConfigureAwait(false);

                if (!ok)
                {
                    return ConnectResult.Fail(
                        "TeraBox rejected the new session: " + verify.error,
                        "Sign in again with the account that owns this TeraBox drive.",
                        steps);
                }

                ApplyUsage(account, verify.usage);
                account.NeedsReconnect = false;
                account.LastError = null;
                account.LastConnectedUtc = DateTimeOffset.UtcNow;
                UpdateAccount(account);
                steps.Add("session cookie refreshed");
                return ConnectResult.Ok(account, steps);
            }

            if (provider.ConnectKind == ProviderConnectKind.OAuth)
            {
                bool useCustom = account.UsesCustomClient;
                string? clientId = useCustom ? inputs?.ClientId ?? account.CustomClientId : null;
                string? clientSecret = useCustom ? _secrets.TryLoad(SecretKeys.OAuthClientSecret(accountId)) : null;

                StateMachineResult result = await _oauthFlow
                    .ConnectAsync(rc, provider, account.RemoteName, useCustom, clientId, clientSecret, progress, cancellationToken)
                    .ConfigureAwait(false);

                steps.AddRange(result.Steps);
                if (!result.Success)
                {
                    return ConnectResult.Fail(result.Error ?? "Sign-in failed.", FixFor(provider, result.Error), steps);
                }
            }
            else
            {
                if (inputs is null)
                {
                    return ConnectResult.Fail(
                        "The " + provider.DisplayName + " login needs its details again.",
                        "Enter the password in the connect form and press Reconnect.",
                        steps);
                }

                StateMachineResult result = await _credentialFlow
                    .ConnectAsync(rc, provider, account.RemoteName, inputs.Values, progress, cancellationToken)
                    .ConfigureAwait(false);
                steps.AddRange(result.Steps);
                if (!result.Success)
                {
                    return ConnectResult.Fail(result.Error ?? "The provider rejected the login.", FixFor(provider, result.Error), steps);
                }
            }

            progress?.Report(new ConnectProgress(ConnectPhase.VerifyingAccess, "Checking the login..."));
            (bool ok2, string? error2, RemoteUsage? usage2) verified = await _oneShot
                .VerifyAccessAsync(flavor, account.RcloneTarget, null, null, cancellationToken)
                .ConfigureAwait(false);

            if (!verified.ok2)
            {
                return ConnectResult.Fail(
                    "NimbusX could not read the account after signing in: " + verified.error2,
                    FixFor(provider, verified.error2),
                    steps);
            }

            ApplyUsage(account, verified.usage2);
            account.NeedsReconnect = false;
            account.LastError = null;
            account.LastConnectedUtc = DateTimeOffset.UtcNow;
            UpdateAccount(account);
            return ConnectResult.Ok(account, steps);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>Unmounts and forgets an account: removes the remote, the secrets and the stored record.</summary>
    public async Task<bool> DisconnectAsync(string accountId, CancellationToken cancellationToken)
    {
        CloudAccount? account = Find(accountId);
        if (account is null)
        {
            return false;
        }

        if (_mounts is not null)
        {
            try
            {
                await _mounts.UnmountAsync(accountId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.Warn("Unmount before disconnect failed: " + exception.Message, "Accounts");
            }
        }

        try
        {
            RcloneRcClient rc = await _standardDaemon.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            await _config.DeleteRemoteAsync(rc, account.RemoteName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Warn("Could not remove the rclone remote: " + exception.Message, "Accounts");
        }

        _secrets.Delete(SecretKeys.TeraboxCookie(accountId));
        _secrets.Delete(SecretKeys.MegaPassword(accountId));
        _secrets.Delete(SecretKeys.ProviderPassword(accountId));
        _secrets.Delete(SecretKeys.OAuthClientSecret(accountId));
        _secrets.Delete(SecretKeys.RcPassword(accountId));

        lock (_gate)
        {
            _accounts.RemoveAll(a => string.Equals(a.Id, accountId, StringComparison.OrdinalIgnoreCase));
        }

        Save();
        AccountRemoved?.Invoke(this, accountId);
        _logger.Info("Disconnected " + account.DisplayName + ".", "Accounts");
        return true;
    }

    /// <summary>Reads the quota for one account from the provider.</summary>
    public async Task<QuotaRefreshResult> RefreshQuotaAsync(string accountId, CancellationToken cancellationToken)
    {
        CloudAccount? account = Find(accountId);
        if (account is null)
        {
            return QuotaRefreshResult.Fail("The account no longer exists.");
        }

        ProviderDefinition? provider = account.Definition;
        RcloneFlavor flavor = provider?.RequiresTeraBoxBuild == true ? RcloneFlavor.TeraBox : RcloneFlavor.Standard;

        string? cookie = flavor == RcloneFlavor.TeraBox ? _secrets.TryLoad(SecretKeys.TeraboxCookie(accountId)) : null;
        string? remoteName = flavor == RcloneFlavor.TeraBox ? account.RemoteName : null;

        if (flavor == RcloneFlavor.TeraBox && string.IsNullOrWhiteSpace(cookie))
        {
            MarkNeedsReconnect(account, "The TeraBox session has expired.");
            return QuotaRefreshResult.Fail("The TeraBox session has expired. Press Reconnect.");
        }

        RemoteUsage? usage = await _oneShot
            .ReadQuotaAsync(flavor, account.RcloneTarget, remoteName, cookie, cancellationToken)
            .ConfigureAwait(false);

        if (usage is null)
        {
            // The quota endpoint may be unsupported even though the login works: prove it with a listing.
            OneShotResult listing = await _oneShot.RunAsync(
                flavor,
                new[] { "lsd", account.RcloneTarget, "--max-depth", "1", "--low-level-retries", "1", "--retries", "1" },
                remoteName,
                cookie,
                TimeSpan.FromSeconds(60),
                cancellationToken).ConfigureAwait(false);

            if (!listing.Success)
            {
                account.LastError = listing.ErrorText;
                if (TeraboxCookieBuilder.IsExpiredSessionError(listing.ErrorText) &&
                    flavor == RcloneFlavor.TeraBox)
                {
                    MarkNeedsReconnect(account, listing.ErrorText);
                }
                else
                {
                    UpdateAccount(account);
                }

                return QuotaRefreshResult.Fail(listing.ErrorText);
            }

            account.LastQuotaUtc = DateTimeOffset.UtcNow;
            account.LastError = null;
            UpdateAccount(account);
            return QuotaRefreshResult.Ok(null, null);
        }

        ApplyUsage(account, usage);
        account.NeedsReconnect = false;
        account.LastError = null;
        UpdateAccount(account);
        return QuotaRefreshResult.Ok(usage.Used, usage.Total);
    }

    /// <summary>Refreshes every account, best effort, in parallel.</summary>
    public async Task RefreshAllQuotasAsync(CancellationToken cancellationToken)
    {
        List<string> ids;
        lock (_gate)
        {
            ids = _accounts.Select(a => a.Id).ToList();
        }

        foreach (string id in ids)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await RefreshQuotaAsync(id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.Debug("Quota refresh failed for " + id + ": " + exception.Message, "Accounts");
            }
        }
    }

    /// <summary>Stores a modified account (drive letter, auto mount flag, display name).</summary>
    public void UpdateAccount(CloudAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        lock (_gate)
        {
            int index = _accounts.FindIndex(a => string.Equals(a.Id, account.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return;
            }

            _accounts[index] = account.Clone();
        }

        Save();
        AccountChanged?.Invoke(this, account.Clone());
    }

    /// <summary>Renames an account (the card title).</summary>
    public bool Rename(string accountId, string displayName)
    {
        CloudAccount? account = Find(accountId);
        if (account is null || string.IsNullOrWhiteSpace(displayName))
        {
            return false;
        }

        account.DisplayName = displayName.Trim();
        UpdateAccount(account);
        return true;
    }

    /// <summary>Sets whether the account mounts on startup.</summary>
    public bool SetAutoMount(string accountId, bool autoMount)
    {
        CloudAccount? account = Find(accountId);
        if (account is null)
        {
            return false;
        }

        account.AutoMount = autoMount;
        UpdateAccount(account);
        return true;
    }

    /// <summary>Records the drive letter that was actually granted to an account.</summary>
    public void SetDriveLetter(string accountId, string? driveLetter)
    {
        CloudAccount? account = Find(accountId);
        if (account is null)
        {
            return;
        }

        account.PreferredDriveLetter = driveLetter;
        UpdateAccount(account);
    }

    /// <summary>Secret key that holds the provider password for an account (used by the mount engine docs).</summary>
    public static string PasswordSecretKey(ProviderDefinition provider, string accountId) =>
        provider.Id switch
        {
            "mega" => SecretKeys.MegaPassword(accountId),
            "terabox" => SecretKeys.TeraboxCookie(accountId),
            _ => SecretKeys.ProviderPassword(accountId)
        };

    private async Task<bool> ConnectStandardAsync(
        RcloneRcClient rc,
        ProviderDefinition provider,
        CloudAccount account,
        ConnectInputs inputs,
        List<string> steps,
        IProgress<ConnectProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (provider.ConnectKind == ProviderConnectKind.OAuth)
        {
            StateMachineResult result = await _oauthFlow.ConnectAsync(
                rc,
                provider,
                account.RemoteName,
                inputs.UseCustomClient,
                inputs.ClientId,
                inputs.ClientSecret,
                progress,
                cancellationToken).ConfigureAwait(false);

            steps.AddRange(result.Steps);
            if (!result.Success)
            {
                steps.Add(result.Error ?? "The sign-in failed.");
                return false;
            }

            if (result.AuthUrl is not null)
            {
                steps.Add("browser approval URL: " + result.AuthUrl);
            }

            return true;
        }

        StateMachineResult credentialResult = await _credentialFlow.ConnectAsync(
            rc,
            provider,
            account.RemoteName,
            inputs.Values,
            progress,
            cancellationToken).ConfigureAwait(false);

        steps.AddRange(credentialResult.Steps);
        if (!credentialResult.Success)
        {
            steps.Add(credentialResult.Error ?? "The provider rejected the login.");
            return false;
        }

        // Keep the password in DPAPI protected storage as well, so a reconnect form can be pre-filled and so the
        // account survives even if rclone's config is rebuilt.
        string? password = inputs.Values.TryGetValue("pass", out string? pass) ? pass : null;
        password ??= inputs.Values.TryGetValue("password", out string? koofrPass) ? koofrPass : null;
        if (!string.IsNullOrEmpty(password))
        {
            _secrets.Save(PasswordSecretKey(provider, account.Id), password!);
        }

        return true;
    }

    private async Task<bool> ConnectTeraboxAsync(
        RcloneRcClient rc,
        ProviderDefinition provider,
        CloudAccount account,
        ConnectInputs inputs,
        List<string> steps,
        IProgress<ConnectProgress>? progress,
        CancellationToken cancellationToken)
    {
        string? cookie = await CaptureTeraboxCookieAsync(account, inputs, progress, cancellationToken).ConfigureAwait(false);
        if (cookie is null)
        {
            steps.Add("the TeraBox login window did not return a session cookie");
            return false;
        }

        string domain = inputs.Get("domain") ?? "https://www.terabox.com";
        Dictionary<string, string> formValues = new(StringComparer.OrdinalIgnoreCase) { ["domain"] = domain };
        Dictionary<string, string> extra = new(StringComparer.OrdinalIgnoreCase) { ["cookie"] = cookie, ["domain"] = domain };

        StateMachineResult result = await _credentialFlow
            .ConnectAsync(rc, provider, account.RemoteName, formValues, extra, progress, cancellationToken)
            .ConfigureAwait(false);

        steps.AddRange(result.Steps);
        if (!result.Success)
        {
            steps.Add(result.Error ?? "TeraBox rejected the session.");
            return false;
        }

        // Prove the session before removing the cookie from the config.
        (bool ok, string? error, _) = await _oneShot
            .VerifyAccessAsync(RcloneFlavor.TeraBox, account.RcloneTarget, null, null, cancellationToken)
            .ConfigureAwait(false);

        if (!ok)
        {
            steps.Add("TeraBox verification failed: " + error);
            return false;
        }

        // The cookie must not stay in rclone.conf: it is passed to rclone through the environment at mount time.
        try
        {
            await rc.CallAsync(
                "config/unset",
                new JsonObject
                {
                    ["name"] = account.RemoteName,
                    ["keys"] = new JsonArray("cookie")
                },
                TimeSpan.FromSeconds(20),
                cancellationToken).ConfigureAwait(false);
            steps.Add("session cookie removed from rclone.conf (kept only in DPAPI storage)");
        }
        catch (RcloneRcException exception)
        {
            _logger.Warn("Could not unset the TeraBox cookie: " + exception.Message, "Accounts");
        }

        _secrets.Save(SecretKeys.TeraboxCookie(account.Id), cookie);
        account.Domain = domain;
        return true;
    }

    private async Task<string?> CaptureTeraboxCookieAsync(
        CloudAccount account,
        ConnectInputs? inputs,
        IProgress<ConnectProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!_browserSessions.IsAvailable)
        {
            progress?.Report(new ConnectProgress(
                ConnectPhase.Failed,
                "The embedded login window needs the WebView2 runtime."));
            return null;
        }

        progress?.Report(new ConnectProgress(
            ConnectPhase.WaitingForUser,
            "Sign in to TeraBox in the NimbusX window, then press Done."));

        Uri start = TeraboxCookieBuilder.LoginUri(inputs?.Get("domain") ?? account.Domain);
        await using IBrowserLoginSession session = await _browserSessions
            .CreateAsync("Connect TeraBox", cancellationToken).ConfigureAwait(false);

        bool completed = await session.ShowAsync(start, "Connect TeraBox", cancellationToken).ConfigureAwait(false);
        if (!completed)
        {
            return null;
        }

        List<BrowserCookie> cookies = new();
        foreach (string host in TeraboxCookieBuilder.CookieHosts())
        {
            cookies.AddRange(await session.GetCookiesAsync(host, cancellationToken).ConfigureAwait(false));
        }

        CookieBuildResult built = TeraboxCookieBuilder.Build(cookies, DateTimeOffset.UtcNow);
        if (!built.Success)
        {
            progress?.Report(new ConnectProgress(ConnectPhase.Failed, built.Error ?? "No session cookie."));
            _logger.Warn("TeraBox cookie capture failed: " + built.Error, "Accounts");
            return null;
        }

        return built.CookieHeader;
    }

    private async Task SafeDeleteRemoteAsync(RcloneRcClient rc, string remoteName, CancellationToken cancellationToken)
    {
        try
        {
            await rc.DeleteRemoteAsync(remoteName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Debug("Cleanup of remote '" + remoteName + "' failed: " + exception.Message, "Accounts");
        }
    }

    private CloudAccount CreateAccount(ProviderDefinition provider, ConnectInputs inputs)
    {
        string id = RandomSecrets.AccountId();
        string remoteName = "nx_" + RandomSecrets.Hex(6);
        while (_accounts.Any(a => string.Equals(a.RemoteName, remoteName, StringComparison.OrdinalIgnoreCase)))
        {
            remoteName = "nx_" + RandomSecrets.Hex(6);
        }

        string displayName = string.IsNullOrWhiteSpace(inputs.DisplayName)
            ? NextDisplayName(provider)
            : inputs.DisplayName!.Trim();

        return new CloudAccount
        {
            Id = id,
            ProviderId = provider.Id,
            DisplayName = displayName,
            RemoteName = remoteName,
            RemotePath = string.Empty,
            PreferredDriveLetter = string.IsNullOrWhiteSpace(inputs.PreferredDriveLetter) ? null : inputs.PreferredDriveLetter,
            AutoMount = inputs.AutoMount,
            CreatedUtc = DateTimeOffset.UtcNow,
            UsesCustomClient = inputs.UseCustomClient && provider.SupportsCustomClient,
            Domain = inputs.Get("domain")
        };
    }

    private string NextDisplayName(ProviderDefinition provider)
    {
        List<string> existing;
        lock (_gate)
        {
            existing = _accounts
                .Where(a => string.Equals(a.ProviderId, provider.Id, StringComparison.OrdinalIgnoreCase))
                .Select(a => a.DisplayName)
                .ToList();
        }

        if (!existing.Contains(provider.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            return provider.DisplayName;
        }

        for (int index = 2; index < 100; index++)
        {
            string candidate = provider.DisplayName + " " + index;
            if (!existing.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return provider.DisplayName + " " + TextFormat.Slug(RandomSecrets.Hex(3));
    }

    private void AddAccount(CloudAccount account)
    {
        lock (_gate)
        {
            _accounts.Add(account.Clone());
        }

        Save();
        AccountChanged?.Invoke(this, account.Clone());
    }

    private void MarkNeedsReconnect(CloudAccount account, string error)
    {
        account.NeedsReconnect = true;
        account.LastError = SecretRedactor.Redact(error);
        UpdateAccount(account);
    }

    private void ApplyUsage(CloudAccount account, RemoteUsage? usage)
    {
        if (usage is null)
        {
            account.LastQuotaUtc = DateTimeOffset.UtcNow;
            return;
        }

        account.QuotaUsedBytes = usage.Used;
        account.QuotaTotalBytes = usage.Total;
        account.LastQuotaUtc = usage.MeasuredUtc;
    }

    private void Save()
    {
        List<CloudAccount> snapshot;
        lock (_gate)
        {
            snapshot = _accounts.Select(a => a.Clone()).ToList();
        }

        _store.Save(snapshot);
    }

    /// <summary>Maps a provider error onto an actionable fix.</summary>
    public static string FixFor(ProviderDefinition provider, string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "Try again. If it keeps failing, export diagnostics from the Diagnostics page.";
        }

        string text = error!;
        if (provider.Id == "terabox")
        {
            return "Sign in again in the TeraBox window, and make sure the account actually owns this drive.";
        }

        if (text.Contains("401", StringComparison.Ordinal) ||
            text.Contains("403", StringComparison.Ordinal) ||
            text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
        {
            return "The provider rejected the saved login. Press Reconnect and sign in again.";
        }

        if (text.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("no such host", StringComparison.OrdinalIgnoreCase))
        {
            return "The provider could not be reached. Check your internet connection or VPN and retry.";
        }

        if (text.Contains("client_id", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("client_secret", StringComparison.OrdinalIgnoreCase))
        {
            return "Check the Advanced client id/secret in the connect dialog. Turn Advanced off to use rclone's built-in client.";
        }

        if (text.Contains("refused", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("blocked", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("captcha", StringComparison.OrdinalIgnoreCase))
        {
            return "The provider blocked the login attempt. Approve NimbusX in the provider's web console or wait a few minutes.";
        }

        return "Open Diagnostics and copy the rclone log tail; the message above is what " + provider.DisplayName + " returned.";
    }

    public void Dispose()
    {
        _connectLock.Dispose();
    }
}
