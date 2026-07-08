using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OpenDisplayReceiver;

internal sealed class MdnsAdvertiser : IAsyncDisposable
{
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.251");
    private static readonly IPEndPoint MulticastEndpoint = new(MulticastAddress, 5353);
    private const string ServiceType = "_opensidecar._tcp.local";

    private readonly ReceiverOptions _options;
    private readonly Action<string> _log;
    private readonly string _instanceName;
    private readonly string _instanceFullName;
    private readonly string _hostName;
    private UdpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private Task? _announceTask;

    public MdnsAdvertiser(ReceiverOptions options, Action<string>? log = null)
    {
        _options = options;
        _log = log ?? Console.WriteLine;
        _instanceName = SanitizeLabel(options.DeviceName, 60);
        _instanceFullName = $"{_instanceName}.{ServiceType}";
        _hostName = SanitizeLabel(Environment.MachineName, 50).ToLowerInvariant() + ".local";
    }

    public Task StartAsync(CancellationToken token)
    {
        if (!_options.EnableMdns) return Task.CompletedTask;

        try
        {
            _client = new UdpClient(AddressFamily.InterNetwork);
            _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _client.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
            _client.JoinMulticastGroup(MulticastAddress);
            _client.MulticastLoopback = false;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            _listenTask = Task.Run(() => ListenAsync(_cts.Token));
            _announceTask = Task.Run(() => AnnounceLoopAsync(_cts.Token));
            _log($"Advertising Bonjour/mDNS service {_instanceFullName} on TCP {_options.Port}");
        }
        catch (Exception ex)
        {
            _log("mDNS/Bonjour advertisement unavailable: " + ex.Message);
            DisposeClient();
        }

        return Task.CompletedTask;
    }

