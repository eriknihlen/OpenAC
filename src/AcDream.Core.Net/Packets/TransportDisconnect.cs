namespace AcDream.Core.Net.Packets;

public static class TransportDisconnect
{
    public static byte[] Build(ushort networkId, ushort iteration)
    {
        var header = new PacketHeader
        {
            Sequence = 0,
            Flags = PacketHeaderFlags.Disconnect,
            Id = networkId,
            Iteration = iteration,
        };

        return PacketCodec.Encode(
            header,
            ReadOnlySpan<byte>.Empty,
            outboundIsaac: null);
    }
}
