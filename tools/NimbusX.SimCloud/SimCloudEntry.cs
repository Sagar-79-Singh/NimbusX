using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NimbusX.SimCloud;

/// <summary>
/// Starts a small WebDAV server over a local folder so a NimbusX mount can be tested end to end without a cloud
/// account: connect a WebDAV account to <c>http://127.0.0.1:port</c>, mount it, and every read, range request and
/// upload goes through the same code path a real provider uses.
/// </summary>
public static class SimCloudEntry
{
    public static async Task<int> Main(string[] args)
    {
        SimCloudOptions options = SimCloudOptions.Parse(args);
        if (options.ShowHelp)
        {
            SimCloudOptions.PrintHelp();
            return 0;
        }

        using CancellationTokenSource shutdown = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            using WebDavServer server = new(options);
            server.Start();

            Console.WriteLine("NimbusX SimCloud (test WebDAV server)");
            Console.WriteLine("  root     : " + options.Root);
            Console.WriteLine("  address  : http://127.0.0.1:" + server.Port + "/");
            Console.WriteLine("  user     : " + options.User);
            Console.WriteLine("  password : " + (options.Password.Length == 0 ? "(none)" : "(set, " + options.Password.Length + " characters)"));
            Console.WriteLine();
            Console.WriteLine("Add it in NimbusX: Cloud Drives -> Add account -> WebDAV");
            Console.WriteLine("  Server URL = http://127.0.0.1:" + server.Port + "/   vendor = other");
            Console.WriteLine();
            Console.WriteLine("Press Ctrl+C to stop.");

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Stopping.");
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("SimCloud failed: " + exception.Message);
            return 1;
        }
    }
}

/// <summary>Command line options for the simulator.</summary>
public sealed class SimCloudOptions
{
    public string Root { get; private set; } = Path.Combine(Path.GetTempPath(), "nimbusx-simcloud");

    public int Port { get; private set; }

    public string User { get; private set; } = "nimbusx";

    public string Password { get; private set; } = "nimbusx";

    public bool ReadOnly { get; private set; }

    public bool ShowHelp { get; private set; }

    public static SimCloudOptions Parse(string[] args)
    {
        SimCloudOptions options = new();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--help" or "-h" or "/?":
                    options.ShowHelp = true;
                    break;
                case "--root" when i + 1 < args.Length:
                    options.Root = args[++i];
                    break;
                case "--port" when i + 1 < args.Length:
                    options.Port = int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) ? port : 0;
                    i++;
                    break;
                case "--user" when i + 1 < args.Length:
                    options.User = args[++i];
                    break;
                case "--password" when i + 1 < args.Length:
                    options.Password = args[++i];
                    break;
                case "--read-only":
                    options.ReadOnly = true;
                    break;
                default:
                    Console.Error.WriteLine("Unknown argument: " + arg);
                    options.ShowHelp = true;
                    break;
            }
        }

        if (!options.ShowHelp && options.Password.Length == 0)
        {
            // A WebDAV share without a password is refused rather than silently open on the loopback interface.
            options.Password = "nimbusx";
        }

        return options;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("NimbusX.SimCloud - a local WebDAV server for testing NimbusX");
        Console.WriteLine();
        Console.WriteLine("  --root <folder>     Folder to serve (default: %TEMP%\\nimbusx-simcloud)");
        Console.WriteLine("  --port <number>     Port on 127.0.0.1 (default: a free one)");
        Console.WriteLine("  --user <name>       Basic auth user (default: nimbusx)");
        Console.WriteLine("  --password <text>   Basic auth password (default: nimbusx)");
        Console.WriteLine("  --read-only         Refuse PUT/DELETE/MKCOL/MOVE/COPY");
        Console.WriteLine("  --help              Show this text");
        Console.WriteLine();
        Console.WriteLine("It binds to 127.0.0.1 only. Test data stays in the folder you point it at.");
    }
}
