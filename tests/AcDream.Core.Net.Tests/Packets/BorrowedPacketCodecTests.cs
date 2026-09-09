using System.Buffers.Binary;
using AcDream.Core.Net.Cryptography;
using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Tests.Packets;

public class BorrowedPacketCodecTests
{
    [Fact]
    public void TryParseBorrowed_AllOptionalFieldsAndFragments_MatchesOwnedDecoder()
    {
        const PacketHeaderFlags flags =
            PacketHeaderFlags.ServerSwitch
            | PacketHeaderFlags.RequestRetransmit
            | PacketHeaderFlags.RejectRetransmit
            | PacketHeaderFlags.AckSequence
            | PacketHeaderFlags.WorldLoginRequest
            | PacketHeaderFlags.ConnectRequest
            | PacketHeaderFlags.ConnectResponse
            | PacketHeaderFlags.CICMDCommand
            | PacketHeaderFlags.TimeSync
            | PacketHeaderFlags.EchoRequest
            | PacketHeaderFlags.Flow
            | PacketHeaderFlags.BlobFragments;

        var optional = new byte[8 + 12 + 8 + 4 + 8 + 32 + 8 + 8 + 8 + 4 + 6];
        int position = 0;
        Fill(optional, ref position, 8, 0x11);

        WriteUInt32(optional, ref position, 2);
        WriteUInt32(optional, ref position, 0x10111213);
        WriteUInt32(optional, ref position, 0x20212223);

        WriteUInt32(optional, ref position, 1);
        WriteUInt32(optional, ref position, 0x30313233);

        WriteUInt32(optional, ref position, 0x40414243);
        Fill(optional, ref position, 8, 0x44);

        double serverTime = 12345.6789;
        ulong cookie = 0xFEEDFACECAFEBABE;
        uint clientId = 0x50515253;
        uint serverSeed = 0x60616263;
        uint clientSeed = 0x70717273;
        WriteDouble(optional, ref position, serverTime);
        WriteUInt64(optional, ref position, cookie);
        WriteUInt32(optional, ref position, clientId);
        WriteUInt32(optional, ref position, serverSeed);
        WriteUInt32(optional, ref position, clientSeed);
        WriteUInt32(optional, ref position, 0);

        Fill(optional, ref position, 8, 0x81);
        Fill(optional, ref position, 8, 0x91);

        double timeSync = 9876.54321;
        float echoTime = 42.25f;
        WriteDouble(optional, ref position, timeSync);
        WriteSingle(optional, ref position, echoTime);
        WriteUInt32(optional, ref position, 0xA0A1A2A3);
        WriteUInt16(optional, ref position, 0xB0B1);
        Assert.Equal(optional.Length, position);

        byte[] first = BuildFragment(
            sequence: 99,
            count: 2,
            index: 0,
            queue: 7,
            payload: [0xC1, 0xC2]);
        byte[] second = BuildFragment(
            sequence: 99,
            count: 2,
            index: 1,
            queue: 7,
            payload: [0xD1, 0xD2, 0xD3]);

        byte[] body = [.. optional, .. first, .. second];
        byte[] datagram = Encode(flags, body);

        PacketCodec.PacketDecodeResult owned =
            PacketCodec.TryDecode(datagram, inboundIsaac: null);
        bool parsedOk = PacketCodec.TryParseBorrowed(
            datagram,
            out BorrowedPacket borrowed,
            out uint headerHash,
            out uint payloadHash,
            out PacketCodec.DecodeError error);

        Assert.True(parsedOk);
        Assert.Equal(PacketCodec.DecodeError.None, error);
        AssertEquivalent(owned, borrowed);
        AssertCleartextChecksumHolds(borrowed, headerHash, payloadHash);
        Assert.Equal(serverTime, borrowed.Optional.ConnectRequestServerTime);
        Assert.Equal(cookie, borrowed.Optional.ConnectRequestCookie);
        Assert.Equal(clientId, borrowed.Optional.ConnectRequestClientId);
        Assert.Equal(serverSeed, borrowed.Optional.ConnectRequestServerSeed);
        Assert.Equal(clientSeed, borrowed.Optional.ConnectRequestClientSeed);
        Assert.Equal(timeSync, borrowed.Optional.TimeSync);
        Assert.Equal(echoTime, borrowed.Optional.EchoRequestClientTime);
        Assert.Equal(0xA0A1A2A3u, borrowed.Optional.FlowBytes);
        Assert.Equal(0xB0B1, borrowed.Optional.FlowInterval);
        Assert.Equal(
            new uint[] { 0x10111213, 0x20212223 },
            ReadIds(
                borrowed.Optional.RetransmitRequestBytes,
                borrowed.Optional.RetransmitRequestCount));

        // N2: the RejectRetransmit ids are exposed on both decoders (they
        // were always inside the hashed span; only the views are new).
        Assert.Equal(
            new uint[] { 0x30313233 },
            ReadIds(
                borrowed.Optional.RejectRetransmitBytes,
                borrowed.Optional.RejectRetransmitCount));
        Assert.Equal(
            new uint[] { 0x30313233 },
            owned.Packet!.Optional.RejectRetransmits);
    }

