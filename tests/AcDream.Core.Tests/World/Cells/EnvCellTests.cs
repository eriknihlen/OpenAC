using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.World.Cells;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Xunit;
using UcgCellPortal = AcDream.Core.World.Cells.CellPortal;

namespace AcDream.Core.Tests.World.Cells;

public class EnvCellTests
{
    private static readonly UcgCellPortal[] OnePortal =
        [new UcgCellPortal(0xA9B4_0175u, 0, 0, 0)];

    private static EnvCell Make(
        CellBSPNode? root,
        bool prepared,
        bool hasPortals,
        Matrix4x4? transform = null)
    {
        var t = transform ?? Matrix4x4.Identity;
        Matrix4x4.Invert(t, out var inv);
        return new EnvCell(
            0xA9B40174u,
            t,
            inv,
            -Vector3.One,
            Vector3.One,
            hasPortals ? OnePortal : Array.Empty<UcgCellPortal>(),
            Array.Empty<uint>(),
            seenOutside: false,
            containmentBsp: new CellBSPTree { Root = root },
            flatContainmentBsp: prepared
                ? FlatCollisionAssetBuilder.FlattenCellContainmentBsp(root)
                : null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PointInCell_ZeroPortals_RejectsBeforeContainment(bool prepared)
    {
        var root = new CellBSPNode { Type = BSPNodeType.Leaf };

        Assert.False(Make(root, prepared, hasPortals: false).PointInCell(Vector3.Zero));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PointInCell_RootlessContainment_IsRejected(bool prepared)
    {
        Assert.False(Make(null, prepared, hasPortals: true).PointInCell(Vector3.Zero));
    }

    [Fact]
    public void PointInCell_TransformsWorldToLocalBeforeTesting()
    {
        var root = new CellBSPNode
        {
            Type = BSPNodeType.BPIn,
            SplittingPlane = new Plane(Vector3.UnitX, 0f),
            PosNode = new CellBSPNode { Type = BSPNodeType.Leaf },
        };
        var cell = Make(
            root,
            prepared: false,
            hasPortals: true,
            transform: Matrix4x4.CreateTranslation(100f, 0f, 0f));

        Assert.True(cell.PointInCell(new Vector3(105f, 0f, 0f)));
        Assert.False(cell.PointInCell(new Vector3(95f, 0f, 0f)));
    }

    [Fact]
    public void PointInCell_PreparedContainmentIsAuthoritative()
    {
        var graphRoot = new CellBSPNode
        {
            Type = BSPNodeType.BPIn,
            SplittingPlane = new Plane(Vector3.UnitX, -1f),
            PosNode = new CellBSPNode { Type = BSPNodeType.Leaf },
        };
        var flatRoot = new CellBSPNode
        {
            Type = BSPNodeType.Leaf,
            LeafIndex = 1,
        };
        FlatCellContainmentBsp flat =
            FlatCollisionAssetBuilder.FlattenCellContainmentBsp(flatRoot);
        var cell = new EnvCell(
            0xA9B4_0174u,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            -Vector3.One,
            Vector3.One,
            OnePortal,
            Array.Empty<uint>(),
            seenOutside: false,
            containmentBsp: new CellBSPTree { Root = graphRoot },
            flatContainmentBsp: flat);

        Assert.True(cell.PointInCell(Vector3.Zero));
        Assert.False(BSPQuery.PointInsideCellBsp(graphRoot, Vector3.Zero));
    }
}
