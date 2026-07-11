using System.Diagnostics;
using System.Windows.Forms;

namespace OpenDisplayReceiver;

internal sealed class FfplaySink : IVideoSink
{
    private readonly ReceiverOptions _options;
    private readonly Control? _videoHost;
    private readonly Action<string> _log;
    private Process? _process;
    private Stream? _input;
    private Task? _diagnosticsTask;
    private volatile bool _stopping;

    public FfplaySink(ReceiverOptions options, Control? videoHost = null, Action<string>? log = null)
    {
        _options = options;
        _videoHost = videoHost;
        _log = log ?? (_ => { });
    }

    public string Name => "ffplay";

    public Task StartAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo
        {
            FileName = _options.FfplayPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            CreateNoWindow = true,
        };

        // SDL/ffplay can embed into a native HWND on some builds via SDL_WINDOWID.
        // Keep this optional because embedding support varies across FFmpeg/SDL builds.
        if (_options.EmbedVideo && _videoHost is { IsHandleCreated: true })
        {
            psi.Environment["SDL_WINDOWID"] = _videoHost.Handle.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
            _log("ffplay embedding requested through SDL_WINDOWID");
        }
        else
        {
            _log("ffplay will use an external SDL window");
        }

        AddArgs(psi,
            "-hide_banner",
            "-loglevel", _options.FfplayLogLevel);

        if (_options.FfplayHardwareAcceleration != "none")
        {
            AddArgs(psi, "-hwaccel", _options.FfplayHardwareAcceleration);
        }

        AddArgs(psi,
            "-fflags", "nobuffer",
            "-flags", "low_delay",
            "-avioflags", "direct",
            "-max_delay", "0",
            "-framedrop",
            "-sync", "video",
            "-analyzeduration", "1000000",
            "-probesize", "1048576",
            "-an",
            "-window_title", $"OpenDisplay - {_options.DeviceName}");

        if (_options.Fullscreen)
        {
            AddArgs(psi, "-fs");
        }

        AddArgs(psi,
            "-f", "h264",
            "-i", "pipe:0");

        _process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffplay");
        _input = _process.StandardInput.BaseStream;
        _log($"Started ffplay: pid={_process.Id}; hwaccel={_options.FfplayHardwareAcceleration}; loglevel={_options.FfplayLogLevel}; probe=1048576; analyze=1000000us");
        try
        {
            var module = _process.MainModule;
            _log($"ffplay executable: path={module?.FileName ?? _options.FfplayPath}; version={module?.FileVersionInfo.FileVersion ?? "unknown"}");
        }
        catch (Exception ex)
        {
            _log("Could not inspect ffplay executable metadata: " + ex.Message);
        }
        _log("ffplay arguments: " + FormatArguments(psi.ArgumentList));
        _diagnosticsTask = DrainDiagnosticsAsync(_process);
        return Task.CompletedTask;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
    {
        var input = _input ?? throw new IOException("ffplay is not running");
        return input.WriteAsync(data, token);
    }

    private static void AddArgs(ProcessStartInfo psi, params string[] args)
    {
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
    }

    private static string FormatArguments(IEnumerable<string> arguments) =>
        string.Join(" ", arguments.Select(arg => arg.Any(char.IsWhiteSpace) ? $"\"{arg.Replace("\"", "\\\"")}\"" : arg));

    private async Task DrainDiagnosticsAsync(Process process)
    {
        try
        {
            while (true)
            {
                var line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                if (!string.IsNullOrWhiteSpace(line)) _log($"ffplay: {line}");
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
            _log($"ffplay exited: pid={process.Id}; exitCode={process.ExitCode}; expected={_stopping}");
        }
        catch (Exception ex)
        {
            if (!_stopping)
            {
                _log("ffplay diagnostics failed: " + ex);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        var process = _process;
        if (process is null) return;

        _stopping = true;
        try
        {
            _input?.Close();
        }
        catch (Exception ex)
        {
            _log("Closing ffplay stdin failed: " + ex);
        }
        _input = null;
        try
        {
            if (!process.HasExited)
            {
                using var gracefulExit = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
                try
                {
                    await process.WaitForExitAsync(gracefulExit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _log($"ffplay pid={process.Id} did not exit after stdin closed; terminating process tree");
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
            }

            if (_diagnosticsTask is not null)
            {
                await _diagnosticsTask.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log("ffplay shutdown failed: " + ex);
        }
        finally
        {
            process.Dispose();
            _process = null;
            _diagnosticsTask = null;
        }
    }
}
