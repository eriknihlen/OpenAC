using System;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class SetTurbineChatChannelsTests
{
    [Fact]
    public void TryParse_HoltburgerGoldenFixture_RoundTrips()
    {
        byte[] full = HexDecode(
            "B0F7000000000000210000009502000078563412" +
            "020000000300000004000000050000000A000000" +
            "09000000070000000800000009000000");

        var env = GameEventEnvelope.TryParse(full);
        Assert.NotNull(env);
        Assert.Equal(GameEventType.SetTurbineChatChannels, env!.Value.EventType);

        var parsed = SetTurbineChatChannels.TryParse(env.Value.Payload.Span);
        Assert.NotNull(parsed);
        Assert.Equal(0x12345678u, parsed!.Value.AllegianceRoom);
        // General..Olthoi
        Assert.Equal(2u, parsed.Value.GeneralRoom);
        Assert.Equal(3u, parsed.Value.TradeRoom);
        Assert.Equal(4u, parsed.Value.LfgRoom);
        Assert.Equal(5u, parsed.Value.RoleplayRoom);
        Assert.Equal(10u, parsed.Value.OlthoiRoom);
        // Society = SocietyRadiantBlood (id 9) per fixture
        Assert.Equal(9u, parsed.Value.SocietyRoom);
        // Sub-society rooms 7, 8, 9
        Assert.Equal(7u, parsed.Value.SocietyCelestialHandRoom);
        Assert.Equal(8u, parsed.Value.SocietyEldrytchWebRoom);
        Assert.Equal(9u, parsed.Value.SocietyRadiantBloodRoom);
    }

    [Fact]
    public void Serialize_RoundTripsThroughTryParse()
    {
        var original = new SetTurbineChatChannels.Parsed(
            AllegianceRoom: 0x100,
            GeneralRoom: 0x200,
            TradeRoom: 0x300,
            LfgRoom: 0x400,
            RoleplayRoom: 0x500,
            OlthoiRoom: 0x600,
            SocietyRoom: 0x700,
            SocietyCelestialHandRoom: 0x800,
            SocietyEldrytchWebRoom: 0x900,
            SocietyRadiantBloodRoom: 0xA00);

        byte[] payload = SetTurbineChatChannels.Serialize(original);
        Assert.Equal(SetTurbineChatChannels.PayloadSize, payload.Length);

        var roundTripped = SetTurbineChatChannels.TryParse(payload);
        Assert.NotNull(roundTripped);
        Assert.Equal(original, roundTripped!.Value);
    }

    [Fact]
    public void TryParse_TruncatedPayload_ReturnsNull()
    {
        // 39 bytes — one short of the 40-byte minimum.
        Assert.Null(SetTurbineChatChannels.TryParse(new byte[39]));
    }

    private static byte[] HexDecode(string hex)
    {
        if ((hex.Length & 1) != 0) throw new ArgumentException("hex length must be even");
        byte[] result = new byte[hex.Length / 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return result;
    }
}
