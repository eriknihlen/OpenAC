using AcDream.Core.Net.Messages;

namespace AcDream.Runtime.Session;

internal static class TurbineChatDisplayNames
{
    public static string Resolve(uint roomId, uint chatType) =>
        (TurbineChat.ChatType)chatType switch
        {
            TurbineChat.ChatType.Allegiance => "Allegiance",
            TurbineChat.ChatType.General => "General",
            TurbineChat.ChatType.Trade => "Trade",
            TurbineChat.ChatType.Lfg => "LFG",
            TurbineChat.ChatType.Roleplay => "Roleplay",
            TurbineChat.ChatType.Society => "Society",
            TurbineChat.ChatType.SocietyCelHan => "Celestial Hand",
            TurbineChat.ChatType.SocietyEldWeb => "Eldrytch Web",
            TurbineChat.ChatType.SocietyRadBlo => "Radiant Blood",
            TurbineChat.ChatType.Olthoi => "Olthoi",
            _ => $"Room 0x{roomId:X8}",
        };

    public static uint LogTextType(uint chatType) =>
        (TurbineChat.ChatType)chatType switch
        {
            TurbineChat.ChatType.Allegiance    => 0x12u,
            TurbineChat.ChatType.General       => 0x1Bu,
            TurbineChat.ChatType.Trade         => 0x1Cu,
            TurbineChat.ChatType.Lfg           => 0x1Du,
            TurbineChat.ChatType.Roleplay      => 0x1Eu,
            TurbineChat.ChatType.Society       => 0x20u,
            TurbineChat.ChatType.SocietyCelHan => 0x20u,
            TurbineChat.ChatType.SocietyEldWeb => 0x20u,
            TurbineChat.ChatType.SocietyRadBlo => 0x20u,
            TurbineChat.ChatType.Olthoi        => 0x12u,
            _                                  => 0x00u,
        };
}
