using AcDream.Core.Items;

namespace AcDream.Core.Tests.Items;

public sealed class ExternalContainerStateTests
{
    [Fact]
    public void ViewContents_onlyCommitsTheExpectedRoot()
    {
        var state = new ExternalContainerState();
        state.RequestOpen(0x70000001u);

        Assert.False(state.ApplyViewContents(0x70000002u));
        Assert.Equal(0u, state.CurrentContainerId);
        Assert.True(state.ApplyViewContents(0x70000001u));
        Assert.Equal(0x70000001u, state.CurrentContainerId);
    }

    [Fact]
    public void ReplacementRetiresOldImmediatelyThenOpensNewOnViewContents()
    {
        var state = new ExternalContainerState();
        var changes = new List<ExternalContainerTransition>();
        state.Changed += changes.Add;
        state.RequestOpen(1u);
        state.ApplyViewContents(1u);
        state.RequestOpen(2u);

        Assert.Equal(0u, state.CurrentContainerId);
        Assert.Equal(2u, state.RequestedContainerId);

        state.ApplyViewContents(2u);
        state.ApplyViewContents(2u);

        Assert.Equal(3, changes.Count);
        Assert.Equal(ExternalContainerTransitionKind.Opened, changes[0].Kind);
        Assert.Equal(
            new ExternalContainerTransition(
                ExternalContainerTransitionKind.ReplacementRequested, 1u, 2u),
            changes[1]);
        Assert.Equal(
            new ExternalContainerTransition(
                ExternalContainerTransitionKind.Opened, 0u, 2u),
            changes[2]);
    }

    [Fact]
    public void CloseIgnoresStaleContainerAndClosesCurrent()
    {
        var state = Open(2u);

        Assert.False(state.ApplyClose(1u));
        Assert.Equal(2u, state.CurrentContainerId);
        Assert.True(state.ApplyClose(2u));
        Assert.Equal(0u, state.CurrentContainerId);
        Assert.Equal(0u, state.RequestedContainerId);
    }

    [Fact]
    public void RefusedReplacementLeavesTheRetiredRootClosed()
    {
        var state = Open(1u);
        state.RequestOpen(2u);

        Assert.True(state.ApplyUseDone(0x550u));
        Assert.Equal(0u, state.CurrentContainerId);
        Assert.Equal(0u, state.RequestedContainerId);
        Assert.False(state.ApplyViewContents(2u));
    }

    [Fact]
    public void Reset_RetryRepublishesAndOneObserverCannotStarveAnother()
    {
        var state = Open(1u);
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
            Assert.Equal(ExternalContainerTransitionKind.Reset, transition.Kind);
            delivered++;
        };

        Assert.Throws<AggregateException>(() => state.Reset());
        Assert.Equal(1, delivered);
        Assert.False(state.Reset());
        Assert.Equal(2, delivered);
    }

    [Fact]
    public void OpenedCorpseHistoryMatchesRetailSetAndDeleteLifetime()
    {
        var state = new ExternalContainerState();
        const uint corpse = 0x70000010u;

        Assert.True(state.RequestOpen(corpse, isCorpse: true));
        Assert.True(state.HasCorpseBeenOpened(corpse));
        Assert.Equal(1, state.OpenedCorpseCount);

        state.ApplyViewContents(corpse);
        state.ApplyClose(corpse);
        Assert.True(state.HasCorpseBeenOpened(corpse));
        Assert.True(state.SetCorpseDeleted(corpse));
        Assert.False(state.HasCorpseBeenOpened(corpse));

        state.RequestOpen(corpse, isCorpse: true);
        Assert.True(state.Reset());
        Assert.False(state.HasCorpseBeenOpened(corpse));
        Assert.Equal(0, state.OpenedCorpseCount);
    }

    [Fact]
    public void RepeatedGroundObjectRequestStillRecordsCorpseIdentity()
    {
        var state = new ExternalContainerState();
        const uint corpse = 0x70000011u;

        Assert.True(state.RequestOpen(corpse));
        Assert.False(state.RequestOpen(corpse, isCorpse: true));

        Assert.True(state.HasCorpseBeenOpened(corpse));
    }

    private static ExternalContainerState Open(uint id)
    {
        var state = new ExternalContainerState();
        state.RequestOpen(id);
        state.ApplyViewContents(id);
        return state;
    }
}
