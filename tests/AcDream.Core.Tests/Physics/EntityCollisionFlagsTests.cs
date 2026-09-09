using AcDream.Core.Items;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class EntityCollisionFlagsTests
{
    [Fact]
    public void FromPwdBitfield_AllZeros_NoFlags()
    {
        Assert.Equal(EntityCollisionFlags.None, EntityCollisionFlagsExt.FromPwdBitfield(0u));
    }

    [Fact]
    public void FromPwdBitfield_PlayerBit_SetsIsPlayer()
    {
        // BF_PLAYER = 0x8 (bit 3)
        var flags = EntityCollisionFlagsExt.FromPwdBitfield(0x8u);
        Assert.True(flags.HasFlag(EntityCollisionFlags.IsPlayer));
        Assert.False(flags.HasFlag(EntityCollisionFlags.IsPK));
    }

    [Fact]
    public void FromPwdBitfield_PlayerKillerBit_SetsIsPK()
    {
        // BF_PLAYER_KILLER = 0x20 (bit 5)
        var flags = EntityCollisionFlagsExt.FromPwdBitfield(0x20u);
        Assert.True(flags.HasFlag(EntityCollisionFlags.IsPK));
        Assert.False(flags.HasFlag(EntityCollisionFlags.IsPKLite));
    }

    [Fact]
    public void FromPwdBitfield_PkLiteBit_SetsIsPKLite()
    {
        var flags = EntityCollisionFlagsExt.FromPwdBitfield(0x2000000u);
        Assert.True(flags.HasFlag(EntityCollisionFlags.IsPKLite));
        Assert.False(flags.HasFlag(EntityCollisionFlags.IsPK));
    }

    [Fact]
    public void FromPwdBitfield_FreePkStatusBit_SetsIsImpenetrable()
    {
        var flags = EntityCollisionFlagsExt.FromPwdBitfield(0x200000u);
        Assert.True(flags.HasFlag(EntityCollisionFlags.IsImpenetrable));
    }

    [Fact]
    public void FromPwdBitfield_PlayerAndPK_SetsBoth()
    {
        // A PK player: BF_PLAYER (0x8) | BF_PLAYER_KILLER (0x20) = 0x28
        var flags = EntityCollisionFlagsExt.FromPwdBitfield(0x28u);
        Assert.True(flags.HasFlag(EntityCollisionFlags.IsPlayer));
        Assert.True(flags.HasFlag(EntityCollisionFlags.IsPK));
        Assert.False(flags.HasFlag(EntityCollisionFlags.IsPKLite));
        Assert.False(flags.HasFlag(EntityCollisionFlags.IsImpenetrable));
    }

    [Fact]
    public void FromPwdBitfield_UnrelatedBits_Ignored()
    {
        var flags = EntityCollisionFlagsExt.FromPwdBitfield(0x1u | 0x2u | 0x4u | 0x10u);
        Assert.Equal(EntityCollisionFlags.None, flags);
    }


    [Fact]
    public void ToMoverState_None_ProducesNoneNotIsPlayer()
    {
        Assert.Equal(ObjectInfoState.None, EntityCollisionFlags.None.ToMoverState());
        Assert.Equal(
            ObjectInfoState.None,
            EntityCollisionFlags.IsPlayer.ToMoverState());
    }

    [Fact]
    public void ToMoverState_IsPK_TranslatesToObjectInfoStateIsPK()
    {
        Assert.Equal(
            ObjectInfoState.IsPK,
            EntityCollisionFlags.IsPK.ToMoverState());
    }

    [Fact]
    public void ToMoverState_IsPKLite_TranslatesToObjectInfoStateIsPKLite()
    {
        Assert.Equal(
            ObjectInfoState.IsPKLite,
            EntityCollisionFlags.IsPKLite.ToMoverState());
    }

    [Fact]
    public void ToMoverState_IsImpenetrable_TranslatesToObjectInfoStateIsImpenetrable()
    {
        Assert.Equal(
            ObjectInfoState.IsImpenetrable,
            EntityCollisionFlags.IsImpenetrable.ToMoverState());
    }

    [Fact]
    public void ToMoverState_AllThreeBits_TranslateIndependently()
    {
        var flags = EntityCollisionFlags.IsPlayer
            | EntityCollisionFlags.IsPK
            | EntityCollisionFlags.IsPKLite
            | EntityCollisionFlags.IsImpenetrable
            | EntityCollisionFlags.IsCreature
            | EntityCollisionFlags.HasWeenie;

        ObjectInfoState state = flags.ToMoverState();

        Assert.Equal(
            ObjectInfoState.IsPK | ObjectInfoState.IsPKLite | ObjectInfoState.IsImpenetrable,
            state);
    }

    [Fact]
    public void FromPwdBitfield_ThenToMoverState_PkPlayer_ProducesIsPKOnly()
    {
        // BF_PLAYER (0x8) | BF_PLAYER_KILLER (0x20).
        uint bitfield = 0x8u | 0x20u;
        ObjectInfoState moverState =
            EntityCollisionFlagsExt.FromPwdBitfield(bitfield).ToMoverState();

        Assert.Equal(ObjectInfoState.IsPK, moverState);
        Assert.False(moverState.HasFlag(ObjectInfoState.IsPlayer));
    }


    private const uint PkGuid = 0x50000101u;
    private const uint NonPkGuid = 0x50000102u;

    [Fact]
    public void ResolveMoverPvpState_NoRowOrZeroBitfield_IsNoneNotJustAbsentIsPlayer()
    {
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = NonPkGuid,
            PublicWeenieBitfield = 0u,
        });

        // No row registered at all for this guid.
        Assert.Equal(
            ObjectInfoState.None,
            EntityCollisionFlagsExt.ResolveMoverPvpState(objects, 0x50000999u));
        // A row exists, but PublicWeenieBitfield is 0 — no PK-relevant bits.
        Assert.Equal(
            ObjectInfoState.None,
            EntityCollisionFlagsExt.ResolveMoverPvpState(objects, NonPkGuid));
    }

    [Fact]
    public void ResolveMoverPvpState_NullBitfield_IsNone()
    {
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = NonPkGuid,
            PublicWeenieBitfield = null,
        });

        Assert.Equal(
            ObjectInfoState.None,
            EntityCollisionFlagsExt.ResolveMoverPvpState(objects, NonPkGuid));
    }

    [Fact]
    public void ResolveMoverPvpState_PkPlayerRow_ResolvesToIsPK()
    {
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = PkGuid,
            // BF_PLAYER (0x8) | BF_PLAYER_KILLER (0x20).
            PublicWeenieBitfield = 0x8u | 0x20u,
        });

        Assert.Equal(
            ObjectInfoState.IsPK,
            EntityCollisionFlagsExt.ResolveMoverPvpState(objects, PkGuid));
    }

    [Fact]
    public void PkVsPk_Collides_PkVsNonPk_staysExempt_ThroughRealTableLookup()
    {
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = PkGuid,
            PublicWeenieBitfield = 0x8u | 0x20u, // BF_PLAYER | BF_PLAYER_KILLER
        });
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = NonPkGuid,
            PublicWeenieBitfield = 0x8u, // BF_PLAYER only — no PK status
        });

        ObjectInfoState pkMoverState =
            ObjectInfoState.IsPlayer
            | EntityCollisionFlagsExt.ResolveMoverPvpState(objects, PkGuid);
        EntityCollisionFlags nonPkTargetFlags =
            EntityCollisionFlagsExt.FromPwdBitfield(
                objects.Get(NonPkGuid)!.PublicWeenieBitfield!.Value);
        EntityCollisionFlags pkTargetFlags =
            EntityCollisionFlagsExt.FromPwdBitfield(
                objects.Get(PkGuid)!.PublicWeenieBitfield!.Value);

        Assert.True(CollisionExemption.ShouldSkip(
            targetState: 0u,
            targetFlags: nonPkTargetFlags,
            moverState: pkMoverState));

        Assert.False(CollisionExemption.ShouldSkip(
            targetState: 0u,
            targetFlags: pkTargetFlags,
            moverState: pkMoverState));

        ObjectInfoState nonPkMoverState =
            ObjectInfoState.IsPlayer
            | EntityCollisionFlagsExt.ResolveMoverPvpState(objects, NonPkGuid);
        Assert.True(CollisionExemption.ShouldSkip(
            targetState: 0u,
            targetFlags: nonPkTargetFlags,
            moverState: nonPkMoverState));
    }
}
