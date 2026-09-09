using System.Buffers.Binary;
using AcDream.Core.Net.Cryptography;

namespace AcDream.Core.Net.Packets;

public struct PacketHeader
{
    public const int Size = 20;

    public const uint ChecksumPlaceholder = 0xBADD70DDu;

    public uint Sequence;
    public PacketHeaderFlags Flags;
    public uint Checksum;
    public ushort Id;
    public ushort Time;
    public ushort DataSize;
    public ushort Iteration;

    /// <summary>True if the header's Flags field has any of the bits in <paramref name="flags"/>.</summary>
    public readonly bool HasFlag(PacketHeaderFlags flags) => (Flags & flags) != 0;

    /// <summary>Write this header into the first 20 bytes of <paramref name="destination"/>.</summary>
    public readonly void Pack(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException($"destination must be at least {Size} bytes", nameof(destination));

        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(0),  Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(4),  (uint)Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(8),  Checksum);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(12), Id);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(14), Time);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(16), DataSize);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(18), Iteration);
    }

    /// <summary>Read a header from the first 20 bytes of <paramref name="source"/>.</summary>
    public static PacketHeader Unpack(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
            throw new ArgumentException($"source must be at least {Size} bytes", nameof(source));

        return new PacketHeader
        {
            Sequence  =                     BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(0)),
            Flags     = (PacketHeaderFlags)BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(4)),
            Checksum  =                     BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(8)),
            Id        =                     BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(12)),
            Time      =                     BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(14)),
            DataSize  =                     BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(16)),
            Iteration =                     BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(18)),
        };
    }

    public readonly uint CalculateHeaderHash32()
    {
        Span<byte> buffer = stackalloc byte[Size];
        var saved = this;
        saved.Checksum = ChecksumPlaceholder;
        saved.Pack(buffer);
        return Hash32.Calculate(buffer);
    }
}
