using AcDream.Core.Net.Cryptography;
using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Tests.Packets;

public class PacketCodecTests
{
    [Fact]
    public void TryDecode_MinimalValidPacket_UnencryptedChecksum_Accepted()
    {
        byte[] body = new byte[4];
        body[0] = 0x78; body[1] = 0x56; body[2] = 0x34; body[3] = 0x12;

        var header = new PacketHeader
        {
            Sequence  = 1,
            Flags     = PacketHeaderFlags.AckSequence,
            Id        = 42,
            Time      = 100,
            DataSize  = (ushort)body.Length,
            Iteration = 0,
        };

        uint headerHash = header.CalculateHeaderHash32();
        uint optionalHash = Hash32.Calculate(body);
        header.Checksum = headerHash + optionalHash;

        // Assemble the datagram.
        byte[] datagram = new byte[PacketHeader.Size + body.Length];
        header.Pack(datagram);
        body.CopyTo(datagram.AsSpan(PacketHeader.Size));

        var result = PacketCodec.TryDecode(datagram, inboundIsaac: null);

        Assert.Equal(PacketCodec.DecodeError.None, result.Error);
        Assert.NotNull(result.Packet);
        Assert.Equal(0x12345678u, result.Packet!.Optional.AckSequence);
    }

    [Fact]
    public void TryDecode_WrongChecksum_Rejected()
    {
        byte[] body = new byte[4] { 1, 2, 3, 4 };
        var header = new PacketHeader
        {
            Flags = PacketHeaderFlags.AckSequence,
            DataSize = (ushort)body.Length,
            Checksum = 0xDEADBEEFu,
        };
        byte[] datagram = new byte[PacketHeader.Size + body.Length];
        header.Pack(datagram);
        body.CopyTo(datagram.AsSpan(PacketHeader.Size));

        var result = PacketCodec.TryDecode(datagram, inboundIsaac: null);
        Assert.Equal(PacketCodec.DecodeError.ChecksumMismatch, result.Error);
    }

    [Fact]
    public void TryDecode_BufferShorterThanHeader_ReturnsTooShort()
    {
        var result = PacketCodec.TryDecode(new byte[19], inboundIsaac: null);
        Assert.Equal(PacketCodec.DecodeError.TooShort, result.Error);
    }

    [Fact]
    public void TryDecode_DataSizeExceedsBuffer_Rejected()
    {
        var header = new PacketHeader { DataSize = 1000 };
        byte[] datagram = new byte[PacketHeader.Size + 10];  // only 10 body bytes available
        header.Pack(datagram);

        var result = PacketCodec.TryDecode(datagram, inboundIsaac: null);
        Assert.Equal(PacketCodec.DecodeError.HeaderSizeExceedsBuffer, result.Error);
    }

    [Fact]
    public void TryDecode_PacketWithOneFragment_ParsesAndValidatesCRC()
    {
        // Body: no optional section + one 16+3 = 19-byte fragment (BlobFragments flag).
        var fragHeader = new MessageFragmentHeader
        {
            Sequence = 1, Id = 0x80000001u, Count = 1,
            TotalSize = 19, Index = 0, Queue = 5,
        };
        byte[] fragBytes = new byte[19];
        fragHeader.Pack(fragBytes);
        fragBytes[16] = 0xAA; fragBytes[17] = 0xBB; fragBytes[18] = 0xCC;

        var header = new PacketHeader
        {
            Flags = PacketHeaderFlags.BlobFragments,
            DataSize = (ushort)fragBytes.Length,
        };

        uint headerHash = header.CalculateHeaderHash32();
        uint optionalHash = 0;  // empty optional section
        var frag = new MessageFragment(fragHeader, new byte[] { 0xAA, 0xBB, 0xCC });
        uint fragmentHash = PacketCodec.CalculateFragmentHash32(frag);
        header.Checksum = headerHash + optionalHash + fragmentHash;

        byte[] datagram = new byte[PacketHeader.Size + fragBytes.Length];
        header.Pack(datagram);
        fragBytes.CopyTo(datagram.AsSpan(PacketHeader.Size));

        var result = PacketCodec.TryDecode(datagram, inboundIsaac: null);

        Assert.Equal(PacketCodec.DecodeError.None, result.Error);
        Assert.Single(result.Packet!.Fragments);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, result.Packet.Fragments[0].Payload);
    }

    [Fact]
    public void TryDecode_EncryptedChecksum_AcceptsMatchingIsaacKey()
    {
        var body = new byte[4] { 1, 2, 3, 4 };
        var header = new PacketHeader
        {
            Flags = PacketHeaderFlags.AckSequence | PacketHeaderFlags.EncryptedChecksum,
            DataSize = (ushort)body.Length,
        };

        var seed = new byte[] { 0x44, 0x33, 0x22, 0x11 };
        var isaacForEncode = new IsaacRandom(seed);
        var isaacForDecode = new IsaacRandom(seed);

        uint headerHash = header.CalculateHeaderHash32();
        uint payloadHash = Hash32.Calculate(body);  // optional hash; no fragments
        uint isaacKey = isaacForEncode.Next();
        header.Checksum = headerHash + (isaacKey ^ payloadHash);

        byte[] datagram = new byte[PacketHeader.Size + body.Length];
        header.Pack(datagram);
        body.CopyTo(datagram.AsSpan(PacketHeader.Size));

        var result = PacketCodec.TryDecode(datagram, isaacForDecode);
        Assert.Equal(PacketCodec.DecodeError.None, result.Error);
    }

    [Fact]
    public void TryDecode_EncryptedChecksum_WrongIsaacKey_Rejected()
    {
        var body = new byte[4] { 1, 2, 3, 4 };
        var header = new PacketHeader
        {
            Flags = PacketHeaderFlags.AckSequence | PacketHeaderFlags.EncryptedChecksum,
            DataSize = (ushort)body.Length,
            Checksum = 0xDEADBEEFu,  // wrong
        };
        byte[] datagram = new byte[PacketHeader.Size + body.Length];
        header.Pack(datagram);
        body.CopyTo(datagram.AsSpan(PacketHeader.Size));

        var result = PacketCodec.TryDecode(datagram, new IsaacRandom(new byte[] { 1, 2, 3, 4 }));
        Assert.Equal(PacketCodec.DecodeError.ChecksumMismatch, result.Error);
    }
}
