using System.ComponentModel;
using System.Diagnostics;

namespace OpenDisplayReceiver;

internal sealed class FfplaySink : IAsyncDisposable
{
    private readonly ReceiverOptions _options;
    private Process? _process;

    public FfplaySink(ReceiverOptions options) => _options = options;

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

        AddArgs(psi,
            "-hide_banner",
            "-loglevel", "warning",
            "-fflags", "nobuffer",
            "-flags", "low_delay",
            "-framedrop",
            "-sync", "ext",
            "-probesize", "32",
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

    private static async Task DrainOutputAsync(Process process, CancellationToken token)
    {
        async Task DrainAsync(StreamReader reader)
        {
            while (!token.IsCancellationRequested && !reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(line)) Console.Error.WriteLine($"ffplay: {line}");
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
