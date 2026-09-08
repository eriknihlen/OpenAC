using AcDream.Core.Physics;

namespace AcDream.Core.Tests.Physics;

public sealed class RetailPhysicsStateTransitionTests
{
    [Fact]
    public void ConstructorState_MatchesRetailAndIsNotHidden()
    {
        Assert.Equal(0x00400C08u, (uint)RetailPhysicsStateTransitions.ConstructorState);
        Assert.Equal(
            PhysicsStateFlags.None,
            RetailPhysicsStateTransitions.ConstructorState & PhysicsStateFlags.Hidden);
    }

    [Fact]
    public void VisibleToHidden_PlaysHiddenShapeAndRewritesCollisionBits()
    {
        PhysicsStateFlags requested = PhysicsStateFlags.Hidden
            | PhysicsStateFlags.ReportCollisions
            | PhysicsStateFlags.Lighting;

        RetailPhysicsStateTransition result = RetailPhysicsStateTransitions.Apply(
            RetailPhysicsStateTransitions.ConstructorState,
            requested);

        Assert.Equal(RetailHiddenTransition.BecameHidden, result.HiddenTransition);
        Assert.False(result.LightingChanged);
        Assert.Equal(PhysicsStateFlags.None, result.FinalState & PhysicsStateFlags.ReportCollisions);
        Assert.NotEqual(PhysicsStateFlags.None, result.FinalState & PhysicsStateFlags.IgnoreCollisions);
        Assert.NotEqual(PhysicsStateFlags.None, result.FinalState & PhysicsStateFlags.Hidden);
    }

    [Fact]
    public void HiddenToVisible_RestoresReportCollisionsEvenWhenWireOmitsIt()
    {
        PhysicsStateFlags hidden = PhysicsStateFlags.Hidden
            | PhysicsStateFlags.IgnoreCollisions;

        RetailPhysicsStateTransition result = RetailPhysicsStateTransitions.Apply(
            hidden,
            PhysicsStateFlags.None);

        Assert.Equal(RetailHiddenTransition.BecameVisible, result.HiddenTransition);
        Assert.Equal(PhysicsStateFlags.None, result.FinalState & PhysicsStateFlags.Hidden);
        Assert.Equal(PhysicsStateFlags.None, result.FinalState & PhysicsStateFlags.IgnoreCollisions);
        Assert.NotEqual(PhysicsStateFlags.None, result.FinalState & PhysicsStateFlags.ReportCollisions);
    }

    [Fact]
    public void IdenticalHiddenState_DoesNotReplayTransition()
    {
        PhysicsStateFlags state = PhysicsStateFlags.Hidden
            | PhysicsStateFlags.IgnoreCollisions;

        RetailPhysicsStateTransition result = RetailPhysicsStateTransitions.Apply(state, state);

        Assert.Equal(RetailHiddenTransition.None, result.HiddenTransition);
        Assert.False(result.LightingChanged);
        Assert.False(result.NoDrawChanged);
        Assert.Equal(state, result.FinalState);
    }

    [Fact]
    public void LightingNoDrawAndHiddenChangeFlags_AreDerivedFromLowWord()
    {
        RetailPhysicsStateTransition result = RetailPhysicsStateTransitions.Apply(
            PhysicsStateFlags.None,
            PhysicsStateFlags.Lighting
            | PhysicsStateFlags.NoDraw
            | PhysicsStateFlags.Hidden);

        Assert.True(result.LightingChanged);
        Assert.True(result.NoDrawChanged);
        Assert.Equal(RetailHiddenTransition.BecameHidden, result.HiddenTransition);
    }
}
