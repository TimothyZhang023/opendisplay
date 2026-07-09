using System.Diagnostics;

namespace OpenDisplayReceiver;

internal static class AppLogger
{
    private static readonly object Gate = new();
    private static bool _initialized;
    private static string? _currentLogPath;
    private static string _logDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenDisplayReceiver",
        "Logs");

    public static string LogDirectory => _logDirectory;

    public static string LatestLogPath => string.IsNullOrWhiteSpace(LogDirectory)
        ? string.Empty
        : Path.Combine(LogDirectory, "latest.log");

    public static string CurrentLogPath
    {
        get
        {
            EnsureInitialized(Array.Empty<string>());
            return _currentLogPath ?? string.Empty;
        }
    }

    public static void Initialize(string[] args)
    {
        EnsureInitialized(args);
    }

    public static void WriteLine(string message)
    {
        EnsureInitialized(Array.Empty<string>());
        if (string.IsNullOrWhiteSpace(_currentLogPath) || string.IsNullOrWhiteSpace(LatestLogPath)) return;

        var line = $"[{DateTimeOffset.Now:O}] [pid {Environment.ProcessId}] [thread {Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}";
        try
        {
            lock (Gate)
            {
                File.AppendAllText(_currentLogPath, line);
                File.AppendAllText(LatestLogPath, line);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("OpenDisplayReceiver logging failed: " + ex);
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
            EnsureInitialized(Array.Empty<string>());
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

            PrepareLogFiles();
            _initialized = true;
        }

        WriteLine("OpenDisplay Receiver log started");
        WriteLine("Log file: " + CurrentLogPath);
        WriteLine("Latest log: " + LatestLogPath);
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

    private static void PrepareLogFiles()
    {
        var fileName = $"OpenDisplayReceiver-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log";
        foreach (var baseDirectory in CandidateBaseDirectories())
        {
            try
            {
                _logDirectory = Path.Combine(baseDirectory, "OpenDisplayReceiver", "Logs");
                Directory.CreateDirectory(_logDirectory);
                DeleteOldLogs();

                _currentLogPath = Path.Combine(_logDirectory, fileName);
                File.WriteAllText(_currentLogPath, string.Empty);
                File.WriteAllText(LatestLogPath, string.Empty);
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Could not initialize OpenDisplayReceiver log in " + baseDirectory + ": " + ex);
            }
        }

        _currentLogPath = string.Empty;
    }

    private static IEnumerable<string> CandidateBaseDirectories()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData)) yield return localAppData;
        yield return Path.GetTempPath();
    }

    private static void DeleteOldLogs()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(LogDirectory) || !Directory.Exists(LogDirectory)) return;

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
