using System.Buffers.Binary;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class InventoryRemoveObjectTests
{
    private static byte[] Build(uint guid, uint opcode = 0x0024u)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), guid);
        return b;
    }

    [Fact]
    public void TryParse_valid_returnsGuid()
    {
        var p = InventoryRemoveObject.TryParse(Build(0x50000A07u));
        Assert.NotNull(p);
        Assert.Equal(0x50000A07u, p!.Value.Guid);
    }

    [Fact]
    public void TryParse_wrongOpcode_returnsNull()
        => Assert.Null(InventoryRemoveObject.TryParse(Build(1, opcode: 0x0023u)));

    [Fact]
    public void TryParse_truncated_returnsNull()
        => Assert.Null(InventoryRemoveObject.TryParse(new byte[7]));
}
