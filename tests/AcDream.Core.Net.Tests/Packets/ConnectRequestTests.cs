using System.Buffers.Binary;
using AcDream.Core.Net.Cryptography;
using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Tests.Packets;

public class ConnectRequestTests
{
    [Fact]
    public void PacketCodec_Decodes_ConnectRequest_Fields()
    {
        byte[] body = new byte[32];
        double serverTime = 12345.6789;
        ulong cookie = 0xFEEDFACECAFEBABEUL;
        uint clientId = 0x11223344u;
        uint serverSeed = 0xAABBCCDDu;
        uint clientSeed = 0x01020304u;

        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(0), BitConverter.DoubleToInt64Bits(serverTime));
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8), cookie);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), clientId);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(20), serverSeed);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(24), clientSeed);
        // bytes 28-31 = trailing padding uint, zero is fine

        var header = new PacketHeader
        {
            Flags = PacketHeaderFlags.ConnectRequest,
            DataSize = (ushort)body.Length,
        };

        uint headerHash = header.CalculateHeaderHash32();
        uint optionalHash = Hash32.Calculate(body);
        header.Checksum = headerHash + optionalHash;

        byte[] datagram = new byte[PacketHeader.Size + body.Length];
        header.Pack(datagram);
        body.CopyTo(datagram.AsSpan(PacketHeader.Size));

        var result = PacketCodec.TryDecode(datagram, inboundIsaac: null);

        Assert.Equal(PacketCodec.DecodeError.None, result.Error);
        Assert.Equal(serverTime, result.Packet!.Optional.ConnectRequestServerTime, 1e-6);
        Assert.Equal(cookie,     result.Packet.Optional.ConnectRequestCookie);
        Assert.Equal(clientId,   result.Packet.Optional.ConnectRequestClientId);
        Assert.Equal(serverSeed, result.Packet.Optional.ConnectRequestServerSeed);
        Assert.Equal(clientSeed, result.Packet.Optional.ConnectRequestClientSeed);
    }

    [Fact]
    public void ConnectRequest_IsaacSeeds_FeedIsaacRandomSuccessfully()
    {
        byte[] body = new byte[32];
        uint serverSeed = 0xDEADBEEFu;
        uint clientSeed = 0xCAFEBABEu;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(20), serverSeed);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(24), clientSeed);

        var header = new PacketHeader { Flags = PacketHeaderFlags.ConnectRequest, DataSize = 32 };
        header.Checksum = header.CalculateHeaderHash32() + Hash32.Calculate(body);

        byte[] datagram = new byte[PacketHeader.Size + 32];
        header.Pack(datagram);
        body.CopyTo(datagram.AsSpan(PacketHeader.Size));

        var result = PacketCodec.TryDecode(datagram, inboundIsaac: null);
        Assert.Equal(PacketCodec.DecodeError.None, result.Error);

        // Reconstruct the seed bytes and feed IsaacRandom.
        var serverSeedBytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(serverSeedBytes, result.Packet!.Optional.ConnectRequestServerSeed);
        var clientSeedBytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(clientSeedBytes, result.Packet.Optional.ConnectRequestClientSeed);

        var serverIsaac = new IsaacRandom(serverSeedBytes);
        var clientIsaac = new IsaacRandom(clientSeedBytes);

        uint firstServerKey = serverIsaac.Next();
        uint firstClientKey = clientIsaac.Next();
        Assert.NotEqual(firstServerKey, firstClientKey);
    }
}
