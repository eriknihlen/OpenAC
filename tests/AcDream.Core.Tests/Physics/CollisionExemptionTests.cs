using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class CollisionExemptionTests
{
    private const uint ETHEREAL_PS = 0x4u;
    private const uint IGNORE_COLLISIONS_PS = 0x10u;

    [Fact]
    public void EtherealAndIgnoreCollisions_AlwaysSkipped()
    {
        // Target with both bits set is exempted from any mover.
        Assert.True(CollisionExemption.ShouldSkip(
            targetState: ETHEREAL_PS | IGNORE_COLLISIONS_PS,
            targetFlags: EntityCollisionFlags.None,
            moverState: ObjectInfoState.IsPlayer));
    }

    [Fact]
    public void EtherealOnly_NotInstantSkipped()
    {
        Assert.False(CollisionExemption.ShouldSkip(
            targetState: ETHEREAL_PS,
            targetFlags: EntityCollisionFlags.None,
            moverState: ObjectInfoState.IsPlayer));
    }

    [Fact]
    public void Viewer_VsCreature_Skipped()
    {
        Assert.True(CollisionExemption.ShouldSkip(
            targetState: 0u,
            targetFlags: EntityCollisionFlags.IsCreature,
            moverState: ObjectInfoState.IsViewer));
    }

    [Fact]
    public void Viewer_VsNonCreature_NotSkipped()
    {
        Assert.False(CollisionExemption.ShouldSkip(
            targetState: 0u,
            targetFlags: EntityCollisionFlags.None,
            moverState: ObjectInfoState.IsViewer));
    }

    [Fact]
    public void IgnoreCreatures_VsCreature_Skipped()
    {
        Assert.True(CollisionExemption.ShouldSkip(
            targetState: 0u,
            targetFlags: EntityCollisionFlags.IsCreature,
            moverState: ObjectInfoState.IgnoreCreatures));
    }

    [Fact]
    public void NonPkPlayer_VsNonPkPlayer_Skipped()
    {
        // The user-visible payoff: two ordinary players walk through each
        // other instead of blocking.
        Assert.True(CollisionExemption.ShouldSkip(
            targetState: 0u,
            targetFlags: EntityCollisionFlags.IsPlayer,
            moverState: ObjectInfoState.IsPlayer));
    }

    [Fact]
    public void Pk_VsPk_NotSkipped()
    {
        Assert.False(CollisionExemption.ShouldSkip(
            targetState: 0u,
            targetFlags: EntityCollisionFlags.IsPlayer | EntityCollisionFlags.IsPK,
            moverState: ObjectInfoState.IsPlayer | ObjectInfoState.IsPK));
    }

    [Fact]
    public void PkLite_VsPkLite_NotSkipped()
    {
        Assert.False(CollisionExemption.ShouldSkip(
            targetState: 0u,
            targetFlags: EntityCollisionFlags.IsPlayer | EntityCollisionFlags.IsPKLite,
            moverState: ObjectInfoState.IsPlayer | ObjectInfoState.IsPKLite));
    }

    [Fact]
    public void Pk_VsNonPk_Skipped()
    {
        Assert.True(CollisionExemption.ShouldSkip(
            targetState: 0u,
            targetFlags: EntityCollisionFlags.IsPlayer,
            moverState: ObjectInfoState.IsPlayer | ObjectInfoState.IsPK));
    }

    [Fact]
    public void Pk_VsPkLite_Skipped()
    {
        // PK and PKLite are different pools — pair doesn't match.
        Assert.True(CollisionExemption.ShouldSkip(
            targetState: 0u,
            targetFlags: EntityCollisionFlags.IsPlayer | EntityCollisionFlags.IsPKLite,
            moverState: ObjectInfoState.IsPlayer | ObjectInfoState.IsPK));
    }

    [Fact]
    public void ImpenetrableTarget_VsAnyPlayer_NotSkipped()
    {
        Assert.False(CollisionExemption.ShouldSkip(
            targetState: 0u,
            targetFlags: EntityCollisionFlags.IsPlayer | EntityCollisionFlags.IsImpenetrable,
            moverState: ObjectInfoState.IsPlayer));
    }

    [Fact]
    public void Player_VsCreature_NotSkipped()
    {
        Assert.False(CollisionExemption.ShouldSkip(
            targetState: 0u,
            targetFlags: EntityCollisionFlags.IsCreature,
            moverState: ObjectInfoState.IsPlayer));
    }

    [Fact]
    public void NonPlayerMover_VsPlayer_NotSkipped()
    {
        // PvP rule requires BOTH to be players. Mover is not a player
        // (e.g., dead-reckoned remote NPC) → no exemption applies.
        Assert.False(CollisionExemption.ShouldSkip(
            targetState: 0u,
            targetFlags: EntityCollisionFlags.IsPlayer,
            moverState: ObjectInfoState.None));
    }

    [Fact]
    public void ImpenetrableMover_VsOrdinaryPlayer_NotSkipped()
    {
        Assert.False(CollisionExemption.ShouldSkip(
            targetState: 0u,
            targetFlags: EntityCollisionFlags.IsPlayer,
            moverState: ObjectInfoState.IsPlayer | ObjectInfoState.IsImpenetrable));
    }

    [Fact]
    public void ImpenetrableMover_StillCollidesEvenWhenAlsoPk()
    {
        Assert.False(CollisionExemption.ShouldSkip(
            targetState: 0u,
            targetFlags: EntityCollisionFlags.IsPlayer,
            moverState: ObjectInfoState.IsPlayer
                | ObjectInfoState.IsImpenetrable
                | ObjectInfoState.IsPK));
    }
}
