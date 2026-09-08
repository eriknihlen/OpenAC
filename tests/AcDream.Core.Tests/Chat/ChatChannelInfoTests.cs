using AcDream.Core.Chat;
using Xunit;

namespace AcDream.Core.Tests.Chat;

public sealed class ChatChannelInfoTests
{
    [Fact]
    public void Legacy_FellowshipBitflag_IsSelfEcho()
    {
        var c = new ChatChannelInfo.Legacy(0x00000800u, "Fellowship");
        Assert.Equal(ChatChannelSource.Legacy, c.Source);
        Assert.Equal("Fellowship", c.DisplayName);
        Assert.True(c.IsSelfEchoChannel());
    }

    [Theory]
    [InlineData(0x00001000u)]  // Vassals
    [InlineData(0x00002000u)]  // Patron
    [InlineData(0x00004000u)]  // Monarch
    [InlineData(0x01000000u)]
    public void Legacy_AllegianceTreeBitflags_AreSelfEcho(uint id)
    {
        var c = new ChatChannelInfo.Legacy(id, "Allegiance");
        Assert.True(c.IsSelfEchoChannel(), $"channel 0x{id:X8} should self-echo");
    }

    [Fact]
    public void Legacy_AllegianceBroadcastBitflag_IsSelfEcho()
    {
        var c = new ChatChannelInfo.Legacy(0x02000000u, "AllegianceBroadcast");
        Assert.True(c.IsSelfEchoChannel());
    }

    [Fact]
    public void Turbine_IsNeverSelfEcho()
    {
        var t = new ChatChannelInfo.Turbine(
            RoomId: 0x12345678u, ChatType: 2u, DispatchType: 2u, DisplayName: "General");
        Assert.Equal(ChatChannelSource.Turbine, t.Source);
        Assert.Equal("General", t.DisplayName);
        Assert.False(t.IsSelfEchoChannel());
    }

    [Fact]
    public void Legacy_UnknownBitflag_IsNotSelfEcho()
    {
        var c = new ChatChannelInfo.Legacy(0xCAFEF00Du, "WatCha");
        Assert.False(c.IsSelfEchoChannel());
    }
}