    [Fact]
    public void TryParseBorrowed_LoginPayload_MatchesOwnedDecoder()
    {
        byte[] payload = LoginRequest.Build("borrowed", "packet", 47);
        byte[] datagram = Encode(PacketHeaderFlags.LoginRequest, payload);

        PacketCodec.PacketDecodeResult owned =
            PacketCodec.TryDecode(datagram, inboundIsaac: null);
        Assert.True(PacketCodec.TryParseBorrowed(
            datagram,
            out BorrowedPacket borrowed,
            out uint headerHash,
            out uint payloadHash,
            out _));

        AssertEquivalent(owned, borrowed);
        AssertCleartextChecksumHolds(borrowed, headerHash, payloadHash);
        Assert.Equal(payload, borrowed.Optional.RawBytes.ToArray());
        Assert.Equal(payload, borrowed.Body.ToArray());
        Assert.Equal(0, borrowed.FragmentCount);
    }

    [Fact]
    public void VerifyChecksum_EncryptedForm_MatchesOwnedDecoder()
    {
        byte[] body = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 0x12345678);
        byte[] seed = [0x44, 0x33, 0x22, 0x11];
        byte[] datagram = Encode(
            PacketHeaderFlags.AckSequence
            | PacketHeaderFlags.EncryptedChecksum,
            body,
            new IsaacRandom(seed));

        PacketCodec.PacketDecodeResult owned =
            PacketCodec.TryDecode(
                datagram,
                new IsaacRandom(seed));
        Assert.True(owned.IsOk);
        Assert.True(PacketCodec.TryParseBorrowed(
            datagram,
            out BorrowedPacket borrowed,
            out uint headerHash,
            out uint payloadHash,
            out _));
        AssertEquivalent(owned, borrowed);

