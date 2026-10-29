using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NimbusX.Cli;

/// <summary>Entry point for <c>cpx</c>.</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // shutting down
            }
        };

        Console.CancelKeyPress += handler;

        try
        {
            CliApplication cli = new(Console.Out, Console.In);
            return await cli.RunAsync(args, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }
}
