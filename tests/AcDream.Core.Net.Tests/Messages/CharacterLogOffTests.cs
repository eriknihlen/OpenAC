using System.Buffers.Binary;
using AcDream.Core.Net.Messages;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class CharacterLogOffTests
{
    [Fact]
    public void BuildRequestBody_MatchesRetailOpcodeThenCharacterId()
    {
        byte[] body = CharacterLogOff.BuildRequestBody(0x5000000Au);

        Assert.Equal(8, body.Length);
        Assert.Equal(
            CharacterLogOff.Opcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body));
        Assert.Equal(
            0x5000000Au,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
    }

    [Fact]
    public void IsConfirmation_AcceptsServerOpcodeOnlyBody()
    {
        byte[] body = BitConverter.GetBytes(CharacterLogOff.Opcode);

        Assert.True(CharacterLogOff.IsConfirmation(body));
    }

    [Fact]
    public void IsConfirmation_RejectsTruncatedOrDifferentMessage()
    {
        Assert.False(CharacterLogOff.IsConfirmation([0x53, 0xF6, 0x00]));
        Assert.False(CharacterLogOff.IsConfirmation(BitConverter.GetBytes(0xF654u)));
    }

    [Fact]
    public void IsConfirmation_RejectsRequestShapedBodyWithCharacterId()
    {
        byte[] request = CharacterLogOff.BuildRequestBody(0x5000000Au);

        Assert.False(CharacterLogOff.IsConfirmation(request));
    }
}
