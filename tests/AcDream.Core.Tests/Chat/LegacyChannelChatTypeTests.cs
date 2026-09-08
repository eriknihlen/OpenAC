using AcDream.Core.Chat;
using Xunit;

namespace AcDream.Core.Tests.Chat;

public sealed class LegacyChannelChatTypeTests
{
    [Theory]
    [InlineData(0x0800u, 0x13u)]      // Fellowship
    [InlineData(0x1000000u, 0x0Au)]   // Co-Vassals
    [InlineData(0x2000000u, 0x0Au)]   // Allegiance Broadcast
    public void Resolve_SameForHearAndSend(uint channelId, uint expected)
    {
        Assert.Equal(expected, LegacyChannelChatType.Resolve(channelId, ownSend: false));
        Assert.Equal(expected, LegacyChannelChatType.Resolve(channelId, ownSend: true));
    }

    [Theory]
    [InlineData(0x1000u)]   // Patron
    [InlineData(0x2000u)]   // Vassal
    [InlineData(0x4000u)]   // Follower / Monarch
    public void Resolve_HearIsSocial_SendIsSocialSend(uint channelId)
    {
        Assert.Equal(0x0Au, LegacyChannelChatType.Resolve(channelId, ownSend: false));
        Assert.Equal(0x0Bu, LegacyChannelChatType.Resolve(channelId, ownSend: true));
    }

    [Fact]
    public void Resolve_Abuse_IsRetailsOnly0xEProducer()
    {
        Assert.Equal(0x0Eu, LegacyChannelChatType.Resolve(0x0001u, ownSend: false));
        Assert.Equal(0x0Eu, LegacyChannelChatType.Resolve(0x0001u, ownSend: true));
    }

    [Fact]
    public void Resolve_Help_IsSameForHearAndSend()
    {
        Assert.Equal(0x0Fu, LegacyChannelChatType.Resolve(0x0400u, ownSend: false));
        Assert.Equal(0x0Fu, LegacyChannelChatType.Resolve(0x0400u, ownSend: true));
    }

    [Fact]
    public void Resolve_FellowBroadcast_HearIsChannel_SendIsFellowship()
    {
        Assert.Equal(0x08u, LegacyChannelChatType.Resolve(0x4000000u, ownSend: false));
        Assert.Equal(0x13u, LegacyChannelChatType.Resolve(0x4000000u, ownSend: true));
    }

    [Theory]
    [InlineData(0x0100u)]
    [InlineData(0xDEADu)]  // any other unmatched bit
    public void Resolve_CatchAll_HearIsChannel_SendIsChannelSend(uint channelId)
    {
        Assert.Equal(0x08u, LegacyChannelChatType.Resolve(channelId, ownSend: false));
        Assert.Equal(0x09u, LegacyChannelChatType.Resolve(channelId, ownSend: true));
    }
}
