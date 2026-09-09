using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace AcDream.Launcher.Core.Status;

public sealed class UdpServerReachabilityProbe : IServerReachabilityProbe
{
    // The status identity is recognized without a password and never continues the handshake.
    private static readonly byte[] Request = Convert.FromHexString(
        "00000000000001009300D005000000004000000004003138303200003400000001000000000000003EB8A8581C006163736572766572747261636B65723A6A6A3968323668637367676300000000000000000000");

    public async Task<double?> ProbeAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var addresses = await Dns.GetHostAddressesAsync(host, timeout.Token).ConfigureAwait(false);
        var address = addresses.FirstOrDefault(value => value.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault();
        if (address is null) return null;
        using var client = new UdpClient(address.AddressFamily);
        client.Connect(new IPEndPoint(address, port));
        long started = Stopwatch.GetTimestamp();
        await client.SendAsync(Request, timeout.Token).ConfigureAwait(false);
        while (true)
        {
            var reply = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false);
            if (IsStatusReply(reply.Buffer))
                return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
    }

    internal static bool IsStatusReply(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 20) return false;
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(packet[4..]);
        int bodySize = BinaryPrimitives.ReadUInt16LittleEndian(packet[16..]);
        if (bodySize != packet.Length - 20) return false;
        // Accept the initial challenge or an explicit network rejection, not unrelated traffic.
        return flags switch
        {
            0x00040000 => bodySize == 32,
            0x00100000 or 0x00200000 => bodySize == 4,
            _ => false,
        };
    }
}
