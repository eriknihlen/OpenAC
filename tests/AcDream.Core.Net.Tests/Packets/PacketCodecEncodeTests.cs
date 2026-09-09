using AcDream.Core.Net.Cryptography;
using AcDream.Core.Net.Messages;
using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Tests.Packets;

public class PacketCodecEncodeTests
{
    [Fact]
    public void Encode_Then_TryDecode_UnencryptedChecksum_RoundTrips()
    {
        // 4-byte ack sequence in the optional section, no fragments.
        byte[] body = new byte[4] { 0x78, 0x56, 0x34, 0x12 };
        var header = new PacketHeader
        {
            Sequence = 7,
            Flags    = PacketHeaderFlags.AckSequence,
            Id       = 1,
            Time     = 100,
        };

        byte[] datagram = PacketCodec.Encode(header, body, outboundIsaac: null);

        var result = PacketCodec.TryDecode(datagram, inboundIsaac: null);
        Assert.Equal(PacketCodec.DecodeError.None, result.Error);
        Assert.Equal(0x12345678u, result.Packet!.Optional.AckSequence);
    }

    [Fact]
    public void Encode_DataSizeIsOverwritten_MatchesBodyLength()
    {
        var header = new PacketHeader { Flags = PacketHeaderFlags.AckSequence, DataSize = 9999 };
        byte[] body = new byte[4];
        byte[] datagram = PacketCodec.Encode(header, body, null);

        var parsed = PacketHeader.Unpack(datagram);
        Assert.Equal(4, parsed.DataSize);
    }

    [Fact]
    public void Encode_EncryptedChecksum_PairsWithDecodeUsingSameIsaac()
    {
        var seed = new byte[] { 0xAB, 0xCD, 0xEF, 0x01 };
        var isaacSend = new IsaacRandom(seed);
        var isaacRecv = new IsaacRandom(seed);

        byte[] body = new byte[4] { 1, 2, 3, 4 };
        var header = new PacketHeader
        {
            Flags = PacketHeaderFlags.AckSequence | PacketHeaderFlags.EncryptedChecksum,
        };

        byte[] datagram = PacketCodec.Encode(header, body, isaacSend);
        var result = PacketCodec.TryDecode(datagram, isaacRecv);

        Assert.Equal(PacketCodec.DecodeError.None, result.Error);
    }

    [Fact]
    public void Encode_EncryptedChecksum_WithoutIsaac_Throws()
    {
        var header = new PacketHeader { Flags = PacketHeaderFlags.EncryptedChecksum };
        Assert.Throws<InvalidOperationException>(
            () => PacketCodec.Encode(header, ReadOnlySpan<byte>.Empty, outboundIsaac: null));
    }

    [Fact]
    public void Encode_LoginRequest_DecodableAndBodyRoundTrips()
    {
        byte[] loginPayload = LoginRequest.Build("testaccount", "testpassword", 42);
        var header = new PacketHeader { Flags = PacketHeaderFlags.LoginRequest };

        byte[] datagram = PacketCodec.Encode(header, loginPayload, outboundIsaac: null);

        var decoded = PacketCodec.TryDecode(datagram, inboundIsaac: null);
        Assert.Equal(PacketCodec.DecodeError.None, decoded.Error);
        var parsed = LoginRequest.Parse(decoded.Packet!.BodyBytes);
        Assert.Equal("testaccount", parsed.Account);
        Assert.Equal("testpassword", parsed.Password);
    }

