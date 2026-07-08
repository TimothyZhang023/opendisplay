using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OpenDisplayReceiver;

internal sealed class FfplaySink : IAsyncDisposable
{
    private readonly ReceiverOptions _options;
    private readonly Control? _host;
    private readonly Action<string> _log;
    private Process? _process;
    private nint _videoWindow;

    public FfplaySink(ReceiverOptions options, Control? host = null, Action<string>? log = null)
    {
        _options = options;
        _host = host;
        _log = log ?? Console.WriteLine;
    }

    public Task StartAsync(CancellationToken token)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _options.FfplayPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = false,
        };

        foreach (var arg in BuildArguments())
        {
            psi.ArgumentList.Add(arg);
        }

        _process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffplay");
        _ = DrainOutputAsync(_process, token);

        if (_options.EmbedVideo && _host is not null)
        {
            _ = AttachWindowWhenReadyAsync(_process, token);
        }

        return Task.CompletedTask;
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
    {
        if (_process is null || _process.HasExited) throw new IOException("ffplay is not running");
        await _process.StandardInput.BaseStream.WriteAsync(data, token).ConfigureAwait(false);
        await _process.StandardInput.BaseStream.FlushAsync(token).ConfigureAwait(false);
    }

    private IEnumerable<string> BuildArguments()
    {
        yield return "-hide_banner";
        yield return "-loglevel";
        yield return "warning";
        yield return "-fflags";
        yield return "nobuffer";
        yield return "-flags";
        yield return "low_delay";
        yield return "-framedrop";
        yield return "-sync";
        yield return "ext";
        yield return "-probesize";
        yield return "32";
        yield return "-an";
        yield return "-window_title";
        yield return $"OpenDisplay - {_options.DeviceName}";

        if (_options.EmbedVideo)
        {
            yield return "-noborder";
        }
        else if (_options.Fullscreen)
        {
            yield return "-fs";
        }

        yield return "-f";
        yield return "h264";
        yield return "-i";
        yield return "pipe:0";
    }

    private async Task AttachWindowWhenReadyAsync(Process process, CancellationToken token)
    {
        try
        {
            try { process.WaitForInputIdle(3000); } catch { }

            for (var i = 0; i < 100 && !token.IsCancellationRequested; i++)
            {
                if (process.HasExited) return;
                process.Refresh();
                if (process.MainWindowHandle != 0)
                {
                    AttachWindow(process.MainWindowHandle);
                    return;
                }
                await Task.Delay(50, token).ConfigureAwait(false);
            }

            _log("ffplay window was not ready; leaving it as a separate window.");
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            _log("Could not embed ffplay window: " + ex.Message);
        }
    }

    private void AttachWindow(nint handle)
    {
        if (_host is null || _host.IsDisposed) return;
        if (_host.InvokeRequired)
        {
            _host.BeginInvoke(new Action(() => AttachWindow(handle)));
            return;
        }

        _videoWindow = handle;
        SetParent(handle, _host.Handle);

        var style = GetWindowLongPtr(handle, GWL_STYLE).ToInt64();
        style &= ~(WS_CAPTION | WS_THICKFRAME);
        style |= WS_CHILD | WS_VISIBLE;
        SetWindowLongPtr(handle, GWL_STYLE, new IntPtr(style));

        _host.Resize -= HostOnResize;
        _host.Resize += HostOnResize;
        FitToHost();
    }

    private void HostOnResize(object? sender, EventArgs e) => FitToHost();

    private void FitToHost()
    {
        if (_videoWindow == 0 || _host is null || _host.IsDisposed) return;
        MoveWindow(_videoWindow, 0, 0, Math.Max(1, _host.ClientSize.Width), Math.Max(1, _host.ClientSize.Height), true);
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
        if (_host is not null && !_host.IsDisposed)
        {
            try { _host.Resize -= HostOnResize; } catch { }
        }

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
            // ignore
        }
        finally
        {
            _process.Dispose();
        }
    }

    private const int GWL_STYLE = -16;
    private const long WS_CHILD = 0x40000000L;
    private const long WS_VISIBLE = 0x10000000L;
    private const long WS_CAPTION = 0x00C00000L;
    private const long WS_THICKFRAME = 0x00040000L;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetParent(nint hWndChild, nint hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(nint hWnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int index, nint newLong);
}
