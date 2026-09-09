using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Tests.Packets;

public sealed class TransportDisconnectTests
{
    [Fact]
    public void Build_MatchesRetailOptionalHeaderShape()
    {
        byte[] datagram = TransportDisconnect.Build(0x1234, 0x0001);

        Assert.Equal(PacketHeader.Size, datagram.Length);

        PacketHeader header = PacketHeader.Unpack(datagram);
        Assert.Equal(0u, header.Sequence);
        Assert.Equal(PacketHeaderFlags.Disconnect, header.Flags);
        Assert.Equal((ushort)0x1234, header.Id);
        Assert.Equal((ushort)0x0001, header.Iteration);
        Assert.Equal((ushort)0, header.DataSize);

        PacketCodec.PacketDecodeResult decoded = PacketCodec.TryDecode(
            datagram,
            inboundIsaac: null);
        Assert.True(decoded.IsOk);
    }
}
