using System.Buffers.Binary;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class DeleteObjectTests
{
    [Fact]
    public void RejectsWrongOpcode()
    {
        Span<byte> body = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 0xDEADBEEFu);

        Assert.Null(DeleteObject.TryParse(body));
    }

    [Fact]
    public void RejectsTruncated()
    {
        Assert.Null(DeleteObject.TryParse(ReadOnlySpan<byte>.Empty));
        Assert.Null(DeleteObject.TryParse(new byte[9]));
    }

    [Fact]
    public void ParsesGuidAndInstanceSequence()
    {
        Span<byte> body = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(body, DeleteObject.Opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(4), 0x80000439u);
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(8), 0x1234);

        var parsed = DeleteObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(0x80000439u, parsed!.Value.Guid);
        Assert.Equal((ushort)0x1234, parsed.Value.InstanceSequence);
    }

}
