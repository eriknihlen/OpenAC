using AcDream.Core.Net.Cryptography;

namespace AcDream.Core.Net.Packets;

public static class PacketCodec
{
    public enum DecodeError
    {
        None = 0,
        TooShort,                      // buffer shorter than 20-byte header
        HeaderSizeExceedsBuffer,       // header.DataSize + 20 > buffer.Length
        InvalidOptionalHeader,
        InvalidFragment,
        ChecksumMismatch,
    }

    public readonly record struct PacketDecodeResult(Packet? Packet, DecodeError Error)
    {
        public bool IsOk => Error == DecodeError.None;
    }

    public static PacketDecodeResult TryDecode(ReadOnlySpan<byte> datagram, IsaacRandom? inboundIsaac)
    {
        if (datagram.Length < PacketHeader.Size)
            return new PacketDecodeResult(null, DecodeError.TooShort);

        var packet = new Packet { Header = PacketHeader.Unpack(datagram) };
        int bodyLen = packet.Header.DataSize;
        if (datagram.Length - PacketHeader.Size < bodyLen)
            return new PacketDecodeResult(null, DecodeError.HeaderSizeExceedsBuffer);

        var body = datagram.Slice(PacketHeader.Size, bodyLen);
        packet.BodyBytes = body.ToArray();

        // Parse the optional header section first.
        int optionalConsumed = packet.Optional.Parse(body, packet.Header.Flags);
        if (optionalConsumed < 0)
            return new PacketDecodeResult(null, DecodeError.InvalidOptionalHeader);

        // If the BlobFragments flag is set, walk the body tail as a sequence
        // of fragments. LoginRequest packets don't have fragments (they have
        // the login payload in the optional section instead).
        if (packet.Header.HasFlag(PacketHeaderFlags.BlobFragments))
        {
            var fragSlice = body.Slice(optionalConsumed);
            while (fragSlice.Length > 0)
            {
                var (frag, consumed) = MessageFragment.TryParse(fragSlice);
                if (frag is null || consumed == 0)
                    return new PacketDecodeResult(null, DecodeError.InvalidFragment);
                packet.Fragments.Add(frag.Value);
                fragSlice = fragSlice.Slice(consumed);
            }
        }

        uint headerHash = packet.Header.CalculateHeaderHash32();
        uint optionalHash = packet.Optional.CalculateHash32();
        uint fragmentHash = 0;
        foreach (var frag in packet.Fragments)
            fragmentHash += CalculateFragmentHash32(frag);

        uint payloadHash = optionalHash + fragmentHash;

        if (packet.Header.HasFlag(PacketHeaderFlags.EncryptedChecksum))
        {
            if (inboundIsaac is null)
                return new PacketDecodeResult(null, DecodeError.ChecksumMismatch);

            // Expected key = (wireChecksum - headerHash) XOR payloadHash.
            // That key must equal the next ISAAC keystream word.
            uint expectedKey = (packet.Header.Checksum - headerHash) ^ payloadHash;
            uint isaacKey = inboundIsaac.Next();
            if (expectedKey != isaacKey)
                return new PacketDecodeResult(null, DecodeError.ChecksumMismatch);
        }
        else
        {
            uint expected = headerHash + payloadHash;
            if (packet.Header.Checksum != expected)
                return new PacketDecodeResult(null, DecodeError.ChecksumMismatch);
        }

        return new PacketDecodeResult(packet, DecodeError.None);
    }

