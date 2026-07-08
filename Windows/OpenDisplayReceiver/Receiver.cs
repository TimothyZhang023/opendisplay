using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Forms;

namespace OpenDisplayReceiver;

internal sealed class Receiver
{
    private readonly ReceiverOptions _options;
    private readonly Control? _videoHost;
    private readonly Action<string> _log;
    private readonly Action<string> _status;
    private CancellationTokenSource? _activeClient;

    public Receiver(ReceiverOptions options, Control? videoHost = null, Action<string>? log = null, Action<string>? status = null)
    {
        _options = options;
        _videoHost = videoHost;
        _log = log ?? Console.WriteLine;
        _status = status ?? (_ => { });
    }

    public async Task RunAsync(CancellationToken token)
    {
        var listener = new TcpListener(_options.BindAddress, _options.Port);
        listener.Server.NoDelay = true;
        listener.Start();
        Log($"Listening on {_options.BindAddress}:{_options.Port}");
        _status($"Listening on :{_options.Port}");

        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                client.NoDelay = true;
                Log($"Accepted {client.Client.RemoteEndPoint}");
                _status("Mac connected");

                _activeClient?.Cancel();
                _activeClient?.Dispose();
                _activeClient = CancellationTokenSource.CreateLinkedTokenSource(token);
                _ = HandleClientAsync(client, _activeClient.Token);
            }
        }
        finally
        {
            listener.Stop();
            _activeClient?.Cancel();
            _activeClient?.Dispose();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        await using var ffplay = new FfplaySink(_options, _videoHost, Log);
        using var _ = client;
        using var stream = client.GetStream();
        var sendLock = new SemaphoreSlim(1, 1);
        var stats = new FrameStats();

        try
        {
            await ffplay.StartAsync(token).ConfigureAwait(false);
            await SendHelloAsync(stream, sendLock, token).ConfigureAwait(false);
            _ = PingLoopAsync(stream, sendLock, token);
            _ = StatsLoopAsync(stream, sendLock, stats, token);
            await ReadFramesAsync(stream, ffplay, stats, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Replaced by a newer connection or app shutdown.
        }
        catch (EndOfStreamException)
        {
            Log("Mac disconnected");
            _status("Mac disconnected");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            Log("ffplay.exe was not found. Use the packaged release zip or pass --ffplay <path>.");
            _status("ffplay.exe not found");
        }
        catch (Exception ex)
        {
            Log($"Connection failed: {ex.Message}");
            _status("Connection failed: " + ex.Message);
        }
    }

    private async Task SendHelloAsync(Stream stream, SemaphoreSlim sendLock, CancellationToken token)
    {
        var hello = new Dictionary<string, object?>
        {
            ["type"] = "hello",
            ["pixelsWide"] = _options.PixelsWide,
            ["pixelsHigh"] = _options.PixelsHigh,
            ["scale"] = _options.Scale,
            ["device"] = "Windows",
            ["id"] = _options.InstallId,
            ["localCursor"] = false,
        };
        await SendControlAsync(stream, sendLock, hello, token).ConfigureAwait(false);
        Log($"Sent hello: {_options.PixelsWide}x{_options.PixelsHigh} @ {_options.Scale:0.#}x");
    }

    private static async Task PingLoopAsync(Stream stream, SemaphoreSlim sendLock, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            await SendControlAsync(stream, sendLock, new Dictionary<string, object?>
            {
                ["type"] = "ping",
                ["t"] = Clock.NowMs,
            }, token).ConfigureAwait(false);
        }
    }

    private static async Task StatsLoopAsync(Stream stream, SemaphoreSlim sendLock, FrameStats stats, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            var snapshot = stats.TakeSnapshot();
            await SendControlAsync(stream, sendLock, new Dictionary<string, object?>
            {
                ["type"] = "stats",
                ["transport"] = "WiFi",
                ["fps"] = snapshot.Fps,
                ["mbps"] = Math.Round(snapshot.Mbps, 1),
                ["stalls"] = snapshot.Stalls,
                ["offsetKnown"] = false,
            }, token).ConfigureAwait(false);
        }
    }

    private async Task ReadFramesAsync(Stream stream, FfplaySink ffplay, FrameStats stats, CancellationToken token)
    {
        var header = new byte[4];
        var announcedVideo = false;

        while (!token.IsCancellationRequested)
        {
            await ReadExactAsync(stream, header, token).ConfigureAwait(false);
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header));
            if (length <= 0 || length > 64 * 1024 * 1024)
            {
                throw new InvalidDataException($"Invalid frame length: {length}");
            }

            var payload = new byte[length];
            await ReadExactAsync(stream, payload, token).ConfigureAwait(false);

            if (IsPureJson(payload))
            {
                HandleMacJson(payload);
                continue;
            }

            var startCode = FindAnnexBStartCode(payload);
            if (startCode < 0) continue;

            var video = payload.AsMemory(startCode);
            await ffplay.WriteAsync(video, token).ConfigureAwait(false);
            stats.RecordFrame(video.Length);

            if (!announcedVideo)
            {
                announcedVideo = true;
                Log("Receiving video");
                _status("Receiving video");
            }
        }
    }

    private void HandleMacJson(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("type", out var typeElement)) return;
            var type = typeElement.GetString();

            if (type == "pong" && doc.RootElement.TryGetProperty("t", out var tElement) && tElement.TryGetDouble(out var t))
            {
                var rtt = Clock.NowMs - t;
                if (rtt >= 0 && rtt < 2000) Log($"Control RTT {rtt:0} ms");
            }
        }
        catch (JsonException)
        {
            // Ignore malformed control payloads.
        }
    }

    private static async Task SendControlAsync(Stream stream, SemaphoreSlim sendLock, Dictionary<string, object?> message, CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);

        await sendLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(header, token).ConfigureAwait(false);
            await stream.WriteAsync(payload, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], token).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
    }

    private static bool IsPureJson(byte[] payload) =>
        payload.Length > 0 && payload[0] == (byte)'{' && Array.IndexOf(payload, (byte)0) < 0;

    private static int FindAnnexBStartCode(byte[] payload)
    {
        for (var i = 0; i <= payload.Length - 4; i++)
        {
            if (payload[i] == 0 && payload[i + 1] == 0 && payload[i + 2] == 0 && payload[i + 3] == 1)
            {
                return i;
            }
        }
        return -1;
    }

    private void Log(string message) => _log(message);
}
