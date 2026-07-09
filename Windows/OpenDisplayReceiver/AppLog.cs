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

            DirectoryPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenDisplayReceiver",
                "Logs");
            Directory.CreateDirectory(DirectoryPath);

            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            CurrentFilePath = Path.Combine(DirectoryPath, $"OpenDisplayReceiver-{timestamp}-{Environment.ProcessId}.log");
            LatestFilePath = Path.Combine(DirectoryPath, "latest.log");

            File.WriteAllText(CurrentFilePath, string.Empty, Encoding.UTF8);
            File.WriteAllText(LatestFilePath, string.Empty, Encoding.UTF8);
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
        lock (Gate)
        {
            File.AppendAllText(CurrentFilePath, text, Encoding.UTF8);
            File.AppendAllText(LatestFilePath, text, Encoding.UTF8);
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
