using System.Diagnostics;
using System.Text;

namespace OpenDisplayReceiver;

internal static class AppLogger
{
    private static readonly object Gate = new();
    private static volatile bool _initialized;
    private static string? _currentLogPath;
    private static StreamWriter? _currentWriter;
    private static StreamWriter? _latestWriter;
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
        var line = $"[{DateTimeOffset.Now:O}] [pid {Environment.ProcessId}] [thread {Environment.CurrentManagedThreadId}] {message}";
        lock (Gate)
        {
            try
            {
                _currentWriter?.WriteLine(line);
                _latestWriter?.WriteLine(line);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("OpenDisplayReceiver logging failed: " + ex);
                CloseWritersNoThrow();
            }
        }
    }

    public static void WriteException(string context, Exception exception)
    {
        WriteLine($"{context}: {exception}");
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            CloseWritersNoThrow();
        }
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
        WriteLine($"CPU count: {Environment.ProcessorCount}; serverGC={System.Runtime.GCSettings.IsServerGC}; GC latency={System.Runtime.GCSettings.LatencyMode}");
        WriteLine($"Startup working set: {Environment.WorkingSet / 1048576d:0.0} MiB");
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
                _currentWriter = CreateWriter(_currentLogPath);
                _latestWriter = CreateWriter(LatestLogPath);
                return;
            }
            catch (Exception ex)
            {
                CloseWritersNoThrow();
                Debug.WriteLine("Could not initialize OpenDisplayReceiver log in " + baseDirectory + ": " + ex);
            }
        }

        _currentLogPath = string.Empty;
    }

    private static StreamWriter CreateWriter(string path)
    {
        var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 32 * 1024,
            FileOptions.SequentialScan);
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 8 * 1024)
        {
            AutoFlush = true,
        };
    }

    private static void CloseWritersNoThrow()
    {
        try { _currentWriter?.Dispose(); } catch { }
        try { _latestWriter?.Dispose(); } catch { }
        _currentWriter = null;
        _latestWriter = null;
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
