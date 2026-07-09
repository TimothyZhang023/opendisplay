using System.Diagnostics;
using System.Windows.Forms;

namespace OpenDisplayReceiver;

internal sealed class FfplaySink : IVideoSink
{
    private readonly ReceiverOptions _options;
    private readonly Control? _videoHost;
    private readonly Action<string> _log;
    private Process? _process;

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
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };

        // SDL/ffplay can embed into a native HWND on some builds via SDL_WINDOWID.
        // Keep this optional because embedding support varies across FFmpeg/SDL builds.
        if (_options.EmbedVideo && _videoHost is { IsHandleCreated: true })
        {
            psi.Environment["SDL_WINDOWID"] = _videoHost.Handle.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
            _log("ffplay embedding requested through SDL_WINDOWID");
        }

        AddArgs(psi,
            "-hide_banner",
            "-loglevel", "info",
            "-fflags", "nobuffer",
            "-flags", "low_delay",
            "-framedrop",
            "-sync", "ext",
            "-analyzeduration", "1000000",
            "-probesize", "1000000",
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
        _log("Started ffplay video renderer");
        _ = DrainOutputAsync(_process, token);
        return Task.CompletedTask;
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
    {
        if (_process is null || _process.HasExited) throw new IOException("ffplay is not running");
        await _process.StandardInput.BaseStream.WriteAsync(data, token).ConfigureAwait(false);
        await _process.StandardInput.BaseStream.FlushAsync(token).ConfigureAwait(false);
    }

    private static void AddArgs(ProcessStartInfo psi, params string[] args)
    {
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
    }

    private async Task DrainOutputAsync(Process process, CancellationToken token)
    {
        async Task DrainAsync(StreamReader reader)
        {
            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                if (!string.IsNullOrWhiteSpace(line)) _log($"ffplay: {line}");
            }
        }

        try
        {
            await Task.WhenAll(DrainAsync(process.StandardError), DrainAsync(process.StandardOutput)).ConfigureAwait(false);
        }
        catch
        {
            // Ignore shutdown races.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is null) return;

        try { _process.StandardInput.Close(); } catch { }
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