        PacketHeader header = borrowed.Header;
        uint key = new IsaacRandom(seed).Next();
        Assert.True(PacketCodec.VerifyChecksum(
            in header, headerHash, payloadHash, key));
        Assert.False(PacketCodec.VerifyChecksum(
            in header, headerHash, payloadHash, key ^ 1u));
        Assert.False(PacketCodec.VerifyChecksum(
            in header, headerHash, payloadHash, isaacKey: null));
    }

    [Fact]
    public void TryParseBorrowed_MalformedPackets_MatchOwnedDecoderErrors()
    {
        byte[] shortHeader = new byte[PacketHeader.Size - 1];
        AssertSameParseError(shortHeader);

        var oversized = new byte[PacketHeader.Size + 2];
        new PacketHeader { DataSize = 3 }.Pack(oversized);
        AssertSameParseError(oversized);

        byte[] shortOptional = EncodeUnchecked(
            PacketHeaderFlags.TimeSync,
            [0x01, 0x02, 0x03, 0x04]);
        AssertSameParseError(shortOptional);

        byte[] zeroCount = BuildFragment(
            sequence: 1,
            count: 0,
            index: 0,
            queue: 0,
            payload: [0x01]);
        AssertSameParseError(EncodeUnchecked(
            PacketHeaderFlags.BlobFragments,
            zeroCount));

        byte[] invalidIndex = BuildFragment(
            sequence: 1,
            count: 1,
            index: 1,
            queue: 0,
            payload: [0x01]);
        AssertSameParseError(EncodeUnchecked(
            PacketHeaderFlags.BlobFragments,
            invalidIndex));

        byte[] wrongChecksum = Encode(
            PacketHeaderFlags.AckSequence,
            [1, 2, 3, 4]);
        wrongChecksum[8] ^= 0x80;
        Assert.Equal(
            PacketCodec.DecodeError.ChecksumMismatch,
            PacketCodec.TryDecode(wrongChecksum, inboundIsaac: null).Error);
        Assert.True(PacketCodec.TryParseBorrowed(
            wrongChecksum,
            out BorrowedPacket parsed,
            out uint headerHash,
            out uint payloadHash,
            out PacketCodec.DecodeError error));
        Assert.Equal(PacketCodec.DecodeError.None, error);
        PacketHeader header = parsed.Header;
        Assert.False(PacketCodec.VerifyChecksum(
            in header, headerHash, payloadHash, isaacKey: null));
    }

    [Fact]
    public void TryParseBorrowed_WarmSingleFragmentPath_AllocatesNothing()
    {
        byte[] fragment = BuildFragment(
            sequence: 77,
            count: 1,
            index: 0,
            queue: 5,
            payload: [0xAA, 0xBB, 0xCC, 0xDD]);
        byte[] datagram = Encode(
            PacketHeaderFlags.BlobFragments,
            fragment);

        int checksum = DecodeAndRead(datagram);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1_000; iteration++)
            checksum += DecodeAndRead(datagram);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotEqual(0, checksum);
        Assert.Equal(0, allocated);
    }

    private static int DecodeAndRead(ReadOnlyMemory<byte> datagram)
    {
        if (!PacketCodec.TryParseBorrowed(
                datagram,
                out BorrowedPacket decoded,
                out uint headerHash,
                out uint payloadHash,
                out _))
        {
            return 0;
        }

        PacketHeader header = decoded.Header;
        if (!PacketCodec.VerifyChecksum(
                in header, headerHash, payloadHash, isaacKey: null))
        {
            return 0;
        }

        int checksum = decoded.FragmentCount;
        foreach (BorrowedMessageFragment fragment
                 in decoded.Fragments)
        {
            checksum += fragment.Header.TotalSize;
            checksum += fragment.Payload.Span[0];
        }

        return checksum;
    }

    private static void AssertCleartextChecksumHolds(
        in BorrowedPacket borrowed,
        uint headerHash,
        uint payloadHash)
    {
        PacketHeader header = borrowed.Header;
        Assert.True(PacketCodec.VerifyChecksum(
            in header, headerHash, payloadHash, isaacKey: null));
    }

    private static void AssertEquivalent(
        PacketCodec.PacketDecodeResult owned,
        in BorrowedPacket borrowed)
    {
        Assert.True(owned.IsOk);
        Packet packet = Assert.IsType<Packet>(owned.Packet);
        Assert.Equal(packet.Header, borrowed.Header);
        Assert.Equal(packet.BodyBytes, borrowed.Body.ToArray());
        Assert.Equal(
            packet.Optional.RawBytes,
            borrowed.Optional.RawBytes.ToArray());
        Assert.Equal(
            packet.Optional.AckSequence,
            borrowed.Optional.AckSequence);
        Assert.Equal(
            packet.Optional.TimeSync,
            borrowed.Optional.TimeSync);
        Assert.Equal(
            packet.Optional.EchoRequestClientTime,
            borrowed.Optional.EchoRequestClientTime);
        Assert.Equal(
            packet.Optional.FlowBytes,
            borrowed.Optional.FlowBytes);
        Assert.Equal(
            packet.Optional.FlowInterval,
            borrowed.Optional.FlowInterval);
        Assert.Equal(
            packet.Optional.ConnectRequestServerTime,
            borrowed.Optional.ConnectRequestServerTime);
        Assert.Equal(
            packet.Optional.ConnectRequestCookie,
            borrowed.Optional.ConnectRequestCookie);
        Assert.Equal(
            packet.Optional.ConnectRequestClientId,
            borrowed.Optional.ConnectRequestClientId);
        Assert.Equal(
            packet.Optional.ConnectRequestServerSeed,
            borrowed.Optional.ConnectRequestServerSeed);
        Assert.Equal(
            packet.Optional.ConnectRequestClientSeed,
            borrowed.Optional.ConnectRequestClientSeed);
        Assert.Equal(
            packet.Optional.RetransmitRequests,
            ReadIds(
                borrowed.Optional.RetransmitRequestBytes,
                borrowed.Optional.RetransmitRequestCount));
        Assert.Equal(
            packet.Optional.RejectRetransmits,
            ReadIds(
                borrowed.Optional.RejectRetransmitBytes,
                borrowed.Optional.RejectRetransmitCount));

        var fragments = new List<BorrowedMessageFragment>();
        foreach (BorrowedMessageFragment fragment
                 in borrowed.Fragments)
        {
            fragments.Add(fragment);
        }

        Assert.Equal(packet.Fragments.Count, borrowed.FragmentCount);
        Assert.Equal(packet.Fragments.Count, fragments.Count);
        for (int index = 0; index < fragments.Count; index++)
        {
            Assert.Equal(
                packet.Fragments[index].Header,
                fragments[index].Header);
            Assert.Equal(
                packet.Fragments[index].Payload,
                fragments[index].Payload.ToArray());
        }
    }

    private static uint[] ReadIds(
        ReadOnlyMemory<byte> idMemory,
        int count)
    {
        var ids = new uint[count];
        ReadOnlySpan<byte> bytes = idMemory.Span;
        for (int index = 0; index < ids.Length; index++)
        {
            ids[index] =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.Slice(index * 4));
        }

        return ids;
    }

    private static void AssertSameParseError(byte[] datagram)
    {
        PacketCodec.PacketDecodeResult owned =
            PacketCodec.TryDecode(datagram, inboundIsaac: null);
        Assert.False(PacketCodec.TryParseBorrowed(
            datagram,
            out _,
            out _,
            out _,
            out PacketCodec.DecodeError borrowedError));
        Assert.Equal(owned.Error, borrowedError);
    }

    private static byte[] Encode(
        PacketHeaderFlags flags,
        byte[] body,
        IsaacRandom? isaac = null) =>
        PacketCodec.Encode(
            new PacketHeader
            {
                Sequence = 0x01020304,
                Flags = flags,
                Id = 0x0506,
                Time = 0x0708,
                Iteration = 0x090A,
            },
            body,
            isaac);

    private static byte[] EncodeUnchecked(
        PacketHeaderFlags flags,
        byte[] body)
    {
        var header = new PacketHeader
        {
            Sequence = 0x01020304,
            Flags = flags,
            Id = 0x0506,
            Time = 0x0708,
            Iteration = 0x090A,
            DataSize = checked((ushort)body.Length),
        };
        header.Checksum =
            header.CalculateHeaderHash32()
            + Hash32.Calculate(body);
        byte[] datagram = new byte[PacketHeader.Size + body.Length];
        header.Pack(datagram);
        body.CopyTo(datagram, PacketHeader.Size);
        return datagram;
    }

    private static byte[] BuildFragment(
        uint sequence,
        ushort count,
        ushort index,
        ushort queue,
        byte[] payload)
    {
        var header = new MessageFragmentHeader
        {
            Sequence = sequence,
            Id = 0x80000000,
            Count = count,
            TotalSize = checked((ushort)(
                MessageFragmentHeader.Size + payload.Length)),
            Index = index,
            Queue = queue,
        };
        byte[] wire = new byte[header.TotalSize];
        header.Pack(wire);
        payload.CopyTo(wire, MessageFragmentHeader.Size);
        return wire;
    }

    private static void WriteUInt16(
        Span<byte> bytes,
        ref int position,
        ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.Slice(position),
            value);
        position += sizeof(ushort);
    }

    private static void WriteUInt32(
        Span<byte> bytes,
        ref int position,
        uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.Slice(position),
            value);
        position += sizeof(uint);
    }

    private static void WriteUInt64(
        Span<byte> bytes,
        ref int position,
        ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.Slice(position),
            value);
        position += sizeof(ulong);
    }

    private static void WriteSingle(
        Span<byte> bytes,
        ref int position,
        float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(
            bytes.Slice(position),
            value);
        position += sizeof(float);
    }

    private static void WriteDouble(
        Span<byte> bytes,
        ref int position,
        double value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(
            bytes.Slice(position),
            BitConverter.DoubleToInt64Bits(value));
        position += sizeof(double);
    }

    private static void Fill(
        Span<byte> bytes,
        ref int position,
        int count,
        byte value)
    {
        bytes.Slice(position, count).Fill(value);
        position += count;
    }
}
