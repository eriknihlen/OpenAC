using System;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public class HearSpeechGoldenTests
{
    private const uint Speech = 0x0Bu;
    private const uint Tell = 0x10u;

    private static byte[] LocalGolden(string text, string sender, uint guid, uint chatType)
        => AceWireWriter.GameMessage(HearSpeech.LocalOpcode)
            .WriteString16L(text)
            .WriteString16L(sender)
            .Write(guid)
            .Write(chatType)
            .ToArray();

    private static byte[] RangedGolden(
        string text, string sender, uint guid, float range, uint chatType)
        => AceWireWriter.GameMessage(HearSpeech.RangedOpcode)
            .WriteString16L(text)
            .WriteString16L(sender)
            .Write(guid)
            .Write(range)
            .Write(chatType)
            .ToArray();

    // ---- 0x02BB HearSpeech ---------------------------------------------------

    public static TheoryData<string, string, uint, uint> LocalCases() => new()
    {
        { "Hello, Dereth!", "Barris", 0x50000001u, Speech },
        // Empty strings still occupy 4 bytes each (2 length + 2 pad).
        { "", "", 0u, 0u },
        // Lengths 1..3 exercise every residue of the 4-byte padding rule.
        { "a", "bb", 0x7C95B01Au, Tell },
        { "ccc", "dddd", 0x800114C0u, Speech },
        { "Café time", "Seán", 0xA9B40001u, Speech },
    };

    [Theory]
    [MemberData(nameof(LocalCases))]
    public void Local_AceGoldenBytes_DecodesEveryFieldExactly(
        string text, string sender, uint guid, uint chatType)
    {
        byte[] body = LocalGolden(text, sender, guid, chatType);

        HearSpeech.Parsed? parsed = HearSpeech.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(text, parsed!.Value.Text);
        Assert.Equal(sender, parsed.Value.SenderName);
        Assert.Equal(guid, parsed.Value.SenderGuid);
        Assert.Equal(chatType, parsed.Value.ChatType);
        Assert.False(parsed.Value.IsRanged);
        // Local speech has no range field on the wire.
        Assert.Equal(0f, parsed.Value.Range);
    }

    // ---- 0x02BC HearRangedSpeech --------------------------------------------

    public static TheoryData<string, string, uint, float, uint> RangedCases() => new()
    {
        { "HELP!", "Barris", 0x50000001u, 60f, Speech },
        { "", "", 0u, 0f, 0u },
        { "a", "bb", 0x7C95B01Au, 12.5f, Tell },
        { "ccc", "dddd", 0x800114C0u, 100f, Speech },
    };

    [Theory]
    [MemberData(nameof(RangedCases))]
    public void Ranged_AceGoldenBytes_DecodesRangeAndChatTypeSeparately(
        string text, string sender, uint guid, float range, uint chatType)
    {
        byte[] body = RangedGolden(text, sender, guid, range, chatType);

        HearSpeech.Parsed? parsed = HearSpeech.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(text, parsed!.Value.Text);
        Assert.Equal(sender, parsed.Value.SenderName);
        Assert.Equal(guid, parsed.Value.SenderGuid);
        Assert.Equal(range, parsed.Value.Range);
        Assert.Equal(chatType, parsed.Value.ChatType);
        Assert.True(parsed.Value.IsRanged);
    }

    [Fact]
    public void Ranged_ChatType_IsNotTheRangeFloatBits()
    {
        const float range = 60f;
        byte[] body = RangedGolden("HELP!", "Barris", 0x50000001u, range, Speech);

        HearSpeech.Parsed? parsed = HearSpeech.TryParse(body);

        Assert.NotNull(parsed);
        uint rangeBits = (uint)BitConverter.SingleToInt32Bits(range);
        Assert.Equal(0x42700000u, rangeBits);
        Assert.NotEqual(rangeBits, parsed!.Value.ChatType);
        Assert.Equal(Speech, parsed.Value.ChatType);
    }

    [Fact]
    public void Ranged_MissingRangeField_ReturnsNull()
    {
        byte[] truncated = AceWireWriter.GameMessage(HearSpeech.RangedOpcode)
            .WriteString16L("HELP!")
            .WriteString16L("Barris")
            .Write(0x50000001u)
            .Write(Speech)
            .ToArray();

        Assert.Null(HearSpeech.TryParse(truncated));
    }

    [Fact]
    public void WrongOpcode_ReturnsNull()
    {
        byte[] body = AceWireWriter.GameMessage(0x02BDu)
            .WriteString16L("x").WriteString16L("y").Write(1u).Write(1u)
            .ToArray();

        Assert.Null(HearSpeech.TryParse(body));
    }

    [Fact]
    public void Local_TruncatedTail_ReturnsNull()
    {
        byte[] body = LocalGolden("Hello", "Barris", 1u, Speech);
        Assert.Null(HearSpeech.TryParse(body.AsSpan(0, body.Length - 1)));
    }
}
