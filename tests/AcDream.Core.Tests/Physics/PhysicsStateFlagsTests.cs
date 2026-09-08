using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public sealed class PhysicsStateFlagsTests
{
    [Theory]
    [InlineData(PhysicsStateFlags.Static, 0x00000001u)]
    [InlineData(PhysicsStateFlags.Ethereal, 0x00000004u)]
    [InlineData(PhysicsStateFlags.ReportCollisions, 0x00000008u)]
    [InlineData(PhysicsStateFlags.IgnoreCollisions, 0x00000010u)]
    [InlineData(PhysicsStateFlags.NoDraw, 0x00000020u)]
    [InlineData(PhysicsStateFlags.Missile, 0x00000040u)]
    [InlineData(PhysicsStateFlags.Pushable, 0x00000080u)]
    [InlineData(PhysicsStateFlags.AlignPath, 0x00000100u)]
    [InlineData(PhysicsStateFlags.PathClipped, 0x00000200u)]
    [InlineData(PhysicsStateFlags.Gravity, 0x00000400u)]
    [InlineData(PhysicsStateFlags.Lighting, 0x00000800u)]
    [InlineData(PhysicsStateFlags.ParticleEmitter, 0x00001000u)]
    [InlineData(PhysicsStateFlags.Hidden, 0x00004000u)]
    [InlineData(PhysicsStateFlags.ScriptedCollision, 0x00008000u)]
    [InlineData(PhysicsStateFlags.HasPhysicsBsp, 0x00010000u)]
    [InlineData(PhysicsStateFlags.Inelastic, 0x00020000u)]
    [InlineData(PhysicsStateFlags.HasDefaultAnim, 0x00040000u)]
    [InlineData(PhysicsStateFlags.HasDefaultScript, 0x00080000u)]
    [InlineData(PhysicsStateFlags.Cloaked, 0x00100000u)]
    [InlineData(PhysicsStateFlags.ReportAsEnvironment, 0x00200000u)]
    [InlineData(PhysicsStateFlags.EdgeSlide, 0x00400000u)]
    [InlineData(PhysicsStateFlags.Sledding, 0x00800000u)]
    [InlineData(PhysicsStateFlags.Frozen, 0x01000000u)]
    public void ValuesMatchRetailPhysicsStateEnum(PhysicsStateFlags flag, uint expected) =>
        Assert.Equal(expected, (uint)flag);
}
