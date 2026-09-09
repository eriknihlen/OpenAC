using System;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public class PlayScriptGoldenTests
{
    // ---- 0xF754 PlayScriptId -------------------------------------------------

    public static TheoryData<uint, uint> ScriptIdCases() => new()
    {
        { 0x50000001u, 0x0D000001u },  // player, a portal-space script DID
        { 0x7C95B01Au, 0x00000000u },  // zero DID must survive as zero, not null
        { 0xFFFFFFFFu, 0xFFFFFFFFu },  // full-width guid + DID
    };

    [Theory]
    [MemberData(nameof(ScriptIdCases))]
    public void PlayScriptId_AceGoldenBytes_DecodesGuidAndDid(uint guid, uint scriptDid)
    {
        byte[] body = AceWireWriter.GameMessage(PlayPhysicsScript.Opcode)
            .WriteGuid(guid)
            .Write(scriptDid)
            .ToArray();

        Assert.Equal(PlayPhysicsScript.WireSize, body.Length);

        PlayPhysicsScript? parsed = PlayPhysicsScript.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(guid, parsed!.Value.Guid);
        Assert.Equal(scriptDid, parsed.Value.ScriptDid);
    }

    [Fact]
    public void PlayScriptId_WrongOpcode_ReturnsNull()
    {
        byte[] body = AceWireWriter.GameMessage(0xF755u)
            .WriteGuid(1u).Write(2u).ToArray();

        Assert.Null(PlayPhysicsScript.TryParse(body));
    }

    [Fact]
    public void PlayScriptId_TrailingByte_ReturnsNull()
    {
        // The parser demands an exact length; a 13-byte body is not a
        // truncated-but-usable 0xF754.
        byte[] body = AceWireWriter.GameMessage(PlayPhysicsScript.Opcode)
            .WriteGuid(1u).Write(2u).Pad(1).ToArray();

        Assert.Null(PlayPhysicsScript.TryParse(body));
    }

    // ---- 0xF755 PlayEffect ---------------------------------------------------

    public static TheoryData<uint, uint, float> ScriptTypeCases() => new()
    {
        { 0x50000001u, 0x00000021u, 1.0f },
        // Intensity is a free float on the wire; fractional values must survive.
        { 0x7C95B01Au, 0x00000083u, 0.25f },
        // Zero intensity is meaningful (script suppressed), not "absent".
        { 0x800114C0u, 0x00000001u, 0f },
        // Unknown type values are retained losslessly for the resolver to reject.
        { 0xA9B40001u, 0xDEADBEEFu, -3.5f },
    };

    [Theory]
    [MemberData(nameof(ScriptTypeCases))]
    public void PlayEffect_AceGoldenBytes_DecodesGuidTypeAndIntensity(
        uint guid, uint rawScriptType, float intensity)
    {
        byte[] body = AceWireWriter.GameMessage(PlayPhysicsScriptType.Opcode)
            .WriteGuid(guid)
            .Write(rawScriptType)
            .Write(intensity)
            .ToArray();

        Assert.Equal(PlayPhysicsScriptType.WireSize, body.Length);
        Assert.Equal(16, body.Length);

        PlayPhysicsScriptType? parsed = PlayPhysicsScriptType.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(guid, parsed!.Value.Guid);
        Assert.Equal(rawScriptType, parsed.Value.RawScriptType);
        Assert.Equal(intensity, parsed.Value.Intensity);
    }

    [Fact]
    public void PlayEffect_NonFiniteIntensity_IsRetainedLosslessly()
    {
        byte[] body = AceWireWriter.GameMessage(PlayPhysicsScriptType.Opcode)
            .WriteGuid(0x50000001u)
            .Write(0x00000021u)
            .Write(float.NaN)
            .ToArray();

        PlayPhysicsScriptType? parsed = PlayPhysicsScriptType.TryParse(body);

        Assert.NotNull(parsed);
        Assert.True(float.IsNaN(parsed!.Value.Intensity));
    }

    [Fact]
    public void PlayEffect_WrongOpcode_ReturnsNull()
    {
        byte[] body = AceWireWriter.GameMessage(0xF754u)
            .WriteGuid(1u).Write(2u).Write(1.0f).ToArray();

        Assert.Null(PlayPhysicsScriptType.TryParse(body));
    }
}
