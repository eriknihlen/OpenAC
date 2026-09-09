using AcDream.Core.Chat;
using Xunit;

namespace AcDream.Core.Tests.Chat;

public sealed class TurbineChatStateTests
{
    [Fact]
    public void Reset_ClearsRoomsAndRestartsSessionCookieAtOne()
    {
        var state = new TurbineChatState();
        state.OnChannelsReceived(1u, 2u, 3u, 4u, 5u, 6u, 7u, 8u, 9u, 10u);
        Assert.Equal(1u, state.NextContextId());
        Assert.Equal(2u, state.NextContextId());

        state.Reset();

        Assert.False(state.Enabled);
        Assert.Equal(0u, state.AllegianceRoom);
        Assert.Equal(0u, state.GeneralRoom);
        Assert.Equal(0u, state.TradeRoom);
        Assert.Equal(0u, state.LfgRoom);
        Assert.Equal(0u, state.RoleplayRoom);
        Assert.Equal(0u, state.OlthoiRoom);
        Assert.Equal(0u, state.SocietyRoom);
        Assert.Equal(0u, state.SocietyCelestialHandRoom);
        Assert.Equal(0u, state.SocietyEldrytchWebRoom);
        Assert.Equal(0u, state.SocietyRadiantBloodRoom);
        Assert.Equal(1u, state.NextContextId());
        state.Reset();
        Assert.Equal(1u, state.NextContextId());
    }

    [Fact]
    public void InitialState_DisabledAndZeroRooms_NextContextIdStartsAt1()
    {
        var s = new TurbineChatState();
        Assert.False(s.Enabled);
        Assert.Equal(0u, s.GeneralRoom);
        Assert.Equal(0u, s.TradeRoom);
        Assert.Equal(0u, s.AllegianceRoom);
        Assert.Equal(1u, s.NextContextId());
        Assert.Equal(2u, s.NextContextId());
    }

    [Fact]
    public void OnChannelsReceived_PopulatesAllFieldsAndEnables()
    {
        var s = new TurbineChatState();
        s.OnChannelsReceived(
            allegianceRoom: 0xA0,
            generalRoom: 0xA1,
            tradeRoom: 0xA2,
            lfgRoom: 0xA3,
            roleplayRoom: 0xA4,
            olthoiRoom: 0xA5,
            societyRoom: 0xA6,
            societyCelestialHandRoom: 0xA7,
            societyEldrytchWebRoom: 0xA8,
            societyRadiantBloodRoom: 0xA9);

        Assert.True(s.Enabled);
        Assert.Equal(0xA0u, s.AllegianceRoom);
        Assert.Equal(0xA1u, s.GeneralRoom);
        Assert.Equal(0xA2u, s.TradeRoom);
        Assert.Equal(0xA3u, s.LfgRoom);
        Assert.Equal(0xA4u, s.RoleplayRoom);
        Assert.Equal(0xA5u, s.OlthoiRoom);
        Assert.Equal(0xA6u, s.SocietyRoom);
        Assert.Equal(0xA7u, s.SocietyCelestialHandRoom);
        Assert.Equal(0xA8u, s.SocietyEldrytchWebRoom);
        Assert.Equal(0xA9u, s.SocietyRadiantBloodRoom);
    }

    [Fact]
    public void NextContextId_IsMonotonicAndStartsAt1()
    {
        var s = new TurbineChatState();
        Assert.Equal(1u, s.NextContextId());
        Assert.Equal(2u, s.NextContextId());
        Assert.Equal(3u, s.NextContextId());
        Assert.Equal(4u, s.NextContextId());
    }

    [Fact]
    public void RoomFor_ReturnsConfiguredRoom()
    {
        var s = new TurbineChatState();
        s.OnChannelsReceived(
            allegianceRoom: 1, generalRoom: 2, tradeRoom: 3,
            lfgRoom: 4, roleplayRoom: 5, olthoiRoom: 6,
            societyRoom: 7, societyCelestialHandRoom: 8,
            societyEldrytchWebRoom: 9, societyRadiantBloodRoom: 10);

        Assert.Equal(1u,  s.RoomFor(ChatChannelKindLite.Allegiance));
        Assert.Equal(2u,  s.RoomFor(ChatChannelKindLite.General));
        Assert.Equal(3u,  s.RoomFor(ChatChannelKindLite.Trade));
        Assert.Equal(4u,  s.RoomFor(ChatChannelKindLite.Lfg));
        Assert.Equal(5u,  s.RoomFor(ChatChannelKindLite.Roleplay));
        Assert.Equal(6u,  s.RoomFor(ChatChannelKindLite.Olthoi));
        Assert.Equal(7u,  s.RoomFor(ChatChannelKindLite.Society));
        Assert.Equal(8u,  s.RoomFor(ChatChannelKindLite.SocietyCelestialHand));
        Assert.Equal(9u,  s.RoomFor(ChatChannelKindLite.SocietyEldrytchWeb));
        Assert.Equal(10u, s.RoomFor(ChatChannelKindLite.SocietyRadiantBlood));
    }
}
