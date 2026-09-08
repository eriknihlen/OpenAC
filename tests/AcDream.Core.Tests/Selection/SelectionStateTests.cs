using AcDream.Core.Selection;
using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Selection;

public sealed class SelectionStateTests
{
    [Fact]
    public void ResetClearsCurrentAndSelectionHistoryForNextSession()
    {
        var state = new SelectionState();
        state.Select(0x50000001u, SelectionChangeSource.World);
        state.Select(0x50000002u, SelectionChangeSource.World);
        SelectionTransition transition = default;
        state.Changed += value => transition = value;

        Assert.True(state.Reset());

        Assert.Null(state.SelectedObjectId);
        Assert.Null(state.PreviousObjectId);
        Assert.Null(state.PreviousValidObjectId);
        Assert.Equal(0x50000002u, transition.PreviousObjectId);
        Assert.Equal(SelectionChangeReason.SessionReset, transition.Reason);
        Assert.False(state.SelectPrevious());
    }

    [Fact]
    public void Reset_RetryRepublishesAndOneObserverCannotStarveAnother()
    {
        var state = new SelectionState();
        state.Select(0x50000001u, SelectionChangeSource.World);
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
            Assert.Equal(SelectionChangeReason.SessionReset, transition.Reason);
            delivered++;
        };

        Assert.Throws<AggregateException>(() => state.Reset());
        Assert.Equal(1, delivered);
        Assert.False(state.Reset());
        Assert.Equal(2, delivered);
    }

    [Fact]
    public void Select_CommitsOneTransitionAndTracksPreviousLikeRetail()
    {
        var state = new SelectionState();
        var transitions = new List<SelectionTransition>();
        state.Changed += transitions.Add;

        Assert.True(state.Select(0x50000001u, SelectionChangeSource.World));
        Assert.True(state.Select(0x50000002u, SelectionChangeSource.Radar));

        Assert.Equal(0x50000002u, state.SelectedObjectId);
        Assert.Equal(0x50000001u, state.PreviousObjectId);
        Assert.Equal(0x50000001u, state.PreviousValidObjectId);
        Assert.Equal(2, transitions.Count);
        Assert.Equal(SelectionChangeSource.Radar, transitions[1].Source);
        Assert.Equal(SelectionChangeReason.Selected, transitions[1].Reason);
    }

    [Fact]
    public void Select_SameObject_IsDeduplicated()
    {
        var state = new SelectionState();
        int changed = 0;
        state.Changed += _ => changed++;

        Assert.True(state.Select(0x50000001u, SelectionChangeSource.World));
        Assert.False(state.Select(0x50000001u, SelectionChangeSource.Inventory));

        Assert.Equal(1, changed);
        Assert.Null(state.PreviousObjectId);
    }

    [Fact]
    public void SelectPrevious_SwapsCurrentAndPrevious()
    {
        var state = new SelectionState();
        state.Select(0x50000001u, SelectionChangeSource.World);
        state.Select(0x50000002u, SelectionChangeSource.World);

        Assert.True(state.SelectPrevious());

        Assert.Equal(0x50000001u, state.SelectedObjectId);
        Assert.Equal(0x50000002u, state.PreviousObjectId);
    }

    [Fact]
    public void ClearRemoved_PreservesRemovedObjectAsPreviousValid()
    {
        var state = new SelectionState();
        SelectionTransition transition = default;
        state.Changed += value => transition = value;
        state.Select(0x50000001u, SelectionChangeSource.World);

        Assert.True(state.Clear(
            SelectionChangeSource.System,
            SelectionChangeReason.SelectedObjectRemoved));

        Assert.Null(state.SelectedObjectId);
        Assert.Equal(0x50000001u, state.PreviousObjectId);
        Assert.Equal(0x50000001u, state.PreviousValidObjectId);
        Assert.Equal(SelectionChangeReason.SelectedObjectRemoved, transition.Reason);
    }

    [Fact]
    public void PluginService_UsesSameStateAndEventStream()
    {
        var state = new SelectionState();
        ISelectionService plugin = state;
        SelectionChangedEvent change = default;
        plugin.Changed += value => change = value;

        Assert.True(plugin.Select(0x50000001u));

        Assert.Equal(0x50000001u, state.SelectedObjectId);
        Assert.Equal(0x50000001u, plugin.SelectedObjectId);
        Assert.Null(change.PreviousObjectId);
        Assert.Equal(0x50000001u, change.SelectedObjectId);
    }

    [Fact]
    public void PluginListenerFailure_DoesNotEscapeOrBlockOtherPlugins()
    {
        var state = new SelectionState();
        ISelectionService plugin = state;
        int delivered = 0;
        plugin.Changed += _ => throw new InvalidOperationException("plugin failure");
        plugin.Changed += _ => delivered++;

        Assert.Null(Record.Exception(() => plugin.Select(0x50000001u)));
        Assert.Equal(1, delivered);
        Assert.Equal(0x50000001u, state.SelectedObjectId);
    }
}
