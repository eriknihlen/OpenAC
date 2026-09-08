using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class InteractionStateTests
{
    [Fact]
    public void Modes_HaveOneOwnerAndOneDeduplicatedTransitionStream()
    {
        var state = new InteractionState();
        var changes = new List<InteractionModeTransition>();
        state.Changed += changes.Add;

        Assert.True(state.EnterUse());
        Assert.False(state.EnterUse());
        Assert.True(state.EnterExamine());
        Assert.True(state.EnterUseItemOnTarget(0x100u));
        Assert.True(state.Clear());

        Assert.Equal(4, changes.Count);
        Assert.Equal(InteractionModeKind.None, state.Current.Kind);
        Assert.Equal(0x100u, changes[2].Current.SourceObjectId);
    }

    [Fact]
    public void UseItemOnTarget_RejectsMissingSource()
    {
        var state = new InteractionState();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => state.EnterUseItemOnTarget(0));
        Assert.Equal(InteractionMode.None, state.Current);
    }

    [Fact]
    public void ResetSession_RetryRepublishesAndOneObserverCannotStarveAnother()
    {
        var state = new InteractionState();
        state.EnterExamine();
        bool fail = true;
        int delivered = 0;
        state.Changed += _ =>
        {
            if (fail)
            {
                fail = false;
                throw new InvalidOperationException("transient");
            }
        };
        state.Changed += transition =>
        {
            Assert.Equal(InteractionMode.None, transition.Current);
            delivered++;
        };

        Assert.Throws<AggregateException>(state.ResetSession);
        Assert.Equal(1, delivered);
        state.ResetSession();
        Assert.Equal(2, delivered);
    }
}