    [Fact]
    public void Encode_FragmentBodyWithBlobFragmentsFlag_DecodeRebuildsFragment()
    {
        // Build a fragment body (16-byte header + 3-byte payload), set
        // BlobFragments flag, encode, decode, verify fragment is intact.
        var fragHeader = new MessageFragmentHeader
        {
            Sequence = 1, Id = 0x80000001u, Count = 1,
            TotalSize = 19, Index = 0, Queue = 0,
        };
        byte[] body = new byte[19];
        fragHeader.Pack(body);
        body[16] = 0xAA; body[17] = 0xBB; body[18] = 0xCC;

        var header = new PacketHeader { Flags = PacketHeaderFlags.BlobFragments };
        byte[] datagram = PacketCodec.Encode(header, body, null);

        var result = PacketCodec.TryDecode(datagram, null);
        Assert.Equal(PacketCodec.DecodeError.None, result.Error);
        Assert.Single(result.Packet!.Fragments);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, result.Packet.Fragments[0].Payload);
    }

    [Fact]
    public void FinalizeInPlace_DirectFragment_MatchesOwnedFraming()
    {
        byte[] message = [0x58, 0xF6, 0, 0, 1, 2, 3, 4];
        const uint fragmentSequence = 91;
        var ownedFragment =
            GameMessageFragment.BuildSingleFragment(
                fragmentSequence,
                GameMessageGroup.UIQueue,
                message);
        byte[] ownedBody =
            GameMessageFragment.Serialize(ownedFragment);
        var header = new PacketHeader
        {
            Sequence = 44,
            Flags = PacketHeaderFlags.BlobFragments
                    | PacketHeaderFlags.EncryptedChecksum,
            Id = 12,
            Time = 13,
            Iteration = 14,
        };
        byte[] seed = [1, 2, 3, 4];
        byte[] expected = PacketCodec.Encode(
            header,
            ownedBody,
            new IsaacRandom(seed));

        Span<byte> actual = stackalloc byte[
            PacketHeader.Size
            + MessageFragmentHeader.MaxFragmentSize];
        int fragmentLength =
            GameMessageFragment.WriteSingleFragment(
                actual.Slice(PacketHeader.Size),
                fragmentSequence,
                GameMessageGroup.UIQueue,
                message);
        int datagramLength = PacketCodec.FinalizeInPlace(
            header,
            actual,
            fragmentLength,
            optionalLength: 0,
            new IsaacRandom(seed));

        Assert.Equal(expected.Length, datagramLength);
        Assert.Equal(
            expected,
            actual.Slice(0, datagramLength).ToArray());
    }

    [Fact]
    public void FinalizeInPlace_DirectAck_MatchesOwnedFraming()
    {
        const uint acknowledgedSequence = 0x12345678;
        var header = new PacketHeader
        {
            Sequence = 22,
            Flags = PacketHeaderFlags.AckSequence,
            Id = 7,
        };
        byte[] body = new byte[sizeof(uint)];
        System.Buffers.Binary.BinaryPrimitives
            .WriteUInt32LittleEndian(
                body,
                acknowledgedSequence);
        byte[] expected = PacketCodec.Encode(
            header,
            body,
            outboundIsaac: null);
        Span<byte> actual = stackalloc byte[
            PacketHeader.Size + sizeof(uint)];
        body.CopyTo(actual.Slice(PacketHeader.Size));

        int datagramLength = PacketCodec.FinalizeInPlace(
            header,
            actual,
            bodyLength: sizeof(uint),
            optionalLength: sizeof(uint),
            outboundIsaac: null);

        Assert.Equal(expected, actual.Slice(0, datagramLength).ToArray());
    }

    [Fact]
    public void FinalizeInPlace_MalformedFragment_DoesNotConsumeIsaac()
    {
        byte[] seed = [9, 8, 7, 6];
        var header = new PacketHeader
        {
            Sequence = 33,
            Flags = PacketHeaderFlags.BlobFragments
                    | PacketHeaderFlags.EncryptedChecksum,
            Id = 5,
        };
        byte[] malformed = new byte[
            PacketHeader.Size + MessageFragmentHeader.Size];
        new MessageFragmentHeader
        {
            Count = 0,
            TotalSize = MessageFragmentHeader.Size,
        }.Pack(malformed.AsSpan(PacketHeader.Size));
        var afterFailure = new IsaacRandom(seed);
        Assert.Throws<ArgumentException>(
            () => PacketCodec.FinalizeInPlace(
                header,
                malformed,
                MessageFragmentHeader.Size,
                optionalLength: 0,
                afterFailure));

        byte[] message = [1, 2, 3, 4];
        Span<byte> actual = stackalloc byte[
            PacketHeader.Size
            + MessageFragmentHeader.MaxFragmentSize];
        int fragmentLength =
            GameMessageFragment.WriteSingleFragment(
                actual.Slice(PacketHeader.Size),
                fragmentSequence: 1,
                GameMessageGroup.UIQueue,
                message);
        int actualLength = PacketCodec.FinalizeInPlace(
            header,
            actual,
            fragmentLength,
            optionalLength: 0,
            afterFailure);

        var owned = GameMessageFragment.BuildSingleFragment(
            fragmentSequence: 1,
            GameMessageGroup.UIQueue,
            message);
        byte[] expected = PacketCodec.Encode(
            header,
            GameMessageFragment.Serialize(owned),
            new IsaacRandom(seed));
        Assert.Equal(
            expected,
            actual.Slice(0, actualLength).ToArray());
    }

    [Fact]
    public void DirectSingleFragmentFraming_WarmPath_AllocatesNothing()
    {
        byte[] message = [1, 2, 3, 4, 5, 6, 7, 8];
        var header = new PacketHeader
        {
            Sequence = 1,
            Flags = PacketHeaderFlags.BlobFragments
                    | PacketHeaderFlags.EncryptedChecksum,
            Id = 2,
        };
        var isaac = new IsaacRandom([4, 3, 2, 1]);
        Span<byte> datagram = stackalloc byte[
            PacketHeader.Size
            + MessageFragmentHeader.MaxFragmentSize];

        WriteDirect(datagram, header, isaac, message);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int checksum = 0;
        for (int iteration = 0; iteration < 1_000; iteration++)
        {
            checksum += WriteDirect(
                datagram,
                header,
                isaac,
                message);
        }
        long allocated =
            GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotEqual(0, checksum);
        Assert.Equal(0, allocated);
    }

    private static int WriteDirect(
        Span<byte> datagram,
        PacketHeader header,
        IsaacRandom isaac,
        ReadOnlySpan<byte> message)
    {
        int fragmentLength =
            GameMessageFragment.WriteSingleFragment(
                datagram.Slice(PacketHeader.Size),
                fragmentSequence: 3,
                GameMessageGroup.UIQueue,
                message);
        return PacketCodec.FinalizeInPlace(
            header,
            datagram,
            fragmentLength,
            optionalLength: 0,
            isaac);
    }
}
