using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Tests.Packets;

public class PacketHeaderTests
{
    [Fact]
    public void PackUnpack_RoundTrip_PreservesAllFields()
    {
        var original = new PacketHeader
        {
            Sequence  = 0x11223344u,
            Flags     = PacketHeaderFlags.EncryptedChecksum | PacketHeaderFlags.LoginRequest,
            Checksum  = 0xDEADBEEFu,
            Id        = 0x1234,
            Time      = 0x5678,
            DataSize  = 256,
            Iteration = 3,
        };

        Span<byte> buffer = stackalloc byte[PacketHeader.Size];
        original.Pack(buffer);
        var decoded = PacketHeader.Unpack(buffer);

        Assert.Equal(original.Sequence,  decoded.Sequence);
        Assert.Equal(original.Flags,     decoded.Flags);
        Assert.Equal(original.Checksum,  decoded.Checksum);
        Assert.Equal(original.Id,        decoded.Id);
        Assert.Equal(original.Time,      decoded.Time);
        Assert.Equal(original.DataSize,  decoded.DataSize);
        Assert.Equal(original.Iteration, decoded.Iteration);
    }

    [Fact]
    public void Pack_WritesLittleEndianWireFormat()
    {
        var header = new PacketHeader
        {
            Sequence  = 0x04030201u,      // LE wire: 01 02 03 04
            Flags     = (PacketHeaderFlags)0x08070605u,  // LE: 05 06 07 08
            Checksum  = 0x0C0B0A09u,      // LE: 09 0A 0B 0C
            Id        = 0x0E0D,           // LE: 0D 0E
            Time      = 0x100F,           // LE: 0F 10
            DataSize  = 0x1211,           // LE: 11 12
            Iteration = 0x1413,           // LE: 13 14
        };

        Span<byte> buf = stackalloc byte[20];
        header.Pack(buf);

        byte[] expected = new byte[]
        {
            0x01, 0x02, 0x03, 0x04,
            0x05, 0x06, 0x07, 0x08,
            0x09, 0x0A, 0x0B, 0x0C,
            0x0D, 0x0E,
            0x0F, 0x10,
            0x11, 0x12,
            0x13, 0x14,
        };
        Assert.Equal(expected, buf.ToArray());
    }

    [Fact]
    public void HasFlag_DetectsSingleBit()
    {
        var h = new PacketHeader { Flags = PacketHeaderFlags.EncryptedChecksum | PacketHeaderFlags.TimeSync };
        Assert.True(h.HasFlag(PacketHeaderFlags.EncryptedChecksum));
        Assert.True(h.HasFlag(PacketHeaderFlags.TimeSync));
        Assert.False(h.HasFlag(PacketHeaderFlags.LoginRequest));
    }

    [Fact]
    public void CalculateHeaderHash32_UsesBaddSentinelNotActualChecksum()
    {
        var a = new PacketHeader { Sequence = 42, Flags = PacketHeaderFlags.None, Checksum = 0x11111111u };
        var b = new PacketHeader { Sequence = 42, Flags = PacketHeaderFlags.None, Checksum = 0xDEADBEEFu };
        Assert.Equal(a.CalculateHeaderHash32(), b.CalculateHeaderHash32());
    }

    [Fact]
    public void CalculateHeaderHash32_DoesNotMutateHeaderChecksum()
    {
        var h = new PacketHeader { Checksum = 0x11111111u };
        _ = h.CalculateHeaderHash32();
        Assert.Equal(0x11111111u, h.Checksum);
    }

    [Fact]
    public void CalculateHeaderHash32_DeterministicForSameInput()
    {
        var h = new PacketHeader
        {
            Sequence  = 1,
            Flags     = PacketHeaderFlags.EncryptedChecksum,
            Checksum  = 0xDEADBEEFu,
            Id        = 0x1234,
            Time      = 0x5678,
            DataSize  = 32,
            Iteration = 0,
        };
        Assert.Equal(h.CalculateHeaderHash32(), h.CalculateHeaderHash32());
    }

    [Fact]
    public void Unpack_InsufficientData_Throws()
    {
        Assert.Throws<ArgumentException>(() => PacketHeader.Unpack(new byte[19]));
    }

    [Fact]
    public void Pack_InsufficientDestination_Throws()
    {
        var h = new PacketHeader();
        var buf = new byte[19];
        Assert.Throws<ArgumentException>(() => h.Pack(buf));
    }
}
