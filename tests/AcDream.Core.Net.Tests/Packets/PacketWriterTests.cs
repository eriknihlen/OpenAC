using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Tests.Packets;

public class PacketWriterTests
{
    [Fact]
    public void WriteUInt32_WritesLittleEndianBytes()
    {
        var w = new PacketWriter();
        w.WriteUInt32(0x04030201u);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04 }, w.ToArray());
    }

    [Fact]
    public void WriteString16L_EmptyString_WritesOnlyLengthZeroAndPadding()
    {
        var w = new PacketWriter();
        w.WriteString16L(string.Empty);

        // u16(0) = 2 bytes, pad to 4 = 2 more bytes
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00 }, w.ToArray());
    }

    [Fact]
    public void WriteString16L_TwoCharString_PadsToMultipleOfFour()
    {
        var w = new PacketWriter();
        w.WriteString16L("ab");

        // u16(2), 'a', 'b', 0 pad byte → total 5 bytes... wait that's not 4-aligned.
        // Record = 2 (length prefix) + 2 (ascii) = 4. No padding needed.
        Assert.Equal(new byte[] { 0x02, 0x00, (byte)'a', (byte)'b' }, w.ToArray());
    }

    [Fact]
    public void WriteString16L_OneCharString_PadsToFour()
    {
        var w = new PacketWriter();
        w.WriteString16L("x");
        // Record = 2 + 1 = 3, pad 1 byte → 4 total
        Assert.Equal(new byte[] { 0x01, 0x00, (byte)'x', 0x00 }, w.ToArray());
    }

    [Fact]
    public void WriteString16L_ThreeCharString_PadsToEight()
    {
        var w = new PacketWriter();
        w.WriteString16L("abc");
        // Record = 2 + 3 = 5, pad 3 → 8 total
        Assert.Equal(new byte[] { 0x03, 0x00, (byte)'a', (byte)'b', (byte)'c', 0, 0, 0 }, w.ToArray());
    }

    [Fact]
    public void WriteString32L_ShortString_WritesU32OuterMarkerStringPadded()
    {
        var w = new PacketWriter();
        w.WriteString32L("hi");

        // outer = 2 + 1 = 3, then marker byte 0, then 'h' 'i', pad from start
        // of u32: record = 4 + 1 + 2 = 7, pad 1 → 8 total
        Assert.Equal(new byte[]
        {
            0x03, 0x00, 0x00, 0x00,   // outer length = 3
            0x00,                      // marker
            (byte)'h', (byte)'i',
            0x00,                      // pad to 8
        }, w.ToArray());
    }

    [Fact]
    public void WriteString32L_EmptyString_WritesOnlyZeroLength()
    {
        var w = new PacketWriter();
        w.WriteString32L("");
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00 }, w.ToArray());
    }

    [Fact]
    public void WriteString32L_TooLong_Throws()
    {
        var w = new PacketWriter();
        Assert.Throws<ArgumentException>(() => w.WriteString32L(new string('x', 256)));
    }

    [Fact]
    public void AlignTo4_NoOpIfAlreadyAligned()
    {
        var w = new PacketWriter();
        w.WriteUInt32(0xCAFEu);
        int before = w.Position;
        w.AlignTo4();
        Assert.Equal(before, w.Position);
    }

    [Fact]
    public void AlignTo4_AddsPaddingWhenMisaligned()
    {
        var w = new PacketWriter();
        w.WriteByte(0xAA);
        w.AlignTo4();
        Assert.Equal(4, w.Position);
        var buf = w.ToArray();
        Assert.Equal(0xAA, buf[0]);
        Assert.Equal(0x00, buf[1]);
        Assert.Equal(0x00, buf[2]);
        Assert.Equal(0x00, buf[3]);
    }

    [Fact]
    public void Grow_HandlesLargeWrites()
    {
        var w = new PacketWriter(16);  // small initial
        var big = new byte[500];
        for (int i = 0; i < big.Length; i++) big[i] = (byte)(i & 0xFF);
        w.WriteBytes(big);
        Assert.Equal(500, w.Position);
        Assert.Equal(big, w.ToArray());
    }
}
