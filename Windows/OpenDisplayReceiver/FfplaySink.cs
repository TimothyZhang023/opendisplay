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

    public FfplaySink(ReceiverOptions options, Control? videoHost = null, Action<string>? log = null)
    {
        _options = options;
        _videoHost = videoHost;
        _log = log ?? (_ => { });
    }

    public string Name => "ffplay";

    public Task StartAsync(CancellationToken token)
    {
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
            "-loglevel", "info");

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
        _log($"Started ffplay video renderer (hwaccel={_options.FfplayHardwareAcceleration}, probe=1048576, analyze=1000000us)");
        _ = DrainDiagnosticsAsync(_process, token);
        return Task.CompletedTask;
    }

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
    {
        var input = _input ?? throw new IOException("ffplay is not running");
        return input.WriteAsync(data, token).AsTask();
    }

    private static void AddArgs(ProcessStartInfo psi, params string[] args)
    {
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
    }

    private async Task DrainDiagnosticsAsync(Process process, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                if (!string.IsNullOrWhiteSpace(line)) _log($"ffplay: {line}");
            }
        }
        catch
        {
            // Ignore shutdown races.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is null) return;

        try { _input?.Close(); } catch { }
        _input = null;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // Ignore shutdown races.
        }
        finally
        {
            _process.Dispose();
        }
    }
}
