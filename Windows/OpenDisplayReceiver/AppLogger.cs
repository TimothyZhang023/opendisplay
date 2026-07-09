using System.Diagnostics;

namespace OpenDisplayReceiver;

internal static class AppLogger
{
    private static readonly object Gate = new();
    private static bool _initialized;
    private static string? _currentLogPath;

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenDisplayReceiver",
        "Logs");

    public static string LatestLogPath => Path.Combine(LogDirectory, "latest.log");

    public static string CurrentLogPath
    {
        get
        {
            EnsureInitialized(Array.Empty<string>());
            return _currentLogPath!;
        }
    }

    public static void Initialize(string[] args)
    {
        EnsureInitialized(args);
    }

    public static void WriteLine(string message)
    {
        EnsureInitialized(Array.Empty<string>());

        var line = $"[{DateTimeOffset.Now:O}] [pid {Environment.ProcessId}] [thread {Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            File.AppendAllText(_currentLogPath!, line);
            File.AppendAllText(LatestLogPath, line);
        }
    }

    public static void WriteException(string context, Exception exception)
    {
        WriteLine($"{context}: {exception}");
    }

    public static void OpenLogDirectory(Action<string>? onError = null)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{LogDirectory}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            onError?.Invoke("Could not open log directory: " + ex.Message);
            WriteException("Could not open log directory", ex);
        }
    }

    private static void EnsureInitialized(string[] args)
    {
        if (_initialized) return;

        lock (Gate)
        {
            if (_initialized) return;

            Directory.CreateDirectory(LogDirectory);
            DeleteOldLogs();

            var fileName = $"OpenDisplayReceiver-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log";
            _currentLogPath = Path.Combine(LogDirectory, fileName);
            File.WriteAllText(_currentLogPath, string.Empty);
            File.WriteAllText(LatestLogPath, string.Empty);
            _initialized = true;
        }

        WriteLine("OpenDisplay Receiver log started");
        WriteLine("Log file: " + CurrentLogPath);
        WriteLine("Application base directory: " + AppContext.BaseDirectory);
        WriteLine("OS: " + Environment.OSVersion);
        WriteLine(".NET: " + Environment.Version);
        WriteLine("Process architecture: " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
        WriteLine("Command line: " + Environment.CommandLine);
        if (args.Length > 0)
        {
            WriteLine("Args: " + string.Join(" ", args.Select(QuoteArgument)));
        }
    }

    private static void DeleteOldLogs()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-14);
            foreach (var file in Directory.EnumerateFiles(LogDirectory, "*.log"))
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc < cutoff)
                {
                    info.Delete();
                }
            }
        }
        catch
        {
            // Keep logging startup best-effort.
        }
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        return value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
    }
}
