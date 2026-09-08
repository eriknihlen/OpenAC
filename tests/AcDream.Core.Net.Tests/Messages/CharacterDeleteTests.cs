using System.Buffers.Binary;
using AcDream.Core.Net.Messages;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class CharacterDeleteTests
{
    [Fact]
    public void BuildRequestBody_Layout_OpcodeThenAccountThenSlot()
    {
        byte[] body = CharacterDelete.BuildRequestBody("testaccount", characterSlot: 3);

        int pos = 0;
        Assert.Equal(CharacterDelete.Opcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos))); pos += 4;

        ushort len = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(pos));
        Assert.Equal(11, len); pos += 2;
        string name = System.Text.Encoding.ASCII.GetString(body.AsSpan(pos, 11));
        Assert.Equal("testaccount", name); pos += 11;
        Assert.Equal(0, body[pos++]);
        Assert.Equal(0, body[pos++]);
        Assert.Equal(0, body[pos++]);

        uint slot = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos)); pos += 4;
        Assert.Equal(3u, slot);

        Assert.Equal(4 + 16 + 4, body.Length);  // opcode + padded string + slot
        Assert.Equal(pos, body.Length);
    }

    [Fact]
    public void BuildRequestBody_ExactByteSequence_ShortAccount()
    {
        // "ab" -> String16L = u16(2) + 2 bytes = 4, already 4-byte aligned,
        // no padding.
        byte[] body = CharacterDelete.BuildRequestBody("ab", characterSlot: 0x11u);

        byte[] expected =
        [
            0x55, 0xF6, 0x00, 0x00,  // opcode 0xF655 LE
            0x02, 0x00,              // String16L length = 2
            (byte)'a', (byte)'b',    // string bytes
            0x11, 0x00, 0x00, 0x00,
        ];

        Assert.Equal(expected, body);
    }

    [Fact]
    public void BuildRequestBody_NullAccountName_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => CharacterDelete.BuildRequestBody(null!, characterSlot: 0));
    }

    [Fact]
    public void IsAcknowledgement_AcceptsOpcodeOnlyBody()
    {
        byte[] body = BitConverter.GetBytes(CharacterDelete.Opcode);

        Assert.True(CharacterDelete.IsAcknowledgement(body));
    }

    [Fact]
    public void IsAcknowledgement_RejectsRequestShapedBody()
    {
        byte[] request = CharacterDelete.BuildRequestBody("acct", characterSlot: 1);

        Assert.False(CharacterDelete.IsAcknowledgement(request));
    }

    [Fact]
    public void IsAcknowledgement_RejectsTruncatedOrDifferentOpcode()
    {
        Assert.False(CharacterDelete.IsAcknowledgement([0x55, 0xF6, 0x00]));
        Assert.False(CharacterDelete.IsAcknowledgement(BitConverter.GetBytes(0xF656u)));
        Assert.False(CharacterDelete.IsAcknowledgement([]));
    }
}
