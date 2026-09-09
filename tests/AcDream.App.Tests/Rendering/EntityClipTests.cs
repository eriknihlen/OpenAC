using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering.Wb;
using AcDream.Core.World;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public sealed class EntityClipTests
{
    // Minimal WorldEntity factory. EntityPassesVisibleCellGate only reads
    // ParentCellId and IsBuildingShell/BuildingShellAnchorCellId from the entity;
    // the other required fields are set to safe sentinel values.
    private static WorldEntity Entity(uint? parentCellId, bool isShell = false, uint? shellAnchor = null) =>
        new WorldEntity
        {
            Id = 1u,
            SourceGfxObjOrSetupId = 0u,
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs = System.Array.Empty<MeshRef>(),
            ParentCellId = parentCellId,
            IsBuildingShell = isShell,
            BuildingShellAnchorCellId = shellAnchor,
        };

    [Fact]
    public void EntityClip_ParentInVisibleSet_Included()
    {
        // Entity whose ParentCellId is in the visible set must pass the gate.
        const uint cellId = 0xA9B40170u;
        var visibleCellIds = new HashSet<uint> { cellId };
        var entity = Entity(parentCellId: cellId);

        bool result = WbDrawDispatcher.EntityPassesVisibleCellGate(
            entity, visibleCellIds, WbDrawDispatcher.EntitySet.All);

        Assert.True(result);
    }

    [Fact]
    public void EntityClip_ParentNotInVisibleSet_Excluded()
    {
        // Entity whose ParentCellId is NOT in the visible set must fail the gate.
        const uint visibleCell = 0xA9B40170u;
        const uint entityCell = 0xA9B40172u;
        var visibleCellIds = new HashSet<uint> { visibleCell };
        var entity = Entity(parentCellId: entityCell);

        bool result = WbDrawDispatcher.EntityPassesVisibleCellGate(
            entity, visibleCellIds, WbDrawDispatcher.EntitySet.All);

        Assert.False(result);
    }

    [Fact]
    public void EntityClip_NullVisibleSet_IncludesAll()
    {
        var entity = Entity(parentCellId: 0xA9B40172u);

        bool result = WbDrawDispatcher.EntityPassesVisibleCellGate(
            entity, visibleCellIds: null, WbDrawDispatcher.EntitySet.All);

        Assert.True(result);
    }

    [Fact]
    public void EntityClip_NullParentCell_NullVisibleSet_Included()
    {
        // An outdoor entity (ParentCellId == null) with null visibleCellIds passes.
        var entity = Entity(parentCellId: null);

        bool result = WbDrawDispatcher.EntityPassesVisibleCellGate(
            entity, visibleCellIds: null, WbDrawDispatcher.EntitySet.All);

        Assert.True(result);
    }

    [Fact]
    public void EntityClip_NullParentCell_NonNullVisibleSet_Excluded()
    {
        var visibleCellIds = new HashSet<uint> { 0xA9B40170u };
        var entity = Entity(parentCellId: null);

        bool result = WbDrawDispatcher.EntityPassesVisibleCellGate(
            entity, visibleCellIds, WbDrawDispatcher.EntitySet.All);

        Assert.False(result);
    }
}
