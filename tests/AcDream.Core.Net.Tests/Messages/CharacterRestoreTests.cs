using System.Buffers.Binary;
using AcDream.Core.Net.Messages;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class CharacterRestoreTests
{
    [Fact]
    public void BuildRequestBody_ExactByteSequence_OpcodeThenGuidOnly()
    {
        byte[] body = CharacterRestore.BuildRequestBody(0x50000001u);

        byte[] expected =
        [
            0xD9, 0xF7, 0x00, 0x00,  // opcode 0xF7D9 LE
            0x01, 0x00, 0x00, 0x50,
        ];

        Assert.Equal(expected, body);
        Assert.Equal(8, body.Length);
    }

    [Fact]
    public void Parse_SuccessResponse_PopulatesAllTrailingFields()
    {
        var w = AceWireWriter.GameMessage(CharacterRestore.ResponseOpcode)
            .Write(1u)
            .WriteGuid(0x50000002u)
            .WriteString16L("+Acdream")
            .Write(0u);

        CharacterRestore.Parsed parsed = CharacterRestore.Parse(w.ToArray());

        Assert.Equal(1u, parsed.VerificationFlag);
        Assert.True(parsed.IsOk);
        Assert.Equal(0x50000002u, parsed.Guid);
        Assert.Equal("+Acdream", parsed.Name);
        Assert.Equal(0u, parsed.SecondsGreyedOut);
    }

    [Fact]
    public void Parse_SuccessResponse_NonzeroSecondsGreyedOutPreserved()
    {
        var w = AceWireWriter.GameMessage(CharacterRestore.ResponseOpcode)
            .Write(1u)
            .WriteGuid(0x50000003u)
            .WriteString16L("Restored")
            .Write(45u);

        CharacterRestore.Parsed parsed = CharacterRestore.Parse(w.ToArray());

        Assert.Equal(45u, parsed.SecondsGreyedOut);
    }

    [Fact]
    public void Parse_FailureShapedResponse_LeavesTrailingFieldsNull()
    {
        var w = AceWireWriter.GameMessage(CharacterRestore.ResponseOpcode)
            .Write(3u);

        CharacterRestore.Parsed parsed = CharacterRestore.Parse(w.ToArray());

        Assert.Equal(3u, parsed.VerificationFlag);
        Assert.False(parsed.IsOk);
        Assert.Null(parsed.Guid);
        Assert.Null(parsed.Name);
        Assert.Null(parsed.SecondsGreyedOut);
    }

    [Fact]
    public void Parse_UndefFlagZero_FlagOnlyBody_LeavesTrailingFieldsNull()
    {
        var w = AceWireWriter.GameMessage(CharacterRestore.ResponseOpcode)
            .Write(0u);

        CharacterRestore.Parsed parsed = CharacterRestore.Parse(w.ToArray());

        Assert.Equal(0u, parsed.VerificationFlag);
        Assert.False(parsed.IsOk);
        Assert.Null(parsed.Guid);
        Assert.Null(parsed.Name);
        Assert.Null(parsed.SecondsGreyedOut);
    }

    [Fact]
    public void Parse_NonOkBodyWithTrailingBytes_IgnoresRatherThanMisreads()
    {
        var w = AceWireWriter.GameMessage(CharacterRestore.ResponseOpcode)
            .Write(3u)      // NameInUse
            .Write(0xDEADBEEFu)
            .Write(0x12345678u);

        CharacterRestore.Parsed parsed = CharacterRestore.Parse(w.ToArray());

        Assert.Equal(3u, parsed.VerificationFlag);
        Assert.False(parsed.IsOk);
        Assert.Null(parsed.Guid);
        Assert.Null(parsed.Name);
        Assert.Null(parsed.SecondsGreyedOut);
    }

    [Fact]
    public void Parse_WrongOpcode_Throws()
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xDEADBEEFu);

        Assert.Throws<FormatException>(() => CharacterRestore.Parse(bytes));
    }

    [Fact]
    public void Parse_TruncatedAfterFlag_Throws()
    {
        var w = AceWireWriter.GameMessage(CharacterRestore.ResponseOpcode).Write(1u);

        Assert.Throws<FormatException>(() => CharacterRestore.Parse(w.ToArray()));
    }

    [Fact]
    public void Parse_TruncatedBeforeFlag_Throws()
    {
        var w = AceWireWriter.GameMessage(CharacterRestore.ResponseOpcode);

        Assert.Throws<FormatException>(() => CharacterRestore.Parse(w.ToArray()));
    }

    [Fact]
    public void RequestThenResponse_RoundTrips_GuidIdentity()
    {
        const uint guid = 0x50000009u;
        byte[] request = CharacterRestore.BuildRequestBody(guid);

        uint requestedGuid = BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(4));
        Assert.Equal(guid, requestedGuid);

        var w = AceWireWriter.GameMessage(CharacterRestore.ResponseOpcode)
            .Write(1u)
            .WriteGuid(guid)
            .WriteString16L("RoundTrip")
            .Write(0u);
        CharacterRestore.Parsed response = CharacterRestore.Parse(w.ToArray());

        Assert.Equal(requestedGuid, response.Guid);
    }
}
