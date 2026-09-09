using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public sealed class MissileCollisionRulesTests
{
    private const uint Target = 0x1111u;
    private const uint Other = 0x2222u;

    private static ObjectInfo Missile(uint targetId = 0) => new()
    {
        MoverPhysicsState = PhysicsStateFlags.Missile,
        TargetId = targetId,
    };

    [Fact]
    public void OtherMissile_IsAlwaysIgnored_EvenWhenDesignated()
    {
        var mover = Missile(Target);
        Assert.True(mover.MissileIgnore(
            Target,
            (uint)PhysicsStateFlags.Missile,
            EntityCollisionFlags.HasWeenie));
    }

    [Fact]
    public void NonMissileMover_DoesNotUseMissileExemptions()
    {
        var mover = new ObjectInfo();
        Assert.False(mover.MissileIgnore(
            Other,
            (uint)PhysicsStateFlags.Ethereal,
            EntityCollisionFlags.HasWeenie | EntityCollisionFlags.IsCreature));
    }

    [Fact]
    public void DesignatedTarget_IsNeverIgnoredByLaterBranches()
    {
        var mover = Missile(Target);
        Assert.False(mover.MissileIgnore(
            Target,
            (uint)PhysicsStateFlags.Ethereal,
            EntityCollisionFlags.HasWeenie | EntityCollisionFlags.IsCreature));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void EtherealTarget_RequiresWeenieMetadata(bool hasWeenie, bool ignored)
    {
        var flags = hasWeenie
            ? EntityCollisionFlags.HasWeenie
            : EntityCollisionFlags.None;

        Assert.Equal(ignored, Missile().MissileIgnore(
            Other,
            (uint)PhysicsStateFlags.Ethereal,
            flags));
    }

    [Fact]
    public void KnownTarget_IgnoresOtherCreatures()
    {
        var mover = Missile(Target);
        Assert.True(mover.MissileIgnore(
            Other,
            0u,
            EntityCollisionFlags.HasWeenie | EntityCollisionFlags.IsCreature));
    }

    [Fact]
    public void UnknownTarget_DoesNotIgnoreCreatures()
    {
        Assert.False(Missile().MissileIgnore(
            Other,
            0u,
            EntityCollisionFlags.HasWeenie | EntityCollisionFlags.IsCreature));
    }

    [Fact]
    public void KnownTarget_DoesNotIgnoreNonCreatureWeenie()
    {
        Assert.False(Missile(Target).MissileIgnore(
            Other,
            0u,
            EntityCollisionFlags.HasWeenie));
    }
}
