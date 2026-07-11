using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
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
    private Task? _activeClientTask;
    private long _nextConnectionId;
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
                var connectionId = Interlocked.Increment(ref _nextConnectionId);
                var remoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                Log($"[conn {connectionId}] Accepted {remoteEndpoint}; noDelay={client.NoDelay}; receiveBuffer={client.ReceiveBufferSize}");
                _status("Mac connected");

                _activeClient?.Cancel();
                _activeClient?.Dispose();
                _activeClient = CancellationTokenSource.CreateLinkedTokenSource(token);
                _activeClientTask = ObserveClientTaskAsync(
                    HandleClientAsync(client, connectionId, remoteEndpoint, _activeClient.Token),
                    connectionId);
            }
        }
        finally
        {
            listener.Stop();
            _activeClient?.Cancel();
            if (_activeClientTask is not null)
            {
                try
                {
                    await _activeClientTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log("Active client shutdown wait failed: " + ex);
                }
            }
            _activeClient?.Dispose();
        }
    }

    private async Task ObserveClientTaskAsync(Task sessionTask, long connectionId)
    {
        try
        {
            await sessionTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"[conn {connectionId}] Unhandled session task failure: {ex}");
            _status("Connection failed: " + ex.Message);
        }
    }

    private async Task HandleClientAsync(TcpClient client, long connectionId, string remoteEndpoint, CancellationToken token)
    {
        void ConnectionLog(string message) => Log($"[conn {connectionId}] {message}");

        var connectionStarted = Stopwatch.GetTimestamp();
        await using var videoSink = CreateVideoSink(ConnectionLog);
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
            ConnectionLog($"Session starting: remote={remoteEndpoint}; renderer={videoSink.Name}");
            await SendHelloAsync(stream, sendLock, ConnectionLog, connectionToken).ConfigureAwait(false);
            await RequestKeyframeAsync(stream, sendLock, "hello", connectionToken).ConfigureAwait(false);
            ConnectionLog("Requested H.264 keyframe immediately after hello");

            startupKeyframeTask = RunBackgroundTaskAsync(
                "startup keyframe loop",
                () => StartupKeyframeRetryLoopAsync(stream, sendLock, ConnectionLog, startupKeyframeCts.Token),
                startupKeyframeCts.Token,
                connectionCts,
                ConnectionLog);
            pingTask = RunBackgroundTaskAsync(
                "ping loop",
                () => PingLoopAsync(stream, sendLock, connectionToken),
                connectionToken,
                connectionCts,
                ConnectionLog);
            statsTask = RunBackgroundTaskAsync(
                "stats loop",
                () => StatsLoopAsync(stream, sendLock, stats, ConnectionLog, connectionToken),
                connectionToken,
                connectionCts,
                ConnectionLog);
            await ReadFramesAsync(
                stream,
                videoSink,
                stats,
                startupKeyframeCts.Cancel,
                ConnectionLog,
                connectionToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (connectionToken.IsCancellationRequested)
        {
            ConnectionLog("Session cancelled (receiver stopping or connection replaced)");
        }
        catch (EndOfStreamException)
        {
            ConnectionLog("Mac disconnected (end of stream)");
            _status("Mac disconnected");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            ConnectionLog($"ffplay.exe was not found: {ex}. Use the packaged release zip or pass --ffplay <path>.");
            _status("ffplay.exe not found");
        }
        catch (Exception ex)
        {
            ConnectionLog("Session failed: " + ex);
            _status("Connection failed: " + ex.Message);
        }
        finally
        {
            startupKeyframeCts.Cancel();
            connectionCts.Cancel();
            await Task.WhenAll(startupKeyframeTask, pingTask, statsTask).ConfigureAwait(false);
            var totals = stats.GetTotals();
            var elapsed = Stopwatch.GetElapsedTime(connectionStarted);
            ConnectionLog($"Session ended: duration={elapsed.TotalSeconds:0.0}s; frames={totals.Frames}; videoMiB={totals.Bytes / 1048576d:0.0}");
        }
    }

    private IVideoSink CreateVideoSink(Action<string> log) => _options.Renderer switch
    {
        VideoRendererKind.Native => new NativeH264Sink(_options, _videoHost, log),
        _ => new FfplaySink(_options, _videoHost, log),
    };

    private async Task SendHelloAsync(Stream stream, SemaphoreSlim sendLock, Action<string> log, CancellationToken token)
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
        log($"Sent hello: {_options.PixelsWide}x{_options.PixelsHigh} @ {_options.Scale:0.#}x");
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

    private static async Task StatsLoopAsync(
        Stream stream,
        SemaphoreSlim sendLock,
        FrameStats stats,
        Action<string> log,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            var snapshot = stats.TakeSnapshot();
            log(
                $"perf: fps={snapshot.Fps}; bitrate={snapshot.Mbps:0.0}Mbps; stalls={snapshot.Stalls}; " +
                $"frameMax={snapshot.MaxFrameBytes / 1024d:0}KiB; sinkWrite={snapshot.AverageSinkWriteMs:0.00}/{snapshot.MaxSinkWriteMs:0.00}ms(avg/max); " +
                $"slowWrites={snapshot.SlowSinkWrites}; rtt={snapshot.ControlRttMs}ms; rxBuffer={snapshot.ReceiveBufferBytes / 1024d:0}KiB; " +
                $"managed={snapshot.ManagedMemoryBytes / 1048576d:0.0}MiB; workingSet={snapshot.WorkingSetBytes / 1048576d:0.0}MiB; " +
                $"gc={snapshot.Gen0Collections}/{snapshot.Gen1Collections}/{snapshot.Gen2Collections}(gen0/1/2)");
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
        Action<string> log,
        CancellationToken token)
    {
        const int initialReceiveBufferBytes = 256 * 1024;
        const int maxPayloadBytes = 32 * 1024 * 1024;
        const int maxCachedParameterSetBytes = 64 * 1024;
        var header = new byte[4];
        var announcedVideo = false;
        var sinkStarted = false;
        var startupGate = new H264StartupGate(log);
        var startupParameterSets = new ArrayBufferWriter<byte>(256);
        var receiveBuffer = ArrayPool<byte>.Shared.Rent(initialReceiveBufferBytes);
        stats.SetReceiveBufferCapacity(receiveBuffer.Length);
        log($"Receive buffer initialized: capacity={receiveBuffer.Length} bytes");

        try
        {
            while (!token.IsCancellationRequested)
            {
                await ReadExactAsync(stream, header, token).ConfigureAwait(false);
                var rawLength = BinaryPrimitives.ReadUInt32BigEndian(header);
                if (rawLength == 0 || rawLength > maxPayloadBytes)
                {
                    throw new InvalidDataException($"Invalid payload length: {rawLength}; max={maxPayloadBytes}");
                }
                var length = (int)rawLength;

                if (length > receiveBuffer.Length)
                {
                    var previousCapacity = receiveBuffer.Length;
                    var largerBuffer = ArrayPool<byte>.Shared.Rent(length);
                    ArrayPool<byte>.Shared.Return(receiveBuffer);
                    receiveBuffer = largerBuffer;
                    stats.SetReceiveBufferCapacity(receiveBuffer.Length);
                    log($"Receive buffer grew: requested={length}; capacity={previousCapacity}->{receiveBuffer.Length} bytes");
                }

                var payload = receiveBuffer.AsMemory(0, length);
                await ReadExactAsync(stream, payload, token).ConfigureAwait(false);

                if (IsPureJson(payload.Span))
                {
                    HandleMacJson(payload, stats, log);
                    continue;
                }

                var startCode = FindAnnexBStartCode(payload.Span);
                if (startCode < 0)
                {
                    log($"Skipping non-JSON payload without Annex B start code ({payload.Length} bytes)");
                    continue;
                }

                var video = payload[startCode..];
                var analysis = default(H264Analysis);
                if (!sinkStarted)
                {
                    analysis = H264Analysis.Analyze(video.Span);
                    if (analysis.NalCount == 0)
                    {
                        log($"Skipping Annex B payload without complete NAL units ({video.Length} bytes)");
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
                        log($"Primed decoder with {startupParameterSets.WrittenCount} cached SPS/PPS bytes");
                    }

                    sinkStarted = true;
                    onRendererStarted();
                    log($"Video renderer started after H.264 sync: {videoSink.Name}; {analysis.Describe()}");
                }

                var sinkWriteStarted = Stopwatch.GetTimestamp();
                await videoSink.WriteAsync(video, token).ConfigureAwait(false);
                var sinkWriteTicks = Stopwatch.GetTimestamp() - sinkWriteStarted;
                stats.RecordFrame(video.Length, sinkWriteTicks);

                if (!announcedVideo)
                {
                    announcedVideo = true;
                    log($"Receiving video: {analysis.Describe()}");
                    _status("Receiving video");
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(receiveBuffer);
        }
    }

    private async Task StartupKeyframeRetryLoopAsync(
        Stream stream,
        SemaphoreSlim sendLock,
        Action<string> log,
        CancellationToken token)
    {
        try
        {
            var previousDelayMs = 0;
            for (var i = 0; i < StartupKeyframeRetryScheduleMs.Length; i++)
            {
                var scheduledDelayMs = StartupKeyframeRetryScheduleMs[i];
                await Task.Delay(scheduledDelayMs - previousDelayMs, token).ConfigureAwait(false);
                await RequestKeyframeAsync(stream, sendLock, $"startup-retry-{i + 1}", token).ConfigureAwait(false);
                log($"Repeated startup keyframe request #{i + 1} at {scheduledDelayMs} ms");
                previousDelayMs = scheduledDelayMs;
            }
            log("Startup keyframe retry schedule completed; still waiting for SPS/PPS/IDR if renderer has not started");
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

    private static async Task RunBackgroundTaskAsync(
        string name,
        Func<Task> operation,
        CancellationToken expectedCancellation,
        CancellationTokenSource connectionCts,
        Action<string> log)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (expectedCancellation.IsCancellationRequested)
        {
            // Expected when the renderer synchronizes or the connection ends.
        }
        catch (Exception ex)
        {
            log($"Background {name} failed; cancelling session: {ex}");
            connectionCts.Cancel();
        }
    }

    private static void HandleMacJson(ReadOnlyMemory<byte> payload, FrameStats stats, Action<string> log)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("type", out var typeElement)) return;
            var type = typeElement.GetString();

            if (type == "pong" && doc.RootElement.TryGetProperty("t", out var tElement) && tElement.TryGetDouble(out var t))
            {
                var rtt = Clock.NowMs - t;
                if (rtt >= 0 && rtt < 2000) stats.RecordControlRtt(rtt);
            }
        }
        catch (JsonException ex)
        {
            var prefix = Convert.ToHexString(payload.Span[..Math.Min(payload.Length, 16)]);
            log($"Malformed control JSON: bytes={payload.Length}; prefix={prefix}; error={ex.Message}");
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
