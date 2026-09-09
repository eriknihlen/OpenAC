using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using AcDream.Core.Net.Cryptography;
using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Tests;

public sealed class WorldSessionNegotiationShutdownTests
{
    [Fact]
    public async Task ThrowingServerTimeSubscriber_StillDisposesWithNegotiatedDisconnect()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var serverEndpoint = (IPEndPoint)server.Client.LocalEndPoint!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task<byte[]> serverFlow = Task.Run(async () =>
        {
            UdpReceiveResult login = await server.ReceiveAsync(timeout.Token);
            byte[] connectRequest = BuildConnectRequest(
                clientId: 0x1234,
                iteration: 7,
                serverTime: 12345.5);
            await server.SendAsync(connectRequest, login.RemoteEndPoint, timeout.Token);

            UdpReceiveResult disconnect = await server.ReceiveAsync(timeout.Token);
            return disconnect.Buffer;
        }, timeout.Token);

        using var session = new WorldSession(serverEndpoint);
        session.ServerTimeUpdated += _ =>
            throw new InvalidOperationException("subscriber failed");

        Assert.Throws<InvalidOperationException>(() =>
            session.Connect("typed-account", "password", TimeSpan.FromSeconds(3)));

        session.Dispose();
        byte[] datagram = await serverFlow;
        PacketCodec.PacketDecodeResult decoded =
            PacketCodec.TryDecode(datagram, inboundIsaac: null);

        Assert.True(decoded.IsOk, decoded.Error.ToString());
        Assert.True(decoded.Packet!.Header.HasFlag(PacketHeaderFlags.Disconnect));
        Assert.Equal((ushort)0x1234, decoded.Packet.Header.Id);
        Assert.Equal((ushort)7, decoded.Packet.Header.Iteration);
        Assert.Equal(WorldSession.State.Disconnected, session.CurrentState);
    }

    private static byte[] BuildConnectRequest(
        ushort clientId,
        ushort iteration,
        double serverTime)
    {
        byte[] body = new byte[32];
        BinaryPrimitives.WriteInt64LittleEndian(
            body,
            BitConverter.DoubleToInt64Bits(serverTime));
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8), 0xFEEDFACECAFEBABEUL);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), clientId);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(20), 0xAABBCCDDu);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(24), 0x01020304u);

        var header = new PacketHeader
        {
            Flags = PacketHeaderFlags.ConnectRequest,
            DataSize = (ushort)body.Length,
            Iteration = iteration,
        };
        header.Checksum = header.CalculateHeaderHash32() + Hash32.Calculate(body);

        byte[] datagram = new byte[PacketHeader.Size + body.Length];
        header.Pack(datagram);
        body.CopyTo(datagram.AsSpan(PacketHeader.Size));
        return datagram;
    }
}
