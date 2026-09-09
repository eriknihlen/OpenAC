using System.Buffers.Binary;

namespace AcDream.Core.Net.Packets;

public struct MessageFragmentHeader
{
    public const int Size = 16;

    /// <summary>Max total fragment size on the wire (including this header).</summary>
    public const int MaxFragmentSize = 464;

    /// <summary>Max payload bytes per fragment (= MaxFragmentSize - Size).</summary>
    public const int MaxFragmentDataSize = MaxFragmentSize - Size;  // 448

    public uint Sequence;
    public uint Id;
    public ushort Count;
    public ushort TotalSize;  // total bytes of this fragment including header
    public ushort Index;
    public ushort Queue;

    public readonly void Pack(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException($"destination must be at least {Size} bytes", nameof(destination));

        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(0),  Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(4),  Id);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(8),  Count);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(10), TotalSize);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(12), Index);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(14), Queue);
    }

    public static MessageFragmentHeader Unpack(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
            throw new ArgumentException($"source must be at least {Size} bytes", nameof(source));

        return new MessageFragmentHeader
        {
            Sequence  = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(0)),
            Id        = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(4)),
            Count     = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(8)),
            TotalSize = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(10)),
            Index     = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(12)),
            Queue     = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(14)),
        };
    }
}
