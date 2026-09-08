using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class PortalInfoTests
{
    [Fact]
    public void PortalSide_FlagsBit2Clear_ReturnsTrue()
    {
        var portal = new PortalInfo(otherCellId: 0x0101, polygonId: 5, flags: 0);
        Assert.True(portal.PortalSide);
    }

    [Fact]
    public void PortalSide_FlagsBit2Set_ReturnsFalse()
    {
        var portal = new PortalInfo(otherCellId: 0x0101, polygonId: 5, flags: 2);
        Assert.False(portal.PortalSide);
    }

    [Fact]
    public void PortalSide_OtherBitsSet_FollowsOnlyBit2()
    {
        var portal = new PortalInfo(otherCellId: 0x0101, polygonId: 5, flags: 0xFF & ~2);
        Assert.True(portal.PortalSide);
    }

    [Fact]
    public void OtherCellId_StoredAsLowSixteenBits()
    {
        var portal = new PortalInfo(otherCellId: 0xFFFF, polygonId: 5, flags: 0);
        Assert.Equal((ushort)0xFFFF, portal.OtherCellId);
    }
}
