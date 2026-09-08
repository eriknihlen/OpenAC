using AcDream.Core.Chat;

namespace AcDream.Core.Tests.Chat;

public sealed class SpewBoxStateTests
{
    [Fact]
    public void Enqueue_DoesNotBecomeVisibleUntilTick()
    {
        var state = new SpewBoxState();
        state.Enqueue("You can't jump while in the air");

        Assert.Equal(0, state.Count);
        Assert.Empty(state.Snapshot());
    }

    [Fact]
    public void Tick_DrainsPendingIntoVisible()
    {
        var state = new SpewBoxState();
        state.Enqueue("You can't jump while in the air");

        state.Tick(nowSeconds: 0d);

        Assert.Equal(1, state.Count);
        SpewBoxEntry entry = Assert.Single(state.Snapshot());
        Assert.Equal("You can't jump while in the air", entry.Text);
    }

    [Fact]
    public void Tick_InsertsNewestAtIndexZero()
    {
        var state = new SpewBoxState();
        state.Enqueue("first");
        state.Tick(0d);
        state.Enqueue("second");
        state.Tick(0d);

        Assert.Equal(2, state.Count);
        SpewBoxEntry[] snapshot = state.Snapshot();
        Assert.Equal("second", snapshot[0].Text);
        Assert.Equal("first", snapshot[1].Text);
    }

    [Fact]
    public void Tick_IdenticalRepeat_RefreshesInPlace_DoesNotStack()
    {
        var state = new SpewBoxState();
        state.Enqueue("You are too encumbered to carry that!");
        state.Tick(0d);
        state.Enqueue("You are too encumbered to carry that!");
        state.Tick(1d);

        Assert.Equal(1, state.Count);
        SpewBoxEntry entry = Assert.Single(state.Snapshot());
        Assert.Equal("You are too encumbered to carry that!", entry.Text);
        Assert.Equal(1d + SpewBoxState.DefaultLifetime.TotalSeconds, entry.ExpiresAtSeconds);
    }

    [Fact]
    public void Tick_DifferentText_DoesNotDedupe()
    {
        var state = new SpewBoxState();
        state.Enqueue("first message");
        state.Tick(0d);
        state.Enqueue("second message");
        state.Tick(0d);

        Assert.Equal(2, state.Count);
        SpewBoxEntry[] snapshot = state.Snapshot();
        Assert.Equal("second message", snapshot[0].Text);
        Assert.Equal("first message", snapshot[1].Text);
    }

    [Fact]
    public void Tick_Overflow_DropsOldest_RespectingMaxConcurrentItems()
    {
        var state = new SpewBoxState();
        Assert.Equal(4, SpewBoxState.MaxConcurrentItems);

        for (int i = 0; i < SpewBoxState.MaxConcurrentItems + 1; i++)
        {
            state.Enqueue($"line {i}");
            state.Tick(0d);
        }

        Assert.Equal(SpewBoxState.MaxConcurrentItems, state.Count);
        SpewBoxEntry[] snapshot = state.Snapshot();
        // Newest at index 0; "line 0" (the oldest) dropped by overflow.
        Assert.Equal("line 4", snapshot[0].Text);
        Assert.Equal("line 1", snapshot[3].Text);
        Assert.DoesNotContain(snapshot, e => e.Text == "line 0");
    }

    [Fact]
    public void Tick_PrunesExpiredEntries()
    {
        var state = new SpewBoxState();
        state.Enqueue("fading message");
        state.Tick(nowSeconds: 0d);
        Assert.Equal(1, state.Count);

        double justPastExpiry = SpewBoxState.DefaultLifetime.TotalSeconds + 0.001;
        state.Tick(justPastExpiry);

        Assert.Equal(0, state.Count);
        Assert.Empty(state.Snapshot());
    }

    [Fact]
    public void Tick_NoPendingNoExpired_RevisionUnchanged()
    {
        var state = new SpewBoxState();
        state.Enqueue("stays visible a while");
        state.Tick(0d);
        long revisionAfterFirstTick = state.Revision;

        // Well within the lifetime window, nothing pending — a second Tick
        // should be a pure no-op.
        state.Tick(0.5d);

        Assert.Equal(revisionAfterFirstTick, state.Revision);
    }

    [Fact]
    public void Reset_ClearsPendingAndVisible()
    {
        var state = new SpewBoxState();
        state.Enqueue("pending, never ticked");
        state.Enqueue("about to be visible");
        state.Tick(0d);
        Assert.True(state.Count > 0);

        state.Reset();

        Assert.Equal(0, state.Count);
        Assert.Empty(state.Snapshot());

        // A Reset while text was still pending (never ticked) must also
        // discard the pending queue — ticking afterward shows nothing.
        state.Tick(1d);
        Assert.Equal(0, state.Count);
    }

    [Fact]
    public void Revision_AdvancesOnTickThatChangesVisibleSet()
    {
        var state = new SpewBoxState();
        long initial = state.Revision;

        state.Enqueue("a line");
        state.Tick(0d);

        Assert.True(state.Revision > initial);
    }
}
