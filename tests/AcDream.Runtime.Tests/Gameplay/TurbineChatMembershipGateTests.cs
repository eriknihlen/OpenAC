using AcDream.Core.Chat;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class TurbineChatMembershipGateTests
{
    [Fact]
    public void TurbineDisabled_ReturnsUnavailable_EvenWithRoomIdAndOptionOn()
    {
        var turbine = new TurbineChatState(); // never received SetTurbineChatChannels
        var options = new RuntimeCharacterOptionsState();

        TurbineChatGateResult result = TurbineChatMembershipGate.Evaluate(
            ChatChannelKindLite.General, turbine, options, isOlthoiPlayer: false);

        Assert.Equal(TurbineChatGateStatus.Unavailable, result.Status);
        Assert.Equal(0u, result.RoomId);
    }

    [Fact]
    public void RoomIdZero_ReturnsUnavailable_NoAllegiance()
    {
        TurbineChatState turbine = ReceivedRooms(allegianceRoom: 0u);
        var options = new RuntimeCharacterOptionsState();

        TurbineChatGateResult result = TurbineChatMembershipGate.Evaluate(
            ChatChannelKindLite.Allegiance, turbine, options, isOlthoiPlayer: false);

        Assert.Equal(TurbineChatGateStatus.Unavailable, result.Status);
    }

    [Theory]
    [InlineData(ChatChannelKindLite.General)]
    [InlineData(ChatChannelKindLite.Trade)]
    [InlineData(ChatChannelKindLite.Lfg)]
    [InlineData(ChatChannelKindLite.Roleplay)]
    [InlineData(ChatChannelKindLite.Society)]
    public void HearOptionOff_ReturnsNotListening_RoomIdAndTypeStillReported(
        ChatChannelKindLite kind)
    {
        TurbineChatState turbine = ReceivedRooms();
        var options = new RuntimeCharacterOptionsState();
        options.Replace(options.Options1, 0u);

        TurbineChatGateResult result = TurbineChatMembershipGate.Evaluate(
            kind, turbine, options, isOlthoiPlayer: false);

        Assert.Equal(TurbineChatGateStatus.NotListening, result.Status);
        Assert.NotEqual(0u, result.RoomId);
        Assert.NotEqual(string.Empty, result.DisplayName);
    }

    [Fact]
    public void AllegianceHearOptionOff_ReturnsNotListening()
    {
        TurbineChatState turbine = ReceivedRooms();
        var options = new RuntimeCharacterOptionsState();
        options.Replace(0u, options.Options2); // HearAllegianceChat bit off

        TurbineChatGateResult result = TurbineChatMembershipGate.Evaluate(
            ChatChannelKindLite.Allegiance, turbine, options, isOlthoiPlayer: false);

        Assert.Equal(TurbineChatGateStatus.NotListening, result.Status);
    }

    [Fact]
    public void OlthoiRoom_GatesOnHeritageNotAnOption()
    {
        TurbineChatState turbine = ReceivedRooms();
        var options = new RuntimeCharacterOptionsState(); // no Hear-Olthoi bit exists

        TurbineChatGateResult notOlthoi = TurbineChatMembershipGate.Evaluate(
            ChatChannelKindLite.Olthoi, turbine, options, isOlthoiPlayer: false);
        TurbineChatGateResult olthoi = TurbineChatMembershipGate.Evaluate(
            ChatChannelKindLite.Olthoi, turbine, options, isOlthoiPlayer: true);

        Assert.Equal(TurbineChatGateStatus.NotListening, notOlthoi.Status);
        Assert.Equal(TurbineChatGateStatus.Allowed, olthoi.Status);
    }

    [Theory]
    [InlineData(ChatChannelKindLite.Allegiance, 0x10u, (uint)TurbineChat.ChatType.Allegiance, "Allegiance")]
    [InlineData(ChatChannelKindLite.General, 0x11u, (uint)TurbineChat.ChatType.General, "General")]
    [InlineData(ChatChannelKindLite.Trade, 0x12u, (uint)TurbineChat.ChatType.Trade, "Trade")]
    [InlineData(ChatChannelKindLite.Lfg, 0x13u, (uint)TurbineChat.ChatType.Lfg, "LFG")]
    [InlineData(ChatChannelKindLite.Society, 0x16u, (uint)TurbineChat.ChatType.Society, "Society")]
    [InlineData(ChatChannelKindLite.Olthoi, 0x17u, (uint)TurbineChat.ChatType.Olthoi, "Olthoi")]
    public void AllowedResultCarriesRoomIdChatTypeAndDisplayName(
        ChatChannelKindLite kind,
        uint expectedRoom,
        uint expectedChatType,
        string expectedName)
    {
        TurbineChatState turbine = ReceivedRooms(
            allegianceRoom: 0x10u,
            generalRoom: 0x11u,
            tradeRoom: 0x12u,
            lfgRoom: 0x13u,
            roleplayRoom: 0x14u,
            olthoiRoom: 0x17u,
            societyRoom: 0x16u);
        var options = new RuntimeCharacterOptionsState();
        options.Replace(
            options.Options1,
            options.Options2
                | (uint)PlayerDescriptionParser.CharacterOptions2.HearSocietyChat);

        TurbineChatGateResult result = TurbineChatMembershipGate.Evaluate(
            kind, turbine, options, isOlthoiPlayer: true);

        Assert.Equal(TurbineChatGateStatus.Allowed, result.Status);
        Assert.Equal(expectedRoom, result.RoomId);
        Assert.Equal(expectedChatType, result.ChatType);
        Assert.Equal(expectedName, result.DisplayName);
    }

    [Fact]
    public void FreshDefaultOptions_MatchAceMembership_RoleplayAndSocietyAreOff()
    {
        TurbineChatState turbine = ReceivedRooms();
        var options = new RuntimeCharacterOptionsState(); // untouched defaults

        Assert.Equal(
            TurbineChatGateStatus.Allowed,
            TurbineChatMembershipGate.Evaluate(
                ChatChannelKindLite.Allegiance, turbine, options, false).Status);
        Assert.Equal(
            TurbineChatGateStatus.Allowed,
            TurbineChatMembershipGate.Evaluate(
                ChatChannelKindLite.General, turbine, options, false).Status);
        Assert.Equal(
            TurbineChatGateStatus.Allowed,
            TurbineChatMembershipGate.Evaluate(
                ChatChannelKindLite.Trade, turbine, options, false).Status);
        Assert.Equal(
            TurbineChatGateStatus.Allowed,
            TurbineChatMembershipGate.Evaluate(
                ChatChannelKindLite.Lfg, turbine, options, false).Status);
        Assert.Equal(
            TurbineChatGateStatus.NotListening,
            TurbineChatMembershipGate.Evaluate(
                ChatChannelKindLite.Roleplay, turbine, options, false).Status);
        Assert.Equal(
            TurbineChatGateStatus.NotListening,
            TurbineChatMembershipGate.Evaluate(
                ChatChannelKindLite.Society, turbine, options, false).Status);
    }

    // ── N3 (CH3 Opus review): shared refusal-text mapping ──

    [Fact]
    public void ResolveRefusalText_AllowedGate_ReturnsNull()
    {
        TurbineChatState turbine = ReceivedRooms();
        var options = new RuntimeCharacterOptionsState();
        TurbineChatGateResult gate = TurbineChatMembershipGate.Evaluate(
            ChatChannelKindLite.General, turbine, options, isOlthoiPlayer: false);

        Assert.Equal(TurbineChatGateStatus.Allowed, gate.Status);
        Assert.Null(TurbineChatMembershipGate.ResolveRefusalText(gate));
    }

    [Fact]
    public void ResolveRefusalText_UnavailableGate_ReturnsRetailUnavailableString()
    {
        var turbine = new TurbineChatState(); // never received SetTurbineChatChannels
        var options = new RuntimeCharacterOptionsState();
        TurbineChatGateResult gate = TurbineChatMembershipGate.Evaluate(
            ChatChannelKindLite.General, turbine, options, isOlthoiPlayer: false);

        (string Text, RetailLogTextType Type)? refusal =
            TurbineChatMembershipGate.ResolveRefusalText(gate);

        Assert.NotNull(refusal);
        Assert.Equal(ClientTextRefusals.TurbineChatUnavailable, refusal!.Value.Text);
    }

    [Fact]
    public void ResolveRefusalText_NotListeningGate_ReturnsWeenieErrorString()
    {
        TurbineChatState turbine = ReceivedRooms();
        var options = new RuntimeCharacterOptionsState();
        options.Replace(options.Options1, 0u);
        TurbineChatGateResult gate = TurbineChatMembershipGate.Evaluate(
            ChatChannelKindLite.General, turbine, options, isOlthoiPlayer: false);

        (string Text, RetailLogTextType Type)? refusal =
            TurbineChatMembershipGate.ResolveRefusalText(gate);

        Assert.NotNull(refusal);
        (string? expectedText, RetailLogTextType expectedType) =
            WeenieErrorMessages.Resolve(0x0551u, gate.DisplayName);
        Assert.Equal(expectedText, refusal!.Value.Text);
        Assert.Equal(expectedType, refusal.Value.Type);
    }


    [Fact]
    public void JoinChannel_SetOptionBit_FlipsGateToAllowed_WithoutFreshPlayerDescription()
    {
        TurbineChatState turbine = ReceivedRooms();
        var options = new RuntimeCharacterOptionsState();
        options.Replace(options.Options1, 0u);

        Assert.Equal(
            TurbineChatGateStatus.NotListening,
            TurbineChatMembershipGate.Evaluate(
                ChatChannelKindLite.General, turbine, options, isOlthoiPlayer: false).Status);

        options.SetOptionBit((uint)CharacterOptionId.ListenToGeneralChat, true);

        Assert.Equal(
            TurbineChatGateStatus.Allowed,
            TurbineChatMembershipGate.Evaluate(
                ChatChannelKindLite.General, turbine, options, isOlthoiPlayer: false).Status);
    }

    [Fact]
    public void LeaveChannel_SetOptionBit_FlipsGateToNotListening()
    {
        TurbineChatState turbine = ReceivedRooms();
        var options = new RuntimeCharacterOptionsState(); // General on by default

        Assert.Equal(
            TurbineChatGateStatus.Allowed,
            TurbineChatMembershipGate.Evaluate(
                ChatChannelKindLite.General, turbine, options, isOlthoiPlayer: false).Status);

        options.SetOptionBit((uint)CharacterOptionId.ListenToGeneralChat, false);

        Assert.Equal(
            TurbineChatGateStatus.NotListening,
            TurbineChatMembershipGate.Evaluate(
                ChatChannelKindLite.General, turbine, options, isOlthoiPlayer: false).Status);
    }

    private static TurbineChatState ReceivedRooms(
        uint allegianceRoom = 0x10u,
        uint generalRoom = 0x11u,
        uint tradeRoom = 0x12u,
        uint lfgRoom = 0x13u,
        uint roleplayRoom = 0x14u,
        uint olthoiRoom = 0x15u,
        uint societyRoom = 0x16u)
    {
        var state = new TurbineChatState();
        state.OnChannelsReceived(
            allegianceRoom,
            generalRoom,
            tradeRoom,
            lfgRoom,
            roleplayRoom,
            olthoiRoom,
            societyRoom,
            societyCelestialHandRoom: 0u,
            societyEldrytchWebRoom: 0u,
            societyRadiantBloodRoom: 0u);
        return state;
    }
}