    internal static bool TryParseBorrowed(
        ReadOnlyMemory<byte> datagram,
        out BorrowedPacket packet,
        out uint headerHash,
        out uint payloadHash,
        out DecodeError error)
    {
        packet = default;
        headerHash = 0;
        payloadHash = 0;

        ReadOnlySpan<byte> wire = datagram.Span;
        if (wire.Length < PacketHeader.Size)
        {
            error = DecodeError.TooShort;
            return false;
        }

        PacketHeader header = PacketHeader.Unpack(wire);
        int bodyLength = header.DataSize;
        if (wire.Length - PacketHeader.Size < bodyLength)
        {
            error = DecodeError.HeaderSizeExceedsBuffer;
            return false;
        }

        ReadOnlyMemory<byte> body = datagram.Slice(
            PacketHeader.Size,
            bodyLength);
        if (!TryParseBorrowedOptional(
                body,
                header.Flags,
                out BorrowedOptionalHeader optional,
                out int optionalConsumed))
        {
            error = DecodeError.InvalidOptionalHeader;
            return false;
        }

        ReadOnlyMemory<byte> fragmentBytes =
            ReadOnlyMemory<byte>.Empty;
        int fragmentCount = 0;
        uint fragmentHash = 0;
        if (header.HasFlag(PacketHeaderFlags.BlobFragments))
        {
            fragmentBytes = body.Slice(optionalConsumed);
            ReadOnlySpan<byte> remaining = fragmentBytes.Span;
            while (!remaining.IsEmpty)
            {
                if (!MessageFragment.TryParseLayout(
                        remaining,
                        out _,
                        out int payloadLength,
                        out int consumed))
                {
                    error = DecodeError.InvalidFragment;
                    return false;
                }

                fragmentHash +=
                    Hash32.Calculate(
                        remaining.Slice(
                            0,
                            MessageFragmentHeader.Size))
                    + Hash32.Calculate(
                        remaining.Slice(
                            MessageFragmentHeader.Size,
                            payloadLength));
                fragmentCount++;
                remaining = remaining.Slice(consumed);
            }
        }

        headerHash = header.CalculateHeaderHash32();
        payloadHash =
            Hash32.Calculate(optional.RawBytes.Span) + fragmentHash;
        packet = new BorrowedPacket(
            header,
            optional,
            body,
            fragmentBytes,
            fragmentCount);
        error = DecodeError.None;
        return true;
    }

    internal static bool VerifyChecksum(
        in PacketHeader header,
        uint headerHash,
        uint payloadHash,
        uint? isaacKey) =>
        isaacKey is uint key
            ? header.Checksum == headerHash + (key ^ payloadHash)
            : header.Checksum == headerHash + payloadHash;

