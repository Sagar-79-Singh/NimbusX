using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NimbusX.App;
using NimbusX.Cache;
using NimbusX.Core;
using NimbusX.Core.Config;
using NimbusX.Core.Io;
using NimbusX.Core.Logging;
using NimbusX.Core.Text;
using NimbusX.Diagnostics;
using NimbusX.Games;
using NimbusX.Mount;
using NimbusX.Providers;
using NimbusX.Streaming;

namespace NimbusX.Cli;

/// <summary>
/// Implements <c>cpx</c>. Every command either reports a measured fact or does the same work the desktop app does,
/// through the same services, so behaviour can never drift between the two front ends.
/// </summary>
public sealed class CliApplication
{
    public const int ExitOk = 0;
    public const int ExitFailure = 1;
    public const int ExitUsage = 2;
    public const int ExitNotReady = 3;

    private readonly TextWriter _output;
    private readonly TextReader _input;
    private readonly string? _dataRoot;

    public CliApplication(TextWriter output, TextReader input, string? dataRoot = null)
    {
        _output = output ?? Console.Out;
        _input = input ?? Console.In;
        _dataRoot = dataRoot;
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!CommandLine.TryParse(args, out ParsedCommand? command, out string? parseError) || command is null)
        {
            if (!string.IsNullOrWhiteSpace(parseError))
            {
                Write("Error: " + parseError);
            }

            CommandLine.PrintUsage();
            return ExitUsage;
        }

        if (command.Has("help"))
        {
            CommandLine.PrintUsage();
            return ExitOk;
        }

