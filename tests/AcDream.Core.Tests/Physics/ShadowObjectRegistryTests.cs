using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class ShadowObjectRegistryTests
{
    private const uint LbId = 0xA9B40000u;   // landblock prefix used throughout
    private const float OffX = 0f;
    private const float OffY = 0f;

    [Fact]
    public void ShadowEntrySnapshot_LiveListMutation_DoesNotChangeCapturedWalk()
    {
        var first = new ShadowEntry(1u, 0u, Vector3.Zero, Quaternion.Identity, 1f);
        var second = new ShadowEntry(2u, 0u, Vector3.One, Quaternion.Identity, 1f);
        var replacement = new ShadowEntry(3u, 0u, Vector3.UnitX, Quaternion.Identity, 1f);
        var liveEntries = new List<ShadowEntry> { first, second };

        using var snapshot = ShadowEntrySnapshot.Capture(liveEntries);

        liveEntries.RemoveAt(0);
        liveEntries.Add(replacement);
        liveEntries.Add(first);

        Assert.Equal(
            new uint[] { first.EntityId, second.EntityId },
            snapshot.Entries.ToArray().Select(entry => entry.EntityId));
    }

    // -----------------------------------------------------------------------
    // Register / TotalRegistered
    // -----------------------------------------------------------------------

    [Fact]
    public void Register_SingleEntity_TotalRegisteredIsOne()
    {
        var reg = new ShadowObjectRegistry();
        reg.Register(1u, 0x01000001u, new Vector3(12f, 12f, 50f), Quaternion.Identity, 1f, OffX, OffY, LbId);
        Assert.Equal(1, reg.TotalRegistered);
    }

    [Fact]
    public void Register_SameEntityTwice_NoDuplicate()
    {
        var reg = new ShadowObjectRegistry();
        reg.Register(1u, 0x01000001u, new Vector3(12f, 12f, 50f), Quaternion.Identity, 1f, OffX, OffY, LbId);
        reg.Register(1u, 0x01000001u, new Vector3(13f, 12f, 50f), Quaternion.Identity, 1f, OffX, OffY, LbId); // re-register (position update)
        Assert.Equal(1, reg.TotalRegistered);
    }

    [Fact]
    public void Register_CornerLandblock_DerivesRealOutdoorSeed()
    {
        var reg = new ShadowObjectRegistry();
        reg.Register(1u, 0x01000001u, new Vector3(12f, 12f, 50f), Quaternion.Identity, 1f, OffX, OffY, 0x0000FFFFu);
        Assert.Equal(1, reg.TotalRegistered);
        Assert.Contains(reg.GetObjectsInCell(0x00000001u), e => e.EntityId == 1u);
    }

    [Fact]
    public void Register_AbsentLandblockId_StillKeepsWhenEmpty()
    {
        var reg = new ShadowObjectRegistry();
        reg.Register(1u, 0x01000001u, new Vector3(12f, 12f, 50f), Quaternion.Identity, 1f, OffX, OffY, 0u);
        Assert.Equal(0, reg.TotalRegistered);
    }

    // -----------------------------------------------------------------------
    // GetObjectsInCell
    // -----------------------------------------------------------------------

    [Fact]
    public void GetObjectsInCell_EntityInCenter_ReturnedInExpectedCell()
    {
        var reg = new ShadowObjectRegistry();
        reg.Register(42u, 0x01000002u, new Vector3(12f, 12f, 50f), Quaternion.Identity, 1f, OffX, OffY, LbId);

        uint cellId = LbId | 1u;   // cx=0, cy=0 → 0*8+0+1 = 1
        var objs = reg.GetObjectsInCell(cellId);
        Assert.Single(objs);
        Assert.Equal(42u, objs[0].EntityId);
    }

    [Fact]
    public void GetObjectsInCell_EntitySpanning2Cells_RegisteredInBoth()
    {
        var reg = new ShadowObjectRegistry();
        reg.Register(7u, 0x01000003u, new Vector3(24f, 12f, 50f), Quaternion.Identity, 2f, OffX, OffY, LbId);

        uint cell00 = LbId | 1u;    // cx=0, cy=0
        uint cell10 = LbId | 9u;    // cx=1, cy=0

        Assert.Contains(reg.GetObjectsInCell(cell00), e => e.EntityId == 7u);
        Assert.Contains(reg.GetObjectsInCell(cell10), e => e.EntityId == 7u);
    }

    // -----------------------------------------------------------------------
    // Deregister
    // -----------------------------------------------------------------------

    [Fact]
    public void Deregister_RemovesFromAllCells()
    {
        var reg = new ShadowObjectRegistry();
        reg.Register(5u, 0x01000004u, new Vector3(24f, 12f, 50f), Quaternion.Identity, 2f, OffX, OffY, LbId);
        reg.Deregister(5u);

        Assert.Equal(0, reg.TotalRegistered);
        Assert.Empty(reg.GetObjectsInCell(LbId | 1u));
        Assert.Empty(reg.GetObjectsInCell(LbId | 9u));
    }

    [Fact]
    public void Deregister_NonexistentEntity_NoThrow()
    {
        var reg = new ShadowObjectRegistry();
        // Should not throw.
        reg.Deregister(999u);
    }

    [Fact]
    public void Suspend_WithdrawsCellsAndUpdatePositionRestoresSameRegistration()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 23u;
        reg.Register(
            entityId,
            0x01000004u,
            new Vector3(12f, 12f, 50f),
            Quaternion.Identity,
            1f,
            OffX,
            OffY,
            LbId);

        Assert.True(reg.Suspend(entityId));
        Assert.Empty(reg.GetObjectsInCell(LbId | 1u));
        Assert.Equal(0, reg.TotalRegistered);

        reg.UpdatePosition(
            entityId,
            new Vector3(36f, 12f, 50f),
            Quaternion.Identity,
            OffX,
            OffY,
            LbId,
            seedCellId: LbId | 9u);

        ShadowEntry restored = Assert.Single(reg.GetObjectsInCell(LbId | 9u));
        Assert.Equal(entityId, restored.EntityId);
        Assert.Equal(new Vector3(36f, 12f, 50f), restored.Position);
        Assert.Equal(1, reg.TotalRegistered);
    }

    [Fact]
    public void Suspend_StateChangeUpdatesRetainedRegistrationBeforeRestore()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 24u;
        reg.Register(
            entityId,
            0x01000004u,
            new Vector3(12f, 12f, 50f),
            Quaternion.Identity,
            1f,
            OffX,
            OffY,
            LbId,
            state: 0x40u,
            seedCellId: LbId | 1u,
            isStatic: false);
        Assert.True(reg.Suspend(entityId));

        reg.UpdatePhysicsState(entityId, 0x44u);
        reg.UpdatePosition(
            entityId,
            new Vector3(12f, 12f, 50f),
            Quaternion.Identity,
            OffX,
            OffY,
            LbId,
            seedCellId: LbId | 1u);

        ShadowEntry restored = Assert.Single(reg.GetObjectsInCell(LbId | 1u));
        Assert.Equal(0x44u, restored.State);
    }


    [Fact]
    public void UpdatePwdBitfieldFlags_ReplacesOnlyPwdDerivedBits()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 60u;
        reg.Register(
            entityId,
            0x01000005u,
            new Vector3(12f, 12f, 50f),
            Quaternion.Identity,
            1f,
            OffX,
            OffY,
            LbId,
            flags: EntityCollisionFlags.HasWeenie
                | EntityCollisionFlags.IsCreature
                | EntityCollisionFlags.IsPlayer,
            seedCellId: LbId | 1u);

        reg.UpdatePwdBitfieldFlags(entityId, pwdBitfield: 0x2000008u); // BF_PLAYER | PKLite

        ShadowEntry entry = Assert.Single(reg.GetObjectsInCell(LbId | 1u));
        Assert.Equal(
            EntityCollisionFlags.HasWeenie
                | EntityCollisionFlags.IsCreature
                | EntityCollisionFlags.IsPlayer
                | EntityCollisionFlags.IsPKLite,
            entry.Flags);
    }

    [Fact]
    public void UpdatePwdBitfieldFlags_ClearsStalePkStateWhenBitfieldNoLongerCarriesIt()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 61u;
        reg.Register(
            entityId,
            0x01000005u,
            new Vector3(12f, 12f, 50f),
            Quaternion.Identity,
            1f,
            OffX,
            OffY,
            LbId,
            flags: EntityCollisionFlags.HasWeenie
                | EntityCollisionFlags.IsPlayer
                | EntityCollisionFlags.IsPKLite,
            seedCellId: LbId | 1u);

        reg.UpdatePwdBitfieldFlags(entityId, pwdBitfield: 0x8u); // BF_PLAYER only

        ShadowEntry entry = Assert.Single(reg.GetObjectsInCell(LbId | 1u));
        Assert.Equal(
            EntityCollisionFlags.HasWeenie | EntityCollisionFlags.IsPlayer,
            entry.Flags);
    }

    [Fact]
    public void UpdatePwdBitfieldFlags_UnregisteredEntity_NoOp()
    {
        var reg = new ShadowObjectRegistry();
        // Must not throw for an entity with no live shadow registration
        // (e.g. an off-screen/never-materialized object).
        reg.UpdatePwdBitfieldFlags(999u, pwdBitfield: 0x2000000u);
        Assert.Equal(0, reg.TotalRegistered);
    }

    [Fact]
    public void UpdatePwdBitfieldFlags_ThenCollisionExemption_BothPkLiteNowCollide()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 62u;
        reg.Register(
            entityId,
            0x01000005u,
            new Vector3(12f, 12f, 50f),
            Quaternion.Identity,
            1f,
            OffX,
            OffY,
            LbId,
            flags: EntityCollisionFlags.HasWeenie | EntityCollisionFlags.IsPlayer,
            seedCellId: LbId | 1u);

        var moverState = ObjectInfoState.IsPlayer | ObjectInfoState.IsPKLite;
        ShadowEntry before = Assert.Single(reg.GetObjectsInCell(LbId | 1u));
        Assert.True(CollisionExemption.ShouldSkip(before.State, before.Flags, moverState));

        reg.UpdatePwdBitfieldFlags(entityId, pwdBitfield: 0x2000008u); // BF_PLAYER | PKLite

        ShadowEntry after = Assert.Single(reg.GetObjectsInCell(LbId | 1u));
        Assert.False(CollisionExemption.ShouldSkip(after.State, after.Flags, moverState));
    }

    [Fact]
    public void ReplaceMultiPartPayload_VisibleOwnerKeepsExistingCellMembership()
    {
        const uint entityId = 25u;
        var reg = new ShadowObjectRegistry();
        Vector3 position = new(12f, 12f, 50f);
        reg.RegisterMultiPart(
            entityId,
            position,
            Quaternion.Identity,
            [BspShape(0x01000011u, radius: 0.5f)],
            state: 0u,
            flags: EntityCollisionFlags.None,
            OffX,
            OffY,
            LbId,
            seedCellId: LbId | 1u,
            isStatic: false);

        reg.ReplaceMultiPartPayload(
            entityId,
            position,
            Quaternion.Identity,
            [BspShape(0x01000012u, radius: 40f)],
            state: 0u,
            flags: EntityCollisionFlags.None,
            OffX,
            OffY,
            landblockId: 0u,
            seedCellId: 0u,
            isStatic: false);

        ShadowEntry replaced = Assert.Single(reg.GetObjectsInCell(LbId | 1u));
        Assert.Equal(0x01000012u, replaced.GfxObjId);
        Assert.Empty(reg.GetObjectsInCell(LbId | 9u));
    }

    [Fact]
    public void ReplaceMultiPartPayload_SuspendedShapedEmptyShaped_RemainsSuspended()
    {
        const uint entityId = 26u;
        var reg = new ShadowObjectRegistry();
        Vector3 position = new(12f, 12f, 50f);
        reg.RegisterMultiPart(
            entityId,
            position,
            Quaternion.Identity,
            [BspShape(0x01000021u, radius: 0.5f)],
            state: 0u,
            flags: EntityCollisionFlags.None,
            OffX,
            OffY,
            LbId,
            seedCellId: LbId | 1u,
            isStatic: false);
        Assert.True(reg.Suspend(entityId));

        reg.ReplaceMultiPartPayload(
            entityId, position, Quaternion.Identity, [], 0u,
            EntityCollisionFlags.None, OffX, OffY, LbId, LbId | 1u);
        reg.ReplaceMultiPartPayload(
            entityId,
            position,
            Quaternion.Identity,
            [BspShape(0x01000022u, radius: 0.5f)],
            0u,
            EntityCollisionFlags.None,
            OffX,
            OffY,
            LbId,
            LbId | 1u);

        Assert.Equal(1, reg.RetainedRegistrationCount);
        Assert.Equal(1, reg.SuspendedRegistrationCount);
        Assert.Empty(reg.GetObjectsInCell(LbId | 1u));
        reg.UpdatePosition(
            entityId, position, Quaternion.Identity, OffX, OffY, LbId, LbId | 1u);
        Assert.Equal(0x01000022u, Assert.Single(reg.GetObjectsInCell(LbId | 1u)).GfxObjId);
    }

    [Fact]
    public void ReplaceMultiPartPayload_NewCelllessOwnerRegistersRetainedButSuspended()
    {
        const uint entityId = 27u;
        var reg = new ShadowObjectRegistry();
        reg.ReplaceMultiPartPayload(
            entityId,
            new Vector3(12f, 12f, 50f),
            Quaternion.Identity,
            [BspShape(0x01000031u, radius: 0.5f)],
            0u,
            EntityCollisionFlags.None,
            OffX,
            OffY,
            LbId,
            seedCellId: LbId | 1u,
            isStatic: false,
            suspendIfNew: true);

        Assert.Equal(1, reg.RetainedRegistrationCount);
        Assert.Equal(1, reg.SuspendedRegistrationCount);
        Assert.Empty(reg.GetObjectsInCell(LbId | 1u));
    }

    // -----------------------------------------------------------------------
    // RemoveLandblock
    // -----------------------------------------------------------------------

    [Fact]
    public void RemoveLandblock_ClearsAllEntitiesForThatBlock()
    {
        const uint otherLb = 0xAAAA0000u;
        var reg = new ShadowObjectRegistry();
        reg.Register(1u, 0x01000001u, new Vector3(12f, 12f, 50f), Quaternion.Identity, 1f, OffX, OffY, LbId);
        reg.Register(2u, 0x01000002u, new Vector3(204f, 12f, 50f), Quaternion.Identity, 1f, 192f, 0f, otherLb);

        reg.RemoveLandblock(LbId);

        Assert.Equal(1, reg.TotalRegistered); // entity 2 (otherLb) survives
        Assert.Empty(reg.GetObjectsInCell(LbId | 1u));
    }

    [Fact]
    public void RemoveLandblock_DynamicRegistrationRefloodsAfterStreamingReload()
    {
        var reg = new ShadowObjectRegistry();
        const uint entityId = 31u;
        reg.Register(
            entityId,
            0x01000005u,
            new Vector3(12f, 12f, 50f),
            Quaternion.Identity,
            1f,
            OffX,
            OffY,
            LbId,
            seedCellId: LbId | 1u,
            isStatic: false);

        reg.RemoveLandblock(LbId);
        Assert.Empty(reg.GetObjectsInCell(LbId | 1u));

        reg.RefloodLandblock(LbId);

        Assert.Contains(
            reg.GetObjectsInCell(LbId | 1u),
            entry => entry.EntityId == entityId);
    }

    [Fact]
    public void StreamingReceipts_AreStableOrderedAndAdvanceOneStaticOwner()
    {
        var reg = new ShadowObjectRegistry();
        reg.Register(
            9u,
            0x01000009u,
            new Vector3(12f, 12f, 50f),
            Quaternion.Identity,
            1f,
            OffX,
            OffY,
            LbId,
            seedCellId: LbId | 1u,
            isStatic: true);
        reg.Register(
            2u,
            0x01000002u,
            new Vector3(14f, 12f, 50f),
            Quaternion.Identity,
            1f,
            OffX,
            OffY,
            LbId,
            seedCellId: LbId | 1u,
            isStatic: true);
        reg.Register(
            5u,
            0x01000005u,
            new Vector3(16f, 12f, 50f),
            Quaternion.Identity,
            1f,
            OffX,
            OffY,
            LbId,
            seedCellId: LbId | 1u,
            isStatic: false);

        Assert.Equal(
            [2u, 9u],
            reg.CaptureStaticOwnersForLandblock(LbId));
        Assert.Equal(
            [2u, 5u, 9u],
            reg.CaptureRefloodOwnersForLandblock(LbId));

        reg.DeregisterStaticOwnerForLandblock(2u, LbId);

        Assert.Equal(2, reg.RetainedRegistrationCount);
        Assert.DoesNotContain(
            reg.GetObjectsInCell(LbId | 1u),
            entry => entry.EntityId == 2u);
        Assert.Contains(
            reg.GetObjectsInCell(LbId | 1u),
            entry => entry.EntityId == 5u);
        Assert.Contains(
            reg.GetObjectsInCell(LbId | 1u),
            entry => entry.EntityId == 9u);
    }

    [Fact]
    public void RefloodLandblock_RestoresAdjacentOwnedFootprintWithdrawnByUnload()
    {
        const uint adjacentOwnerLb = 0xAAB40000u;
        const uint ownerCell = adjacentOwnerLb | 1u;
        const uint touchedCell = LbId | 57u;
        const uint entityId = 32u;
        var reg = new ShadowObjectRegistry();
        var cache = new PhysicsDataCache();
        cache.CellGraph.RegisterTerrain(
            adjacentOwnerLb,
            new TerrainSurface(new byte[81], new float[256]),
            new Vector3(192f, 0f, 0f));
        reg.DataCache = cache;
        reg.Register(
            entityId,
            0x01000006u,
            new Vector3(192.5f, 12f, 50f),
            Quaternion.Identity,
            2f,
            192f,
            0f,
            adjacentOwnerLb,
            seedCellId: ownerCell,
            isStatic: false);
        Assert.Contains(reg.GetObjectsInCell(touchedCell), e => e.EntityId == entityId);
        Assert.Contains(reg.GetObjectsInCell(ownerCell), e => e.EntityId == entityId);

        reg.RemoveLandblock(LbId);
        Assert.DoesNotContain(reg.GetObjectsInCell(touchedCell), e => e.EntityId == entityId);
        Assert.Contains(reg.GetObjectsInCell(ownerCell), e => e.EntityId == entityId);
        Assert.Equal(1, reg.WithdrawnPrefixMarkerCount);

        reg.RefloodLandblock(LbId);

        Assert.Contains(reg.GetObjectsInCell(touchedCell), e => e.EntityId == entityId);
        Assert.Contains(reg.GetObjectsInCell(ownerCell), e => e.EntityId == entityId);
        Assert.Equal(0, reg.WithdrawnPrefixMarkerCount);
    }

    [Fact]
    public void AuthoritativeMove_ClearsObsoleteWithdrawnPrefix()
    {
        const uint adjacentOwnerLb = 0xAAB40000u;
        const uint ownerCell = adjacentOwnerLb | 1u;
        const uint touchedCell = LbId | 57u;
        const uint entityId = 33u;
        var cache = new PhysicsDataCache();
        cache.CellGraph.RegisterTerrain(
            adjacentOwnerLb,
            new TerrainSurface(new byte[81], new float[256]),
            new Vector3(192f, 0f, 0f));
        var reg = new ShadowObjectRegistry { DataCache = cache };
        reg.Register(
            entityId, 0x01000007u, new Vector3(192.5f, 12f, 50f),
            Quaternion.Identity, 2f, 192f, 0f, adjacentOwnerLb,
            seedCellId: ownerCell, isStatic: false);
        reg.RemoveLandblock(LbId);
        Assert.Equal(1, reg.WithdrawnPrefixMarkerCount);

        reg.UpdatePosition(
            entityId,
            new Vector3(204f, 12f, 50f),
            Quaternion.Identity,
            192f,
            0f,
            adjacentOwnerLb,
            seedCellId: ownerCell);

        Assert.Equal(0, reg.WithdrawnPrefixMarkerCount);
        reg.RefloodLandblock(LbId);
        Assert.DoesNotContain(reg.GetObjectsInCell(touchedCell), e => e.EntityId == entityId);
    }

    [Fact]
    public void RefloodLandblock_RetiresOnlyCurrentMarker_WhenTwoPrefixesWereWithdrawn()
    {
        const uint ownerLb = 0xAAB50000u;
        const uint ownerCell = ownerLb | 1u;
        const uint westLb = 0xA9B50000u;
        const uint southLb = 0xAAB40000u;
        const uint westCell = westLb | 57u;
        const uint southCell = southLb | 8u;
        const uint entityId = 35u;
        var cache = new PhysicsDataCache();
        cache.CellGraph.RegisterTerrain(
            ownerLb,
            new TerrainSurface(new byte[81], new float[256]),
            new Vector3(192f, 192f, 0f));
        var reg = new ShadowObjectRegistry { DataCache = cache };
        reg.Register(
            entityId, 0x01000009u, new Vector3(192.5f, 192.5f, 50f),
            Quaternion.Identity, 2f, 192f, 192f, ownerLb,
            seedCellId: ownerCell, isStatic: false);
        Assert.Contains(reg.GetObjectsInCell(westCell), e => e.EntityId == entityId);
        Assert.Contains(reg.GetObjectsInCell(southCell), e => e.EntityId == entityId);

        reg.RemoveLandblock(westLb);
        reg.RemoveLandblock(southLb);
        Assert.Equal(2, reg.WithdrawnPrefixMarkerCount);

        reg.RefloodLandblock(westLb);
        Assert.Equal(1, reg.WithdrawnPrefixMarkerCount);

        reg.RefloodLandblock(southLb);
        Assert.Equal(0, reg.WithdrawnPrefixMarkerCount);
        Assert.Contains(reg.GetObjectsInCell(westCell), e => e.EntityId == entityId);
        Assert.Contains(reg.GetObjectsInCell(southCell), e => e.EntityId == entityId);
    }

    [Fact]
    public void RemoveLandblock_DirectStaticRetirement_ClearsWithdrawnMarker()
    {
        const uint entityId = 34u;
        var reg = new ShadowObjectRegistry();
        reg.Register(
            entityId, 0x01000008u, new Vector3(12f, 12f, 50f),
            Quaternion.Identity, 1f, OffX, OffY, LbId,
            isStatic: true);

        reg.RemoveLandblock(LbId);

        Assert.Equal(0, reg.RetainedRegistrationCount);
        Assert.Equal(0, reg.WithdrawnPrefixMarkerCount);
    }


    [Fact]
    public void PerCellQuery_EntityCell_ReturnsIt()
    {
        var reg = new ShadowObjectRegistry();
        reg.Register(10u, 0x01000005u, new Vector3(30f, 30f, 50f), Quaternion.Identity, 1f, OffX, OffY, LbId);

        // local (30,30) → landcell (1,1) → 1*8+1+1 = 10.
        var results = reg.GetObjectsInCell(LbId | 10u);
        Assert.Single(results);
        Assert.Equal(10u, results[0].EntityId);
    }

    [Fact]
    public void PerCellQuery_FarCell_ReturnsEmpty()
    {
        var reg = new ShadowObjectRegistry();
        reg.Register(11u, 0x01000006u, new Vector3(12f, 12f, 50f), Quaternion.Identity, 1f, OffX, OffY, LbId);

        Assert.Empty(reg.GetObjectsInCell(LbId | 64u));
    }

    [Fact]
    public void PerCellQuery_EntityInMultipleCells_OncePerCellList()
    {
        var reg = new ShadowObjectRegistry();
        reg.Register(20u, 0x01000007u, new Vector3(24f, 12f, 50f), Quaternion.Identity, 2f, OffX, OffY, LbId);

        Assert.Single(reg.GetObjectsInCell(LbId | 1u), e => e.EntityId == 20u);
        Assert.Single(reg.GetObjectsInCell(LbId | 9u), e => e.EntityId == 20u);
    }


    [Theory]
    [InlineData(0f, 0f, 1u)]
    [InlineData(48f, 0f, 17u)]
    [InlineData(0f, 48f, 3u)]
    [InlineData(168f, 168f, 64u)]
    public void GetObjectsInCell_CellIdFormula_Correct(float lx, float ly, uint expectedLow)
    {
        var reg = new ShadowObjectRegistry();
        reg.Register(99u, 0x01000008u, new Vector3(lx + 0.5f, ly + 0.5f, 50f), Quaternion.Identity, 0.1f, OffX, OffY, LbId);

        uint cellId = LbId | expectedLow;
        var objs = reg.GetObjectsInCell(cellId);
        Assert.Contains(objs, e => e.EntityId == 99u);
    }


    [Fact]
    public void UpdatePosition_MovedEntity_NewCellOccupied()
    {
        var reg = new ShadowObjectRegistry();
        reg.Register(42u, 0x01000010u, new Vector3(12f, 12f, 50f), Quaternion.Identity, 0.5f, OffX, OffY, LbId);

        reg.UpdatePosition(42u, new Vector3(60f, 12f, 50f), Quaternion.Identity, OffX, OffY, LbId);

        Assert.Empty(reg.GetObjectsInCell(LbId | 1u));
        var newCell = reg.GetObjectsInCell(LbId | 17u);
        Assert.Single(newCell);
        Assert.Equal(42u, newCell[0].EntityId);
        Assert.Equal(1, reg.TotalRegistered);            // not duplicated
    }

    [Fact]
    public void UpdatePosition_PreservesFlags()
    {
        // Register with PK flags + PhysicsState; UpdatePosition must keep them.
        var reg = new ShadowObjectRegistry();
        reg.Register(50u, 0x01000011u, new Vector3(12f, 12f, 50f), Quaternion.Identity,
                     0.5f, OffX, OffY, LbId,
                     state: 0x4u, // ETHEREAL_PS
                     flags: EntityCollisionFlags.IsPlayer | EntityCollisionFlags.IsPK);

        reg.UpdatePosition(50u, new Vector3(60f, 12f, 50f), Quaternion.Identity, OffX, OffY, LbId);

        var newCell = reg.GetObjectsInCell(LbId | 17u);
        Assert.Single(newCell);
        Assert.Equal(0x4u, newCell[0].State);
        Assert.Equal(EntityCollisionFlags.IsPlayer | EntityCollisionFlags.IsPK, newCell[0].Flags);
    }

    [Fact]
    public void UpdatePosition_UnregisteredEntity_NoOp()
    {
        var reg = new ShadowObjectRegistry();
        // Should not throw, should not register a new entity.
        reg.UpdatePosition(99u, new Vector3(12f, 12f, 50f), Quaternion.Identity, OffX, OffY, LbId);
        Assert.Equal(0, reg.TotalRegistered);
    }

    [Fact]
    public void Register_WithStateAndFlags_StoredOnEntry()
    {
        var reg = new ShadowObjectRegistry();
        reg.Register(60u, 0x01000012u, new Vector3(12f, 12f, 50f), Quaternion.Identity,
                     0.5f, OffX, OffY, LbId,
                     state: 0x10u, // IGNORE_COLLISIONS_PS
                     flags: EntityCollisionFlags.IsImpenetrable);

        var entry = reg.GetObjectsInCell(LbId | 1u)[0];
        Assert.Equal(0x10u, entry.State);
        Assert.Equal(EntityCollisionFlags.IsImpenetrable, entry.Flags);
    }

    [Fact]
    public void Register_DefaultStateAndFlags_AreZeroAndNone()
    {
        var reg = new ShadowObjectRegistry();
        reg.Register(70u, 0x01000013u, new Vector3(12f, 12f, 50f), Quaternion.Identity,
                     0.5f, OffX, OffY, LbId);

        var entry = reg.GetObjectsInCell(LbId | 1u)[0];
        Assert.Equal(0u, entry.State);
        Assert.Equal(EntityCollisionFlags.None, entry.Flags);
    }


    [Fact]
    public void UpdatePhysicsState_FlipsEthereal_NextLookupSeesNewBits()
    {
        var reg = new ShadowObjectRegistry();
        const uint doorId = 0x000F4244u;
        reg.Register(doorId, 0x020019FFu, new Vector3(12f, 12f, 50f),
                     Quaternion.Identity, 1f, OffX, OffY, LbId,
                     state: 0u, flags: EntityCollisionFlags.None);

        var before = reg.AllEntriesForDebug().Single(e => e.EntityId == doorId);
        Assert.Equal(0u, before.State);

        reg.UpdatePhysicsState(doorId, 0x00000004u);

        var after = reg.AllEntriesForDebug().Single(e => e.EntityId == doorId);
        Assert.Equal(0x00000004u, after.State);
    }

    [Fact]
    public void UpdatePhysicsState_UnregisteredEntity_IsNoOp()
    {
        var reg = new ShadowObjectRegistry();
        reg.UpdatePhysicsState(0xDEADBEEFu, 0x00000004u);
        Assert.Equal(0, reg.TotalRegistered);
    }

    [Fact]
    public void UpdatePhysicsState_EntitySpanningMultipleCells_AllCellsUpdated()
    {
        var reg = new ShadowObjectRegistry();
        reg.Register(99u, 0x01000099u, new Vector3(24f, 12f, 50f),
                     Quaternion.Identity, 2f, OffX, OffY, LbId,
                     state: 0u);

        reg.UpdatePhysicsState(99u, 0x00000004u);

        uint cellA = LbId | 1u;
        uint cellB = LbId | (1u*8 + 0 + 1);
        var inA = reg.GetObjectsInCell(cellA).Single(e => e.EntityId == 99u);
        var inB = reg.GetObjectsInCell(cellB).Single(e => e.EntityId == 99u);
        Assert.Equal(0x00000004u, inA.State);
        Assert.Equal(0x00000004u, inB.State);
    }


    [Fact]
    public void Register_OutdoorSeed_DoorVisibleInItsOutdoorCell_NotInUnrelatedIndoorCell()
    {
        var reg = new ShadowObjectRegistry();

        const uint doorEntityId = 0x000F4244u;
        reg.Register(doorEntityId, 0x020019FFu, new Vector3(12f, 12f, 50f),
                     Quaternion.Identity, 1f, OffX, OffY, LbId,
                     ShadowCollisionType.Cylinder, cylHeight: 2.5f,
                     isStatic: false);

        uint doorOutdoorCellId = LbId | 1u;
        uint vestibuleCellId   = LbId | 0x0145u;

        Assert.Contains(reg.GetObjectsInCell(doorOutdoorCellId), e => e.EntityId == doorEntityId);
        Assert.DoesNotContain(reg.GetObjectsInCell(vestibuleCellId), e => e.EntityId == doorEntityId);
    }

    [Fact]
    public void Register_OutdoorFootprint_NeverLandsInInteriorCells()
    {
        var reg = new ShadowObjectRegistry();

        const uint cottageEntityId = 0xA9B47900u;
        reg.Register(cottageEntityId, 0x01000A2Bu, new Vector3(12f, 12f, 90f),
                     Quaternion.Identity, 5.5f, OffX, OffY, LbId);

        uint cellarCellId = LbId | 0x0146u;
        Assert.Empty(reg.GetObjectsInCell(cellarCellId));

        foreach (var entry in reg.AllEntriesForDebug())
            Assert.Equal(cottageEntityId, entry.EntityId);
    }

    [Fact]
    public void RefloodLandblock_RerunsFloodAfterCellsHydrate()
    {
        var reg = new ShadowObjectRegistry();
        var cache = new PhysicsDataCache();
        reg.DataCache = cache;

        const uint seedCell     = 0xA9B40100u;
        const uint neighborCell = 0xA9B40101u;

        reg.Register(77u, 0x01000009u, new Vector3(2.0f, 0f, 2.5f),
                     Quaternion.Identity, 0.5f, OffX, OffY, LbId,
                     seedCellId: seedCell, isStatic: false);

        Assert.Contains(reg.GetObjectsInCell(seedCell), e => e.EntityId == 77u);
        Assert.Empty(reg.GetObjectsInCell(neighborCell));

        // Hydrate the seed (portal to the neighbor at x=2.5) + the neighbor
        // (leaf BSP admits the straddling sphere), then re-flood.
        cache.RegisterCellStructForTest(seedCell,
            BuildShadowCellSetTests_MakeCellWithPortalAtRightWall(Matrix4x4.Identity, 0x0101));
        cache.RegisterCellStructForTest(neighborCell,
            BuildShadowCellSetTests_MakeLeafCell(Matrix4x4.CreateTranslation(5f, 0f, 0f)));

        reg.RefloodLandblock(LbId);

        Assert.Contains(reg.GetObjectsInCell(seedCell), e => e.EntityId == 77u);
        Assert.Contains(reg.GetObjectsInCell(neighborCell), e => e.EntityId == 77u);
    }

    private static CellPhysics BuildShadowCellSetTests_MakeCellWithPortalAtRightWall(
        Matrix4x4 worldTransform, ushort otherCellId)
    {
        var portalPoly = new ResolvedPolygon
        {
            Vertices  = new[]
            {
                new Vector3(2.5f, -2.5f,  0f),
                new Vector3(2.5f,  2.5f,  0f),
                new Vector3(2.5f,  2.5f,  5f),
                new Vector3(2.5f, -2.5f,  5f),
            },
            Plane     = new System.Numerics.Plane(new Vector3(1, 0, 0), -2.5f),
            NumPoints = 4,
            SidesType = DatReaderWriter.Enums.CullMode.None,
        };

        Matrix4x4.Invert(worldTransform, out var inv);
        return new CellPhysics
        {
            WorldTransform        = worldTransform,
            InverseWorldTransform = inv,
            Resolved              = new Dictionary<ushort, ResolvedPolygon>(),
            PortalPolygons        = new Dictionary<ushort, ResolvedPolygon> { [10] = portalPoly },
            Portals               = new[]
            {
                new PortalInfo(otherCellId: otherCellId, polygonId: 10, flags: 0),
            },
            CellBSP = new DatReaderWriter.Types.CellBSPTree
            {
                Root = new DatReaderWriter.Types.CellBSPNode
                {
                    Type = DatReaderWriter.Enums.BSPNodeType.Leaf,
                },
            },
        };
    }

    private static ShadowShape BspShape(uint gfxObjId, float radius)
        => ShadowShape.Bsp(
            gfxObjId,
            Vector3.Zero,
            Quaternion.Identity,
            scale: 1f,
            localGeometry: ShadowPartGeometry.Create(new FlatCollisionSphere(Vector3.Zero, radius), null));

    private static CellPhysics BuildShadowCellSetTests_MakeLeafCell(Matrix4x4 worldTransform)
    {
        Matrix4x4.Invert(worldTransform, out var inv);
        return new CellPhysics
        {
            WorldTransform        = worldTransform,
            InverseWorldTransform = inv,
            Resolved              = new Dictionary<ushort, ResolvedPolygon>(),
            CellBSP = new DatReaderWriter.Types.CellBSPTree
            {
                Root = new DatReaderWriter.Types.CellBSPNode
                {
                    Type = DatReaderWriter.Enums.BSPNodeType.Leaf,
                },
            },
        };
    }
}
