using AcDream.Core.Chat;
using AcDream.UI.Abstractions.Panels.SpewBox;

namespace AcDream.UI.Abstractions.Tests.Panels.SpewBox;

public sealed class SpewBoxVMTests
{
    [Fact]
    public void Lines_NoContent_ReturnsEmpty()
    {
        var vm = new SpewBoxVM(new SpewBoxState());

        Assert.Empty(vm.Lines(0d));
        Assert.False(vm.HasVisibleLines);
    }

    [Fact]
    public void Lines_DrainsPendingAndReturnsRemainingLifetime()
    {
        var state = new SpewBoxState();
        var vm = new SpewBoxVM(state);
        state.Enqueue("You can't jump while in the air");

        IReadOnlyList<SpewBoxLine> lines = vm.Lines(nowSeconds: 10d);

        SpewBoxLine line = Assert.Single(lines);
        Assert.Equal("You can't jump while in the air", line.Text);
        Assert.Equal(SpewBoxState.DefaultLifetime.TotalSeconds, line.RemainingLifetimeSeconds, precision: 3);
        Assert.True(vm.HasVisibleLines);
    }

    [Fact]
    public void Lines_RemainingLifetime_CountsDownAndFloorsAtZero()
    {
        var state = new SpewBoxState();
        var vm = new SpewBoxVM(state);
        state.Enqueue("fading");

        vm.Lines(nowSeconds: 0d);
        double halfway = SpewBoxState.DefaultLifetime.TotalSeconds / 2;
        SpewBoxLine midLine = Assert.Single(vm.Lines(nowSeconds: halfway));
        Assert.Equal(halfway, midLine.RemainingLifetimeSeconds, precision: 3);

        Assert.Empty(vm.Lines(nowSeconds: SpewBoxState.DefaultLifetime.TotalSeconds + 1d));
    }

    [Fact]
    public void Lines_NewestFirst_MatchesRetailInsertAtZero()
    {
        var state = new SpewBoxState();
        var vm = new SpewBoxVM(state);
        state.Enqueue("older");
        vm.Lines(0d);
        state.Enqueue("newer");

        IReadOnlyList<SpewBoxLine> lines = vm.Lines(0d);

        Assert.Equal(2, lines.Count);
        Assert.Equal("newer", lines[0].Text);
        Assert.Equal("older", lines[1].Text);
    }

    [Fact]
    public void Revision_TracksUnderlyingState()
    {
        var state = new SpewBoxState();
        var vm = new SpewBoxVM(state);
        long initial = vm.Revision;

        state.Enqueue("a line");
        vm.Lines(0d);

        Assert.True(vm.Revision > initial);
        Assert.Equal(state.Revision, vm.Revision);
    }
}
