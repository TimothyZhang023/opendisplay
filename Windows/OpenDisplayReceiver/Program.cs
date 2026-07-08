using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace OpenDisplayReceiver;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var options = ReceiverOptions.Parse(args);
        if (options.ShowHelp)
        {
            ReceiverOptions.PrintHelp();
            return 0;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.WriteLine("OpenDisplay Receiver for Windows");
        Console.WriteLine($"Receiver name : {options.DeviceName}");
        Console.WriteLine($"Resolution    : {options.PixelsWide}x{options.PixelsHigh} @ {options.Scale:0.#}x");
        Console.WriteLine($"Port          : {options.Port}");
        Console.WriteLine($"ffplay        : {options.FfplayPath}");
        Console.WriteLine();
        Console.WriteLine("Connect from the Mac with:");
        foreach (var ip in NetworkAddresses.GetLocalIPv4Addresses())
        {
            Console.WriteLine($"  defaults write com.peetzweg.opensidecar.mac host {ip}");
            Console.WriteLine($"  defaults write com.peetzweg.opensidecar.mac port {options.Port}");
            Console.WriteLine("  open -a OpenDisplay");
            Console.WriteLine();
        }
        Console.WriteLine("Debug Mac build bundle id: com.peetzweg.opensidecar.mac.debug");
        Console.WriteLine("Press Ctrl+C to stop.");
        Console.WriteLine();

        try
        {
            await new Receiver(options).RunAsync(cts.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