        try
        {
            return command.Command switch
            {
                "version" => CommandVersion(command),
                "help" or "--help" or "-h" => CommandHelp(),
                "doctor" => await CommandDoctorAsync(command, cancellationToken).ConfigureAwait(false),
                "status" => await CommandStatusAsync(command, cancellationToken).ConfigureAwait(false),
                "mounts" => await CommandMountsAsync(command, cancellationToken).ConfigureAwait(false),
                "mount" => await CommandMountAsync(command, cancellationToken).ConfigureAwait(false),
                "unmount" => await CommandUnmountAsync(command, cancellationToken).ConfigureAwait(false),
                "accounts" => await CommandAccountsAsync(command, cancellationToken).ConfigureAwait(false),
                "cache" => await CommandCacheAsync(command, cancellationToken).ConfigureAwait(false),
                "games" => await CommandGamesAsync(command, cancellationToken).ConfigureAwait(false),
                "stream" => await CommandStreamAsync(command, cancellationToken).ConfigureAwait(false),
                "diag" => await CommandDiagAsync(command, cancellationToken).ConfigureAwait(false),
                "deps" => await CommandDepsAsync(command, cancellationToken).ConfigureAwait(false),
                "logs" => CommandLogs(command),
                _ => Unknown(command.Command)
            };
        }
        catch (OperationCanceledException)
        {
            Write("Cancelled.");
            return ExitFailure;
        }
        catch (Exception exception)
        {
            Write("Error: " + SecretRedactor.Redact(exception.Message));
            if (command.Has("verbose"))
            {
                Write(SecretRedactor.Redact(exception.ToString()));
            }

            return ExitFailure;
        }
    }

    private int CommandHelp()
    {
        CommandLine.PrintUsage();
        return ExitOk;
    }

    private int Unknown(string command)
    {
        Write("Unknown command '" + command + "'.");
        CommandLine.PrintUsage();
        return ExitUsage;
    }

    private int CommandVersion(ParsedCommand command)
    {
        if (command.Has("json"))
        {
            WriteJson(new JsonObject
            {
                ["name"] = AppInfo.Name,
                ["cli"] = AppInfo.CliName,
                ["version"] = AppInfo.Version,
                ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription
            });
            return ExitOk;
        }

        Write(AppInfo.Name + " " + AppInfo.Version + " (" + AppInfo.CliName + ")");
        Write(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription + " on " + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        return ExitOk;
    }

    private async Task<int> CommandDoctorAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        await using AppServices services = AppServices.Create(_dataRoot);
        DiagnosticsSnapshot snapshot = await services.Diagnostics.CollectAsync(cancellationToken).ConfigureAwait(false);

        if (command.Has("json"))
        {
            WriteJson(SnapshotToJson(snapshot));
        }
        else
        {
            Write(snapshot.Headline);
            Write(string.Empty);
            foreach (DependencyReport dependency in snapshot.Dependencies)
            {
                Write("[" + (dependency.Usable ? "ready" : "missing") + "] " + dependency.Name + " " + dependency.Expected +
                      " (found: " + (dependency.Installed ?? "nothing") + ")");
            }

            Write(string.Empty);
            foreach (DiagnosticCheck check in snapshot.Checks)
            {
                Write("[" + check.Severity.ToString().ToLowerInvariant() + "] " + check.Area + ": " + check.Title);
                Write("        " + check.Detail);
                if (!string.IsNullOrWhiteSpace(check.Fix))
                {
                    Write("        fix: " + check.Fix);
                }
            }
        }

        return snapshot.Healthy ? ExitOk : ExitNotReady;
    }

    private async Task<int> CommandStatusAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        await using AppServices services = AppServices.Create(_dataRoot);
        IReadOnlyList<MountSnapshot> mounts = services.Mounts.Snapshots;
        CacheStatus cache = services.CacheService.Refresh();
        IReadOnlyList<RunningGame> running = services.Launcher.Running;

        if (command.Has("json"))
        {
            WriteJson(new JsonObject
            {
                ["mounts"] = new JsonArray(mounts.Select(MountToJson).ToArray()),
                ["cache"] = new JsonObject
                {
                    ["root"] = cache.CacheRoot ?? string.Empty,
                    ["budgetBytes"] = cache.Plan.TotalBudgetBytes,
                    ["usedBytes"] = cache.TotalPhysicalBytes,
                    ["description"] = cache.Description,
                    ["prefetchPaused"] = cache.PrefetchPaused,
                    ["level"] = cache.Safety.Level.ToString()
                },
                ["runningGames"] = new JsonArray(running.Select(g => (JsonNode)new JsonObject
                {
                    ["gameId"] = g.GameId,
                    ["name"] = g.Name,
                    ["processId"] = g.ProcessId
                }).ToArray())
            });
            return ExitOk;
        }

        Write("Mounts: " + mounts.Count);
        foreach (MountSnapshot mount in mounts)
        {
            Write("  " + mount.DriveLetter + ":  " + mount.DisplayName + "  " + mount.Phase +
                  (string.IsNullOrWhiteSpace(mount.LastError) ? string.Empty : "  (" + mount.LastError + ")"));
        }

        Write("Cache:  " + cache.Description);
        Write("Prefetch: " + (cache.PrefetchPaused ? "paused (" + cache.Safety.Explanation + ")" : "active"));
        Write("Games running: " + running.Count);
        foreach (RunningGame game in running)
        {
            Write("  " + game.Name + " (pid " + game.ProcessId + ", " + (int)game.Uptime.TotalSeconds + " s)");
        }

        return ExitOk;
    }

    private async Task<int> CommandMountsAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        await using AppServices services = AppServices.Create(_dataRoot);
        IReadOnlyList<MountSnapshot> mounts = services.Mounts.Snapshots;

        if (command.Has("json"))
        {
            WriteJson(new JsonArray(mounts.Select(MountToJson).ToArray()));
            return ExitOk;
        }

        if (mounts.Count == 0)
        {
            Write("No drives are mounted.");
            return ExitOk;
        }

        Write("Drive  Account                     Phase     Pid     Restarts  Error");
        foreach (MountSnapshot mount in mounts)
        {
            Write(
                (mount.DriveLetter + ":").PadRight(6) + " " +
                Truncate(mount.DisplayName, 28).PadRight(28) + " " +
                mount.Phase.ToString().PadRight(9) + " " +
                (mount.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "-").PadRight(7) + " " +
                mount.RestartCount.ToString(CultureInfo.InvariantCulture).PadRight(9) + " " +
                (mount.LastError ?? string.Empty));
        }

        return ExitOk;
    }

    private async Task<int> CommandMountAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        string target = command.Positionals.FirstOrDefault() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(target))
        {
            Write("Usage: cpx mount <account|all>");
            return ExitUsage;
        }

        AppHost? host = AppHost.TryCreate(_dataRoot, logger: null, out int? owner);
        if (host is null)
        {
            Write("Another NimbusX process already owns the data folder (pid " + (owner?.ToString(CultureInfo.InvariantCulture) ?? "unknown") + ").");
            Write("Use that window, or close it and run this command again.");
            return ExitNotReady;
        }

        try
        {
            StartupReport startup = await host.StartAsync(autoMount: false, cancellationToken).ConfigureAwait(false);
            if (!startup.CanMount)
            {
                Write(startup.Headline);
                foreach (StartupStep step in startup.Steps.Where(s => !s.Success))
                {
                    Write("  " + step.Name + ": " + step.Detail);
                    if (!string.IsNullOrWhiteSpace(step.Fix))
                    {
                        Write("    fix: " + step.Fix);
                    }
                }

                return ExitNotReady;
            }

            IReadOnlyList<CloudAccount> accounts = host.Services.Accounts.Accounts;
            IEnumerable<CloudAccount> targets = target.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? accounts
                : accounts.Where(a => Matches(a, target));

            List<CloudAccount> list = targets.ToList();
            if (list.Count == 0)
            {
                Write("No account matches '" + target + "'.");
                return ExitFailure;
            }

            int failures = 0;
            foreach (CloudAccount account in list)
            {
                CloudAccount? found = host.Services.Accounts.Find(account.Id);
                if (found is null)
                {
                    continue;
                }

                ProviderCatalog.TryGet(found.ProviderId, out ProviderDefinition? definition);
                RcloneFlavor flavor = definition is not null && definition.RequiresTeraBoxBuild ? RcloneFlavor.TeraBox : RcloneFlavor.Standard;

                MountRequest request = new(found.Id, found.DisplayName, found.ProviderId, found.RemoteName)
                {
                    RemotePath = found.RemotePath,
                    PreferredDriveLetter = string.IsNullOrWhiteSpace(found.PreferredDriveLetter) ? null : found.PreferredDriveLetter![0],
                    Flavor = flavor,
                    VolumeName = AppInfo.Name + " - " + found.DisplayName,
                    CacheBudgetBytes = host.Services.Cache.BudgetFor(found.Id),
                    CacheMinFreeSpaceBytes = host.Services.Settings.Current.ProtectedFreeSpaceBytes
                };

                MountResult result = await host.Services.Mounts.MountAsync(request, cancellationToken).ConfigureAwait(false);
                if (result.Success && result.Snapshot is not null)
                {
                    Write("Mounted " + found.DisplayName + " on " + result.Snapshot.DriveLetter + ":");
                }
                else
                {
                    failures++;
                    Write("Failed to mount " + found.DisplayName + ": " + result.Error);
                    if (!string.IsNullOrWhiteSpace(result.Fix))
                    {
                        Write("  fix: " + result.Fix);
                    }
                }
            }

            return failures == 0 ? ExitOk : ExitFailure;
        }
        finally
        {
            await host.ShutdownAsync().ConfigureAwait(false);
            await host.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<int> CommandUnmountAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        string target = command.Positionals.FirstOrDefault() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(target))
        {
            Write("Usage: cpx unmount <account|all>");
            return ExitUsage;
        }

        AppHost? host = AppHost.TryCreate(_dataRoot, logger: null, out int? owner);
        if (host is null)
        {
            Write("Another NimbusX process already owns the data folder (pid " + (owner?.ToString(CultureInfo.InvariantCulture) ?? "unknown") + ").");
            Write("Unmount from that window instead.");
            return ExitNotReady;
        }

        try
        {
            await host.StartAsync(autoMount: false, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<MountSnapshot> mounts = host.Services.Mounts.Snapshots;

            IEnumerable<MountSnapshot> targets = target.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? mounts
                : mounts.Where(m => Matches(m.AccountId, target) || Matches(m.DisplayName, target) || Matches(m.DriveLetter, target));

            int unmounted = 0;
            foreach (MountSnapshot mount in targets.ToList())
            {
                bool ok = await host.Services.Mounts.UnmountAsync(mount.AccountId, cancellationToken).ConfigureAwait(false);
                Write((ok ? "Unmounted " : "Could not unmount ") + mount.DisplayName + " (" + mount.DriveLetter + ":)");
                if (ok)
                {
                    unmounted++;
                }
            }

            if (unmounted == 0)
            {
                Write("Nothing matched '" + target + "'.");
                return ExitFailure;
            }

            return ExitOk;
        }
        finally
        {
            await host.ShutdownAsync().ConfigureAwait(false);
            await host.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<int> CommandAccountsAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        string sub = command.Positionals.FirstOrDefault()?.ToLowerInvariant() ?? "list";

        await using AppServices services = AppServices.Create(_dataRoot);

        switch (sub)
        {
            case "list":
            {
                IReadOnlyList<CloudAccount> accounts = services.Accounts.Accounts;
                if (command.Has("json"))
                {
                    WriteJson(new JsonArray(accounts.Select(a => (JsonNode)new JsonObject
                    {
                        ["id"] = a.Id,
                        ["provider"] = a.ProviderId,
                        ["displayName"] = a.DisplayName,
                        ["remote"] = a.RemoteName,
                        ["remotePath"] = a.RemotePath,
                        ["quotaTotal"] = a.QuotaTotalBytes,
                        ["quotaUsed"] = a.QuotaUsedBytes,
                        ["needsReconnect"] = a.NeedsReconnect
                    }).ToArray()));
                    return ExitOk;
                }

                if (accounts.Count == 0)
                {
                    Write("No accounts yet. Use 'cpx accounts add <provider>' or the desktop app.");
                    return ExitOk;
                }

                Write("Id                                     Provider  Name                 Quota           Status");
                foreach (CloudAccount account in accounts)
                {
                    Write(
                        account.Id.PadRight(38) + " " +
                        account.ProviderId.PadRight(9) + " " +
                        Truncate(account.DisplayName, 20).PadRight(20) + " " +
                        (account.QuotaText ?? "Unavailable").PadRight(15) + " " +
                        (account.NeedsReconnect ? "reconnect needed" : "ok"));
                }

                return ExitOk;
            }

            case "add":
            {
                string providerId = command.Positionals.Skip(1).FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(providerId))
                {
                    Write("Available providers: " + string.Join(", ", ProviderCatalog.All.Select(p => p.Id)));
                    Write("Usage: cpx accounts add <provider>");
                    return ExitUsage;
                }

                if (!ProviderCatalog.TryGet(providerId, out ProviderDefinition? provider))
                {
                    Write("Unknown provider '" + providerId + "'. Available: " + string.Join(", ", ProviderCatalog.All.Select(p => p.Id)));
                    return ExitUsage;
                }

                if (provider.ConnectKind == ProviderConnectKind.CookieCapture || provider.RequiresTeraBoxBuild)
                {
                    Write(provider.DisplayName + " needs the embedded login window, which only exists in the desktop app.");
                    Write("Use Cloud Drives -> Add account -> " + provider.DisplayName + ".");
                    return ExitUsage;
                }

                if (provider.ConnectKind == ProviderConnectKind.OAuth)
                {
                    Write(provider.DisplayName + " uses a browser sign-in. Open the URL that appears and finish signing in.");
                }

                ConnectInputs inputs = new(provider.Id);
                foreach (ProviderField field in provider.Fields)
                {
                    string? value = Prompt(field);
                    if (value is not null)
                    {
                        inputs.Values[field.Name] = value;
                    }
                }

                Progress<ConnectProgress> progress = new(p => Write("  " + p.Phase + ": " + p.Message));
                ConnectResult result = await services.Accounts.ConnectAsync(inputs, progress, cancellationToken).ConfigureAwait(false);

                if (result.Success && result.Account is not null)
                {
                    Write("Connected " + result.Account.DisplayName + " (" + result.Account.QuotaText + ").");
                    return ExitOk;
                }

                Write("Could not connect: " + result.Error);
                if (!string.IsNullOrWhiteSpace(result.Fix))
                {
                    Write("  fix: " + result.Fix);
                }

                if (!string.IsNullOrWhiteSpace(result.AuthUrl))
                {
                    Write("  sign in at: " + result.AuthUrl);
                }

                return ExitFailure;
            }

            case "remove":
            {
                string id = command.Positionals.Skip(1).FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(id))
                {
                    Write("Usage: cpx accounts remove <account>");
                    return ExitUsage;
                }

                CloudAccount? account = services.Accounts.Accounts.FirstOrDefault(a => Matches(a, id));
                if (account is null)
                {
                    Write("No account matches '" + id + "'.");
                    return ExitFailure;
                }

                bool removed = await services.Accounts.DisconnectAsync(account.Id, cancellationToken).ConfigureAwait(false);
                Write(removed ? "Removed " + account.DisplayName + "." : "Could not remove " + account.DisplayName + ".");
                return removed ? ExitOk : ExitFailure;
            }

            case "quota":
            {
                string id = command.Positionals.Skip(1).FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(id))
                {
                    await services.Accounts.RefreshAllQuotasAsync(cancellationToken).ConfigureAwait(false);
                    foreach (CloudAccount account in services.Accounts.Accounts)
                    {
                        Write(account.DisplayName + ": " + (account.QuotaText ?? "Unavailable"));
                    }

                    return ExitOk;
                }

                CloudAccount? account = services.Accounts.Accounts.FirstOrDefault(a => Matches(a, id));
                if (account is null)
                {
                    Write("No account matches '" + id + "'.");
                    return ExitFailure;
                }

                QuotaRefreshResult result = await services.Accounts.RefreshQuotaAsync(account.Id, cancellationToken).ConfigureAwait(false);
                if (result.Success)
                {
                    Write(account.DisplayName + ": " + (result.Total is long total && result.Used is long used
                        ? ByteSize.Format(used) + " of " + ByteSize.Format(total)
                        : "quota unavailable for this provider"));
                    return ExitOk;
                }

                Write("Quota refresh failed: " + result.Error);
                return ExitFailure;
            }

            default:
                Write("Unknown accounts command '" + sub + "'. Try: list, add, remove, quota.");
                return ExitUsage;
        }
    }

    private async Task<int> CommandCacheAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        string sub = command.Positionals.FirstOrDefault()?.ToLowerInvariant() ?? "status";
        await using AppServices services = AppServices.Create(_dataRoot);

        switch (sub)
        {
            case "status":
            {
                CacheStatus status = services.CacheService.Refresh();
                if (command.Has("json"))
                {
                    WriteJson(new JsonObject
                    {
                        ["root"] = status.CacheRoot ?? string.Empty,
                        ["budgetBytes"] = status.Plan.TotalBudgetBytes,
                        ["physicalBytes"] = status.TotalPhysicalBytes,
                        ["logicalBytes"] = status.TotalLogicalBytes,
                        ["paused"] = status.PrefetchPaused,
                        ["level"] = status.Safety.Level.ToString(),
                        ["shares"] = new JsonObject(status.Plan.Shares.Select(kv =>
                            new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value)))
                    });
                    return ExitOk;
                }

                Write(status.Description);
                Write("Budget: " + status.BudgetText + ", used: " + status.UsedText + ", free: " + status.FreeText);
                Write(status.Plan.Describe());
                Write(status.Safety.Explanation);
                foreach (CacheUsage usage in status.Usages)
                {
                    Write("  " + usage.CacheDirectory + ": " + usage.Summary);
                }

                return ExitOk;
            }

            case "set":
            {
                string value = command.Positionals.Skip(1).FirstOrDefault() ?? string.Empty;
                if (!int.TryParse(value, out int gigabytes))
                {
                    Write("Usage: cpx cache set <gigabytes> (10, 20, 30 or 40)");
                    return ExitUsage;
                }

                bool ok = services.CacheService.SetBudgetPreset(gigabytes);
                Write(ok
                    ? "Cache budget set to " + gigabytes + " GB (clamped to " + ByteSize.Format(services.Settings.Current.CacheBudgetBytes) + " by free space)."
                    : "Unsupported cache preset. Use 10, 20, 30 or 40 GB.");
                return ok ? ExitOk : ExitUsage;
            }

            case "clear":
            {
                string target = command.Positionals.Skip(1).FirstOrDefault() ?? string.Empty;
                bool includePinned = command.Has("include-pinned");
                CacheClearResult result;

                if (target.Equals("all", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(target))
                {
                    result = services.CacheService.ClearEverything(includePinned);
                }
                else
                {
                    MountSnapshot? mount = services.Mounts.Snapshots.FirstOrDefault(m =>
                        Matches(m.AccountId, target) || Matches(m.DisplayName, target) || Matches(m.DriveLetter, target));
                    if (mount is null)
                    {
                        Write("No mounted drive matches '" + target + "'. Cache can only be cleared for a mounted account.");
                        return ExitFailure;
                    }

                    result = services.CacheService.ClearAccount(mount.AccountId, includePinned);
                }

                Write(result.Describe());
                foreach (string skipped in result.Skipped.Take(10))
                {
                    Write("  kept: " + skipped);
                }

                return result.Success ? ExitOk : ExitFailure;
            }

            case "pins":
            {
                string target = command.Positionals.Skip(1).FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(target))
                {
                    Write("Usage: cpx cache pins <account>");
                    return ExitUsage;
                }

                MountSnapshot? mount = services.Mounts.Snapshots.FirstOrDefault(m => Matches(m.AccountId, target) || Matches(m.DisplayName, target));
                if (mount is null)
                {
                    Write("That account is not mounted.");
                    return ExitFailure;
                }

                IReadOnlyList<PinnedEntry> pins = services.CacheService.Pins(mount.AccountId);
                if (pins.Count == 0)
                {
                    Write("No pinned files.");
                    return ExitOk;
                }

                foreach (PinnedEntry pin in pins)
                {
                    Write(ByteSize.Format(pin.Bytes).PadLeft(10) + "  " + pin.RelativePath);
                }

                return ExitOk;
            }

            case "pin":
            {
                string target = command.Positionals.Skip(1).FirstOrDefault() ?? string.Empty;
                string path = command.Positionals.Skip(2).FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(path))
                {
                    Write("Usage: cpx cache pin <account> <path-inside-the-drive>");
                    return ExitUsage;
                }

                MountSnapshot? mount = services.Mounts.Snapshots.FirstOrDefault(m => Matches(m.AccountId, target) || Matches(m.DisplayName, target));
                if (mount is null)
                {
                    Write("That account is not mounted.");
                    return ExitFailure;
                }

                PinnedEntry? entry = services.CacheService.PinPath(mount.AccountId, path);
                if (entry is null)
                {
                    Write("Could not pin '" + path + "'.");
                    return ExitFailure;
                }

                Write("Pinned " + entry.RelativePath + " (" + ByteSize.Format(entry.Bytes) + ").");
                return ExitOk;
            }

            default:
                Write("Unknown cache command '" + sub + "'. Try: status, set, clear, pins, pin.");
                return ExitUsage;
        }
    }

    private async Task<int> CommandGamesAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        string sub = command.Positionals.FirstOrDefault()?.ToLowerInvariant() ?? "list";
        await using AppServices services = AppServices.Create(_dataRoot);

        switch (sub)
        {
            case "list":
            {
                string filter = command.Positionals.Skip(1).FirstOrDefault() ?? string.Empty;
                IReadOnlyList<GameEntry> games = string.IsNullOrWhiteSpace(filter)
                    ? services.GameStore.Games
                    : services.GameStore.Games.Where(g => Matches(g.AccountId, filter) || Matches(g.Name, filter)).ToList();

                if (command.Has("json"))
                {
                    WriteJson(new JsonArray(games.Select(g => (JsonNode)new JsonObject
                    {
                        ["id"] = g.Id,
                        ["name"] = g.Name,
                        ["account"] = g.AccountId,
                        ["folder"] = g.RelativeFolder,
                        ["executable"] = g.ExecutableRelativePath,
                        ["confidence"] = g.Confidence.ToString(),
                        ["verified"] = g.VerifiedLaunch,
                        ["startupSeconds"] = g.LastStartupSeconds
                    }).ToArray()));
                    return ExitOk;
                }

                if (games.Count == 0)
                {
                    Write("No games in the library. Run 'cpx games scan <account>' first.");
                    return ExitOk;
                }

                foreach (GameEntry game in games)
                {
                    Write(
                        Truncate(game.Name, 32).PadRight(32) + " " +
                        game.Confidence.ToString().PadRight(10) + " " +
                        game.StartupText.PadLeft(8) + "  " +
                        game.StatusText + "  " + game.RelativeFolder);
                }

                return ExitOk;
            }

            case "scan":
            {
                string account = command.Positionals.Skip(1).FirstOrDefault() ?? string.Empty;
                string root = command.Positionals.Skip(2).FirstOrDefault() ?? string.Empty;

                MountSnapshot? mount = services.Mounts.Snapshots.FirstOrDefault(m =>
                    Matches(m.AccountId, account) || Matches(m.DisplayName, account) || Matches(m.DriveLetter, account));
                if (mount is null)
                {
                    Write("Mount that account first: the scan reads the mounted drive so it uses the same cache and speed.");
                    return ExitFailure;
                }

                GameScanResult scan = await services.Games.ScanAsync(mount.AccountId, root, new ScanOptions(), cancellationToken).ConfigureAwait(false);
                Write(scan.Describe());
                foreach (string warning in scan.Warnings)
                {
                    Write("  warning: " + warning);
                }

                if (scan.Candidates.Count == 0)
                {
                    return ExitOk;
                }

                foreach (GameCandidate candidate in scan.Candidates)
                {
                    GameEntry? stored = services.Games.AddCandidate(mount.AccountId, candidate);
                    Write("  + " + candidate.SuggestedName + "  [" + candidate.Confidence + "]  " + candidate.ExecutableRelativePath +
                          (stored is null ? "  (not stored)" : string.Empty));
                }

                return ExitOk;
            }

            case "play":
            {
                string id = command.Positionals.Skip(1).FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(id))
                {
                    Write("Usage: cpx games play <gameId>");
                    return ExitUsage;
                }

                Write("Launching and watching the process for 60 seconds...");
                LaunchResult result = await services.Games.PlayAsync(id, cancellationToken).ConfigureAwait(false);
                Write("Startup: " + result.StartupText + (result.WindowAppeared ? " (window detected)" : " (no window detected)"));
                Write("Verified: " + (result.Verified ? "yes" : "no"));
                if (!string.IsNullOrWhiteSpace(result.Error))
                {
                    Write("Error: " + result.Error);
                    Write("Fix:   " + (result.Fix ?? "Open Diagnostics and export the log."));
                }

                return result.Verified ? ExitOk : ExitFailure;
            }

            default:
                Write("Unknown games command '" + sub + "'. Try: list, scan, play.");
                return ExitUsage;
        }
    }

    private async Task<int> CommandStreamAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        string first = command.Positionals.FirstOrDefault() ?? string.Empty;
        await using AppServices services = AppServices.Create(_dataRoot);

        if (first.Equals("list", StringComparison.OrdinalIgnoreCase) && command.Positionals.Count == 1)
        {
            IReadOnlyList<StreamSession> sessions = services.Streams.Sessions;
            if (sessions.Count == 0)
            {
                Write("No active stream sessions.");
                return ExitOk;
            }

            foreach (StreamSession session in sessions)
            {
                Write(session.Id + "  " + session.DisplayName + "  " + session.RelativePath + "  " + session.ServedText);
            }

            return ExitOk;
        }

        if (first.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            string id = command.Positionals.Skip(1).FirstOrDefault() ?? string.Empty;
            bool stopped = services.Streams.Close(id);
            Write(stopped ? "Stopped session " + id + "." : "No session with that id.");
            return stopped ? ExitOk : ExitFailure;
        }

        string account = first;
        string path = command.Positionals.Skip(1).FirstOrDefault() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(path))
        {
            Write("Usage: cpx stream <account> <path-inside-the-drive>");
            Write("       cpx stream list | stop <session>");
            return ExitUsage;
        }

        MountSnapshot? mount = services.Mounts.Snapshots.FirstOrDefault(m =>
            Matches(m.AccountId, account) || Matches(m.DisplayName, account) || Matches(m.DriveLetter, account));
        if (mount is null)
        {
            Write("That drive is not mounted, so there is nothing to stream.");
            return ExitFailure;
        }

        StreamOpenResult result = services.Streams.Open(mount.AccountId, path);
        if (!result.Success || result.Session is null)
        {
            Write("Could not open the stream: " + result.Error);
            Write("  fix: " + result.Fix);
            return ExitFailure;
        }

        Write("Serving " + result.Session.RelativePath + " (" + result.Session.Media.Container + ", " + result.Session.Media.SizeText + ", duration " + result.Session.Media.DurationText + ")");
        Write("URL: " + result.Session.Url);
        Write("Session " + result.Session.Id + ". Press Ctrl+C to stop.");

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            services.Streams.Close(result.Session.Id);
            Write("Stream stopped.");
        }

        return ExitOk;
    }

    private async Task<int> CommandDiagAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        string sub = command.Positionals.FirstOrDefault()?.ToLowerInvariant() ?? "export";
        if (sub != "export")
        {
            Write("Unknown diag command '" + sub + "'. Try: export.");
            return ExitUsage;
        }

        await using AppServices services = AppServices.Create(_dataRoot);
        DiagnosticsSnapshot snapshot = await services.Diagnostics.CollectAsync(cancellationToken).ConfigureAwait(false);
        ExportResult export = await services.Exporter.ExportAsync(snapshot, command.Value("note"), cancellationToken).ConfigureAwait(false);

        Write(export.Describe());
        foreach (string warning in export.Warnings)
        {
            Write("  note: " + warning);
        }

        return ExitOk;
    }

    private async Task<int> CommandDepsAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        string sub = command.Positionals.FirstOrDefault()?.ToLowerInvariant() ?? "install";
        if (sub != "install")
        {
            Write("Unknown deps command '" + sub + "'. Try: install.");
            return ExitUsage;
        }

        await using AppServices services = AppServices.Create(_dataRoot);
        int failures = 0;

        foreach (RcloneFlavor flavor in new[] { RcloneFlavor.Standard, RcloneFlavor.TeraBox })
        {
            RcloneBinaryInfo current = await services.Provisioner.InspectAsync(flavor, cancellationToken).ConfigureAwait(false);
            if (current.IsUsable)
            {
                Write(DependencyVersions.DisplayName(flavor) + " already installed: " + current.Describe());
                continue;
            }

            Write("Installing " + DependencyVersions.DisplayName(flavor) + "...");
            Progress<double> progress = new(p => Write("  " + p.ToString("0", CultureInfo.InvariantCulture) + "%"));
            ProvisionResult result = await services.Provisioner
                .EnsureAsync(flavor, allowDownload: !command.Has("no-download"), progress, cancellationToken)
                .ConfigureAwait(false);

            Write((result.Success ? "Installed: " : "Failed: ") + result.Message);
            if (!result.Success)
            {
                failures++;
                if (!string.IsNullOrWhiteSpace(result.ManualInstructions))
                {
                    Write(result.ManualInstructions);
                }
            }
        }

        return failures == 0 ? ExitOk : ExitFailure;
    }

    private int CommandLogs(ParsedCommand command)
    {
        AppPaths paths = string.IsNullOrWhiteSpace(_dataRoot) ? AppPaths.CreateDefault() : new AppPaths(_dataRoot!);
        int tail = Math.Clamp(command.IntValue("tail", 40), 1, 2000);

        if (!Directory.Exists(paths.LogsDirectory))
        {
            Write("No log folder yet at " + paths.LogsDirectory);
            return ExitOk;
        }

        string? newest = Directory.EnumerateFiles(paths.LogsDirectory, "*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (newest is null)
        {
            Write("No log files yet.");
            return ExitOk;
        }

        string[] lines = File.ReadAllLines(newest);
        foreach (string line in lines.Skip(Math.Max(0, lines.Length - tail)))
        {
            Write(SecretRedactor.Redact(line));
        }

        return ExitOk;
    }

    private string? Prompt(ProviderField field)
    {
        Write(field.Label + (field.Required ? string.Empty : " (optional)") +
              (string.IsNullOrWhiteSpace(field.Help) ? string.Empty : " - " + field.Help));
        if (field.Choices.Count > 0)
        {
            Write("  choices: " + string.Join(", ", field.Choices));
        }

        if (!string.IsNullOrWhiteSpace(field.OpensUrl))
        {
            Write("  create credentials at: " + field.OpensUrl);
        }

        Write("  " + field.Label + ": ");

        string? value = field.IsSecret ? ReadHidden() : _input.ReadLine();
        if (string.IsNullOrWhiteSpace(value))
        {
            return field.DefaultValue;
        }

        return value!.Trim();
    }

    private string? ReadHidden()
    {
        if (Console.IsInputRedirected)
        {
            // Piped input (CI, scripts): read one line, never echo it.
            return _input.ReadLine();
        }

        StringBuilder builder = new();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Write(string.Empty);
                return builder.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                builder.Append(key.KeyChar);
            }
        }
    }

    private static bool Matches(CloudAccount account, string value) =>
        Matches(account.Id, value) ||
        Matches(account.DisplayName, value) ||
        Matches(account.ProviderId, value) ||
        Matches(account.RemoteName, value);

    private static bool Matches(string? candidate, string value) =>
        !string.IsNullOrWhiteSpace(candidate) &&
        (candidate!.StartsWith(value, StringComparison.OrdinalIgnoreCase) ||
         candidate.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static string Truncate(string value, int width) =>
        string.IsNullOrEmpty(value) ? string.Empty : (value.Length <= width ? value : value.Substring(0, width - 1) + "…");

    private static JsonObject MountToJson(MountSnapshot mount) => new()
    {
        ["accountId"] = mount.AccountId,
        ["displayName"] = mount.DisplayName,
        ["provider"] = mount.ProviderId,
        ["drive"] = mount.DriveLetter,
        ["phase"] = mount.Phase.ToString(),
        ["processId"] = mount.ProcessId,
        ["restarts"] = mount.RestartCount,
        ["lastError"] = mount.LastError,
        ["cacheDirectory"] = mount.CacheDirectory,
        ["isExperimental"] = mount.IsExperimental
    };

    private static JsonObject SnapshotToJson(DiagnosticsSnapshot snapshot) => new()
    {
        ["generatedUtc"] = snapshot.GeneratedUtc.ToString("O", CultureInfo.InvariantCulture),
        ["version"] = snapshot.AppVersion,
        ["os"] = snapshot.OperatingSystem,
        ["runtime"] = snapshot.DotNetVersion,
        ["healthy"] = snapshot.Healthy,
        ["headline"] = snapshot.Headline,
        ["checks"] = new JsonArray(snapshot.Checks.Select(c => (JsonNode)new JsonObject
        {
            ["area"] = c.Area,
            ["title"] = c.Title,
            ["severity"] = c.Severity.ToString(),
            ["detail"] = c.Detail,
            ["fix"] = c.Fix
        }).ToArray()),
        ["dependencies"] = new JsonArray(snapshot.Dependencies.Select(d => (JsonNode)new JsonObject
        {
            ["name"] = d.Name,
            ["expected"] = d.Expected,
            ["installed"] = d.Installed,
            ["usable"] = d.Usable,
            ["detail"] = d.Detail
        }).ToArray()),
        ["mounts"] = new JsonArray(snapshot.MountSummaries.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
        ["accounts"] = new JsonArray(snapshot.AccountSummaries.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
        ["cache"] = snapshot.CacheSummary
    };

    private void Write(string? text) => _output.WriteLine(text ?? string.Empty);

    private void WriteJson(JsonNode node) =>
        _output.WriteLine(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}
