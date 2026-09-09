using System.Text;
using AcDream.Content.Pak;

namespace AcDream.Content.Tests;

public class Crc32Tests {
    [Fact]
    public void KnownAnswer_123456789_IsCBF43926() {
        var input = Encoding.ASCII.GetBytes("123456789");
        Assert.Equal(0xCBF43926u, Crc32.Compute(input));
    }

    [Fact]
    public void KnownAnswer_EmptyInput_IsZero() {
        Assert.Equal(0x00000000u, Crc32.Compute(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void KnownAnswer_SingleZeroByte() {
        Assert.Equal(0xD202EF8Du, Crc32.Compute(new byte[] { 0x00 }));
    }
}
