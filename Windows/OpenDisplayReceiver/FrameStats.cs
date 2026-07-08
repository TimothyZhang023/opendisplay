using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OpenDisplayReceiver;

internal sealed class FrameStats
{
    private readonly object _gate = new();
    private DateTime _start = DateTime.UtcNow;
    private DateTime? _lastFrame;
    private int _frames;
    private long _bytes;
    private int _stalls;

    public void RecordFrame(int bytes)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (_lastFrame is { } last && (now - last).TotalMilliseconds > 50)
            {
                _stalls++;
            }

            _lastFrame = now;
            _frames++;
            _bytes += bytes;
        }
    }

    public Snapshot TakeSnapshot()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var elapsed = Math.Max((now - _start).TotalSeconds, 0.001);
            var snapshot = new Snapshot(
                Fps: (int)Math.Round(_frames / elapsed),
                Mbps: _bytes * 8 / elapsed / 1_000_000,
                Stalls: _stalls);

            _start = now;
            _frames = 0;
            _bytes = 0;
            _stalls = 0;
            return snapshot;
        }
    }

    public readonly record struct Snapshot(int Fps, double Mbps, int Stalls);
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