    private async Task AnnounceLoopAsync(CancellationToken token)
    {
        try
        {
            await SendAnnouncementAsync(token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                await SendAnnouncementAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            _log("mDNS announcement stopped: " + ex.Message);
        }
    }

    private async Task ListenAsync(CancellationToken token)
    {
        if (_client is null) return;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var result = await _client.ReceiveAsync(token).ConfigureAwait(false);
                if (IsQueryForUs(result.Buffer))
                {
                    await SendAnnouncementAsync(token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (ObjectDisposedException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            _log("mDNS listener stopped: " + ex.Message);
        }
    }

    private async Task SendAnnouncementAsync(CancellationToken token)
    {
        if (_client is null) return;
        var packet = BuildResponsePacket();
        await _client.SendAsync(packet, packet.Length, MulticastEndpoint).WaitAsync(token).ConfigureAwait(false);
    }

    private bool IsQueryForUs(byte[] packet)
    {
        try
        {
            if (packet.Length < 12) return false;
            var flags = ReadUInt16(packet, 2);
            if ((flags & 0x8000) != 0) return false; // already a response

            var questionCount = ReadUInt16(packet, 4);
            var offset = 12;
            for (var i = 0; i < questionCount; i++)
            {
                var name = ReadName(packet, ref offset);
                if (offset + 4 > packet.Length) return false;
                offset += 4; // qtype + qclass

                if (name.Equals(ServiceType, StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(_instanceFullName, StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(_hostName, StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("_services._dns-sd._udp.local", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Ignore malformed packets.
        }

        return false;
    }

    private byte[] BuildResponsePacket()
    {
        var addresses = NetworkAddresses.GetLocalIPv4Addresses().ToArray();
        var records = new List<ResourceRecord>
        {
            ResourceRecord.Ptr(ServiceType, _instanceFullName),
            ResourceRecord.Srv(_instanceFullName, _options.Port, _hostName),
            ResourceRecord.Txt(_instanceFullName, BuildTxtValues()),
        };

        foreach (var address in addresses)
        {
            records.Add(ResourceRecord.A(_hostName, address));
        }

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        WriteUInt16(writer, 0);       // transaction id
        WriteUInt16(writer, 0x8400);  // response + authoritative answer
        WriteUInt16(writer, 0);       // questions
        WriteUInt16(writer, records.Count);
        WriteUInt16(writer, 0);       // authority
        WriteUInt16(writer, 0);       // additional

        foreach (var record in records)
        {
            WriteRecord(writer, record);
        }

        return ms.ToArray();
    }

    private IEnumerable<string> BuildTxtValues()
    {
        yield return "txtvers=1";
        yield return "id=" + _options.InstallId;
        yield return "name=" + _options.DeviceName;
        yield return "device=Windows";
        yield return "platform=windows";
        yield return "width=" + _options.PixelsWide;
        yield return "height=" + _options.PixelsHigh;
        yield return "scale=" + _options.Scale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void WriteRecord(BinaryWriter writer, ResourceRecord record)
    {
        WriteName(writer, record.Name);
        WriteUInt16(writer, record.Type);
        WriteUInt16(writer, record.CacheFlush ? 0x8001 : 0x0001);
        WriteUInt32(writer, record.Ttl);
        WriteUInt16(writer, record.Data.Length);
        writer.Write(record.Data);
    }

    private static void WriteName(BinaryWriter writer, string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            writer.Write((byte)Math.Min(bytes.Length, 63));
            writer.Write(bytes, 0, Math.Min(bytes.Length, 63));
        }
        writer.Write((byte)0);
    }

    private static string ReadName(byte[] packet, ref int offset)
    {
        var labels = new List<string>();
        var jumped = false;
        var originalOffset = offset;
        var guard = 0;

        while (offset < packet.Length && guard++ < 64)
        {
            var len = packet[offset++];
            if (len == 0) break;

            if ((len & 0xC0) == 0xC0)
            {
                if (offset >= packet.Length) break;
                var pointer = ((len & 0x3F) << 8) | packet[offset++];
                if (!jumped) originalOffset = offset;
                offset = pointer;
                jumped = true;
                continue;
            }

            if (offset + len > packet.Length) break;
            labels.Add(Encoding.UTF8.GetString(packet, offset, len));
            offset += len;
        }

        if (jumped) offset = originalOffset;
        return string.Join('.', labels);
    }

    private static ushort ReadUInt16(byte[] packet, int offset) =>
        (ushort)((packet[offset] << 8) | packet[offset + 1]);

    private static void WriteUInt16(BinaryWriter writer, int value)
    {
        writer.Write((byte)((value >> 8) & 0xFF));
        writer.Write((byte)(value & 0xFF));
    }

    private static void WriteUInt32(BinaryWriter writer, uint value)
    {
        writer.Write((byte)((value >> 24) & 0xFF));
        writer.Write((byte)((value >> 16) & 0xFF));
        writer.Write((byte)((value >> 8) & 0xFF));
        writer.Write((byte)(value & 0xFF));
    }

    private static string SanitizeLabel(string value, int maxLength)
    {
        var label = new string(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or ' ' ? ch : '-').ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(label)) label = "OpenDisplay Windows";
        return label.Length <= maxLength ? label : label[..maxLength].Trim();
    }

    private void DisposeClient()
    {
        try { _client?.DropMulticastGroup(MulticastAddress); } catch { }
        _client?.Dispose();
        _client = null;
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        DisposeClient();

        try
        {
            if (_listenTask is not null) await _listenTask.ConfigureAwait(false);
            if (_announceTask is not null) await _announceTask.ConfigureAwait(false);
        }
        catch
        {
            // Ignore shutdown races.
        }

        _cts?.Dispose();
    }

    private sealed record ResourceRecord(string Name, ushort Type, bool CacheFlush, uint Ttl, byte[] Data)
    {
        public static ResourceRecord Ptr(string name, string target)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
            WriteName(writer, target);
            return new ResourceRecord(name, 12, CacheFlush: false, Ttl: 120, ms.ToArray());
        }

        public static ResourceRecord Srv(string name, int port, string target)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
            WriteUInt16(writer, 0); // priority
            WriteUInt16(writer, 0); // weight
            WriteUInt16(writer, port);
            WriteName(writer, target);
            return new ResourceRecord(name, 33, CacheFlush: true, Ttl: 120, ms.ToArray());
        }

        public static ResourceRecord Txt(string name, IEnumerable<string> values)
        {
            using var ms = new MemoryStream();
            foreach (var value in values)
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                ms.WriteByte((byte)Math.Min(bytes.Length, 255));
                ms.Write(bytes, 0, Math.Min(bytes.Length, 255));
            }
            return new ResourceRecord(name, 16, CacheFlush: true, Ttl: 120, ms.ToArray());
        }

        public static ResourceRecord A(string name, IPAddress address) =>
            new(name, 1, CacheFlush: true, Ttl: 120, address.GetAddressBytes());
    }
}
