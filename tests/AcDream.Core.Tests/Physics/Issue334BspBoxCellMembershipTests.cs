using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public sealed class Issue334BspBoxCellMembershipTests
{
    // Landblock (0xA9, 0xB4). Global lcoord origin = (0xA9*8, 0xB4*8).
    private const uint LbId = 0xA9B40000u;
    private const int GxBase = 0xA9 * 8;    // 1352
    private const int GyBase = 0xB4 * 8;    // 1440

    private static uint Cell(int gx, int gy)
        => (uint)(((((gx >> 3) << 8) | (gy >> 3)) << 16) | ((gx & 7) * 8 + (gy & 7) + 1));

    private static ShadowShape BspPart(
        Vector3 boxMin,
        Vector3 boxMax,
        float sphereRadius = 1f,
        Vector3 sphereCentre = default,
        Vector3 localPosition = default,
        Quaternion localRotation = default)
        => ShadowShape.Bsp(
            gfxObjId: 0x010046D8u,
            localPosition: localPosition,
            localRotation: localRotation == default ? Quaternion.Identity : localRotation,
            scale: 1f,
            localGeometry: ShadowPartGeometry.Create(
                new FlatCollisionSphere(sphereCentre, sphereRadius),
                new FlatGfxObjVisualBounds(
                    boxMin,
                    boxMax,
                    (boxMin + boxMax) * 0.5f,
                    ((boxMax - boxMin) * 0.5f).Length(),
                    (boxMax - boxMin) * 0.5f)));

    private static ShadowShape SphereOnlyPart(
        float sphereRadius,
        Vector3 sphereCentre = default,
        Vector3 localPosition = default)
        => ShadowShape.Bsp(
            gfxObjId: 0x010046D8u,
            localPosition: localPosition,
            localRotation: Quaternion.Identity,
            scale: 1f,
            localGeometry: ShadowPartGeometry.Create(
                new FlatCollisionSphere(sphereCentre, sphereRadius),
                null));

    private static List<uint> Rectangle(
        Vector3 entityWorldPos,
        uint seedCellId,
        params ShadowShape[] shapes)
    {
        var boxes = shapes
            .Select(s => ShadowPartBox.FromShape(s, entityWorldPos, Quaternion.Identity))
            .ToList();
        var candidates = new CellArray();
        CellTransit.AddAllOutsideCellsFromParts(
            boxes, seedCellId, Vector3.Zero, candidates);
        return candidates.OrderedIds.ToList();
    }

    [Fact]
    public void T1_HundredMetreBox_SpansFiveCellsPerAxis()
    {
        var shape = BspPart(new Vector3(-50f, -50f, -3f), new Vector3(50f, 50f, 3f));
        List<uint> cells = Rectangle(new Vector3(36f, 36f, 0f), LbId | 10u, shape);

        var expected = new List<uint>();
        for (int x = GxBase - 1; x <= GxBase + 3; x++)
            for (int y = GyBase - 1; y <= GyBase + 3; y++)
                expected.Add(Cell(x, y));

        Assert.Equal(25, cells.Count);
        Assert.Equal(expected.OrderBy(v => v), cells.OrderBy(v => v));

        List<uint> sphereOnly = Rectangle(
            new Vector3(36f, 36f, 0f), LbId | 10u, SphereOnlyPart(1f));
        Assert.Single(sphereOnly);
        Assert.Equal(Cell(GxBase + 1, GyBase + 1), sphereOnly[0]);
    }

    [Fact]
    public void T2_LShapedPartArray_ClaimsTheCornerThatClosesTheL()
    {
        var box = (Min: new Vector3(-5f, -5f, -2f), Max: new Vector3(5f, 5f, 2f));
        var anchor = BspPart(box.Min, box.Max);
        var eastArm = BspPart(box.Min, box.Max, localPosition: new Vector3(48f, 0f, 0f));
        var northArm = BspPart(box.Min, box.Max, localPosition: new Vector3(0f, 48f, 0f));

        var entity = new Vector3(36f, 36f, 0f);
        List<uint> cells = Rectangle(entity, LbId | 10u, anchor, eastArm, northArm);

        uint corner = Cell(GxBase + 3, GyBase + 3);
        Assert.Contains(corner, cells);
        Assert.Equal(9, cells.Count);

        Assert.DoesNotContain(corner, Rectangle(entity, LbId | 10u, anchor));
        Assert.DoesNotContain(corner, Rectangle(entity, LbId | 10u, anchor, eastArm));
        Assert.DoesNotContain(corner, Rectangle(entity, LbId | 10u, anchor, northArm));
    }

    [Fact]
    public void T3_BoxPastTheBlockEdge_ProducesNeighbourLandblockCellIds()
    {
        var shape = BspPart(new Vector3(-30f, -30f, -2f), new Vector3(30f, 30f, 2f));
        List<uint> cells = Rectangle(new Vector3(180f, 180f, 0f), LbId | 64u, shape);

        Assert.Equal(9, cells.Count);
        Assert.Contains(Cell(GxBase + 7, GyBase + 7), cells);
        Assert.Contains(Cell(GxBase + 8, GyBase + 7), cells);     // 0xAAB4xxxx
        Assert.Contains(Cell(GxBase + 7, GyBase + 8), cells);     // 0xA9B5xxxx
        Assert.Contains(Cell(GxBase + 8, GyBase + 8), cells);     // 0xAAB5xxxx

        Assert.Contains(cells, id => (id & 0xFFFF0000u) == 0xAAB40000u);
        Assert.Contains(cells, id => (id & 0xFFFF0000u) == 0xA9B50000u);
        Assert.Contains(cells, id => (id & 0xFFFF0000u) == 0xAAB50000u);
    }

    [Fact]
    public void T4_RectangleAtTheMapCorners_EmitsNothingOutsideTheMap()
    {
        var box = BspPart(new Vector3(-50f, -50f, -2f), new Vector3(50f, 50f, 2f));
        List<uint> sw = Rectangle(new Vector3(12f, 12f, 0f), 0x00000001u, box);
        Assert.All(sw, id => Assert.NotEqual(0u, id));
        Assert.Equal(9, sw.Count);                    // x 0..2 × y 0..2 survive
        Assert.Contains(0x00000001u, sw);

        const uint neLb = 0xFEFE0000u;
        int neGx = 254 * 8, neGy = 254 * 8;
        List<uint> ne = Rectangle(new Vector3(180f, 180f, 0f), neLb | 64u, box);
        Assert.All(ne, id => Assert.NotEqual(0u, id));
        Assert.Equal(9, ne.Count);                    // x 2035..2037+ y likewise
        Assert.Contains(Cell(neGx + 7, neGy + 7), ne);
        Assert.DoesNotContain(Cell(2040 & 0x7FF, 2040 & 0x7FF), ne);
    }

    [Fact]
    public void T5_RotatedAsymmetricBox_KeepsTheCornerOverhangMinMaxWouldLose()
    {
        Quaternion yaw37 = Quaternion.CreateFromAxisAngle(
            Vector3.UnitZ, 37f * MathF.PI / 180f);
        var shape = BspPart(
            new Vector3(0f, 0f, 0f), new Vector3(60f, 4f, 2f),
            localRotation: yaw37);

        List<uint> cells = Rectangle(new Vector3(48f, 12f, 0f), LbId | 17u, shape);

        Assert.Contains(Cell(GxBase + 1, GyBase + 0), cells);
        Assert.Contains(Cell(GxBase + 3, GyBase + 2), cells);

        var unrotated = BspPart(new Vector3(0f, 0f, 0f), new Vector3(60f, 4f, 2f));
        List<uint> flat = Rectangle(new Vector3(48f, 12f, 0f), LbId | 17u, unrotated);
        Assert.DoesNotContain(Cell(GxBase + 1, GyBase + 0), flat);
    }

    [Fact]
    public void T9_BoxOverhangingTheBlockOrigin_ReachesTheNegativeColumn()
    {
        var shape = BspPart(new Vector3(-20f, -20f, -2f), new Vector3(20f, 20f, 2f));
        // Entity at world (12, 12): the box spans -8..32, whose floor is -1.
        List<uint> cells = Rectangle(new Vector3(12f, 12f, 0f), LbId | 1u, shape);

        Assert.Contains(Cell(GxBase - 1, GyBase - 1), cells);
        Assert.Contains(Cell(GxBase - 1, GyBase + 0), cells);
        Assert.Contains(Cell(GxBase + 0, GyBase - 1), cells);
        Assert.Equal(9, cells.Count);
        Assert.Contains(Cell(GxBase + 1, GyBase + 1), cells);
    }

    [Fact]
    public void T8_BasePositionOffTheMap_AddsNothingAndDoesNotThrow()
    {
        var shape = BspPart(new Vector3(-5f, -5f, -2f), new Vector3(5f, 5f, 2f));
        var boxes = new List<ShadowPartBox>
        {
            ShadowPartBox.FromShape(
                shape, new Vector3(-100000f, -100000f, 0f), Quaternion.Identity),
        };
        var candidates = new CellArray();

        bool added = CellTransit.AddAllOutsideCellsFromParts(
            boxes, 0x00000001u, Vector3.Zero, candidates);

        Assert.False(added);
        Assert.Empty(candidates.OrderedIds);
    }

    [Fact]
    public void T6_CylinderOnlyOwner_MatchesTheUntouchedSphereFlood()
    {
        var cylinder = ShadowShape.Cylinder(
            gfxObjId: 0u,
            localPosition: Vector3.Zero,
            localRotation: Quaternion.Identity,
            scale: 1f,
            radius: 12f,
            cylHeight: 24f);

        var reg = new ShadowObjectRegistry();
        const uint ownerId = 0x334001u;
        var worldPos = new Vector3(36f, 36f, 50f);
        reg.RegisterMultiPart(
            ownerId, worldPos, Quaternion.Identity,
            new[] { cylinder }, 0u, EntityCollisionFlags.None,
            0f, 0f, LbId);

        IReadOnlyList<uint> expected = CellTransit.BuildShadowCellSet(
            new PhysicsDataCache(),
            LbId | 10u,
            new[]
            {
                new DatReaderWriter.Types.Sphere { Origin = worldPos, Radius = 12f },
            },
            1,
            isStatic: false);

        Assert.Equal(new[] { LbId | 10u }, expected);

        var actual = new List<uint>();
        foreach (uint id in expected)
        {
            if (reg.GetObjectsInCell(id).Any(e => e.EntityId == ownerId))
                actual.Add(id);
        }

        Assert.NotEmpty(expected);
        Assert.Equal(expected.OrderBy(v => v), actual.OrderBy(v => v));

        for (uint index = 1u; index <= 64u; index++)
        {
            uint cellId = LbId | index;
            bool held = reg.GetObjectsInCell(cellId).Any(e => e.EntityId == ownerId);
            Assert.Equal(expected.Contains(cellId), held);
        }
    }

    [Fact]
    public void RegisterMultiPart_BspBearingOwner_OccupiesTheFullBoxRectangle()
    {
        var shape = BspPart(new Vector3(-50f, -50f, -3f), new Vector3(50f, 50f, 3f));
        var reg = new ShadowObjectRegistry();
        const uint ownerId = 0x334002u;

        reg.RegisterMultiPart(
            ownerId, new Vector3(36f, 36f, 0f), Quaternion.Identity,
            new[] { shape }, 0u, EntityCollisionFlags.None,
            0f, 0f, LbId, seedCellId: LbId | 10u);

        int held = 0;
        for (int x = GxBase - 1; x <= GxBase + 3; x++)
            for (int y = GyBase - 1; y <= GyBase + 3; y++)
            {
                uint cellId = Cell(x, y);
                Assert.Contains(
                    reg.GetObjectsInCell(cellId),
                    e => e.EntityId == ownerId);
                held++;
            }

        Assert.Equal(25, held);
    }
}
