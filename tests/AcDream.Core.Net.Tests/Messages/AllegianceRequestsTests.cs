using System;
using System.Buffers.Binary;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class AllegianceRequestsTests
{
    [Fact]
    public void BuildSwear_EncodesOpcodeAndTarget()
    {
        byte[] body = AllegianceRequests.BuildSwear(gameActionSequence: 3, patronGuid: 0xAAAAu);

        Assert.Equal(16, body.Length);
        Assert.Equal(AllegianceRequests.GameActionEnvelope,
            BinaryPrimitives.ReadUInt32LittleEndian(body));
        Assert.Equal(3u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
        Assert.Equal(AllegianceRequests.SwearOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(0xAAAAu,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
    }

    [Fact]
    public void BuildBreak_EncodesOpcodeAndTarget()
    {
        byte[] body = AllegianceRequests.BuildBreak(gameActionSequence: 5, targetGuid: 0xBBBBu);
        Assert.Equal(AllegianceRequests.BreakOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(0xBBBBu,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
    }

    [Fact]
    public void BuildSwear_GoldenByteVector()
    {
        byte[] body = AllegianceRequests.BuildSwear(gameActionSequence: 3, patronGuid: 0xAAAAu);

        byte[] expected =
        [
            0xB1, 0xF7, 0x00, 0x00, // envelope 0xF7B1
            0x03, 0x00, 0x00, 0x00, // seq 3
            0x1D, 0x00, 0x00, 0x00, // opcode 0x001D
            0xAA, 0xAA, 0x00, 0x00, // targetGuid 0xAAAA
        ];

        Assert.Equal(expected, body);
    }

    [Fact]
    public void BuildBreak_GoldenByteVector()
    {
        byte[] body = AllegianceRequests.BuildBreak(gameActionSequence: 5, targetGuid: 0xBBBBu);

        byte[] expected =
        [
            0xB1, 0xF7, 0x00, 0x00, // envelope 0xF7B1
            0x05, 0x00, 0x00, 0x00, // seq 5
            0x1E, 0x00, 0x00, 0x00, // opcode 0x001E
            0xBB, 0xBB, 0x00, 0x00, // targetGuid 0xBBBB
        ];

        Assert.Equal(expected, body);
    }

    [Fact]
    public void BuildKick_GoldenByteVector_SameShapeAsBreak()
    {
        byte[] body = AllegianceRequests.BuildKick(gameActionSequence: 6, vassalGuid: 0x50000042u);

        byte[] expected =
        [
            0xB1, 0xF7, 0x00, 0x00, // envelope 0xF7B1
            0x06, 0x00, 0x00, 0x00, // seq 6
            0x1E, 0x00, 0x00, 0x00, // opcode 0x001E — SAME as Break
            0x42, 0x00, 0x00, 0x50,
        ];

        Assert.Equal(expected, body);
        Assert.Equal(AllegianceRequests.BreakOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
    }

    [Fact]
    public void BuildAllegianceUpdateRequest_GoldenByteVector_On()
    {
        byte[] body = AllegianceRequests.BuildAllegianceUpdateRequest(gameActionSequence: 4, on: true);

        byte[] expected =
        [
            0xB1, 0xF7, 0x00, 0x00, // envelope 0xF7B1
            0x04, 0x00, 0x00, 0x00, // seq 4
            0x1F, 0x00, 0x00, 0x00, // opcode 0x001F
            0x01, 0x00, 0x00, 0x00, // on = 1
        ];

        Assert.Equal(expected, body);
    }

    [Fact]
    public void BuildAllegianceUpdateRequest_GoldenByteVector_Off()
    {
        byte[] body = AllegianceRequests.BuildAllegianceUpdateRequest(gameActionSequence: 7, on: false);

        byte[] expected =
        [
            0xB1, 0xF7, 0x00, 0x00, // envelope 0xF7B1
            0x07, 0x00, 0x00, 0x00, // seq 7
            0x1F, 0x00, 0x00, 0x00, // opcode 0x001F
            0x00, 0x00, 0x00, 0x00, // on = 0
        ];

        Assert.Equal(expected, body);
    }
}
