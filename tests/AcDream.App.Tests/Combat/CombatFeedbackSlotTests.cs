using AcDream.App.Combat;

namespace AcDream.App.Tests.Combat;

public sealed class CombatFeedbackSlotTests
{
    [Fact]
    public void ExpectedOwnerUnbindCannotClearReplacement()
    {
        var slot = new CombatFeedbackSlot();
        List<string> first = [];
        List<string> second = [];
        Action<string> firstTarget = first.Add;
        Action<string> secondTarget = second.Add;

        slot.Bind(firstTarget);
        slot.Unbind(secondTarget);
        slot.Show("first");
        Assert.Equal(["first"], first);
        Assert.Empty(second);

        slot.Unbind(firstTarget);
        slot.Bind(secondTarget);
        slot.Show("second");
        Assert.Equal(["second"], second);
    }

    [Fact]
    public void RebindingADifferentTargetWhileBoundIsRejected()
    {
        var slot = new CombatFeedbackSlot();
        slot.Bind(_ => { });

        Assert.Throws<InvalidOperationException>(() => slot.Bind(_ => { }));
    }

    [Fact]
    public void SessionOwnedBindingForwardsAndItsDisposalRestoresSilence()
    {
        var slot = new CombatFeedbackSlot();
        List<string> sink = [];

        IDisposable binding = slot.BindOwned(sink.Add);
        slot.Show("routed");
        Assert.Equal(["routed"], sink);

        binding.Dispose();
        slot.Show("after-teardown");
        Assert.Equal(["routed"], sink);
    }

    [Fact]
    public void SessionOwnedBindingRefusesASecondOwner()
    {
        var slot = new CombatFeedbackSlot();
        using IDisposable binding = slot.BindOwned(_ => { });

        Assert.Throws<InvalidOperationException>(() => slot.BindOwned(_ => { }));
    }

    [Fact]
    public void AnUnboundSlotDropsItsMessages()
    {
        var slot = new CombatFeedbackSlot();

        slot.Show("dropped");
    }
}
