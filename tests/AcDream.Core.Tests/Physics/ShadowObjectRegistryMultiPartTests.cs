using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class ShadowObjectRegistryMultiPartTests
{
    private const uint LbId = 0xA9B40000u;
    private const float OffX = 0f;
    private const float OffY = 0f;

    private static IReadOnlyList<ShadowShape> DoorShapes() => new[]
    {
        ShadowShape.Cylinder(
            gfxObjId:      0u,
            localPosition: new Vector3(0f, 0f, 0.018f),
            localRotation: Quaternion.Identity,
            scale:         1.0f,
            radius:        0.100f,
            cylHeight:     0.200f),
        ShadowShape.Bsp(
            gfxObjId:      0x010044B5u,
            localPosition: Vector3.Zero,
            localRotation: Quaternion.Identity,
            scale:         1.0f,
            localGeometry:   ShadowPartGeometry.Create(new FlatCollisionSphere(Vector3.Zero, 2.0f), null)),
        ShadowShape.Bsp(
            gfxObjId:      0x010044B6u,
            localPosition: Vector3.Zero,
            localRotation: Quaternion.Identity,
            scale:         1.0f,
            localGeometry:   ShadowPartGeometry.Create(new FlatCollisionSphere(Vector3.Zero, 2.0f), null)),
        ShadowShape.Bsp(
            gfxObjId:      0x010044B6u,
            localPosition: Vector3.Zero,
            localRotation: Quaternion.Identity,
            scale:         1.0f,
            localGeometry:   ShadowPartGeometry.Create(new FlatCollisionSphere(Vector3.Zero, 2.0f), null))
    };

    [Fact]
    public void RegisterMultiPart_FourShapes_AllShareEntityId()
    {
        var reg = new ShadowObjectRegistry();
        const uint doorEntityId = 0x000F4244u;

        reg.RegisterMultiPart(
            entityId:        doorEntityId,
            entityWorldPos:  new Vector3(132.6f, 17.1f, 94.08f),
            entityWorldRot:  Quaternion.Identity,
            shapes:          DoorShapes(),
            state:           0x10008u,
            flags:           EntityCollisionFlags.None,
            worldOffsetX:    OffX,
            worldOffsetY:    OffY,
            landblockId:     LbId);

        int found = 0;
        foreach (var entry in reg.AllEntriesForDebug())
        {
            if (entry.EntityId == doorEntityId) found++;
        }
        Assert.True(found >= 4,
            $"Expected at least 4 entries for door entity (one per shape); found {found}");
    }

    [Fact]
    public void RegisterMultiPart_EmptyShapeList_NoOp()
    {
        var reg = new ShadowObjectRegistry();
        reg.RegisterMultiPart(
            entityId:       0x1u,
            entityWorldPos: Vector3.Zero,
            entityWorldRot: Quaternion.Identity,
            shapes:         System.Array.Empty<ShadowShape>(),
            state:          0u,
            flags:          EntityCollisionFlags.None,
            worldOffsetX:   OffX, worldOffsetY: OffY, landblockId: LbId);

        Assert.Equal(0, reg.TotalRegistered);
    }

    [Fact]
    public void Deregister_RemovesAllParts()
    {
        var reg = new ShadowObjectRegistry();
        const uint doorEntityId = 0x000F4244u;
        reg.RegisterMultiPart(doorEntityId, new Vector3(132.6f, 17.1f, 94.08f),
                               Quaternion.Identity, DoorShapes(), 0x10008u,
                               EntityCollisionFlags.None, OffX, OffY, LbId);

        reg.Deregister(doorEntityId);

        Assert.Equal(0, reg.TotalRegistered);
        foreach (var entry in reg.AllEntriesForDebug())
            Assert.NotEqual(doorEntityId, entry.EntityId);
    }

    [Fact]
    public void UpdatePhysicsState_PropagatesEtherealToAllParts()
    {
        var reg = new ShadowObjectRegistry();
        const uint doorEntityId = 0x000F4244u;
        reg.RegisterMultiPart(doorEntityId, new Vector3(132.6f, 17.1f, 94.08f),
                               Quaternion.Identity, DoorShapes(), 0x10008u,
                               EntityCollisionFlags.None, OffX, OffY, LbId);

        reg.UpdatePhysicsState(doorEntityId, 0x1000Cu);

        int updated = 0;
        foreach (var entry in reg.AllEntriesForDebug())
        {
            if (entry.EntityId != doorEntityId) continue;
            Assert.Equal(0x1000Cu, entry.State);
            updated++;
        }
        Assert.True(updated >= 4, $"Expected all parts updated, only {updated} were");
    }

    [Fact]
    public void RegisterMultiPart_PartsAcrossMultipleCells_AllCellsListed()
    {
        var reg = new ShadowObjectRegistry();
        var shapes = new[]
        {
            ShadowShape.Cylinder(0u, new Vector3(  0f, 0f, 0f), Quaternion.Identity, 1f, 1f, 2f),
            ShadowShape.Cylinder(0u, new Vector3(30f, 0f, 0f), Quaternion.Identity, 1f, 1f, 2f),
        };
        reg.RegisterMultiPart(0x1u, new Vector3(12f, 12f, 50f), Quaternion.Identity,
                               shapes, 0u, EntityCollisionFlags.None, OffX, OffY, LbId);

        var entriesIn1 = reg.GetObjectsInCell(LbId | 1u);
        var entriesIn9 = reg.GetObjectsInCell(LbId | 9u);
        Assert.Contains(entriesIn1, e => e.EntityId == 0x1u);
        Assert.Contains(entriesIn9, e => e.EntityId == 0x1u);
    }

    [Fact]
    public void Register_SingleShapeCompat_Unchanged()
    {
        var reg = new ShadowObjectRegistry();
        reg.Register(42u, 0x01000001u, new Vector3(12f, 12f, 50f),
                     Quaternion.Identity, 1f, OffX, OffY, LbId);

        Assert.Equal(1, reg.TotalRegistered);
        Assert.Single(reg.GetObjectsInCell(LbId | 1u),
                      e => e.EntityId == 42u);
    }

    [Fact]
    public void UpdatePosition_MovesAllPartsWithEntity()
    {
        var reg = new ShadowObjectRegistry();
        const uint movingEntityId = 0xA1u;

        var shapes = new[]
        {
            ShadowShape.Cylinder(0u, new Vector3(0f, 0f, 0f), Quaternion.Identity, 1f, 0.5f, 1f),
            ShadowShape.Cylinder(0u, new Vector3(1f, 0f, 0f), Quaternion.Identity, 1f, 0.5f, 1f),
        };
        reg.RegisterMultiPart(movingEntityId, new Vector3(10f, 10f, 50f),
                               Quaternion.Identity, shapes, 0u,
                               EntityCollisionFlags.None, OffX, OffY, LbId);

        // Move entity to (50, 10, 50). Parts should be at (50, 10, 50) and (51, 10, 50).
        reg.UpdatePosition(movingEntityId,
                           new Vector3(50f, 10f, 50f), Quaternion.Identity,
                           OffX, OffY, LbId);

        Vector3 expectedPart0 = new(50f, 10f, 50f);
        Vector3 expectedPart1 = new(51f, 10f, 50f);
        var atNew = reg.AllEntriesForDebug().Where(e => e.EntityId == movingEntityId).ToList();
        Assert.Equal(2, atNew.Count);
        bool found0 = atNew.Any(e => Vector3.Distance(e.Position, expectedPart0) < 0.01f);
        bool found1 = atNew.Any(e => Vector3.Distance(e.Position, expectedPart1) < 0.01f);
        Assert.True(found0 && found1,
            "Expected both parts at new world positions (50, 10, 50) and (51, 10, 50)");
    }

    [Fact]
    public void Deregister_ClearsEntityShapesCache_NoStaleUpdatePositionRebuild()
    {
        var reg = new ShadowObjectRegistry();
        const uint doorEntityId = 0x000F4244u;
        reg.RegisterMultiPart(doorEntityId, new Vector3(132.6f, 17.1f, 94.08f),
                               Quaternion.Identity, DoorShapes(), 0x10008u,
                               EntityCollisionFlags.None, OffX, OffY, LbId);

        reg.Deregister(doorEntityId);

        // Stray UpdatePosition should be a no-op now (no entry to find AND
        // no _entityShapes entry to rebuild from).
        reg.UpdatePosition(doorEntityId, new Vector3(200f, 200f, 50f),
                           Quaternion.Identity, OffX, OffY, LbId);

        Assert.Equal(0, reg.TotalRegistered);
    }


    private static List<uint> OutdoorCellsHolding(ShadowObjectRegistry reg, uint ownerId)
    {
        var cells = new List<uint>();
        for (uint index = 1u; index <= 64u; index++)
        {
            uint cellId = LbId | index;
            if (reg.GetObjectsInCell(cellId).Any(e => e.EntityId == ownerId))
                cells.Add(cellId);
        }
        return cells;
    }

    private static ShadowShape Cyl(float radius, Vector3 localPosition = default)
        => ShadowShape.Cylinder(
            gfxObjId: 0u,
            localPosition: localPosition,
            localRotation: Quaternion.Identity,
            scale: 1f,
            radius: radius,
            cylHeight: radius * 2f);

    private static ShadowShape Sph(float radius, Vector3 localPosition = default)
        => ShadowShape.Sphere(
            gfxObjId: 0u,
            localPosition: localPosition,
            localRotation: Quaternion.Identity,
            scale: 1f,
            radius: radius);

    private static ShadowShape Bsp(
        float radius,
        Vector3 boundsCenter = default,
        Vector3 localPosition = default,
        Quaternion localRotation = default)
        => ShadowShape.Bsp(
            gfxObjId: 0x010044B5u,
            localPosition: localPosition,
            localRotation: localRotation == default ? Quaternion.Identity : localRotation,
            scale: 1f,
            localGeometry: ShadowPartGeometry.Create(
                new FlatCollisionSphere(
                    boundsCenter == default ? new Vector3(0f, 6f, 0f) : boundsCenter,
                    radius),
                null));

    private static List<uint> FloodCellsFor(params ShadowShape[] shapes)
    {
        var reg = new ShadowObjectRegistry();
        const uint ownerId = 0xBEEF01u;
        reg.RegisterMultiPart(
            ownerId, new Vector3(36f, 36f, 50f), Quaternion.Identity,
            shapes, 0x10008u, EntityCollisionFlags.None, OffX, OffY, LbId);
        return OutdoorCellsHolding(reg, ownerId);
    }

    [Fact]
    public void BuildFloodSpheres_BspBearingOwner_FloodsFromBspNotFromCylinder()
    {
        List<uint> cylinderOnly = FloodCellsFor(Cyl(0.5f));
        List<uint> bspOnly      = FloodCellsFor(Bsp(14f));
        List<uint> mixed        = FloodCellsFor(Cyl(0.5f), Bsp(14f));


        Assert.Equal([LbId | 10u], cylinderOnly);
        Assert.True(bspOnly.Count > 1,
            $"BSP footprint control failed: expected >1 cell, got {bspOnly.Count}");

        Assert.Equal(bspOnly, mixed);
        Assert.NotEqual(cylinderOnly, mixed);
    }

    [Fact]
    public void BuildFloodSpheres_BspShape_CentresOnTheBoundsCentreNotThePartOrigin()
    {
        List<uint> concentric = FloodCellsFor(
            Bsp(6f, boundsCenter: new Vector3(0.001f, 0f, 0f)));
        List<uint> viaBoundsCentre = FloodCellsFor(
            Bsp(6f, boundsCenter: new Vector3(0f, 30f, 0f)));
        List<uint> viaPartOrigin = FloodCellsFor(
            Bsp(6f,
                boundsCenter: new Vector3(0.001f, 0f, 0f),
                localPosition: new Vector3(0f, 30f, 0f)));

        Assert.NotEqual(concentric, viaBoundsCentre);

        Assert.Equal(viaPartOrigin, viaBoundsCentre);
    }

    [Fact]
    public void BuildFloodSpheres_CapsCylSpheresAtTenButNeverTheBspParts()
    {
        var near = new Vector3(0f, 0f, 0f);
        var far = new Vector3(0f, 72f, 0f);
        uint ownCell = LbId | (uint)(1 * 8 + 1 + 1);   // (x=1, y=1)
        uint farCell = LbId | (uint)(1 * 8 + 4 + 1);   // (x=1, y=4)

        var bsp = new ShadowShape[11];
        for (int i = 0; i < 10; i++)
            bsp[i] = Bsp(1f, boundsCenter: new Vector3(0.001f, 0f, 0f));
        bsp[10] = Bsp(1f, boundsCenter: far);
        List<uint> bspCells = FloodCellsFor(bsp);

        var cyls = new ShadowShape[11];
        for (int i = 0; i < 10; i++)
            cyls[i] = Cyl(1f, near);
        cyls[10] = Cyl(1f, far);
        List<uint> cylCells = FloodCellsFor(cyls);

        Assert.Contains(ownCell, bspCells);
        Assert.Contains(ownCell, cylCells);

        Assert.Contains(farCell, bspCells);
        Assert.DoesNotContain(farCell, cylCells);
    }

    [Fact]
    public void BuildFloodSpheres_SphereBranchIsNeverCappedAtTen()
    {
        var far = new Vector3(0f, 72f, 0f);
        uint ownCell = LbId | (uint)(1 * 8 + 1 + 1);   // (x=1, y=1)
        uint farCell = LbId | (uint)(1 * 8 + 4 + 1);   // (x=1, y=4)

        var spheres = new ShadowShape[11];
        for (int i = 0; i < 10; i++)
            spheres[i] = Sph(1f);
        spheres[10] = Sph(1f, far);
        List<uint> sphereCells = FloodCellsFor(spheres);

        Assert.Contains(ownCell, sphereCells);
        Assert.Contains(farCell, sphereCells);
    }

    [Fact]
    public void BuildFloodSpheres_BspShape_RotatesTheBoundsCentreByThePartRotation()
    {
        Quaternion yaw180 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI);

        List<uint> yawed = FloodCellsFor(
            Bsp(6f, boundsCenter: new Vector3(0f, 20f, 0f), localRotation: yaw180));
        List<uint> negatedUnrotated = FloodCellsFor(
            Bsp(6f, boundsCenter: new Vector3(0f, -20f, 0f)));
        List<uint> unrotated = FloodCellsFor(
            Bsp(6f, boundsCenter: new Vector3(0f, 20f, 0f)));

        Assert.NotEqual(unrotated, negatedUnrotated);
        Assert.Equal(negatedUnrotated, yawed);
    }

    [Fact]
    public void FromSetup_CylSphereAndBspSetup_FloodsTheBspFootprint()
    {
        const uint part = 0x010044B5u;
        var setup = new DatReaderWriter.DBObjs.Setup
        {
            Parts      = { part },
            CylSpheres = { new DatReaderWriter.Types.CylSphere
                { Radius = 0.5f, Height = 1f, Origin = Vector3.Zero } },
        };

        // 14 m stands in for a slab wide enough to leave its own landcell;
        // the +18 m Y offset stands in for the 376-of-973 installed parts
        // whose root sphere is nowhere near the part origin.
        var bounds = new FlatCollisionSphere(new Vector3(0f, 18f, 0f), 14f);
        IReadOnlyList<ShadowShape> shapes = ShadowShapeBuilder.FromSetup(
            setup,
            entScale: 1f,
            hasPhysicsBsp: id => id == part,
            physicsBspBounds: id => id == part
                ? ShadowPartGeometry.Create(bounds, null)
                : (ShadowPartGeometry?)null);

        ShadowShape only = Assert.Single(shapes);
        Assert.Equal(ShadowCollisionType.BSP, only.CollisionType);
        Assert.Equal(14f, only.Radius);
        Assert.Equal(new Vector3(0f, 18f, 0f), only.BoundsCenter);

        var reg = new ShadowObjectRegistry();
        const uint ownerId = 0xBEEF02u;
        reg.RegisterMultiPart(
            ownerId, new Vector3(36f, 36f, 50f), Quaternion.Identity,
            shapes, 0x10008u, EntityCollisionFlags.None, OffX, OffY, LbId);

        List<uint> cells = OutdoorCellsHolding(reg, ownerId);
        Assert.True(cells.Count > 1,
            $"Expected the slab footprint to span more than its own landcell; got {cells.Count}");
        Assert.Contains(LbId | (uint)(1 * 8 + 2 + 1), cells);        // (x=1, y=2)
        Assert.DoesNotContain(LbId | (uint)(1 * 8 + 0 + 1), cells);  // (x=1, y=0)
    }
}
