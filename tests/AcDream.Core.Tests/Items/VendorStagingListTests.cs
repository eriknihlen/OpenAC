using AcDream.Core.Items;

namespace AcDream.Core.Tests.Items;

public sealed class VendorStagingListTests
{
    private const uint ItemA = 0x60000101u;
    private const uint ItemB = 0x60000102u;

    [Fact]
    public void RemoveTailDropsTheNewestRowOutright()
    {
        var list = new VendorStagingList();
        int changes = 0;
        list.Add(ItemA, 5);
        list.Add(ItemB, 7);
        list.Changed += () => changes++;

        Assert.True(list.RemoveTail());

        VendorStagingEntry entry = Assert.Single(list.Entries);
        Assert.Equal(ItemA, entry.ItemGuid);
        Assert.Equal(5, entry.Quantity);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void RemoveTailOnAnEmptyListDoesNothing()
    {
        var list = new VendorStagingList();
        int changes = 0;
        list.Changed += () => changes++;

        Assert.False(list.RemoveTail());

        Assert.True(list.IsEmpty);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void AddAppendsANewEntry()
    {
        var list = new VendorStagingList();

        list.Add(ItemA, 5);

        VendorStagingEntry entry = Assert.Single(list.Entries);
        Assert.Equal(ItemA, entry.ItemGuid);
        Assert.Equal(5, entry.Quantity);
        Assert.False(list.IsEmpty);
    }

    // Add() is the Buying-tab path: repeated adds of a shop item accumulate
    // under the 5000 cap. Stage() is the Selling-tab path: see below.
    [Fact]
    public void AddingTheSameGuidTwiceAccumulatesRatherThanDuplicatingOrOverwriting()
    {
        var list = new VendorStagingList();

        list.Add(ItemA, 5);
        list.Add(ItemA, 20);

        VendorStagingEntry entry = Assert.Single(list.Entries);
        Assert.Equal(25, entry.Quantity);
    }

    [Fact]
    public void AddReturnsAddedOnASuccessfulStage()
    {
        var list = new VendorStagingList();

        Assert.Equal(VendorStagingAddOutcome.Added, list.Add(ItemA, 5));
        Assert.Equal(VendorStagingAddOutcome.Added, list.Add(ItemA, 5));
    }

    [Fact]
    public void AddAccumulatingPastTheCapIsRejectedAndLeavesTheEntryUnchanged()
    {
        var list = new VendorStagingList();
        list.Add(ItemA, VendorStagingList.MaxStagedQuantity - 10);

        VendorStagingAddOutcome outcome = list.Add(ItemA, 11);

        Assert.Equal(VendorStagingAddOutcome.Capped, outcome);
        VendorStagingEntry entry = Assert.Single(list.Entries);
        Assert.Equal(VendorStagingList.MaxStagedQuantity - 10, entry.Quantity);
    }

    [Fact]
    public void AddAccumulatingExactlyToTheCapSucceeds()
    {
        var list = new VendorStagingList();
        list.Add(ItemA, VendorStagingList.MaxStagedQuantity - 10);

        VendorStagingAddOutcome outcome = list.Add(ItemA, 10);

        Assert.Equal(VendorStagingAddOutcome.Added, outcome);
        VendorStagingEntry entry = Assert.Single(list.Entries);
        Assert.Equal(VendorStagingList.MaxStagedQuantity, entry.Quantity);
    }

    [Fact]
    public void AddOfABrandNewEntryHasNoCapEvenAboveTheThreshold()
    {
        var list = new VendorStagingList();

        VendorStagingAddOutcome outcome = list.Add(ItemA, VendorStagingList.MaxStagedQuantity + 500);

        Assert.Equal(VendorStagingAddOutcome.Added, outcome);
        VendorStagingEntry entry = Assert.Single(list.Entries);
        Assert.Equal(VendorStagingList.MaxStagedQuantity + 500, entry.Quantity);
    }

    [Theory]
    [InlineData(0u, 5)]
    [InlineData(ItemA, 0)]
    [InlineData(ItemA, -1)]
    public void AddIgnoresAZeroGuidOrNonPositiveQuantity(uint guid, int quantity)
    {
        var list = new VendorStagingList();

        VendorStagingAddOutcome outcome = list.Add(guid, quantity);

        Assert.Equal(VendorStagingAddOutcome.Ignored, outcome);
        Assert.True(list.IsEmpty);
    }

    [Fact]
    public void ChangedFiresOnAddAndNotOnANoOpAdd()
    {
        var list = new VendorStagingList();
        int fired = 0;
        list.Changed += () => fired++;

        list.Add(ItemA, 5);
        Assert.Equal(1, fired);

        list.Add(0u, 5); // no-op: zero guid
        Assert.Equal(1, fired);
    }


    [Fact]
    public void RemoveWithNegativeOneAmountRemovesTheWholeEntryRegardlessOfQuantity()
    {
        var list = new VendorStagingList();
        list.Add(ItemA, 100);

        bool removed = list.Remove(ItemA, -1);

        Assert.True(removed);
        Assert.True(list.IsEmpty);
    }

    [Fact]
    public void RemoveWithAnAmountAtOrAboveTheStagedQuantityRemovesTheWholeEntry()
    {
        var list = new VendorStagingList();
        list.Add(ItemA, 5);

        Assert.True(list.Remove(ItemA, 5));

        Assert.True(list.IsEmpty);
    }

    [Fact]
    public void RemoveWithAPartialAmountDecrementsInPlace()
    {
        var list = new VendorStagingList();
        list.Add(ItemA, 10);

        Assert.True(list.Remove(ItemA, 3));

        VendorStagingEntry entry = Assert.Single(list.Entries);
        Assert.Equal(ItemA, entry.ItemGuid);
        Assert.Equal(7, entry.Quantity);
    }

    [Fact]
    public void RemoveOfAnUnstagedGuidIsANoOp()
    {
        var list = new VendorStagingList();
        list.Add(ItemA, 5);

        bool removed = list.Remove(ItemB, -1);

        Assert.False(removed);
        Assert.Single(list.Entries);
    }

    [Fact]
    public void RemoveOnlyTouchesTheMatchingEntry()
    {
        var list = new VendorStagingList();
        list.Add(ItemA, 5);
        list.Add(ItemB, 9);

        list.Remove(ItemA, -1);

        VendorStagingEntry remaining = Assert.Single(list.Entries);
        Assert.Equal(ItemB, remaining.ItemGuid);
        Assert.Equal(9, remaining.Quantity);
    }

    [Fact]
    public void TryGetFindsAStagedEntryByGuid()
    {
        var list = new VendorStagingList();
        list.Add(ItemA, 5);

        Assert.True(list.TryGet(ItemA, out VendorStagingEntry entry));
        Assert.Equal(5, entry.Quantity);
        Assert.False(list.TryGet(ItemB, out _));
    }

    [Fact]
    public void ReplacePreservesTheSplitPlaceholderPositionAndQuantity()
    {
        var list = new VendorStagingList();
        list.Add(ItemA, 2);
        list.Add(ItemB, 9);
        const uint splitGuid = 0x60000003u;
        int fired = 0;
        list.Changed += () => fired++;

        Assert.True(list.Replace(ItemA, splitGuid));

        Assert.Equal(
            new[]
            {
                new VendorStagingEntry(splitGuid, 2),
                new VendorStagingEntry(ItemB, 9),
            },
            list.Entries);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void ClearRemovesEveryEntryAndFiresChangedOnce()
    {
        var list = new VendorStagingList();
        list.Add(ItemA, 5);
        list.Add(ItemB, 9);
        int fired = 0;
        list.Changed += () => fired++;

        list.Clear();

        Assert.True(list.IsEmpty);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void ClearOnAnAlreadyEmptyListDoesNotFireChanged()
    {
        var list = new VendorStagingList();
        int fired = 0;
        list.Changed += () => fired++;

        list.Clear();

        Assert.Equal(0, fired);
    }

    [Fact]
    public void StagingAnAlreadyStagedGuidReplacesTheRowAndMovesItLast()
    {
        var list = new VendorStagingList();
        list.Stage(ItemA, 1);
        list.Stage(ItemB, 3);

        VendorStagingAddOutcome outcome = list.Stage(ItemA, 1);

        Assert.Equal(VendorStagingAddOutcome.Added, outcome);
        Assert.Equal(
            new[] { new VendorStagingEntry(ItemB, 3), new VendorStagingEntry(ItemA, 1) },
            list.Entries);
    }

    [Fact]
    public void StagingReplacesTheQuantityInsteadOfSummingIt()
    {
        var list = new VendorStagingList();
        list.Stage(ItemA, 20);

        list.Stage(ItemA, 5);

        VendorStagingEntry entry = Assert.Single(list.Entries);
        Assert.Equal(5, entry.Quantity);
    }

    [Fact]
    public void StageIgnoresAnEmptyGuidOrNonPositiveQuantity()
    {
        var list = new VendorStagingList();

        Assert.Equal(VendorStagingAddOutcome.Ignored, list.Stage(0u, 1));
        Assert.Equal(VendorStagingAddOutcome.Ignored, list.Stage(ItemA, 0));
        Assert.True(list.IsEmpty);
    }
}
