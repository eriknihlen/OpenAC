using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class CellPhysicsPortalWiringTests
{
    [Fact]
    public void NewFields_HaveSensibleDefaults()
    {
        var cp = new CellPhysics
        {
            WorldTransform = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Resolved = new System.Collections.Generic.Dictionary<ushort, ResolvedPolygon>(),
        };

        Assert.Null(cp.CellBSP);
        Assert.Empty(cp.Portals);
        Assert.Null(cp.PortalPolygons);
        Assert.Empty(cp.VisibleCellIds);
    }

    [Fact]
    public void NewFields_AcceptInitValues()
    {
        var portal = new PortalInfo(otherCellId: 0x0101, polygonId: 5, flags: 0);

        var cp = new CellPhysics
        {
            WorldTransform = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Resolved = new System.Collections.Generic.Dictionary<ushort, ResolvedPolygon>(),
            Portals = new[] { portal },
            VisibleCellIds = new System.Collections.Generic.HashSet<uint> { 0xA9B40101 },
        };

        Assert.Single(cp.Portals);
        Assert.Equal((ushort)0x0101, cp.Portals[0].OtherCellId);
        Assert.Contains(0xA9B40101u, cp.VisibleCellIds);
    }

    [Fact]
    public void CellPhysics_PortalsRoundTrip()
    {
        var portals = new[]
        {
            new PortalInfo(otherCellId: 0x0101, polygonId: 7, flags: 0),
            new PortalInfo(otherCellId: 0xFFFF, polygonId: 8, flags: 2),
        };

        var cp = new CellPhysics
        {
            WorldTransform = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Resolved = new System.Collections.Generic.Dictionary<ushort, ResolvedPolygon>(),
            Portals = portals,
        };

        Assert.Equal(2, cp.Portals.Count);
        Assert.Equal((ushort)0x0101, cp.Portals[0].OtherCellId);
        Assert.True(cp.Portals[0].PortalSide);
        Assert.Equal((ushort)0xFFFF, cp.Portals[1].OtherCellId);
        Assert.False(cp.Portals[1].PortalSide);
    }
}