    private static bool TryParseBorrowedOptional(
        ReadOnlyMemory<byte> bodyMemory,
        PacketHeaderFlags flags,
        out BorrowedOptionalHeader optional,
        out int consumed)
    {
        ReadOnlySpan<byte> body = bodyMemory.Span;
        int position = 0;
        uint ackSequence = 0;
        double timeSync = 0;
        float echoRequestClientTime = 0;
        uint flowBytes = 0;
        ushort flowInterval = 0;
        double connectRequestServerTime = 0;
        ulong connectRequestCookie = 0;
        uint connectRequestClientId = 0;
        uint connectRequestServerSeed = 0;
        uint connectRequestClientSeed = 0;
        int retransmitOffset = 0;
        int retransmitCount = 0;
        int rejectOffset = 0;
        int rejectCount = 0;

        if (HasFlag(flags, PacketHeaderFlags.ServerSwitch)
            && !Take(body, ref position, 8))
        {
            return Invalid(out optional, out consumed);
        }

        if (HasFlag(flags, PacketHeaderFlags.RequestRetransmit))
        {
            if (!Take(body, ref position, 4))
                return Invalid(out optional, out consumed);
            uint count = System.Buffers.Binary.BinaryPrimitives
                .ReadUInt32LittleEndian(
                    body.Slice(position - 4));
            if (count > 1024
                || body.Length - position < (int)count * 4)
            {
                return Invalid(out optional, out consumed);
            }

            retransmitOffset = position;
            retransmitCount = checked((int)count);
            position += retransmitCount * 4;
        }

        if (HasFlag(flags, PacketHeaderFlags.RejectRetransmit))
        {
            if (!Take(body, ref position, 4))
                return Invalid(out optional, out consumed);
            uint count = System.Buffers.Binary.BinaryPrimitives
                .ReadUInt32LittleEndian(
                    body.Slice(position - 4));
            if (count > 1024
                || body.Length - position < (int)count * 4)
            {
                return Invalid(out optional, out consumed);
            }

            rejectOffset = position;
            rejectCount = checked((int)count);
            position += rejectCount * 4;
        }

        if (HasFlag(flags, PacketHeaderFlags.AckSequence))
        {
            if (!Take(body, ref position, 4))
                return Invalid(out optional, out consumed);
            ackSequence =
                System.Buffers.Binary.BinaryPrimitives
                    .ReadUInt32LittleEndian(
                        body.Slice(position - 4));
        }

        if (HasFlag(flags, PacketHeaderFlags.LoginRequest))
        {
            position = body.Length;
            optional = BuildOptional(
                bodyMemory,
                position,
                ackSequence,
                timeSync,
                echoRequestClientTime,
                flowBytes,
                flowInterval,
                connectRequestServerTime,
                connectRequestCookie,
                connectRequestClientId,
                connectRequestServerSeed,
                connectRequestClientSeed,
                retransmitOffset,
                retransmitCount,
                rejectOffset,
                rejectCount);
            consumed = position;
            return true;
        }

        if (HasFlag(flags, PacketHeaderFlags.WorldLoginRequest)
            && !Take(body, ref position, 8))
        {
            return Invalid(out optional, out consumed);
        }

        if (HasFlag(flags, PacketHeaderFlags.ConnectRequest))
        {
            if (body.Length - position < 32)
                return Invalid(out optional, out consumed);

            connectRequestServerTime =
                BitConverter.Int64BitsToDouble(
                    System.Buffers.Binary.BinaryPrimitives
                        .ReadInt64LittleEndian(
                            body.Slice(position)));
            connectRequestCookie =
                System.Buffers.Binary.BinaryPrimitives
                    .ReadUInt64LittleEndian(
                        body.Slice(position + 8));
            connectRequestClientId =
                System.Buffers.Binary.BinaryPrimitives
                    .ReadUInt32LittleEndian(
                        body.Slice(position + 16));
            connectRequestServerSeed =
                System.Buffers.Binary.BinaryPrimitives
                    .ReadUInt32LittleEndian(
                        body.Slice(position + 20));
            connectRequestClientSeed =
                System.Buffers.Binary.BinaryPrimitives
                    .ReadUInt32LittleEndian(
                        body.Slice(position + 24));
            position += 32;
        }

        if (HasFlag(flags, PacketHeaderFlags.ConnectResponse)
            && !Take(body, ref position, 8))
        {
            return Invalid(out optional, out consumed);
        }

        if (HasFlag(flags, PacketHeaderFlags.CICMDCommand)
            && !Take(body, ref position, 8))
        {
            return Invalid(out optional, out consumed);
        }

        if (HasFlag(flags, PacketHeaderFlags.TimeSync))
        {
            if (!Take(body, ref position, 8))
                return Invalid(out optional, out consumed);
            timeSync = BitConverter.Int64BitsToDouble(
                System.Buffers.Binary.BinaryPrimitives
                    .ReadInt64LittleEndian(
                        body.Slice(position - 8)));
        }

        if (HasFlag(flags, PacketHeaderFlags.EchoRequest))
        {
            if (!Take(body, ref position, 4))
                return Invalid(out optional, out consumed);
            echoRequestClientTime =
                System.Buffers.Binary.BinaryPrimitives
                    .ReadSingleLittleEndian(
                        body.Slice(position - 4));
        }

        if (HasFlag(flags, PacketHeaderFlags.Flow))
        {
            if (!Take(body, ref position, 6))
                return Invalid(out optional, out consumed);
            flowBytes =
                System.Buffers.Binary.BinaryPrimitives
                    .ReadUInt32LittleEndian(
                        body.Slice(position - 6));
            flowInterval =
                System.Buffers.Binary.BinaryPrimitives
                    .ReadUInt16LittleEndian(
                        body.Slice(position - 2));
        }

        optional = BuildOptional(
            bodyMemory,
            position,
            ackSequence,
            timeSync,
            echoRequestClientTime,
            flowBytes,
            flowInterval,
            connectRequestServerTime,
            connectRequestCookie,
            connectRequestClientId,
            connectRequestServerSeed,
            connectRequestClientSeed,
            retransmitOffset,
            retransmitCount,
            rejectOffset,
            rejectCount);
        consumed = position;
        return true;
    }

    private static BorrowedOptionalHeader BuildOptional(
        ReadOnlyMemory<byte> body,
        int consumed,
        uint ackSequence,
        double timeSync,
        float echoRequestClientTime,
        uint flowBytes,
        ushort flowInterval,
        double connectRequestServerTime,
        ulong connectRequestCookie,
        uint connectRequestClientId,
        uint connectRequestServerSeed,
        uint connectRequestClientSeed,
        int retransmitOffset,
        int retransmitCount,
        int rejectOffset,
        int rejectCount) =>
        new(
            ackSequence,
            timeSync,
            echoRequestClientTime,
            flowBytes,
            flowInterval,
            connectRequestServerTime,
            connectRequestCookie,
            connectRequestClientId,
            connectRequestServerSeed,
            connectRequestClientSeed,
            body.Slice(0, consumed),
            retransmitCount == 0
                ? ReadOnlyMemory<byte>.Empty
                : body.Slice(
                    retransmitOffset,
                    retransmitCount * 4),
            retransmitCount,
            rejectCount == 0
                ? ReadOnlyMemory<byte>.Empty
                : body.Slice(
                    rejectOffset,
                    rejectCount * 4),
            rejectCount);

    private static bool Invalid(
        out BorrowedOptionalHeader optional,
        out int consumed)
    {
        optional = default;
        consumed = 0;
        return false;
    }

