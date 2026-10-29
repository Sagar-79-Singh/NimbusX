using System;
using System.Collections.Generic;

namespace NimbusX.Cli;

/// <summary>A parsed <c>cpx</c> invocation: command, positional arguments and switches.</summary>
public sealed class ParsedCommand
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    public ParsedCommand(string command, IReadOnlyList<string> positionals, IReadOnlyDictionary<string, string?> options)
    {
        Command = command;
        Positionals = positionals;
        foreach (KeyValuePair<string, string?> option in options)
        {
            _options[option.Key] = option.Value;
        }
    }

    public string Command { get; }

    public IReadOnlyList<string> Positionals { get; }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Value(string name) => _options.TryGetValue(name, out string? value) ? value : null;

    public int IntValue(string name, int fallback) =>
        int.TryParse(Value(name), out int parsed) ? parsed : fallback;
}

/// <summary>
/// Minimal, dependency free argument parser. No secrets are ever accepted as command line values; the interactive
/// prompts read them from the console instead.
/// </summary>
public static class CommandLine
{
    private static readonly HashSet<string> FlagOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "help", "verbose", "all", "force", "include-pinned", "no-automount", "yes"
    };

    public static bool TryParse(string[] args, out ParsedCommand? parsed, out string? error)
    {
        parsed = null;
        error = null;

        if (args is null || args.Length == 0)
        {
            error = "No command given.";
            return false;
        }

        string command = args[0].Trim().ToLowerInvariant();
        List<string> positionals = new();
        Dictionary<string, string?> options = new(StringComparer.OrdinalIgnoreCase);

        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                string name = arg.Substring(2);
                string? value = null;
                int equals = name.IndexOf('=');
                if (equals > 0)
                {
                    value = name.Substring(equals + 1);
                    name = name.Substring(0, equals);
                }
                else if (!FlagOptions.Contains(name) && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = args[++i];
                }

                options[name] = value;
                continue;
            }

            positionals.Add(arg);
        }

        parsed = new ParsedCommand(command, positionals, options);
        return true;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("cpx - NimbusX command line");
        Console.WriteLine();
        Console.WriteLine("Usage: cpx <command> [arguments] [--options]");
        Console.WriteLine();
        Console.WriteLine("  version                              Show the NimbusX version");
        Console.WriteLine("  doctor [--json]                      Check WinFsp, rclone, cache and accounts");
        Console.WriteLine("  status [--json]                      Mounts, cache usage and running games");
        Console.WriteLine("  mounts [--json]                      List mounted drives");
        Console.WriteLine("  mount <account|all>                  Mount an account (or every auto-mount account)");
        Console.WriteLine("  unmount <account|all>                Unmount a drive");
        Console.WriteLine("  accounts [--json]                    List connected cloud accounts");
        Console.WriteLine("  accounts add <provider>              Connect a new account (prompts, never echoes secrets)");
        Console.WriteLine("  accounts remove <account>            Disconnect and forget an account");
        Console.WriteLine("  accounts quota [<account>]           Refresh quota for one or all accounts");
        Console.WriteLine("  cache status [--json]                Cache budget and real usage");
        Console.WriteLine("  cache set <gigabytes>                Set the cache budget (10, 20, 30 or 40)");
        Console.WriteLine("  cache clear <account|all>            Delete cache files (pinned files are kept)");
        Console.WriteLine("  cache pins <account>                 List pinned files");
        Console.WriteLine("  cache pin <account> <path>           Pin a file so it is never cleared");
        Console.WriteLine("  games scan <account> [<root>]        Find game folders on a mounted drive");
        Console.WriteLine("  games list [<account>]               Show the game library");
        Console.WriteLine("  games play <gameId>                  Launch a game and report the measured result");
        Console.WriteLine("  stream <account> <path>              Serve a file to a player over 127.0.0.1");
        Console.WriteLine("  stream list                          List active stream sessions");
        Console.WriteLine("  stream stop <session>                Stop a stream session");
        Console.WriteLine("  diag export [--note \"text\"]          Write a redacted diagnostics bundle");
        Console.WriteLine("  deps install                         Download and verify the pinned rclone builds");
        Console.WriteLine("  logs [--tail 40]                     Show the newest log lines");
        Console.WriteLine();
        Console.WriteLine("Exit codes: 0 success, 1 failure, 2 usage error, 3 NimbusX is not ready.");
    }
}
