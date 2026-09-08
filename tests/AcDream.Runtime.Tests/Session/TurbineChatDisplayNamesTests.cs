using AcDream.Core.Net.Messages;
using AcDream.Runtime.Session;
using Xunit;

namespace AcDream.Runtime.Tests.Session;

public sealed class TurbineChatDisplayNamesTests
{
    [Theory]
    [InlineData(TurbineChat.ChatType.Allegiance, 0x12u)]
    [InlineData(TurbineChat.ChatType.General, 0x1Bu)]
    [InlineData(TurbineChat.ChatType.Trade, 0x1Cu)]
    [InlineData(TurbineChat.ChatType.Lfg, 0x1Du)]
    [InlineData(TurbineChat.ChatType.Roleplay, 0x1Eu)]
    [InlineData(TurbineChat.ChatType.Society, 0x20u)]
    [InlineData(TurbineChat.ChatType.SocietyCelHan, 0x20u)]
    [InlineData(TurbineChat.ChatType.SocietyEldWeb, 0x20u)]
    [InlineData(TurbineChat.ChatType.SocietyRadBlo, 0x20u)]
    [InlineData(TurbineChat.ChatType.Olthoi, 0x12u)]
    public void LogTextType_MatchesRetailChatFormat(TurbineChat.ChatType chatType, uint expected)
    {
        Assert.Equal(expected, TurbineChatDisplayNames.LogTextType((uint)chatType));
    }

    [Fact]
    public void LogTextType_UnknownChatType_FallsBackToDefault()
    {
        Assert.Equal(0x00u, TurbineChatDisplayNames.LogTextType((uint)TurbineChat.ChatType.Undef));
    }
}
