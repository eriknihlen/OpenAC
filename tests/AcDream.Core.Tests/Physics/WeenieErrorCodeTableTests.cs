using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public sealed class WeenieErrorCodeTableTests
{
    [Fact]
    public void None_Is0x00()
        => Assert.Equal(0x00u, (uint)WeenieError.None);

    [Fact]
    public void NoPhysicsObject_Is0x08()
        => Assert.Equal(0x08u, (uint)WeenieError.NoPhysicsObject);

    [Fact]
    public void NoMotionInterpreter_Is0x0B()
        => Assert.Equal(0x0Bu, (uint)WeenieError.NoMotionInterpreter);

    [Fact]
    public void NotGrounded_Is0x24()
        => Assert.Equal(0x24u, (uint)WeenieError.NotGrounded);

    [Fact]
    public void CrouchInCombatStance_Is0x3f()
        => Assert.Equal(0x3fu, (uint)WeenieError.CrouchInCombatStance);

    [Fact]
    public void SitInCombatStance_Is0x40()
        => Assert.Equal(0x40u, (uint)WeenieError.SitInCombatStance);

    [Fact]
    public void SleepInCombatStance_Is0x41()
        => Assert.Equal(0x41u, (uint)WeenieError.SleepInCombatStance);

    [Fact]
    public void ChatEmoteOutsideNonCombat_Is0x42()
        => Assert.Equal(0x42u, (uint)WeenieError.ChatEmoteOutsideNonCombat);

    [Fact]
    public void ActionCancelled_Is0x36()
        => Assert.Equal(0x36u, (uint)WeenieError.ActionCancelled);

    [Fact]
    public void ObjectGone_Is0x37()
        => Assert.Equal(0x37u, (uint)WeenieError.ObjectGone);

    [Fact]
    public void NoObject_Is0x38()
        => Assert.Equal(0x38u, (uint)WeenieError.NoObject);

    [Fact]
    public void ActionDepthExceeded_Is0x45()
        => Assert.Equal(0x45u, (uint)WeenieError.ActionDepthExceeded);

    [Fact]
    public void GeneralMovementFailure_Is0x47()
        => Assert.Equal(0x47u, (uint)WeenieError.GeneralMovementFailure);

    [Fact]
    public void YouCantJumpFromThisPosition_Is0x48()
        => Assert.Equal(0x48u, (uint)WeenieError.YouCantJumpFromThisPosition);

    [Fact]
    public void CantJumpLoadedDown_Is0x49()
        => Assert.Equal(0x49u, (uint)WeenieError.CantJumpLoadedDown);

    [Fact]
    public void YouChargedTooFar_Is0x3D()
        => Assert.Equal(0x3Du, (uint)WeenieError.YouChargedTooFar);

    [Theory]
    [InlineData(WeenieError.None, 0x00u)]
    [InlineData(WeenieError.NoPhysicsObject, 0x08u)]
    [InlineData(WeenieError.NoMotionInterpreter, 0x0Bu)]
    [InlineData(WeenieError.NotGrounded, 0x24u)]
    [InlineData(WeenieError.ActionCancelled, 0x36u)]
    [InlineData(WeenieError.ObjectGone, 0x37u)]
    [InlineData(WeenieError.NoObject, 0x38u)]
    [InlineData(WeenieError.CrouchInCombatStance, 0x3fu)]
    [InlineData(WeenieError.SitInCombatStance, 0x40u)]
    [InlineData(WeenieError.SleepInCombatStance, 0x41u)]
    [InlineData(WeenieError.ChatEmoteOutsideNonCombat, 0x42u)]
    [InlineData(WeenieError.ActionDepthExceeded, 0x45u)]
    [InlineData(WeenieError.GeneralMovementFailure, 0x47u)]
    [InlineData(WeenieError.YouCantJumpFromThisPosition, 0x48u)]
    [InlineData(WeenieError.CantJumpLoadedDown, 0x49u)]
    [InlineData(WeenieError.YouChargedTooFar, 0x3Du)]
    public void A10Table_EveryCode_MatchesRetailNumericValue(WeenieError code, uint expected)
        => Assert.Equal(expected, (uint)code);
}
