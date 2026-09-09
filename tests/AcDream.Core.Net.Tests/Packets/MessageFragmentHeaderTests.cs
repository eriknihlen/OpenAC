using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Tests.Packets;

public class MessageFragmentHeaderTests
{
    [Fact]
    public void PackUnpack_RoundTrip_PreservesAllFields()
    {
        var original = new MessageFragmentHeader
        {
            Sequence  = 0x11223344u,
            Id        = 0x80000010u,
            Count     = 3,
            TotalSize = 64,
            Index     = 2,
            Queue     = 0x0A,
        };

        Span<byte> buf = stackalloc byte[MessageFragmentHeader.Size];
        original.Pack(buf);
        var decoded = MessageFragmentHeader.Unpack(buf);

        Assert.Equal(original.Sequence,  decoded.Sequence);
        Assert.Equal(original.Id,        decoded.Id);
        Assert.Equal(original.Count,     decoded.Count);
        Assert.Equal(original.TotalSize, decoded.TotalSize);
        Assert.Equal(original.Index,     decoded.Index);
        Assert.Equal(original.Queue,     decoded.Queue);
    }

    [Fact]
    public void Pack_WritesLittleEndianWireFormat()
    {
        var h = new MessageFragmentHeader
        {
            Sequence  = 0x04030201u,   // 01 02 03 04
            Id        = 0x08070605u,   // 05 06 07 08
            Count     = 0x0A09,        // 09 0A
            TotalSize = 0x0C0B,        // 0B 0C
            Index     = 0x0E0D,        // 0D 0E
            Queue     = 0x100F,        // 0F 10
        };
        Span<byte> buf = stackalloc byte[16];
        h.Pack(buf);

        byte[] expected =
        {
            0x01, 0x02, 0x03, 0x04,
            0x05, 0x06, 0x07, 0x08,
            0x09, 0x0A,
            0x0B, 0x0C,
            0x0D, 0x0E,
            0x0F, 0x10,
        };
        Assert.Equal(expected, buf.ToArray());
    }

    [Fact]
    public void Constants_MatchAcProtocolLimits()
    {
        Assert.Equal(16, MessageFragmentHeader.Size);
        Assert.Equal(464, MessageFragmentHeader.MaxFragmentSize);
        Assert.Equal(448, MessageFragmentHeader.MaxFragmentDataSize);
    }

    [Fact]
    public void Unpack_InsufficientData_Throws()
    {
        Assert.Throws<ArgumentException>(() => MessageFragmentHeader.Unpack(new byte[15]));
    }
}
