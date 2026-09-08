using AcDream.Core.Chat;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Session;

namespace AcDream.Runtime.Gameplay;

public enum TurbineChatGateStatus
{
    /// <summary>The send may proceed to the wire.</summary>
    Allowed,

    Unavailable,

    NotListening,
}

public readonly record struct TurbineChatGateResult(
    TurbineChatGateStatus Status,
    uint RoomId,
    uint ChatType,
    string DisplayName);

public static class TurbineChatMembershipGate
{
    public static TurbineChatGateResult Evaluate(
        ChatChannelKindLite kind,
        TurbineChatState turbineChat,
        RuntimeCharacterOptionsState options,
        bool isOlthoiPlayer)
    {
        ArgumentNullException.ThrowIfNull(turbineChat);
        ArgumentNullException.ThrowIfNull(options);

        (uint room, uint chatType) = kind switch
        {
            ChatChannelKindLite.Allegiance => (
                turbineChat.AllegianceRoom,
                (uint)TurbineChat.ChatType.Allegiance),
            ChatChannelKindLite.General => (
                turbineChat.GeneralRoom,
                (uint)TurbineChat.ChatType.General),
            ChatChannelKindLite.Trade => (
                turbineChat.TradeRoom,
                (uint)TurbineChat.ChatType.Trade),
            ChatChannelKindLite.Lfg => (
                turbineChat.LfgRoom,
                (uint)TurbineChat.ChatType.Lfg),
            ChatChannelKindLite.Roleplay => (
                turbineChat.RoleplayRoom,
                (uint)TurbineChat.ChatType.Roleplay),
            ChatChannelKindLite.Society => (
                turbineChat.SocietyRoom,
                (uint)TurbineChat.ChatType.Society),
            ChatChannelKindLite.Olthoi => (
                turbineChat.OlthoiRoom,
                (uint)TurbineChat.ChatType.Olthoi),
            _ => (0u, 0u),
        };
        string name = TurbineChatDisplayNames.Resolve(room, chatType);

        if (!turbineChat.Enabled || room == 0u)
        {
            return new TurbineChatGateResult(
                TurbineChatGateStatus.Unavailable, room, chatType, name);
        }

        bool hearOption = kind switch
        {
            ChatChannelKindLite.Allegiance =>
                (options.Options1
                    & (uint)PlayerDescriptionParser.CharacterOptions1.HearAllegianceChat)
                != 0u,
            ChatChannelKindLite.General =>
                (options.Options2
                    & (uint)PlayerDescriptionParser.CharacterOptions2.HearGeneralChat)
                != 0u,
            ChatChannelKindLite.Trade =>
                (options.Options2
                    & (uint)PlayerDescriptionParser.CharacterOptions2.HearTradeChat)
                != 0u,
            ChatChannelKindLite.Lfg =>
                (options.Options2
                    & (uint)PlayerDescriptionParser.CharacterOptions2.HearLFGChat)
                != 0u,
            ChatChannelKindLite.Roleplay =>
                (options.Options2
                    & (uint)PlayerDescriptionParser.CharacterOptions2.HearRoleplayChat)
                != 0u,
            ChatChannelKindLite.Society =>
                (options.Options2
                    & (uint)PlayerDescriptionParser.CharacterOptions2.HearSocietyChat)
                != 0u,
            ChatChannelKindLite.Olthoi => isOlthoiPlayer,
            _ => true,
        };

        return hearOption
            ? new TurbineChatGateResult(TurbineChatGateStatus.Allowed, room, chatType, name)
            : new TurbineChatGateResult(TurbineChatGateStatus.NotListening, room, chatType, name);
    }

    public static (string Text, RetailLogTextType Type)? ResolveRefusalText(
        TurbineChatGateResult gate)
    {
        switch (gate.Status)
        {
            case TurbineChatGateStatus.Unavailable:
                return (ClientTextRefusals.TurbineChatUnavailable, RetailLogTextType.Default);
            case TurbineChatGateStatus.NotListening:
                (string? text, RetailLogTextType type) =
                    WeenieErrorMessages.Resolve(0x0551u, gate.DisplayName);
                return text is not null ? (text, type) : null;
            default:
                return null;
        }
    }
}
