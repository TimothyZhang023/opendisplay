using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace OpenDisplayReceiver;

internal sealed class Receiver
{
    private readonly ReceiverOptions _options;
    private CancellationTokenSource? _activeClient;

    public Receiver(ReceiverOptions options) => _options = options;

    public async Task RunAsync(CancellationToken token)
    {
        var listener = new TcpListener(IPAddress.Any, _options.Port);
        listener.Server.NoDelay = true;
        listener.Start();
        Console.WriteLine($"Listening on :{_options.Port}");

        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                client.NoDelay = true;
                Console.WriteLine($"Accepted {client.Client.RemoteEndPoint}");

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
        await using var ffplay = new FfplaySink(_options);
        using var ownedClient = client;
        using var stream = ownedClient.GetStream();
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
            // Replaced by a newer connection or stopped by Ctrl+C.
        }
        catch (EndOfStreamException)
        {
            Console.WriteLine("Mac disconnected");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            Console.Error.WriteLine("ffplay.exe was not found. Install FFmpeg or pass --ffplay <path>.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Connection failed: {ex.Message}");
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
        Console.WriteLine($"Sent hello: {_options.PixelsWide}x{_options.PixelsHigh} @ {_options.Scale:0.#}x");
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
                ["transport"] = "TCP",
                ["fps"] = snapshot.Fps,
                ["mbps"] = Math.Round(snapshot.Mbps, 1),
                ["stalls"] = snapshot.Stalls,
                ["offsetKnown"] = false,
            }, token).ConfigureAwait(false);
        }
    }

    private static async Task ReadFramesAsync(Stream stream, FfplaySink ffplay, FrameStats stats, CancellationToken token)
    {
        var header = new byte[4];
        var announcedVideo = false;

        while (!token.IsCancellationRequested)
        {
            await ReadExactAsync(stream, header, token).ConfigureAwait(false);
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(header);
            if (len <= 0 || len > 64 * 1024 * 1024)
            {
                throw new InvalidDataException($"Invalid frame length: {len}");
            }

            var payload = new byte[len];
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
                Console.WriteLine("Receiving video");
            }
        }
    }

    private static void HandleMacJson(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("type", out var typeElement)) return;
            var type = typeElement.GetString();

            if (type == "pong" && doc.RootElement.TryGetProperty("t", out var tElement) && tElement.TryGetDouble(out var t))
            {
                var rtt = Clock.NowMs - t;
                if (rtt >= 0 && rtt < 2000) Console.WriteLine($"Control RTT {rtt:0} ms");
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
}
