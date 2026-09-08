using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Items;
using AcDream.Core.Physics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public sealed class EntryRestrictionGateTests
{
    private const uint RestrictionObjGuid = 0x80001234u;
    private const uint MoverGuid = 0x50000001u;

    [Fact]
    public void OrdinaryCell_NoRestrictionObj_IsNoOp_ForPlayer()
    {
        var mover = new ObjectInfo { State = ObjectInfoState.IsPlayer };

        Assert.Equal(TransitionState.OK, mover.CheckEntryRestrictions(cellRestrictionObj: 0, objects: null));
    }

    [Fact]
    public void OrdinaryCell_NoRestrictionObj_IsNoOp_ForNonPlayer()
    {
        var mover = new ObjectInfo { State = ObjectInfoState.None };

        Assert.Equal(TransitionState.OK, mover.CheckEntryRestrictions(cellRestrictionObj: 0, objects: null));
    }

    [Fact]
    public void RestrictedCell_NonPlayerMover_Bypasses()
    {
        var mover = new ObjectInfo { State = ObjectInfoState.None };

        Assert.Equal(
            TransitionState.OK,
            mover.CheckEntryRestrictions(RestrictionObjGuid, objects: null));
    }

    [Fact]
    public void RestrictedCell_PlayerMover_UnresolvedRestrictionObject_FailsClosed()
    {
        var mover = new ObjectInfo { State = ObjectInfoState.IsPlayer, SelfEntityId = MoverGuid };

        Assert.Equal(
            TransitionState.Collided,
            mover.CheckEntryRestrictions(RestrictionObjGuid, objects: null));
    }

    [Fact]
    public void RestrictedCell_PlayerMover_ResolvedButObjectsTableHasNoRow_FailsClosed()
    {
        var mover = new ObjectInfo { State = ObjectInfoState.IsPlayer, SelfEntityId = MoverGuid };
        var objects = new ClientObjectTable();

        Assert.Equal(
            TransitionState.Collided,
            mover.CheckEntryRestrictions(RestrictionObjGuid, objects));
    }

    [Fact]
    public void RestrictedCell_PlayerMover_CanBypassMoveRestrictions_PassesThrough()
    {
        var mover = new ObjectInfo
        {
            State = ObjectInfoState.IsPlayer | ObjectInfoState.CanBypassMoveRestrictions,
            SelfEntityId = MoverGuid,
        };

        Assert.Equal(
            TransitionState.OK,
            mover.CheckEntryRestrictions(RestrictionObjGuid, objects: null));
    }


    private static ClientObjectTable MakeObjectsWithRestrictionObject(
        uint? houseOwnerId,
        HouseRestrictionRecord? restrictions)
    {
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = RestrictionObjGuid,
            HouseOwnerId = houseOwnerId,
            Restrictions = restrictions,
        });
        return objects;
    }

    [Fact]
    public void ResolvedUnownedHouse_NoOwnerNoRestrictions_Admits()
    {
        var mover = new ObjectInfo { State = ObjectInfoState.IsPlayer, SelfEntityId = MoverGuid };
        var objects = MakeObjectsWithRestrictionObject(houseOwnerId: null, restrictions: null);

        Assert.Equal(
            TransitionState.OK,
            mover.CheckEntryRestrictions(RestrictionObjGuid, objects));
    }

    [Fact]
    public void ResolvedHouse_MoverIsOwner_Admits()
    {
        var mover = new ObjectInfo { State = ObjectInfoState.IsPlayer, SelfEntityId = MoverGuid };
        var closedList = new HouseRestrictionRecord(
            OpenToPublic: false,
            AllegianceMonarchId: 0,
            Guests: new Dictionary<uint, uint>());
        var objects = MakeObjectsWithRestrictionObject(houseOwnerId: MoverGuid, restrictions: closedList);

        Assert.Equal(
            TransitionState.OK,
            mover.CheckEntryRestrictions(RestrictionObjGuid, objects));
    }

    [Fact]
    public void ResolvedHouse_NoRestrictionDb_Admits()
    {
        var mover = new ObjectInfo { State = ObjectInfoState.IsPlayer, SelfEntityId = MoverGuid };
        const uint otherOwnerId = 0x50000099u;
        var objects = MakeObjectsWithRestrictionObject(houseOwnerId: otherOwnerId, restrictions: null);

        Assert.Equal(
            TransitionState.OK,
            mover.CheckEntryRestrictions(RestrictionObjGuid, objects));
    }

    [Fact]
    public void ResolvedHouse_PresentListExcludesMover_Blocks()
    {
        var mover = new ObjectInfo { State = ObjectInfoState.IsPlayer, SelfEntityId = MoverGuid };
        const uint otherOwnerId = 0x50000099u;
        var restrictions = new HouseRestrictionRecord(
            OpenToPublic: false,
            AllegianceMonarchId: 0,
            Guests: new Dictionary<uint, uint> { [0x50000002u] = 0u }); // a DIFFERENT guest
        var objects = MakeObjectsWithRestrictionObject(houseOwnerId: otherOwnerId, restrictions: restrictions);

        Assert.Equal(
            TransitionState.Collided,
            mover.CheckEntryRestrictions(RestrictionObjGuid, objects));
    }

    [Fact]
    public void ResolvedHouse_PresentListIncludesMover_Admits()
    {
        var mover = new ObjectInfo { State = ObjectInfoState.IsPlayer, SelfEntityId = MoverGuid };
        const uint otherOwnerId = 0x50000099u;
        var restrictions = new HouseRestrictionRecord(
            OpenToPublic: false,
            AllegianceMonarchId: 0,
            Guests: new Dictionary<uint, uint> { [MoverGuid] = 1u }); // storage guest
        var objects = MakeObjectsWithRestrictionObject(houseOwnerId: otherOwnerId, restrictions: restrictions);

        Assert.Equal(
            TransitionState.OK,
            mover.CheckEntryRestrictions(RestrictionObjGuid, objects));
    }

    [Fact]
    public void ResolvedHouse_OpenToPublic_AdmitsEvenWithoutGuestEntry()
    {
        // Flags bit 0 set (open to public) -- everyone in, regardless of the
        // guest table or allegiance.
        var mover = new ObjectInfo { State = ObjectInfoState.IsPlayer, SelfEntityId = MoverGuid };
        const uint otherOwnerId = 0x50000099u;
        var restrictions = new HouseRestrictionRecord(
            OpenToPublic: true,
            AllegianceMonarchId: 0,
            Guests: new Dictionary<uint, uint>());
        var objects = MakeObjectsWithRestrictionObject(houseOwnerId: otherOwnerId, restrictions: restrictions);

        Assert.Equal(
            TransitionState.OK,
            mover.CheckEntryRestrictions(RestrictionObjGuid, objects));
    }

    [Fact]
    public void ResolvedHouse_MoverSharesAllegianceMonarch_Admits()
    {
        var mover = new ObjectInfo { State = ObjectInfoState.IsPlayer, SelfEntityId = MoverGuid };
        const uint otherOwnerId = 0x50000099u;
        const uint monarchId = 0x50000500u;
        var restrictions = new HouseRestrictionRecord(
            OpenToPublic: false,
            AllegianceMonarchId: monarchId,
            Guests: new Dictionary<uint, uint>());
        var objects = MakeObjectsWithRestrictionObject(houseOwnerId: otherOwnerId, restrictions: restrictions);
        objects.AddOrUpdate(new ClientObject { ObjectId = MoverGuid, MonarchId = monarchId });

        Assert.Equal(
            TransitionState.OK,
            mover.CheckEntryRestrictions(RestrictionObjGuid, objects));
    }

    // ── PWD-bitfield decode (mirrors the existing EntityCollisionFlagsTests
    // pattern for IsPK/IsPKLite/IsImpenetrable) ─────────────────────────────

    [Fact]
    public void FromPwdBitfield_OnlyAdminBit_DoesNotGrantBypass()
    {
        var flags = EntityCollisionFlagsExt.FromPwdBitfield(0x100000u);
        Assert.False(flags.HasFlag(EntityCollisionFlags.CanBypassMoveRestrictions));
    }

    [Fact]
    public void FromPwdBitfield_OnlyImmuneCellRestrictionsBit_DoesNotGrantBypass()
    {
        var flags = EntityCollisionFlagsExt.FromPwdBitfield(0x400000u);
        Assert.False(flags.HasFlag(EntityCollisionFlags.CanBypassMoveRestrictions));
    }

    [Fact]
    public void FromPwdBitfield_BothAdminAndImmuneCellRestrictionsBits_GrantsBypass()
    {
        uint bitfield = 0x100000u | 0x400000u;
        var flags = EntityCollisionFlagsExt.FromPwdBitfield(bitfield);
        Assert.True(flags.HasFlag(EntityCollisionFlags.CanBypassMoveRestrictions));
    }

    [Fact]
    public void ToMoverState_CanBypassMoveRestrictions_TranslatesIndependently()
    {
        Assert.Equal(
            ObjectInfoState.CanBypassMoveRestrictions,
            EntityCollisionFlags.CanBypassMoveRestrictions.ToMoverState());
    }

    [Fact]
    public void FromPwdBitfield_ThenToMoverState_EndToEnd_AdminBypassesRestriction()
    {
        uint bitfield = 0x8u | 0x100000u | 0x400000u;
        ObjectInfoState moverState =
            ObjectInfoState.IsPlayer | EntityCollisionFlagsExt.FromPwdBitfield(bitfield).ToMoverState();

        var mover = new ObjectInfo { State = moverState, SelfEntityId = MoverGuid };

        Assert.Equal(
            TransitionState.OK,
            mover.CheckEntryRestrictions(RestrictionObjGuid, objects: null));
    }


    [Fact]
    public void CellPhysics_DefaultRestrictionObj_IsZero()
    {
        var cellPhysics = new CellPhysics
        {
            WorldTransform = System.Numerics.Matrix4x4.Identity,
            InverseWorldTransform = System.Numerics.Matrix4x4.Identity,
            Resolved = new System.Collections.Generic.Dictionary<ushort, ResolvedPolygon>(),
        };

        Assert.Equal(0u, cellPhysics.RestrictionObj);
    }


    private const uint CellId = 0xA9B40157u;

    private static CellPhysics MakeIndoorCell(uint restrictionObj) => new()
    {
        BSP = new PhysicsBSPTree
        {
            Root = new PhysicsBSPNode
            {
                Type = BSPNodeType.Leaf,
                BoundingSphere = new Sphere { Origin = Vector3.Zero, Radius = 10f },
            },
        },
        WorldTransform = Matrix4x4.Identity,
        InverseWorldTransform = Matrix4x4.Identity,
        Resolved = new Dictionary<ushort, ResolvedPolygon>(),
        CellBSP = new CellBSPTree { Root = new CellBSPNode { Type = BSPNodeType.Leaf } },
        RestrictionObj = restrictionObj,
    };

    private static PhysicsEngine MakeEngine(CellPhysics cellPhysics)
    {
        var engine = new PhysicsEngine();
        engine.DataCache = new PhysicsDataCache();
        engine.DataCache.RegisterCellStructForTest(CellId, cellPhysics);
        return engine;
    }

    [Fact]
    public void EndToEnd_RestrictedCell_PlayerCannotBypass_TransitionHaltsAtOrigin()
    {
        var engine = MakeEngine(MakeIndoorCell(restrictionObj: 0xABCDu));

        var from = new Vector3(0.1f, 0f, 0.2f);
        var to = new Vector3(0.7f, 0f, 0.2f);
        var t = BSPStepUpFixtures.MakeGroundedTransition(from, to, cellId: CellId);
        t.ObjectInfo.State |= ObjectInfoState.IsPlayer;

        t.FindTransitionalPosition(engine);

        Assert.True(
            System.MathF.Abs(t.SpherePath.CurPos.X - from.X) < 1e-4f,
            $"Restricted cell must halt the player at the origin; CurPos.X={t.SpherePath.CurPos.X:F4}");
    }

    [Fact]
    public void EndToEnd_RestrictedCell_PlayerCanBypass_TransitionReachesTarget()
    {
        var engine = MakeEngine(MakeIndoorCell(restrictionObj: 0xABCDu));

        var from = new Vector3(0.1f, 0f, 0.2f);
        var to = new Vector3(0.7f, 0f, 0.2f);
        var t = BSPStepUpFixtures.MakeGroundedTransition(from, to, cellId: CellId);
        t.ObjectInfo.State |= ObjectInfoState.IsPlayer | ObjectInfoState.CanBypassMoveRestrictions;

        t.FindTransitionalPosition(engine);

        Assert.True(
            System.MathF.Abs(t.SpherePath.CurPos.X - to.X) < 1e-4f,
            $"An admin/bypass-flagged player must pass through unimpeded; CurPos.X={t.SpherePath.CurPos.X:F4}");
    }

    [Fact]
    public void EndToEnd_OrdinaryCell_NoRestrictionObj_PlayerUnaffected()
    {
        var engine = MakeEngine(MakeIndoorCell(restrictionObj: 0u));

        var from = new Vector3(0.1f, 0f, 0.2f);
        var to = new Vector3(0.7f, 0f, 0.2f);
        var t = BSPStepUpFixtures.MakeGroundedTransition(from, to, cellId: CellId);
        t.ObjectInfo.State |= ObjectInfoState.IsPlayer;

        t.FindTransitionalPosition(engine);

        Assert.True(
            System.MathF.Abs(t.SpherePath.CurPos.X - to.X) < 1e-4f,
            $"Ordinary cell must be a complete no-op; CurPos.X={t.SpherePath.CurPos.X:F4}");
    }


    [Fact]
    public void EndToEnd_RestrictedCell_MoverIsResolvedOwner_TransitionReachesTarget()
    {
        var engine = MakeEngine(MakeIndoorCell(restrictionObj: RestrictionObjGuid));
        engine.Objects = MakeObjectsWithRestrictionObject(
            houseOwnerId: MoverGuid,
            restrictions: new HouseRestrictionRecord(
                OpenToPublic: false,
                AllegianceMonarchId: 0,
                Guests: new Dictionary<uint, uint>()));

        var from = new Vector3(0.1f, 0f, 0.2f);
        var to = new Vector3(0.7f, 0f, 0.2f);
        var t = BSPStepUpFixtures.MakeGroundedTransition(from, to, cellId: CellId);
        t.ObjectInfo.State |= ObjectInfoState.IsPlayer;
        t.ObjectInfo.SelfEntityId = MoverGuid;

        t.FindTransitionalPosition(engine);

        Assert.True(
            System.MathF.Abs(t.SpherePath.CurPos.X - to.X) < 1e-4f,
            $"The house's own owner must pass through unimpeded; CurPos.X={t.SpherePath.CurPos.X:F4}");
    }

    [Fact]
    public void EndToEnd_RestrictedCell_MoverIsResolvedNonGuest_TransitionHaltsAtOrigin()
    {
        var engine = MakeEngine(MakeIndoorCell(restrictionObj: RestrictionObjGuid));
        engine.Objects = MakeObjectsWithRestrictionObject(
            houseOwnerId: 0x50000099u,
            restrictions: new HouseRestrictionRecord(
                OpenToPublic: false,
                AllegianceMonarchId: 0,
                Guests: new Dictionary<uint, uint> { [0x50000002u] = 0u }));

        var from = new Vector3(0.1f, 0f, 0.2f);
        var to = new Vector3(0.7f, 0f, 0.2f);
        var t = BSPStepUpFixtures.MakeGroundedTransition(from, to, cellId: CellId);
        t.ObjectInfo.State |= ObjectInfoState.IsPlayer;
        t.ObjectInfo.SelfEntityId = MoverGuid;

        t.FindTransitionalPosition(engine);

        Assert.True(
            System.MathF.Abs(t.SpherePath.CurPos.X - from.X) < 1e-4f,
            $"A resolved house that excludes this mover must still halt them; CurPos.X={t.SpherePath.CurPos.X:F4}");
    }
}
