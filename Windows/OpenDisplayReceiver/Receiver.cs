using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
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
    private static readonly int[] StartupKeyframeRetryScheduleMs = [250, 750, 1500, 2500];

    public Receiver(ReceiverOptions options, Control? videoHost = null, Action<string>? log = null, Action<string>? status = null)
    {
        _options = options;
        _videoHost = videoHost;
        _log = log ?? Console.WriteLine;
        _status = status ?? (_ => { });
    }

    public async Task RunAsync(CancellationToken token)
    {
        await using var mdns = new MdnsAdvertiser(_options, Log);
        await mdns.StartAsync(token).ConfigureAwait(false);

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
        await using var videoSink = CreateVideoSink();
        using var ownedClient = client;
        using var stream = ownedClient.GetStream();
        using var sendLock = new SemaphoreSlim(1, 1);
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var startupKeyframeCts = CancellationTokenSource.CreateLinkedTokenSource(connectionCts.Token);
        var connectionToken = connectionCts.Token;
        var stats = new FrameStats();
        var pingTask = Task.CompletedTask;
        var statsTask = Task.CompletedTask;
        var startupKeyframeTask = Task.CompletedTask;

        try
        {
            Log("Video renderer selected: " + videoSink.Name);
            await SendHelloAsync(stream, sendLock, connectionToken).ConfigureAwait(false);
            await RequestKeyframeAsync(stream, sendLock, "hello", connectionToken).ConfigureAwait(false);
            Log("Requested H.264 keyframe immediately after hello");

            startupKeyframeTask = StartupKeyframeRetryLoopAsync(stream, sendLock, startupKeyframeCts.Token);
            pingTask = PingLoopAsync(stream, sendLock, connectionToken);
            statsTask = StatsLoopAsync(stream, sendLock, stats, connectionToken);
            await ReadFramesAsync(
                stream,
                videoSink,
                stats,
                startupKeyframeCts.Cancel,
                connectionToken).ConfigureAwait(false);
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
        finally
        {
            startupKeyframeCts.Cancel();
            connectionCts.Cancel();
            await ObserveBackgroundTasksAsync(startupKeyframeTask, pingTask, statsTask).ConfigureAwait(false);
        }
    }

    private IVideoSink CreateVideoSink() => _options.Renderer switch
    {
        VideoRendererKind.Native => new NativeH264Sink(_options, _videoHost, Log),
        _ => new FfplaySink(_options, _videoHost, Log),
    };

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
                ["transport"] = "TCP",
                ["fps"] = snapshot.Fps,
                ["mbps"] = Math.Round(snapshot.Mbps, 1),
                ["stalls"] = snapshot.Stalls,
                ["offsetKnown"] = false,
            }, token).ConfigureAwait(false);
        }
    }

    private async Task ReadFramesAsync(
        Stream stream,
        IVideoSink videoSink,
        FrameStats stats,
        Action onRendererStarted,
        CancellationToken token)
    {
        const int maxCachedParameterSetBytes = 64 * 1024;
        var header = new byte[4];
        var announcedVideo = false;
        var sinkStarted = false;
        var startupGate = new H264StartupGate(Log);
        var startupParameterSets = new ArrayBufferWriter<byte>(256);

        while (!token.IsCancellationRequested)
        {
            await ReadExactAsync(stream, header, token).ConfigureAwait(false);
            var rawLength = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (rawLength == 0 || rawLength > 64 * 1024 * 1024)
            {
                throw new InvalidDataException($"Invalid frame length: {rawLength}");
            }
            var length = (int)rawLength;

            var rentedPayload = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                var payload = rentedPayload.AsMemory(0, length);
                await ReadExactAsync(stream, payload, token).ConfigureAwait(false);

                if (IsPureJson(payload.Span))
                {
                    HandleMacJson(payload);
                    continue;
                }

                var startCode = FindAnnexBStartCode(payload.Span);
                if (startCode < 0)
                {
                    Log($"Skipping non-JSON payload without Annex B start code ({payload.Length} bytes)");
                    continue;
                }

                var video = payload[startCode..];
                var analysis = default(H264Analysis);
                if (!sinkStarted)
                {
                    analysis = H264Analysis.Analyze(video.Span);
                    if (analysis.NalCount == 0)
                    {
                        Log($"Skipping Annex B payload without complete NAL units ({video.Length} bytes)");
                        continue;
                    }

                    startupGate.Observe(analysis, video.Length);
                    H264Analysis.CopyParameterSets(video.Span, startupParameterSets, maxCachedParameterSetBytes);
                    if (!startupGate.Ready)
                    {
                        continue;
                    }

                    await videoSink.StartAsync(token).ConfigureAwait(false);
                    if (startupParameterSets.WrittenCount > 0 && (!analysis.HasSps || !analysis.HasPps))
                    {
                        await videoSink.WriteAsync(startupParameterSets.WrittenMemory, token).ConfigureAwait(false);
                        Log($"Primed decoder with {startupParameterSets.WrittenCount} cached SPS/PPS bytes");
                    }

                    sinkStarted = true;
                    onRendererStarted();
                    Log($"Video renderer started after H.264 sync: {videoSink.Name}; {analysis.Describe()}");
                }

                await videoSink.WriteAsync(video, token).ConfigureAwait(false);
                stats.RecordFrame(video.Length);

                if (!announcedVideo)
                {
                    announcedVideo = true;
                    Log($"Receiving video: {analysis.Describe()}");
                    _status("Receiving video");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rentedPayload);
            }
        }
    }

    private async Task StartupKeyframeRetryLoopAsync(Stream stream, SemaphoreSlim sendLock, CancellationToken token)
    {
        try
        {
            var previousDelayMs = 0;
            for (var i = 0; i < StartupKeyframeRetryScheduleMs.Length; i++)
            {
                var scheduledDelayMs = StartupKeyframeRetryScheduleMs[i];
                await Task.Delay(scheduledDelayMs - previousDelayMs, token).ConfigureAwait(false);
                await RequestKeyframeAsync(stream, sendLock, $"startup-retry-{i + 1}", token).ConfigureAwait(false);
                Log($"Repeated startup keyframe request #{i + 1} at {scheduledDelayMs} ms");
                previousDelayMs = scheduledDelayMs;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Renderer synchronized or connection stopped.
        }
    }

    private async Task RequestKeyframeAsync(Stream stream, SemaphoreSlim sendLock, string reason, CancellationToken token)
    {
        await SendControlAsync(stream, sendLock, new Dictionary<string, object?>
        {
            ["type"] = "kf",
            ["reason"] = reason,
            ["t"] = Clock.NowMs,
        }, token).ConfigureAwait(false);
    }

    private static async Task ObserveBackgroundTasksAsync(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // The foreground read path reports the connection result.
        }
    }

    private void HandleMacJson(ReadOnlyMemory<byte> payload)
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

    private static bool IsPureJson(ReadOnlySpan<byte> payload) =>
        payload.Length > 0 && payload[0] == (byte)'{' && payload.IndexOf((byte)0) < 0;

    private static int FindAnnexBStartCode(ReadOnlySpan<byte> payload)
    {
        for (var i = 0; i <= payload.Length - 3; i++)
        {
            if (payload[i] != 0 || payload[i + 1] != 0) continue;
            if (payload[i + 2] == 1 || (i <= payload.Length - 4 && payload[i + 2] == 0 && payload[i + 3] == 1))
            {
                return i;
            }
        }
        return -1;
    }

    private void Log(string message) => _log(message);

    private sealed class H264StartupGate
    {
        private readonly Action<string> _log;
        private int _framesObserved;
        private bool _seenSps;
        private bool _seenPps;
        private bool _seenIdr;

        public H264StartupGate(Action<string> log) => _log = log;

        public bool Ready => _seenSps && _seenPps && _seenIdr;

        public void Observe(H264Analysis analysis, int bytes)
        {
            _framesObserved++;
            if (_framesObserved == 1)
            {
                _log("H.264 NAL legend: 7=SPS, 8=PPS, 5=IDR");
            }
            _seenSps |= analysis.HasSps;
            _seenPps |= analysis.HasPps;
            _seenIdr |= analysis.HasIdr && _seenSps && _seenPps;

            if (_framesObserved <= 8 || Ready)
            {
                _log($"H.264 startup frame #{_framesObserved}: {bytes} bytes; {analysis.Describe()}; sync sps={_seenSps} pps={_seenPps} idr={_seenIdr}");
            }
        }
    }

    private readonly record struct H264Analysis(int NalCount, bool HasSps, bool HasPps, bool HasIdr, bool HasSlice, string NalTypes)
    {
        public string Describe() => $"nals={NalCount} types=[{NalTypes}] sps={HasSps} pps={HasPps} idr={HasIdr} slice={HasSlice}";

        public static H264Analysis Analyze(ReadOnlySpan<byte> annexB)
        {
            var types = new List<int>();
            var hasSps = false;
            var hasPps = false;
            var hasIdr = false;
            var hasSlice = false;

            var offset = 0;
            while (TryFindStartCode(annexB, offset, out var startCodeOffset, out var startCodeLength))
            {
                var nalStart = startCodeOffset + startCodeLength;
                if (nalStart >= annexB.Length) break;

                var nextSearchOffset = nalStart;
                if (!TryFindStartCode(annexB, nextSearchOffset, out var nextStartCodeOffset, out _))
                {
                    nextStartCodeOffset = annexB.Length;
                }

                if (nextStartCodeOffset > nalStart)
                {
                    var type = annexB[nalStart] & 0x1F;
                    types.Add(type);
                    hasSps |= type == 7;
                    hasPps |= type == 8;
                    hasIdr |= type == 5;
                    hasSlice |= type is 1 or 5;
                }

                offset = nextStartCodeOffset;
                if (offset >= annexB.Length) break;
            }

            var display = types.Count == 0 ? "none" : string.Join(',', types.Take(16));
            if (types.Count > 16) display += ",…";
            return new H264Analysis(types.Count, hasSps, hasPps, hasIdr, hasSlice, display);
        }

        public static void CopyParameterSets(
            ReadOnlySpan<byte> annexB,
            ArrayBufferWriter<byte> destination,
            int maxBytes)
        {
            var offset = 0;
            while (TryFindStartCode(annexB, offset, out var startCodeOffset, out var startCodeLength))
            {
                var nalStart = startCodeOffset + startCodeLength;
                if (nalStart >= annexB.Length) break;

                if (!TryFindStartCode(annexB, nalStart, out var nextStartCodeOffset, out _))
                {
                    nextStartCodeOffset = annexB.Length;
                }

                var type = annexB[nalStart] & 0x1F;
                var nalLength = nextStartCodeOffset - startCodeOffset;
                if (type is 7 or 8 && nalLength > 0 && destination.WrittenCount + nalLength <= maxBytes)
                {
                    annexB.Slice(startCodeOffset, nalLength).CopyTo(destination.GetSpan(nalLength));
                    destination.Advance(nalLength);
                }

                offset = nextStartCodeOffset;
                if (offset >= annexB.Length) break;
            }
        }

        private static bool TryFindStartCode(ReadOnlySpan<byte> data, int start, out int offset, out int length)
        {
            for (var i = Math.Max(0, start); i <= data.Length - 3; i++)
            {
                if (data[i] != 0 || data[i + 1] != 0) continue;
                if (data[i + 2] == 1)
                {
                    offset = i;
                    length = 3;
                    return true;
                }
                if (i <= data.Length - 4 && data[i + 2] == 0 && data[i + 3] == 1)
                {
                    offset = i;
                    length = 4;
                    return true;
                }
            }

            offset = -1;
            length = 0;
            return false;
        }
    }
}
