using System.Buffers.Binary;

namespace AcDream.Core.Net.Packets;

public sealed class PacketHeaderOptional
{
    public byte[] RawBytes { get; private set; } = Array.Empty<byte>();

    public uint AckSequence { get; private set; }
    public IReadOnlyList<uint> RetransmitRequests { get; private set; } = Array.Empty<uint>();
    public IReadOnlyList<uint> RejectRetransmits { get; private set; } = Array.Empty<uint>();
    public double TimeSync { get; private set; }
    public float EchoRequestClientTime { get; private set; }
    public uint FlowBytes { get; private set; }
    public ushort FlowInterval { get; private set; }

    public double ConnectRequestServerTime { get; private set; }
    public ulong ConnectRequestCookie { get; private set; }
    public uint ConnectRequestClientId { get; private set; }
    /// <summary>4-byte seed to feed the ISAAC instance used for INBOUND
    /// packets (server's outgoing stream = our incoming).</summary>
    public uint ConnectRequestServerSeed { get; private set; }
    /// <summary>4-byte seed for the ISAAC used for OUTBOUND packets
    /// (our outgoing stream = server's incoming).</summary>
    public uint ConnectRequestClientSeed { get; private set; }

    public int Parse(ReadOnlySpan<byte> body, PacketHeaderFlags flags)
    {
        int pos = 0;

        if (HasFlag(flags, PacketHeaderFlags.ServerSwitch))
        {
            if (!Take(body, ref pos, 8)) return -1;
        }

        if (HasFlag(flags, PacketHeaderFlags.RequestRetransmit))
        {
            if (!Take(body, ref pos, 4)) return -1;
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos - 4));
            if (count > 1024 || body.Length - pos < (int)count * 4) return -1;
            var list = new uint[count];
            for (int i = 0; i < count; i++)
            {
                list[i] = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
                pos += 4;
            }
            RetransmitRequests = list;
        }

        if (HasFlag(flags, PacketHeaderFlags.RejectRetransmit))
        {
            if (!Take(body, ref pos, 4)) return -1;
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos - 4));
            if (count > 1024 || body.Length - pos < (int)count * 4) return -1;
            var rejected = new uint[count];
            for (int i = 0; i < count; i++)
            {
                rejected[i] = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
                pos += 4;
            }
            RejectRetransmits = rejected;
        }

        if (HasFlag(flags, PacketHeaderFlags.AckSequence))
        {
            if (!Take(body, ref pos, 4)) return -1;
            AckSequence = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos - 4));
        }

        if (HasFlag(flags, PacketHeaderFlags.LoginRequest))
        {
            int loginBytes = body.Length - pos;
            if (loginBytes < 0) return -1;
            RawBytes = body.Slice(0, pos + loginBytes).ToArray();
            return pos + loginBytes;
        }

        if (HasFlag(flags, PacketHeaderFlags.WorldLoginRequest))
        {
            if (!Take(body, ref pos, 8)) return -1;
        }

        if (HasFlag(flags, PacketHeaderFlags.ConnectRequest))
        {
            if (body.Length - pos < 32) return -1;
            ConnectRequestServerTime = BitConverter.Int64BitsToDouble(
                BinaryPrimitives.ReadInt64LittleEndian(body.Slice(pos)));
            ConnectRequestCookie     = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(pos + 8));
            ConnectRequestClientId   = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos + 16));
            ConnectRequestServerSeed = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos + 20));
            ConnectRequestClientSeed = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos + 24));
            // bytes 28-31 are the trailing padding uint — skip via Take.
            pos += 32;
        }

        if (HasFlag(flags, PacketHeaderFlags.ConnectResponse))
        {
            if (!Take(body, ref pos, 8)) return -1;
        }

        if (HasFlag(flags, PacketHeaderFlags.CICMDCommand))
        {
            if (!Take(body, ref pos, 8)) return -1;
        }

        if (HasFlag(flags, PacketHeaderFlags.TimeSync))
        {
            if (!Take(body, ref pos, 8)) return -1;
            TimeSync = BitConverter.Int64BitsToDouble(
                BinaryPrimitives.ReadInt64LittleEndian(body.Slice(pos - 8)));
        }

        if (HasFlag(flags, PacketHeaderFlags.EchoRequest))
        {
            if (!Take(body, ref pos, 4)) return -1;
            EchoRequestClientTime = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos - 4));
        }

        if (HasFlag(flags, PacketHeaderFlags.Flow))
        {
            if (!Take(body, ref pos, 6)) return -1;
            FlowBytes = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos - 6));
            FlowInterval = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos - 2));
        }

        RawBytes = body.Slice(0, pos).ToArray();
        return pos;
    }

    public uint CalculateHash32() => Cryptography.Hash32.Calculate(RawBytes);

    private static bool HasFlag(PacketHeaderFlags all, PacketHeaderFlags bit) => (all & bit) != 0;

    private static bool Take(ReadOnlySpan<byte> body, ref int pos, int n)
    {
        if (body.Length - pos < n) return false;
        pos += n;
        return true;
    }
}
