using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OpenDisplayReceiver;

internal sealed class FrameStats
{
    private const long SlowSinkWriteMilliseconds = 20;
    private long _windowStartTimestamp = Stopwatch.GetTimestamp();
    private long _lastFrameTimestamp;
    private long _frames;
    private long _bytes;
    private int _stalls;
    private int _maxFrameBytes;
    private long _sinkWriteTicks;
    private long _maxSinkWriteTicks;
    private int _slowSinkWrites;
    private int _receiveBufferBytes;
    private int _latestControlRttMs = -1;
    private long _totalFrames;
    private long _totalBytes;
    private int _previousGen0Collections = GC.CollectionCount(0);
    private int _previousGen1Collections = GC.CollectionCount(1);
    private int _previousGen2Collections = GC.CollectionCount(2);

    public void RecordFrame(int bytes, long sinkWriteTicks)
    {
        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Exchange(ref _lastFrameTimestamp, now);
        if (previous != 0 && now - previous > Stopwatch.Frequency / 20)
        {
            Interlocked.Increment(ref _stalls);
        }

        Interlocked.Increment(ref _frames);
        Interlocked.Add(ref _bytes, bytes);
        Interlocked.Increment(ref _totalFrames);
        Interlocked.Add(ref _totalBytes, bytes);
        Interlocked.Add(ref _sinkWriteTicks, sinkWriteTicks);
        UpdateMax(ref _maxFrameBytes, bytes);
        UpdateMax(ref _maxSinkWriteTicks, sinkWriteTicks);
        if (sinkWriteTicks * 1000 >= Stopwatch.Frequency * SlowSinkWriteMilliseconds)
        {
            Interlocked.Increment(ref _slowSinkWrites);
        }
    }

    public void SetReceiveBufferCapacity(int bytes) => Volatile.Write(ref _receiveBufferBytes, bytes);

    public void RecordControlRtt(double milliseconds) =>
        Volatile.Write(ref _latestControlRttMs, (int)Math.Round(milliseconds));

    public Snapshot TakeSnapshot()
    {
        var now = Stopwatch.GetTimestamp();
        var start = Interlocked.Exchange(ref _windowStartTimestamp, now);
        var elapsed = Math.Max((double)(now - start) / Stopwatch.Frequency, 0.001);
        var frames = Interlocked.Exchange(ref _frames, 0);
        var bytes = Interlocked.Exchange(ref _bytes, 0);
        var sinkWriteTicks = Interlocked.Exchange(ref _sinkWriteTicks, 0);
        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);

        return new Snapshot(
            Fps: (int)Math.Round(frames / elapsed),
            Mbps: bytes * 8 / elapsed / 1_000_000,
            Stalls: Interlocked.Exchange(ref _stalls, 0),
            MaxFrameBytes: Interlocked.Exchange(ref _maxFrameBytes, 0),
            AverageSinkWriteMs: frames == 0 ? 0 : TicksToMilliseconds(sinkWriteTicks) / frames,
            MaxSinkWriteMs: TicksToMilliseconds(Interlocked.Exchange(ref _maxSinkWriteTicks, 0)),
            SlowSinkWrites: Interlocked.Exchange(ref _slowSinkWrites, 0),
            ReceiveBufferBytes: Volatile.Read(ref _receiveBufferBytes),
            ControlRttMs: Volatile.Read(ref _latestControlRttMs),
            ManagedMemoryBytes: GC.GetTotalMemory(forceFullCollection: false),
            WorkingSetBytes: Environment.WorkingSet,
            Gen0Collections: gen0 - Interlocked.Exchange(ref _previousGen0Collections, gen0),
            Gen1Collections: gen1 - Interlocked.Exchange(ref _previousGen1Collections, gen1),
            Gen2Collections: gen2 - Interlocked.Exchange(ref _previousGen2Collections, gen2));
    }

    public Totals GetTotals() => new(
        Frames: Interlocked.Read(ref _totalFrames),
        Bytes: Interlocked.Read(ref _totalBytes));

    private static double TicksToMilliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;

    private static void UpdateMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current) return;
            current = observed;
        }
    }

    private static void UpdateMax(ref long target, long value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current) return;
            current = observed;
        }
    }

    public readonly record struct Snapshot(
        int Fps,
        double Mbps,
        int Stalls,
        int MaxFrameBytes,
        double AverageSinkWriteMs,
        double MaxSinkWriteMs,
        int SlowSinkWrites,
        int ReceiveBufferBytes,
        int ControlRttMs,
        long ManagedMemoryBytes,
        long WorkingSetBytes,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections);

    public readonly record struct Totals(long Frames, long Bytes);
}

internal static class NetworkAddresses
{
    public static IEnumerable<IPAddress> GetLocalIPv4Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                          nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                          nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Where(addr => addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                           !IPAddress.IsLoopback(addr.Address))
            .Select(addr => addr.Address)
            .Distinct();
}

internal static class Clock
{
    public static double NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
