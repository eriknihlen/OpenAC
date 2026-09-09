using System.Numerics;
using AcDream.Core.World.Cells;
using Xunit;

namespace AcDream.Core.Tests.World.Cells;

public class ObjCellBaseTests
{
    private sealed class StubCell : ObjCell
    {
        public StubCell(uint id)
            : base(id, Matrix4x4.Identity, Matrix4x4.Identity,
                   Vector3.Zero, Vector3.One,
                   System.Array.Empty<CellPortal>(), System.Array.Empty<uint>(), false) { }
        public override bool PointInCell(Vector3 worldPoint) => false;
    }

    [Theory]
    [InlineData(0xA9B40174u, true)]
    [InlineData(0xA9B40005u, false)]
    [InlineData(0xA9B40100u, true)]
    [InlineData(0xA9B400FFu, false)]
    public void IsEnv_DispatchesByLow16Magnitude(uint id, bool expected)
        => Assert.Equal(expected, new StubCell(id).IsEnv);

    [Fact]
    public void Ctor_StoresBaseProperties()
    {
        var c = new StubCell(0xA9B40174u);
        Assert.Equal(0xA9B40174u, c.Id);
        Assert.Equal(Vector3.One, c.LocalBoundsMax);
        Assert.Empty(c.Portals);
        Assert.Empty(c.StabList);
        Assert.False(c.SeenOutside);
    }
}