    private static bool HasFlag(
        PacketHeaderFlags all,
        PacketHeaderFlags bit) =>
        (all & bit) != 0;

    private static bool Take(
        ReadOnlySpan<byte> body,
        ref int position,
        int count)
    {
        if (body.Length - position < count)
            return false;
        position += count;
        return true;
    }

    public static byte[] Encode(PacketHeader header, ReadOnlySpan<byte> body, IsaacRandom? outboundIsaac)
    {
        var optional = new PacketHeaderOptional();
        int optionalLen = optional.Parse(body, header.Flags);
        if (optionalLen < 0)
            throw new ArgumentException("body's optional section is malformed", nameof(body));

        byte[] datagram = new byte[PacketHeader.Size + body.Length];
        body.CopyTo(datagram.AsSpan(PacketHeader.Size));
        FinalizeInPlace(
            header,
            datagram,
            body.Length,
            optionalLen,
            outboundIsaac);
        return datagram;
    }

    internal static int FinalizeInPlace(
        PacketHeader header,
        Span<byte> datagram,
        int bodyLength,
        int optionalLength,
        IsaacRandom? outboundIsaac) =>
        FinalizeInPlace(
            header,
            datagram,
            bodyLength,
            optionalLength,
            outboundIsaac,
            out _,
            out _);

    internal static int FinalizeInPlace(
        PacketHeader header,
        Span<byte> datagram,
        int bodyLength,
        int optionalLength,
        IsaacRandom? outboundIsaac,
        out uint isaacKeyUsed,
        out uint sealedChecksum)
    {
        if ((uint)bodyLength > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bodyLength));
        }

        int datagramLength = checked(PacketHeader.Size + bodyLength);
        if (datagram.Length < datagramLength)
        {
            throw new ArgumentException(
                $"datagram must be at least {datagramLength} bytes",
                nameof(datagram));
        }

        if ((uint)optionalLength > (uint)bodyLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(optionalLength));
        }

        ReadOnlySpan<byte> body = datagram.Slice(
            PacketHeader.Size,
            bodyLength);
        uint payloadHash = CalculatePayloadHash(
            body,
            header.Flags,
            optionalLength);

        header.DataSize = checked((ushort)bodyLength);
        uint headerHash = header.CalculateHeaderHash32();
        if (header.HasFlag(PacketHeaderFlags.EncryptedChecksum))
        {
            if (outboundIsaac is null)
            {
                throw new InvalidOperationException(
                    "EncryptedChecksum flag set but no ISAAC keystream provided");
            }

            uint isaacKey = outboundIsaac.Next();
            isaacKeyUsed = isaacKey;
            sealedChecksum = isaacKey ^ payloadHash;
        }
        else
        {
            isaacKeyUsed = 0;
            sealedChecksum = payloadHash;
        }

        header.Checksum = headerHash + sealedChecksum;
        header.Pack(datagram);
        return datagramLength;
    }

    private static uint CalculatePayloadHash(
        ReadOnlySpan<byte> body,
        PacketHeaderFlags flags,
        int optionalLength)
    {
        uint optionalHash = Hash32.Calculate(
            body.Slice(0, optionalLength));
        if ((flags & PacketHeaderFlags.BlobFragments) == 0)
        {
            if (optionalLength != body.Length)
            {
                throw new ArgumentException(
                    "non-fragment body contains bytes outside the optional section",
                    nameof(body));
            }

            return optionalHash;
        }

        uint fragmentHash = 0;
        ReadOnlySpan<byte> remaining =
            body.Slice(optionalLength);
        while (!remaining.IsEmpty)
        {
            if (!MessageFragment.TryParseLayout(
                    remaining,
                    out _,
                    out int payloadLength,
                    out int consumed))
            {
                throw new ArgumentException(
                    "body contains a malformed fragment",
                    nameof(body));
            }

            fragmentHash +=
                Hash32.Calculate(
                    remaining.Slice(
                        0,
                        MessageFragmentHeader.Size))
                + Hash32.Calculate(
                    remaining.Slice(
                        MessageFragmentHeader.Size,
                        payloadLength));
            remaining = remaining.Slice(consumed);
        }

        return optionalHash + fragmentHash;
    }

    public static uint CalculateFragmentHash32(in MessageFragment frag)
    {
        Span<byte> headerBuf = stackalloc byte[MessageFragmentHeader.Size];
        frag.Header.Pack(headerBuf);
        return Hash32.Calculate(headerBuf) + Hash32.Calculate(frag.Payload);
    }
}
