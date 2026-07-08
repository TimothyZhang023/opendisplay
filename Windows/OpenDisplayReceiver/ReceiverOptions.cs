using System.Globalization;
using System.Net;

namespace OpenDisplayReceiver;

internal sealed class ReceiverOptions
{
    public int PixelsWide { get; init; } = 1920;
    public int PixelsHigh { get; init; } = 1080;
    public double Scale { get; init; } = 2.0;
    public int Port { get; init; } = 9000;
    public IPAddress BindAddress { get; init; } = IPAddress.Any;
    public string DeviceName { get; init; } = $"OpenDisplay Windows ({Environment.MachineName})";
    public string FfplayPath { get; init; } = ResolveDefaultFfplayPath();
    public bool Fullscreen { get; init; } = true;
    public bool EmbedVideo { get; init; } = true;
    public string InstallId { get; init; } = LoadOrCreateInstallId();
    public bool ShowHelp { get; init; }

    public static string HelpText => string.Join(Environment.NewLine,
        "OpenDisplayReceiver options:",
        "  --width 1920",
        "  --height 1080",
        "  --scale 2",
        "  --port 9000",
        "  --bind 0.0.0.0",
        "  --name \"Windows Display\"",
        "  --ffplay \"C:\\ffmpeg\\bin\\ffplay.exe\"",
        "  --fullscreen       Start fullscreen. This is the default.",
        "  --windowed         Start as a normal window.",
        "  --no-embed         Let ffplay create its own window instead of embedding it.");

    public static ReceiverOptions Parse(string[] args)
    {
        var defaults = new ReceiverOptions();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help")
            {
                flags.Add("--help");
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
            else
            {
                flags.Add(arg);
            }
        }

        return new ReceiverOptions
        {
            PixelsWide = Math.Max(320, ReadInt(values, "--width", defaults.PixelsWide)),
            PixelsHigh = Math.Max(240, ReadInt(values, "--height", defaults.PixelsHigh)),
            Scale = Math.Max(1.0, ReadDouble(values, "--scale", defaults.Scale)),
            Port = ReadInt(values, "--port", defaults.Port),
            BindAddress = ReadIPAddress(values, "--bind", defaults.BindAddress),
            DeviceName = ReadString(values, "--name", defaults.DeviceName),
            FfplayPath = ReadString(values, "--ffplay", defaults.FfplayPath),
            Fullscreen = flags.Contains("--fullscreen") || (!flags.Contains("--windowed") && defaults.Fullscreen),
            EmbedVideo = !flags.Contains("--no-embed"),
            InstallId = defaults.InstallId,
            ShowHelp = flags.Contains("--help"),
        };
    }

    private static int ReadInt(Dictionary<string, string> values, string key, int fallback) =>
        values.TryGetValue(key, out var raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static double ReadDouble(Dictionary<string, string> values, string key, double fallback) =>
        values.TryGetValue(key, out var raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static string ReadString(Dictionary<string, string> values, string key, string fallback) =>
        values.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw) ? raw : fallback;

    private static IPAddress ReadIPAddress(Dictionary<string, string> values, string key, IPAddress fallback) =>
        values.TryGetValue(key, out var raw) && IPAddress.TryParse(raw, out var address) ? address : fallback;

    private static string ResolveDefaultFfplayPath()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "ffplay.exe");
        return File.Exists(bundled) ? bundled : "ffplay.exe";
    }

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
