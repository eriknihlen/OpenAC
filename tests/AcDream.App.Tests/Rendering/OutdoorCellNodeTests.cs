using System.Numerics;
using AcDream.App.Rendering;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public class OutdoorCellNodeTests
{
    [Fact]
    public void Build_ReturnsPortallessOutdoorRoot()
    {
        uint outdoorId = 0xA9B40031;
        LoadedCell node = OutdoorCellNode.Build(outdoorId);

        Assert.Equal(outdoorId, node.CellId);
        Assert.True(node.IsOutdoorNode);
        Assert.True(node.SeenOutside);
        Assert.Equal(Matrix4x4.Identity, node.WorldTransform);
        Assert.Empty(node.Portals);
    }
}
