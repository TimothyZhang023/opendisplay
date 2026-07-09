using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenDisplayReceiver;

internal static class AppLog
{
    private static readonly object Gate = new();
    private static bool _initialized;

    public static string DirectoryPath { get; private set; } = string.Empty;
    public static string CurrentFilePath { get; private set; } = string.Empty;
    public static string LatestFilePath { get; private set; } = string.Empty;

    public static void Initialize(string[] args)
    {
        lock (Gate)
        {
            if (_initialized) return;
            PrepareLogFiles();
            _initialized = true;
        }

        Info("OpenDisplay Receiver log started");
        Info($"Log file: {CurrentFilePath}");
        Info($"Latest log: {LatestFilePath}");
        Info($"Process id: {Environment.ProcessId}");
        Info($"App base directory: {AppContext.BaseDirectory}");
        Info($"OS: {RuntimeInformation.OSDescription}");
        Info($"Runtime: {RuntimeInformation.FrameworkDescription}");
        Info($"Command line: {Environment.CommandLine}");
        if (args.Length > 0)
        {
            Info("Arguments: " + string.Join(' ', args.Select(EscapeArgument)));
        }

        DeleteOldLogs(maxFilesToKeep: 20);
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void Exception(string message, Exception exception)
    {
        Write("ERROR", message);
        Write("ERROR", exception.ToString());
    }

    public static void Write(string level, string message)
    {
        EnsureInitializedWithoutArgs();
        if (string.IsNullOrWhiteSpace(CurrentFilePath) || string.IsNullOrWhiteSpace(LatestFilePath)) return;

        var normalized = message.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var thread = Environment.CurrentManagedThreadId;
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            builder.Append('[')
                .Append(now)
                .Append("] [")
                .Append(level)
                .Append("] [T")
                .Append(thread)
                .Append("] ")
                .AppendLine(line);
        }

        var text = builder.ToString();
        try
        {
            lock (Gate)
            {
                File.AppendAllText(CurrentFilePath, text, Encoding.UTF8);
                File.AppendAllText(LatestFilePath, text, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("OpenDisplayReceiver logging failed: " + ex);
        }
    }

    public static void OpenLogFolder()
    {
        EnsureInitializedWithoutArgs();
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = DirectoryPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Exception("Could not open log folder", ex);
        }
    }

    private static void PrepareLogFiles()
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var fileName = $"OpenDisplayReceiver-{timestamp}-{Environment.ProcessId}.log";

        foreach (var baseDirectory in CandidateBaseDirectories())
        {
            try
            {
                DirectoryPath = Path.Combine(baseDirectory, "OpenDisplayReceiver", "Logs");
                Directory.CreateDirectory(DirectoryPath);
                CurrentFilePath = Path.Combine(DirectoryPath, fileName);
                LatestFilePath = Path.Combine(DirectoryPath, "latest.log");
                File.WriteAllText(CurrentFilePath, string.Empty, Encoding.UTF8);
                File.WriteAllText(LatestFilePath, string.Empty, Encoding.UTF8);
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Could not initialize OpenDisplayReceiver log in " + baseDirectory + ": " + ex);
            }
        }

        DirectoryPath = string.Empty;
        CurrentFilePath = string.Empty;
        LatestFilePath = string.Empty;
    }

    private static IEnumerable<string> CandidateBaseDirectories()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.GetTempPath();
    }

    private static void EnsureInitializedWithoutArgs()
    {
        if (_initialized) return;
        Initialize(Array.Empty<string>());
    }

    private static string EscapeArgument(string arg)
    {
        if (arg.Length == 0) return "\"\"";
        return arg.Any(char.IsWhiteSpace) ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg;
    }

    private static void DeleteOldLogs(int maxFilesToKeep)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(DirectoryPath)) return;

            var logs = Directory.GetFiles(DirectoryPath, "OpenDisplayReceiver-*.log")
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.CreationTimeUtc)
                .Skip(maxFilesToKeep);

            foreach (var file in logs)
            {
                try { file.Delete(); } catch { }
            }
        }
        catch
        {
            // Logging cleanup should never prevent startup.
        }
    }
}
