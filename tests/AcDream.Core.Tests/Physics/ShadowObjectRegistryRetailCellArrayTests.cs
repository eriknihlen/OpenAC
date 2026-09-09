using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class ShadowObjectRegistryRetailCellArrayTests
{
    private const uint LbId = 0xA9B40000u;
    private const float OffX = 0f;
    private const float OffY = 0f;

    private const uint CellA = LbId | 1u;
    private const uint CellB = LbId | 9u;

    private static readonly Vector3 Pos = new(12f, 12f, 50f);

    private static ShadowShape Bsp(uint gfxObjId, Vector3 localPosition = default, float radius = 1f)
        => ShadowShape.Bsp(
            gfxObjId,
            localPosition,
            Quaternion.Identity,
            scale: 1f,
            localGeometry: ShadowPartGeometry.Create(
                new FlatCollisionSphere(Vector3.Zero, radius), null));

    private static ShadowShape Cyl(uint gfxObjId, Vector3 localPosition = default, float radius = 1f)
        => ShadowShape.Cylinder(
            gfxObjId, localPosition, Quaternion.Identity, scale: 1f,
            radius: radius, cylHeight: 1f);


    [Fact]
    public void RegisterMultiPart_WithoutPartArray_NoRetailProduct()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 0x10u;
        var shapes = new[] { Bsp(0x0100_0001u) };

        reg.RegisterMultiPart(entityId, Pos, Quaternion.Identity, shapes,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId);

        Assert.False(reg.TryGetRetailCellArray(entityId, out var cells));
        Assert.Empty(cells);
        Assert.Equal(RetailCellArrayRoute.None, reg.GetRetailCellArrayRoute(entityId));
        foreach (uint cellId in reg.GetOwnerCells(entityId))
            Assert.Empty(reg.GetRetailPartEntriesInCell(cellId));
    }

    [Fact]
    public void Register_SingleShapeWithoutPartArray_NoRetailProduct()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 0x11u;

        reg.Register(entityId, 0x0100_0002u, Pos, Quaternion.Identity, 1f, OffX, OffY, LbId);

        Assert.False(reg.TryGetRetailCellArray(entityId, out var cells));
        Assert.Empty(cells);
        Assert.Equal(RetailCellArrayRoute.None, reg.GetRetailCellArrayRoute(entityId));
    }


    [Fact]
    public void RegisterMultiPart_WithPartArray_BoundingBoxRouteMatchesCollisionFloodForTheSamePartArray()
    {
        var withPartArray = new ShadowObjectRegistry();
        var withoutPartArray = new ShadowObjectRegistry();
        const uint entityId = 0x12u;
        IReadOnlyList<ShadowShape> parts = new[] { Bsp(0x0100_0044u, radius: 2f) };

        withPartArray.RegisterMultiPart(entityId, Pos, Quaternion.Identity, parts,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            seedCellId: CellA, isStatic: true, partArray: parts);
        withoutPartArray.RegisterMultiPart(entityId, Pos, Quaternion.Identity, parts,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            seedCellId: CellA, isStatic: true);

        Assert.Equal(RetailCellArrayRoute.BoundingBox, withPartArray.GetRetailCellArrayRoute(entityId));
        Assert.True(withPartArray.TryGetRetailCellArray(entityId, out var retailCells));
        Assert.NotEmpty(retailCells); // the fixture must actually exercise a flood

        Assert.Equal(withPartArray.GetOwnerCells(entityId), retailCells);

        Assert.Equal(withoutPartArray.GetOwnerCells(entityId), withPartArray.GetOwnerCells(entityId));
    }


    [Fact]
    public void RegisterMultiPart_PartArrayRoute_DispatchesOnStateBitAndCylinderPresence()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 0x13u;
        IReadOnlyList<ShadowShape> mixed = new[]
        {
            Cyl(0x3001u),
            Bsp(0x3002u, new Vector3(30f, 0f, 0f)),
        };

        reg.RegisterMultiPart(entityId, Pos, Quaternion.Identity, mixed,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId, partArray: mixed);

        Assert.Equal(RetailCellArrayRoute.Cylsphere, reg.GetRetailCellArrayRoute(entityId));
        Assert.True(reg.TryGetRetailCellArray(entityId, out var cylCells));
        Assert.Equal(new[] { CellA }, cylCells);

        var cylEntries = reg.GetRetailPartEntriesInCell(CellA)
            .Where(e => e.EntityId == entityId)
            .OrderBy(e => e.PartIndex)
            .ToList();
        Assert.Equal(2, cylEntries.Count);
        Assert.Equal(0, cylEntries[0].PartIndex);
        Assert.Equal(0x3001u, cylEntries[0].GfxObjId);
        Assert.Equal(1, cylEntries[1].PartIndex);
        Assert.Equal(0x3002u, cylEntries[1].GfxObjId);
        Assert.False(cylEntries[0].ClipPlanesRequired);
        Assert.False(cylEntries[1].ClipPlanesRequired);

        reg.RegisterMultiPart(entityId, Pos, Quaternion.Identity, mixed,
            state: 0x10000u, flags: EntityCollisionFlags.None, OffX, OffY, LbId, partArray: mixed);

        Assert.Equal(RetailCellArrayRoute.BoundingBox, reg.GetRetailCellArrayRoute(entityId));
        Assert.True(reg.TryGetRetailCellArray(entityId, out var bboxCells));
        Assert.Equal(new[] { CellA, CellB }, bboxCells);

        foreach (uint cellId in bboxCells)
        {
            var entries = reg.GetRetailPartEntriesInCell(cellId)
                .Where(e => e.EntityId == entityId)
                .OrderBy(e => e.PartIndex)
                .ToList();
            Assert.Equal(2, entries.Count);
            Assert.Equal(0, entries[0].PartIndex);
            Assert.Equal(1, entries[1].PartIndex);
            Assert.True(entries[0].ClipPlanesRequired);
            Assert.True(entries[1].ClipPlanesRequired);
        }
    }

    [Fact]
    public void RegisterMultiPart_RouteIsDecidedByCollisionCylspheres_NotByTheVisualPartArray()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 0x14u;
        IReadOnlyList<ShadowShape> collision = new[] { Cyl(0x3001u) };
        IReadOnlyList<ShadowShape> visualParts = new[]
        {
            Bsp(0x3002u, new Vector3(30f, 0f, 0f)),
        };

        reg.RegisterMultiPart(entityId, Pos, Quaternion.Identity, collision,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId, partArray: visualParts);

        Assert.Equal(RetailCellArrayRoute.Cylsphere, reg.GetRetailCellArrayRoute(entityId));
        Assert.True(reg.TryGetRetailCellArray(entityId, out var cells));
        Assert.Equal(new[] { CellA }, cells);
        // ...but the entries are the VISUAL parts (AddPartsShadow walks the part array).
        var entries = reg.GetRetailPartEntriesInCell(CellA).Where(e => e.EntityId == entityId).ToList();
        Assert.Single(entries);
        Assert.Equal(0x3002u, entries[0].GfxObjId);

        IReadOnlyList<ShadowShape> bspCollision = new[] { Bsp(0x3001u, Vector3.Zero) };
        reg.RegisterMultiPart(entityId, Pos, Quaternion.Identity, bspCollision,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId, partArray: visualParts);
        Assert.Equal(RetailCellArrayRoute.BoundingBox, reg.GetRetailCellArrayRoute(entityId));
    }

    // -------------------------------------------------------------------
    // Deregister is the exact inverse transaction.
    // -------------------------------------------------------------------

    [Fact]
    public void Deregister_ClearsRetailCellArrayAndEveryPerCellEntry()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 0x14u;
        IReadOnlyList<ShadowShape> parts = new[]
        {
            Cyl(0x4001u),
            Bsp(0x4002u, new Vector3(30f, 0f, 0f)),
        };

        reg.RegisterMultiPart(entityId, Pos, Quaternion.Identity, parts,
            state: 0x10000u, flags: EntityCollisionFlags.None, OffX, OffY, LbId, partArray: parts);
        Assert.True(reg.TryGetRetailCellArray(entityId, out var cellsBeforeRemoval));
        Assert.NotEmpty(cellsBeforeRemoval);
        var trackedCells = cellsBeforeRemoval.ToArray();

        reg.Deregister(entityId);

        Assert.False(reg.TryGetRetailCellArray(entityId, out var cellsAfterRemoval));
        Assert.Empty(cellsAfterRemoval);
        Assert.Equal(RetailCellArrayRoute.None, reg.GetRetailCellArrayRoute(entityId));
        foreach (uint cellId in trackedCells)
            Assert.Empty(reg.GetRetailPartEntriesInCell(cellId));
    }


    [Fact]
    public void UpdatePosition_WithRetainedPartArray_RecomputesRetailCellArray()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 0x15u;
        IReadOnlyList<ShadowShape> parts = new[] { Cyl(0x5001u) };

        reg.RegisterMultiPart(entityId, Pos, Quaternion.Identity, parts,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId, partArray: parts);
        Assert.True(reg.TryGetRetailCellArray(entityId, out var before));
        Assert.Equal(new[] { CellA }, before);

        var moved = new Vector3(42f, 12f, 50f);
        reg.UpdatePosition(entityId, moved, Quaternion.Identity, OffX, OffY, LbId);

        Assert.True(reg.TryGetRetailCellArray(entityId, out var after));
        Assert.Equal(new[] { CellB }, after);
        var movedEntries = reg.GetRetailPartEntriesInCell(CellB)
            .Where(e => e.EntityId == entityId).ToList();
        Assert.Single(movedEntries);
        Assert.Equal(0x5001u, movedEntries[0].GfxObjId);
        Assert.DoesNotContain(
            reg.GetRetailPartEntriesInCell(CellA),
            e => e.EntityId == entityId);
    }


    [Fact]
    public void CommitSetPosition_NoneAction_PublishesRetailProductFromTheRetainedCells()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 0x16u;
        IReadOnlyList<ShadowShape> parts = new[] { Cyl(0x6001u) };

        reg.RegisterMultiPart(entityId, Pos, Quaternion.Identity, parts,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            isStatic: false, partArray: parts);
        Assert.True(reg.TryGetRetailCellArray(entityId, out var before));
        Assert.Equal(new[] { CellA }, before);

        var moved = new Vector3(42f, 12f, 50f);
        reg.CommitSetPosition(
            entityId,
            moved,
            Quaternion.Identity,
            seedCellId: CellB,
            worldOffsetX: OffX,
            worldOffsetY: OffY,
            action: PhysicsShadowCommitAction.None,
            crossCellIds: ImmutableArray<uint>.Empty);

        Assert.Equal(new[] { CellA }, reg.GetOwnerCells(entityId));
        Assert.True(reg.TryGetRetailCellArray(entityId, out var after));
        Assert.Equal(reg.GetOwnerCells(entityId), after);
    }

    [Fact]
    public void CommitSetPosition_ReplaceAction_PublishesRetailProductFromTheTransitionCellArray()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 0x18u;
        IReadOnlyList<ShadowShape> parts = new[] { Cyl(0x6101u) };

        reg.RegisterMultiPart(entityId, Pos, Quaternion.Identity, parts,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            isStatic: false, partArray: parts);
        Assert.True(reg.TryGetRetailCellArray(entityId, out var before));
        Assert.Equal(new[] { CellA }, before);

        var moved = new Vector3(42f, 12f, 50f);
        reg.CommitSetPosition(
            entityId,
            moved,
            Quaternion.Identity,
            seedCellId: CellB,
            worldOffsetX: OffX,
            worldOffsetY: OffY,
            action: PhysicsShadowCommitAction.Replace,
            crossCellIds: [CellB]);

        Assert.Equal(new[] { CellB }, reg.GetOwnerCells(entityId));
        Assert.True(reg.TryGetRetailCellArray(entityId, out var after));
        Assert.Equal(reg.GetOwnerCells(entityId), after);
    }

    [Fact]
    public void CommitSetPosition_KeepWhenEmpty_LeavesBothProductsUntouched()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 0x19u;
        IReadOnlyList<ShadowShape> parts = new[] { Cyl(0x6201u) };

        reg.RegisterMultiPart(entityId, Pos, Quaternion.Identity, parts,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            isStatic: false, partArray: parts);
        Assert.True(reg.TryGetRetailCellArray(entityId, out var before));
        Assert.Equal(new[] { CellA }, before);

        reg.CommitSetPosition(
            entityId,
            Pos,
            Quaternion.Identity,
            seedCellId: CellA,
            worldOffsetX: OffX,
            worldOffsetY: OffY,
            action: PhysicsShadowCommitAction.Preserve,
            crossCellIds: ImmutableArray<uint>.Empty);

        Assert.Equal(new[] { CellA }, reg.GetOwnerCells(entityId));
        Assert.True(reg.TryGetRetailCellArray(entityId, out var after));
        Assert.Equal(new[] { CellA }, after);
    }


    [Fact]
    public void ReplaceMultiPartPayload_SwapsPartArrayWithoutReflooding()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 0x17u;
        IReadOnlyList<ShadowShape> initial = new[] { Cyl(0x7001u) };

        reg.RegisterMultiPart(entityId, Pos, Quaternion.Identity, initial,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId, partArray: initial);
        Assert.True(reg.TryGetRetailCellArray(entityId, out var before));
        Assert.Equal(new[] { CellA }, before);
        Assert.Equal(RetailCellArrayRoute.Cylsphere, reg.GetRetailCellArrayRoute(entityId));

        IReadOnlyList<ShadowShape> replacement = new[] { Cyl(0x7002u), Cyl(0x7003u) };
        reg.ReplaceMultiPartPayload(entityId, Pos, Quaternion.Identity, replacement,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId, partArray: replacement);

        Assert.True(reg.TryGetRetailCellArray(entityId, out var after));
        Assert.Equal(before, after);
        // The route stays whatever the LAST recompute (at registration)
        // decided; ReplaceMultiPartPayload does not re-run the dispatch.
        Assert.Equal(RetailCellArrayRoute.Cylsphere, reg.GetRetailCellArrayRoute(entityId));

        var entries = reg.GetRetailPartEntriesInCell(CellA)
            .Where(e => e.EntityId == entityId)
            .OrderBy(e => e.PartIndex)
            .ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal(0x7002u, entries[0].GfxObjId);
        Assert.Equal(0x7003u, entries[1].GfxObjId);
        Assert.DoesNotContain(entries, e => e.GfxObjId == 0x7001u);
    }


    [Fact]
    public void RegisterMultiPart_DecorativePartArrayEntry_WidensCollisionRowsToo()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 0x20u;
        const uint collidingGfxObj = 0xAAAA0001u;
        const uint decorativeGfxObj = 0xBBBB0001u;

        IReadOnlyList<ShadowShape> collisionShapes = new[] { Bsp(collidingGfxObj) };
        IReadOnlyList<ShadowShape> partArray = new[]
        {
            Bsp(collidingGfxObj),
            Bsp(decorativeGfxObj, new Vector3(30f, 0f, 0f)),
        };

        reg.RegisterMultiPart(entityId, Pos, Quaternion.Identity, collisionShapes,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            partArray: partArray);

        Assert.True(reg.TryGetRetailCellArray(entityId, out var retailCells));
        Assert.Equal(new[] { CellA, CellB }, retailCells);
        Assert.Equal(RetailCellArrayRoute.BoundingBox, reg.GetRetailCellArrayRoute(entityId));

        Assert.Equal(retailCells, reg.GetOwnerCells(entityId));

        foreach (uint cellId in new[] { CellA, CellB })
        {
            var collisionRows = reg.GetObjectsInCell(cellId)
                .Where(e => e.EntityId == entityId)
                .ToList();
            Assert.Single(collisionRows);
            Assert.Equal(collidingGfxObj, collisionRows[0].GfxObjId);

            Assert.DoesNotContain(collisionRows, e => e.GfxObjId == decorativeGfxObj);

            var partEntries = reg.GetRetailPartEntriesInCell(cellId)
                .Where(e => e.EntityId == entityId)
                .Select(e => e.GfxObjId)
                .ToList();
            Assert.Contains(collidingGfxObj, partEntries);
            Assert.Contains(decorativeGfxObj, partEntries);
        }
    }

    [Fact]
    public void RegisterMultiPart_WithoutDecorativeParts_RetailArrayStillEqualsCollisionCells()
    {
        var withPartArray = new ShadowObjectRegistry();
        var withoutPartArray = new ShadowObjectRegistry();
        const uint entityId = 0x21u;
        IReadOnlyList<ShadowShape> shapes = new[] { Bsp(0x0100_0055u, radius: 2f) };

        withPartArray.RegisterMultiPart(entityId, Pos, Quaternion.Identity, shapes,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            seedCellId: CellA, isStatic: true, partArray: shapes);
        withoutPartArray.RegisterMultiPart(entityId, Pos, Quaternion.Identity, shapes,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            seedCellId: CellA, isStatic: true);

        Assert.Equal(
            withoutPartArray.GetOwnerCells(entityId),
            withPartArray.GetOwnerCells(entityId));
        Assert.True(withPartArray.TryGetRetailCellArray(entityId, out var retailCells));
        Assert.Equal(withPartArray.GetOwnerCells(entityId), retailCells);
    }


    [Fact]
    public void StagedSetPosition_WithRetainedPartArray_MovesBothProductsTogether()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 0x30u;
        IReadOnlyList<ShadowShape> parts = new[] { Cyl(0x8001u) };

        reg.RegisterMultiPart(entityId, Pos, Quaternion.Identity, parts,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId, partArray: parts);
        Assert.True(reg.TryGetRetailCellArray(entityId, out var before));
        Assert.Equal(new[] { CellA }, before);
        Assert.Equal(new[] { CellA }, reg.GetOwnerCells(entityId));

        var moved = new Vector3(42f, 12f, 50f);
        Assert.True(reg.TryPrepareSetPosition(
            entityId,
            moved,
            Quaternion.Identity,
            seedCellId: CellB,
            worldOffsetX: OffX,
            worldOffsetY: OffY,
            PhysicsShadowCommitAction.Replace,
            crossCellIds: [CellB],
            provenShapeless: false,
            suspendOwner: false,
            out var prepared));

        // Not yet applied — both products still describe the OLD position.
        Assert.Equal(new[] { CellA }, reg.GetOwnerCells(entityId));
        Assert.True(reg.TryGetRetailCellArray(entityId, out var stillBefore));
        Assert.Equal(new[] { CellA }, stillBefore);

        Assert.True(reg.TryApplySetPosition(prepared!, out var receipt));
        Assert.True(receipt.Mutated);

        Assert.Equal(new[] { CellB }, reg.GetOwnerCells(entityId));
        Assert.True(reg.TryGetRetailCellArray(entityId, out var after));
        Assert.Equal(new[] { CellB }, after);
        Assert.Equal(RetailCellArrayRoute.Cylsphere, reg.GetRetailCellArrayRoute(entityId));

        var partEntries = reg.GetRetailPartEntriesInCell(CellB)
            .Where(e => e.EntityId == entityId).ToList();
        Assert.Single(partEntries);
        Assert.Equal(0x8001u, partEntries[0].GfxObjId);
        Assert.DoesNotContain(
            reg.GetRetailPartEntriesInCell(CellA), e => e.EntityId == entityId);

        var collisionRows = reg.GetObjectsInCell(CellB)
            .Where(e => e.EntityId == entityId).ToList();
        Assert.Single(collisionRows);
        Assert.Equal(0x8001u, collisionRows[0].GfxObjId);
        Assert.DoesNotContain(
            reg.GetObjectsInCell(CellA), e => e.EntityId == entityId);
    }

    [Fact]
    public void StagedSetPosition_KeepWhenEmpty_PreservesBothProductsTogether()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 0x31u;
        IReadOnlyList<ShadowShape> parts = new[] { Cyl(0x8101u) };

        reg.RegisterMultiPart(entityId, Pos, Quaternion.Identity, parts,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId, partArray: parts);
        Assert.True(reg.TryGetRetailCellArray(entityId, out var before));
        Assert.Equal(new[] { CellA }, before);
        Assert.Equal(new[] { CellA }, reg.GetOwnerCells(entityId));

        Assert.True(reg.TryPrepareSetPosition(
            entityId,
            new Vector3(999f, 999f, 50f),
            Quaternion.Identity,
            seedCellId: 0u,
            worldOffsetX: OffX,
            worldOffsetY: OffY,
            PhysicsShadowCommitAction.Recalculate,
            crossCellIds: ImmutableArray<uint>.Empty,
            provenShapeless: false,
            suspendOwner: false,
            out var prepared));
        Assert.True(reg.TryApplySetPosition(prepared!, out _));

        Assert.Equal(new[] { CellA }, reg.GetOwnerCells(entityId));
        Assert.True(reg.TryGetRetailCellArray(entityId, out var after));
        Assert.Equal(new[] { CellA }, after);
        Assert.Equal(RetailCellArrayRoute.Cylsphere, reg.GetRetailCellArrayRoute(entityId));

        var partEntries = reg.GetRetailPartEntriesInCell(CellA)
            .Where(e => e.EntityId == entityId).ToList();
        Assert.Single(partEntries);
        Assert.Equal(0x8101u, partEntries[0].GfxObjId);
    }


    private static IReadOnlyList<ShadowShape> ChildParts(params uint[] gfxObjIds)
    {
        var shapes = new ShadowShape[gfxObjIds.Length];
        for (int i = 0; i < gfxObjIds.Length; i++)
            shapes[i] = Bsp(gfxObjIds[i]);
        return shapes;
    }

    [Fact]
    public void AttachChild_GivesChildTheRootsArrayAndEntries()
    {
        var reg = new ShadowObjectRegistry();
        const uint rootId = 0x40u;
        const uint childId = 0x41u;
        IReadOnlyList<ShadowShape> rootParts = new[] { Cyl(0x9001u) };

        reg.RegisterMultiPart(rootId, Pos, Quaternion.Identity, rootParts,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            partArray: rootParts);
        Assert.True(reg.TryGetRetailCellArray(rootId, out var rootCells));
        Assert.Equal(new[] { CellA }, rootCells);

        IReadOnlyList<ShadowShape> childParts = ChildParts(0xC001u, 0xC002u);
        Assert.True(reg.AttachChild(childId, rootId, childParts));

        Assert.True(reg.TryGetRetailCellArray(childId, out var childCells));
        Assert.Equal(rootCells, childCells);

        var entries = reg.GetRetailPartEntriesInCell(CellA)
            .Where(e => e.EntityId == childId)
            .OrderBy(e => e.PartIndex)
            .ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal(0xC001u, entries[0].GfxObjId);
        Assert.Equal(0xC002u, entries[1].GfxObjId);

        Assert.Empty(reg.GetOwnerCells(childId));
        Assert.DoesNotContain(
            reg.GetObjectsInCell(CellA),
            e => e.EntityId == childId);
    }

    [Fact]
    public void AttachChild_RootMove_RepublishesTheChild()
    {
        var reg = new ShadowObjectRegistry();
        const uint rootId = 0x42u;
        const uint childId = 0x43u;
        IReadOnlyList<ShadowShape> rootParts = new[] { Cyl(0x9101u) };

        reg.RegisterMultiPart(rootId, Pos, Quaternion.Identity, rootParts,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            isStatic: false, partArray: rootParts);
        Assert.True(reg.AttachChild(childId, rootId, ChildParts(0xC101u)));
        Assert.True(reg.TryGetRetailCellArray(childId, out var before));
        Assert.Equal(new[] { CellA }, before);

        var moved = new Vector3(42f, 12f, 50f);
        reg.CommitSetPosition(
            rootId,
            moved,
            Quaternion.Identity,
            seedCellId: CellB,
            worldOffsetX: OffX,
            worldOffsetY: OffY,
            action: PhysicsShadowCommitAction.Replace,
            crossCellIds: [CellB]);

        Assert.True(reg.TryGetRetailCellArray(rootId, out var rootAfter));
        Assert.Equal(new[] { CellB }, rootAfter);
        Assert.True(reg.TryGetRetailCellArray(childId, out var childAfter));
        Assert.Equal(rootAfter, childAfter);

        Assert.DoesNotContain(
            reg.GetRetailPartEntriesInCell(CellA),
            e => e.EntityId == childId);
        var movedEntries = reg.GetRetailPartEntriesInCell(CellB)
            .Where(e => e.EntityId == childId).ToList();
        Assert.Single(movedEntries);
        Assert.Equal(0xC101u, movedEntries[0].GfxObjId);
    }

    [Fact]
    public void DetachChild_ClearsTheChildsProducts()
    {
        var reg = new ShadowObjectRegistry();
        const uint rootId = 0x44u;
        const uint childId = 0x45u;
        IReadOnlyList<ShadowShape> rootParts = new[] { Cyl(0x9201u) };

        reg.RegisterMultiPart(rootId, Pos, Quaternion.Identity, rootParts,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            partArray: rootParts);
        Assert.True(reg.AttachChild(childId, rootId, ChildParts(0xC201u)));
        Assert.True(reg.TryGetRetailCellArray(childId, out _));

        Assert.True(reg.DetachChild(childId));

        Assert.False(reg.TryGetRetailCellArray(childId, out var afterCells));
        Assert.Empty(afterCells);
        Assert.DoesNotContain(
            reg.GetRetailPartEntriesInCell(CellA),
            e => e.EntityId == childId);
        // The root itself is untouched.
        Assert.True(reg.TryGetRetailCellArray(rootId, out var rootCells));
        Assert.Equal(new[] { CellA }, rootCells);

        // A second detach is a no-op, not an error.
        Assert.False(reg.DetachChild(childId));
    }

    [Fact]
    public void Deregister_RootCascadesDetachOfAttachedChildren()
    {
        var reg = new ShadowObjectRegistry();
        const uint rootId = 0x46u;
        const uint childId = 0x47u;
        IReadOnlyList<ShadowShape> rootParts = new[] { Cyl(0x9301u) };

        reg.RegisterMultiPart(rootId, Pos, Quaternion.Identity, rootParts,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            partArray: rootParts);
        Assert.True(reg.AttachChild(childId, rootId, ChildParts(0xC301u)));

        reg.Deregister(rootId);

        Assert.False(reg.TryGetRetailCellArray(rootId, out _));
        Assert.False(reg.TryGetRetailCellArray(childId, out var childCells));
        Assert.Empty(childCells);
        Assert.DoesNotContain(
            reg.GetRetailPartEntriesInCell(CellA),
            e => e.EntityId == childId);
    }

    [Fact]
    public void AttachChild_NestedChildOfAChild_ResolvesToTheRoot()
    {
        var reg = new ShadowObjectRegistry();
        const uint rootId = 0x48u;
        const uint directChildId = 0x49u;
        const uint grandchildId = 0x4Au;
        IReadOnlyList<ShadowShape> rootParts = new[] { Cyl(0x9401u) };

        reg.RegisterMultiPart(rootId, Pos, Quaternion.Identity, rootParts,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            partArray: rootParts);
        Assert.True(reg.AttachChild(directChildId, rootId, ChildParts(0xC401u)));
        Assert.True(reg.AttachChild(grandchildId, directChildId, ChildParts(0xC402u)));

        Assert.True(reg.TryGetRetailCellArray(rootId, out var rootCells));
        Assert.True(reg.TryGetRetailCellArray(grandchildId, out var grandchildCells));
        Assert.Equal(rootCells, grandchildCells);

        var entries = reg.GetRetailPartEntriesInCell(CellA)
            .Where(e => e.EntityId == grandchildId).ToList();
        Assert.Single(entries);
        Assert.Equal(0xC402u, entries[0].GfxObjId);

        var moved = new Vector3(42f, 12f, 50f);
        reg.CommitSetPosition(
            rootId, moved, Quaternion.Identity, seedCellId: CellB,
            worldOffsetX: OffX, worldOffsetY: OffY,
            action: PhysicsShadowCommitAction.Replace, crossCellIds: [CellB]);

        Assert.True(reg.TryGetRetailCellArray(grandchildId, out var grandchildAfter));
        Assert.Equal(new[] { CellB }, grandchildAfter);

        Assert.True(reg.DetachChild(directChildId));
        Assert.False(reg.TryGetRetailCellArray(grandchildId, out _));
    }

    [Fact]
    public void AttachChild_SelfAttachRejected()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 0x4Bu;
        IReadOnlyList<ShadowShape> rootParts = new[] { Cyl(0x9501u) };

        reg.RegisterMultiPart(entityId, Pos, Quaternion.Identity, rootParts,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            partArray: rootParts);

        Assert.False(reg.AttachChild(entityId, entityId, ChildParts(0xC501u)));
    }

    [Fact]
    public void AttachChild_CycleRejected_RootUnaffected()
    {
        var reg = new ShadowObjectRegistry();
        const uint rootId = 0x4Cu;
        const uint childId = 0x4Du;
        IReadOnlyList<ShadowShape> rootParts = new[] { Cyl(0x9601u) };

        reg.RegisterMultiPart(rootId, Pos, Quaternion.Identity, rootParts,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            partArray: rootParts);
        Assert.True(reg.AttachChild(childId, rootId, ChildParts(0xC601u)));
        Assert.True(reg.TryGetRetailCellArray(rootId, out var rootCellsBefore));

        Assert.False(reg.AttachChild(rootId, childId, ChildParts(0xC602u)));

        Assert.True(reg.TryGetRetailCellArray(rootId, out var rootCellsAfter));
        Assert.Equal(rootCellsBefore, rootCellsAfter);
        Assert.Equal(new[] { CellA }, reg.GetOwnerCells(rootId));
        Assert.True(reg.TryGetRetailCellArray(childId, out var childCells));
        Assert.Equal(rootCellsAfter, childCells);
    }


    [Fact]
    public void RegisterMultiPart_EmptyShapesWithPartArray_RegistersRenderOnlyNoCollisionRows()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 0x50u;
        IReadOnlyList<ShadowShape> partArray = new[] { Bsp(0x0100_0060u, radius: 2f) };

        reg.RegisterMultiPart(entityId, Pos, Quaternion.Identity, Array.Empty<ShadowShape>(),
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            seedCellId: CellA, isStatic: true, partArray: partArray);

        Assert.True(reg.TryGetRetailCellArray(entityId, out var cells));
        Assert.Equal(new[] { CellA }, cells);
        Assert.Equal(RetailCellArrayRoute.BoundingBox, reg.GetRetailCellArrayRoute(entityId));

        var entries = reg.GetRetailPartEntriesInCell(CellA)
            .Where(e => e.EntityId == entityId).ToList();
        Assert.Single(entries);
        Assert.Equal(0x0100_0060u, entries[0].GfxObjId);

        Assert.Empty(reg.GetOwnerCells(entityId));
    }

    [Fact]
    public void RegisterMultiPart_EmptyShapesAndNoPartArray_StillDeregisters()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 0x51u;
        IReadOnlyList<ShadowShape> parts = new[] { Bsp(0x0100_0061u) };
        reg.RegisterMultiPart(entityId, Pos, Quaternion.Identity, parts,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            seedCellId: CellA, isStatic: true, partArray: parts);
        Assert.True(reg.TryGetRetailCellArray(entityId, out _));

        reg.RegisterMultiPart(entityId, Pos, Quaternion.Identity, Array.Empty<ShadowShape>(),
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            seedCellId: CellA, isStatic: true);

        Assert.False(reg.TryGetRetailCellArray(entityId, out var cells));
        Assert.Empty(cells);
        Assert.Empty(reg.GetOwnerCells(entityId));
        Assert.Empty(reg.GetRetailPartEntriesInCell(CellA));
    }

    [Fact]
    public void RegisterMultiPart_RenderOnly_KeepWhenEmptyPreservesThePriorRegistration()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 0x52u;
        IReadOnlyList<ShadowShape> partArray = new[] { Bsp(0x0100_0062u, radius: 2f) };
        reg.RegisterMultiPart(entityId, Pos, Quaternion.Identity, Array.Empty<ShadowShape>(),
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            seedCellId: CellA, isStatic: true, partArray: partArray);
        Assert.True(reg.TryGetRetailCellArray(entityId, out var before));
        Assert.Equal(new[] { CellA }, before);

        reg.RegisterMultiPart(entityId, Pos, Quaternion.Identity, Array.Empty<ShadowShape>(),
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, landblockId: 0u,
            isStatic: true, partArray: partArray);

        Assert.True(reg.TryGetRetailCellArray(entityId, out var after));
        Assert.Equal(before, after);
        Assert.NotEmpty(reg.GetRetailPartEntriesInCell(CellA));
    }

    [Fact]
    public void RegisterMultiPart_RenderOnly_DeregisterClearsEveryProduct()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 0x53u;
        IReadOnlyList<ShadowShape> partArray = new[] { Bsp(0x0100_0063u, radius: 2f) };
        reg.RegisterMultiPart(entityId, Pos, Quaternion.Identity, Array.Empty<ShadowShape>(),
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            seedCellId: CellA, isStatic: true, partArray: partArray);
        Assert.True(reg.TryGetRetailCellArray(entityId, out _));

        reg.Deregister(entityId);

        Assert.False(reg.TryGetRetailCellArray(entityId, out var cells));
        Assert.Empty(cells);
        Assert.Empty(reg.GetRetailPartEntriesInCell(CellA));
    }

    [Fact]
    public void UpdatePosition_RenderOnly_RecomputesTheRetailCellArrayAtTheNewPosition()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 0x54u;
        IReadOnlyList<ShadowShape> partArray = new[] { Bsp(0x0100_0064u, radius: 2f) };
        reg.RegisterMultiPart(entityId, Pos, Quaternion.Identity, Array.Empty<ShadowShape>(),
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            seedCellId: CellA, isStatic: true, partArray: partArray);
        Assert.True(reg.TryGetRetailCellArray(entityId, out var before));
        Assert.Equal(new[] { CellA }, before);

        var moved = new Vector3(42f, 12f, 50f);
        reg.UpdatePosition(entityId, moved, Quaternion.Identity, OffX, OffY, LbId);

        Assert.True(reg.TryGetRetailCellArray(entityId, out var after));
        Assert.Equal(new[] { CellB }, after);
        Assert.DoesNotContain(
            reg.GetRetailPartEntriesInCell(CellA),
            e => e.EntityId == entityId);
        var movedEntries = reg.GetRetailPartEntriesInCell(CellB)
            .Where(e => e.EntityId == entityId).ToList();
        Assert.Single(movedEntries);
        Assert.Equal(0x0100_0064u, movedEntries[0].GfxObjId);
        Assert.Empty(reg.GetOwnerCells(entityId));
    }


    [Fact]
    public void RefloodLandblock_KeepsTheRetailProductOfACollisionOwnerWithAPartArray()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 0x60u;
        IReadOnlyList<ShadowShape> collision = new[] { Bsp(0x0100_0070u, radius: 2f) };
        IReadOnlyList<ShadowShape> parts = new[]
        {
            Bsp(0x0100_0070u, radius: 2f),
            Bsp(0x0100_0071u, localPosition: new Vector3(0.5f, 0f, 0f), radius: 1f),
        };

        reg.RegisterMultiPart(entityId, Pos, Quaternion.Identity, collision,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            seedCellId: CellA, isStatic: true, partArray: parts);
        Assert.True(reg.TryGetRetailCellArray(entityId, out var before));
        var beforeEntries = reg.GetRetailPartEntriesInCell(CellA)
            .Where(e => e.EntityId == entityId).Select(e => e.GfxObjId).ToList();
        Assert.Equal(new[] { 0x0100_0070u, 0x0100_0071u }, beforeEntries);

        reg.RefloodLandblock(LbId);

        Assert.True(reg.TryGetRetailCellArray(entityId, out var after));
        Assert.Equal(before, after);
        Assert.Equal(RetailCellArrayRoute.BoundingBox, reg.GetRetailCellArrayRoute(entityId));
        var afterEntries = reg.GetRetailPartEntriesInCell(CellA)
            .Where(e => e.EntityId == entityId).Select(e => e.GfxObjId).ToList();
        Assert.Equal(beforeEntries, afterEntries);
        Assert.Equal(new[] { CellA }, reg.GetOwnerCells(entityId));
    }

    [Fact]
    public void RefloodLandblock_KeepsARenderOnlyOwner()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 0x61u;
        IReadOnlyList<ShadowShape> parts = new[] { Bsp(0x0100_0072u, radius: 2f) };

        reg.RegisterMultiPart(entityId, Pos, Quaternion.Identity, Array.Empty<ShadowShape>(),
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            seedCellId: CellA, isStatic: true, partArray: parts);
        Assert.True(reg.TryGetRetailCellArray(entityId, out _));

        reg.RefloodLandblock(LbId);

        Assert.True(reg.TryGetRetailCellArray(entityId, out var cells));
        Assert.Equal(new[] { CellA }, cells);
        var entries = reg.GetRetailPartEntriesInCell(CellA)
            .Where(e => e.EntityId == entityId).ToList();
        Assert.Single(entries);
        Assert.Equal(0x0100_0072u, entries[0].GfxObjId);
        Assert.Empty(reg.GetOwnerCells(entityId));
    }


    private static int CountRows(ShadowObjectRegistry reg, uint cellId, uint entityId)
        => reg.GetRetailPartEntriesInCell(cellId).Count(e => e.EntityId == entityId);

    [Fact]
    public void Suspend_ClearsTheRetailProduct_AndTheUnsuspendingMoveRepublishesIt()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 0x70u;
        IReadOnlyList<ShadowShape> parts = new[] { Bsp(0x0100_0080u, radius: 2f) };
        reg.RegisterMultiPart(entityId, Pos, Quaternion.Identity, parts,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            seedCellId: CellA, isStatic: false, partArray: parts);
        Assert.Equal(1, CountRows(reg, CellA, entityId));

        Assert.True(reg.Suspend(entityId));

        Assert.False(reg.TryGetRetailCellArray(entityId, out _));
        Assert.Equal(0, CountRows(reg, CellA, entityId));
        Assert.Empty(reg.GetOwnerCells(entityId));
        // The route and part array are retained for the republish.
        Assert.Equal(RetailCellArrayRoute.BoundingBox, reg.GetRetailCellArrayRoute(entityId));

        reg.CommitSetPosition(
            entityId, Pos, Quaternion.Identity,
            seedCellId: CellA, worldOffsetX: OffX, worldOffsetY: OffY,
            action: PhysicsShadowCommitAction.None,
            crossCellIds: ImmutableArray<uint>.Empty);

        Assert.Equal(new[] { CellA }, reg.GetOwnerCells(entityId));
        Assert.True(reg.TryGetRetailCellArray(entityId, out var cells));
        Assert.Equal(new[] { CellA }, cells);
        Assert.Equal(1, CountRows(reg, CellA, entityId));
    }

    [Fact]
    public void AttachChild_AndDetachChild_AdvanceTheMutationRevision()
    {
        var reg = new ShadowObjectRegistry();
        const uint rootId = 0x71u;
        const uint childId = 0x72u;
        IReadOnlyList<ShadowShape> rootParts = new[] { Bsp(0x0100_0081u, radius: 2f) };
        reg.RegisterMultiPart(rootId, Pos, Quaternion.Identity, rootParts,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            seedCellId: CellA, isStatic: false, partArray: rootParts);

        ulong before = reg.MutationRevision;
        Assert.True(reg.AttachChild(childId, rootId, new[] { Bsp(0x0100_0082u) }));
        Assert.True(reg.MutationRevision > before);

        before = reg.MutationRevision;
        Assert.True(reg.DetachChild(childId));
        Assert.True(reg.MutationRevision > before);
    }

    [Fact]
    public void RegisterMultiPart_OfAnAttachedChild_KeepsInheritingTheRootsCells()
    {
        var reg = new ShadowObjectRegistry();
        const uint rootId = 0x73u;
        const uint childId = 0x74u;
        IReadOnlyList<ShadowShape> rootParts = new[] { Bsp(0x0100_0083u, radius: 2f) };
        reg.RegisterMultiPart(rootId, Pos, Quaternion.Identity, rootParts,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            seedCellId: CellA, isStatic: false, partArray: rootParts);
        Assert.True(reg.AttachChild(childId, rootId, new[] { Bsp(0x0100_0084u) }));
        Assert.Equal(1, CountRows(reg, CellA, childId));

        IReadOnlyList<ShadowShape> newParts = new[] { Bsp(0x0100_0085u, radius: 1f) };
        reg.RegisterMultiPart(childId, new Vector3(42f, 12f, 50f), Quaternion.Identity, newParts,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            seedCellId: CellB, isStatic: false, partArray: newParts);

        Assert.True(reg.TryGetRetailCellArray(childId, out var childCells));
        Assert.Equal(new[] { CellA }, childCells);
        Assert.Equal(0, CountRows(reg, CellB, childId));
        var rows = reg.GetRetailPartEntriesInCell(CellA).Where(e => e.EntityId == childId).ToList();
        Assert.Single(rows);
        Assert.Equal(0x0100_0085u, rows[0].GfxObjId);
    }

    [Fact]
    public void RemoveLandblock_ClearsTheRetailProduct_IncludingRenderOnlyStatics()
    {
        var reg = new ShadowObjectRegistry();
        const uint collidingId = 0x75u;
        const uint renderOnlyId = 0x76u;
        IReadOnlyList<ShadowShape> parts = new[] { Bsp(0x0100_0086u, radius: 2f) };
        reg.RegisterMultiPart(collidingId, Pos, Quaternion.Identity, parts,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            seedCellId: CellA, isStatic: true, partArray: parts);
        reg.RegisterMultiPart(renderOnlyId, Pos, Quaternion.Identity, Array.Empty<ShadowShape>(),
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            seedCellId: CellA, isStatic: true, partArray: parts);
        Assert.Equal(1, CountRows(reg, CellA, renderOnlyId));

        reg.RemoveLandblock(LbId);

        Assert.False(reg.TryGetRetailCellArray(collidingId, out _));
        Assert.False(reg.TryGetRetailCellArray(renderOnlyId, out _));
        Assert.Empty(reg.GetRetailPartEntriesInCell(CellA));
        // Both statics ended with their landblock — nothing left to suspend.
        Assert.False(reg.Suspend(collidingId));
        Assert.False(reg.Suspend(renderOnlyId));
    }

    [Fact]
    public void RetireOwnerFromLandblock_PrunesTheRetailRowsOfANonRootedOwner()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 0x77u;
        IReadOnlyList<ShadowShape> parts = new[] { Bsp(0x0100_0087u, radius: 2f) };
        reg.RegisterMultiPart(entityId, Pos, Quaternion.Identity, parts,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            seedCellId: CellA, isStatic: false, partArray: parts);
        Assert.Equal(1, CountRows(reg, CellA, entityId));

        reg.RetireOwnerFromLandblock(entityId, LbId);

        Assert.Equal(0, CountRows(reg, CellA, entityId));
        Assert.False(reg.TryGetRetailCellArray(entityId, out _));
        Assert.Empty(reg.GetOwnerCells(entityId));
        // A live owner survives the prefix retirement (it is still logically
        // alive for the reload reflood), so it can still be suspended.
        Assert.True(reg.Suspend(entityId));
    }

    [Fact]
    public void ReplaceMultiPartPayload_WithAnEmptyPartArray_KeepsThePriorRows()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 0x78u;
        IReadOnlyList<ShadowShape> parts = new[] { Bsp(0x0100_0088u, radius: 2f) };
        reg.RegisterMultiPart(entityId, Pos, Quaternion.Identity, parts,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            seedCellId: CellA, isStatic: false, partArray: parts);

        reg.ReplaceMultiPartPayload(entityId, Pos, Quaternion.Identity, parts,
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            seedCellId: CellA, isStatic: false, partArray: Array.Empty<ShadowShape>());

        Assert.True(reg.TryGetRetailCellArray(entityId, out var cells));
        Assert.Equal(new[] { CellA }, cells);
        Assert.Equal(1, CountRows(reg, CellA, entityId));
    }

    [Fact]
    public void CommitSetPosition_None_MovesARenderOnlyOwnerToItsDestinationCell()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 0x79u;
        IReadOnlyList<ShadowShape> parts = new[] { Bsp(0x0100_0089u, radius: 1f) };
        reg.RegisterMultiPart(entityId, Pos, Quaternion.Identity, Array.Empty<ShadowShape>(),
            state: 0u, flags: EntityCollisionFlags.None, OffX, OffY, LbId,
            seedCellId: CellA, isStatic: false, partArray: parts);
        Assert.Equal(1, CountRows(reg, CellA, entityId));

        reg.CommitSetPosition(
            entityId, new Vector3(42f, 12f, 50f), Quaternion.Identity,
            seedCellId: CellB, worldOffsetX: OffX, worldOffsetY: OffY,
            action: PhysicsShadowCommitAction.None,
            crossCellIds: ImmutableArray<uint>.Empty);

        Assert.True(reg.TryGetRetailCellArray(entityId, out var cells));
        Assert.Equal(new[] { CellB }, cells);
        Assert.Equal(0, CountRows(reg, CellA, entityId));
        Assert.Equal(1, CountRows(reg, CellB, entityId));
        Assert.Empty(reg.GetOwnerCells(entityId));
    }
}
