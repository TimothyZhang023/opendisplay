using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace OpenDisplayReceiver;

internal sealed class ReceiverOptions
{
    public int PixelsWide { get; init; } = 1920;
    public int PixelsHigh { get; init; } = 1080;
    public double Scale { get; init; } = 2.0;
    public int Port { get; init; } = 9000;
    public string DeviceName { get; init; } = $"OpenDisplay Windows ({Environment.MachineName})";
    public string FfplayPath { get; init; } = "ffplay";
    public string InstallId { get; init; } = LoadOrCreateInstallId();
    public bool ShowHelp { get; init; }

    public static ReceiverOptions Parse(string[] args)
    {
        var defaults = new ReceiverOptions();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var help = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help")
            {
                help = true;
                continue;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var eq = arg.IndexOf('=');
            if (eq > 2)
            {
                values[arg[..eq]] = arg[(eq + 1)..];
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                values[arg] = args[++i];
            }
        }

        return new ReceiverOptions
        {
            PixelsWide = ReadInt(values, "--width", defaults.PixelsWide),
            PixelsHigh = ReadInt(values, "--height", defaults.PixelsHigh),
            Scale = ReadDouble(values, "--scale", defaults.Scale),
            Port = ReadInt(values, "--port", defaults.Port),
            DeviceName = ReadString(values, "--name", defaults.DeviceName),
            FfplayPath = ReadString(values, "--ffplay", defaults.FfplayPath),
            InstallId = defaults.InstallId,
            ShowHelp = help,
        };
    }

    public static void PrintHelp()
    {
        Console.WriteLine("OpenDisplayReceiver options:");
        Console.WriteLine("  --width 1920");
        Console.WriteLine("  --height 1080");
        Console.WriteLine("  --scale 2");
        Console.WriteLine("  --port 9000");
        Console.WriteLine("  --name \"Windows Display\"");
        Console.WriteLine("  --ffplay \"C:\\ffmpeg\\bin\\ffplay.exe\"");
    }

    private static int ReadInt(Dictionary<string, string> values, string key, int fallback) =>
        values.TryGetValue(key, out var raw) && int.TryParse(raw, out var value) ? value : fallback;

    private static double ReadDouble(Dictionary<string, string> values, string key, double fallback) =>
        values.TryGetValue(key, out var raw) && double.TryParse(raw, out var value) ? value : fallback;

    private static string ReadString(Dictionary<string, string> values, string key, string fallback) =>
        values.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw) ? raw : fallback;

    private static string LoadOrCreateInstallId()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenDisplayReceiver");
        var path = Path.Combine(dir, "install-id.txt");

        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (Guid.TryParse(existing, out _)) return existing;
            }

            Directory.CreateDirectory(dir);
            var fresh = Guid.NewGuid().ToString();
            File.WriteAllText(path, fresh);
            return fresh;
        }
        catch
        {
            return Guid.NewGuid().ToString();
        }
    }
}
