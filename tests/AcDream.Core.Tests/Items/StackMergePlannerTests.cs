using AcDream.Core.Items;

namespace AcDream.Core.Tests.Items;

public sealed class StackMergePlannerTests
{
    [Fact]
    public void MatchingStacks_TransfersRequestedAmount()
    {
        StackMergePlan? plan = StackMergePlanner.Plan(
            Item(1, size: 40), Item(2, size: 20),
            readyForInventoryRequest: true, requestedAmount: 15);

        Assert.Equal(new StackMergePlan(1, 2, 15), plan);
    }

    [Fact]
    public void Transfer_IsLimitedByDestinationCapacity()
    {
        StackMergePlan? plan = StackMergePlanner.Plan(
            Item(1, size: 40), Item(2, size: 95),
            readyForInventoryRequest: true, requestedAmount: 40);

        Assert.Equal(new StackMergePlan(1, 2, 5), plan);
    }

    [Fact]
    public void ZeroRequestedAmount_UsesWholeSourceStack()
    {
        StackMergePlan? plan = StackMergePlanner.Plan(
            Item(1, size: 7), Item(2, size: 20),
            readyForInventoryRequest: true, requestedAmount: 0);

        Assert.Equal(new StackMergePlan(1, 2, 7), plan);
    }

    [Theory]
    [InlineData(false, 10u, 10, 0, 100, 100)]
    [InlineData(true, 11u, 10, 0, 100, 100)]
    [InlineData(true, 10u, 10, 1, 100, 100)]
    [InlineData(true, 10u, 10, 0, 1, 100)]
    [InlineData(true, 10u, 10, 0, 100, 1)]
    [InlineData(true, 10u, 100, 0, 100, 100)]
    public void IllegalMerge_ReturnsNoPlan(
        bool ready,
        uint targetWcid,
        int targetSize,
        int sourceTradeState,
        int sourceMax,
        int targetMax)
    {
        var source = new StackMergeItem(1, 10, 10, sourceMax, sourceTradeState);
        var target = new StackMergeItem(2, targetWcid, targetSize, targetMax, 0);

        Assert.Null(StackMergePlanner.Plan(source, target, ready, requestedAmount: 10));
    }

    private static StackMergeItem Item(uint id, int size)
        => new(id, WeenieClassId: 10, StackSize: size, MaxStackSize: 100, TradeState: 0);
}
