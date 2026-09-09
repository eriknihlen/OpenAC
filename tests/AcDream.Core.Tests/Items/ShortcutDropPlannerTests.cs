using AcDream.Core.Items;

namespace AcDream.Core.Tests.Items;

public sealed class ShortcutDropPlannerTests
{
    [Fact]
    public void FreshItemToEmpty_AddsOnlyAtTarget()
    {
        var slots = Empty();

        ShortcutMutation[] plan = ShortcutDropPlanner.PlanDrop(
            slots, ShortcutDropSource.FreshItem, sourceSlot: 11, targetSlot: 4,
            new ShortcutEntry(11, 0xA001u, 0u));

        Assert.Equal(new[]
        {
            ShortcutMutation.Add(new ShortcutEntry(4, 0xA001u, 0u)),
        }, plan);
    }

    [Fact]
    public void FreshItemToOccupied_MovesDisplacedToFirstEmptyOnRight()
    {
        var slots = Empty();
        slots[5] = new ShortcutEntry(5, 0xB001u, 0x11223344u);

        ShortcutMutation[] plan = ShortcutDropPlanner.PlanDrop(
            slots, ShortcutDropSource.FreshItem, sourceSlot: 0, targetSlot: 5,
            new ShortcutEntry(0, 0xA001u, 0u));

        Assert.Equal(new[]
        {
            ShortcutMutation.Remove(5),
            ShortcutMutation.Add(new ShortcutEntry(5, 0xA001u, 0u)),
            ShortcutMutation.Add(new ShortcutEntry(6, 0xB001u, 0x11223344u)),
        }, plan);
    }

    [Fact]
    public void FreshItemToOccupied_CyclicSearchWrapsToSlotZeroSide()
    {
        var slots = Full();
        slots[2] = null;
        slots[16] = new ShortcutEntry(16, 0xB001u, 0u);

        ShortcutMutation[] plan = ShortcutDropPlanner.PlanDrop(
            slots, ShortcutDropSource.FreshItem, sourceSlot: 0, targetSlot: 16,
            new ShortcutEntry(0, 0xA001u, 0u));

        Assert.Equal(ShortcutMutation.Add(new ShortcutEntry(2, 0xB001u, 0u)), plan[^1]);
    }

    [Fact]
    public void FreshItemToFullBar_DropsDisplacedWhenNoEmptySlotExists()
    {
        var slots = Full();
        slots[5] = new ShortcutEntry(5, 0xB001u, 0u);

        ShortcutMutation[] plan = ShortcutDropPlanner.PlanDrop(
            slots, ShortcutDropSource.FreshItem, sourceSlot: 0, targetSlot: 5,
            new ShortcutEntry(0, 0xA001u, 0u));

        Assert.Equal(new[]
        {
            ShortcutMutation.Remove(5),
            ShortcutMutation.Add(new ShortcutEntry(5, 0xA001u, 0u)),
        }, plan);
    }

    [Fact]
    public void ShortcutAliasToOccupied_RestoresDisplacedToVacatedSource()
    {
        var slots = Empty(); // alias was already removed from source slot 3 at lift
        slots[5] = new ShortcutEntry(5, 0xB001u, 0x55667788u);

        ShortcutMutation[] plan = ShortcutDropPlanner.PlanDrop(
            slots, ShortcutDropSource.ShortcutAlias, sourceSlot: 3, targetSlot: 5,
            new ShortcutEntry(3, 0xA001u, 0x11223344u));

        Assert.Equal(new[]
        {
            ShortcutMutation.Remove(5),
            ShortcutMutation.Add(new ShortcutEntry(5, 0xA001u, 0x11223344u)),
            ShortcutMutation.Add(new ShortcutEntry(3, 0xB001u, 0x55667788u)),
        }, plan);
    }

    [Fact]
    public void FreshDuplicate_RemovesOldOccurrenceBeforeSearchingForDisplacedSlot()
    {
        var slots = Full();
        slots[5] = new ShortcutEntry(5, 0xB001u, 0u);
        slots[7] = new ShortcutEntry(7, 0xA001u, 0xA5C31234u);

        ShortcutMutation[] plan = ShortcutDropPlanner.PlanDrop(
            slots, ShortcutDropSource.FreshItem, sourceSlot: 0, targetSlot: 5,
            new ShortcutEntry(0, 0xA001u, 0u));

        Assert.Equal(new[]
        {
            ShortcutMutation.Remove(5),
            ShortcutMutation.Remove(7),
            ShortcutMutation.Add(new ShortcutEntry(5, 0xA001u, 0u)),
            ShortcutMutation.Add(new ShortcutEntry(7, 0xB001u, 0u)),
        }, plan);
    }

    [Fact]
    public void NonObjectRecord_IsVisuallyEmptyForCyclicSearch()
    {
        var slots = Full();
        slots[6] = new ShortcutEntry(6, 0u, 0x01020304u);
        slots[5] = new ShortcutEntry(5, 0xB001u, 0u);

        ShortcutMutation[] plan = ShortcutDropPlanner.PlanDrop(
            slots, ShortcutDropSource.FreshItem, sourceSlot: 0, targetSlot: 5,
            new ShortcutEntry(0, 0xA001u, 0u));

        Assert.Equal(ShortcutMutation.Add(new ShortcutEntry(6, 0xB001u, 0u)), plan[^1]);
    }

    [Fact]
    public void FullStackMerge_RemovesExistingTargetShortcutThenRekeysSourceSlot()
    {
        var slots = Empty();
        slots[2] = new ShortcutEntry(2, 0xA001u, 0x11223344u);
        slots[9] = new ShortcutEntry(9, 0xB001u, 0x55667788u);

        ShortcutMutation[] plan = ShortcutDropPlanner.PlanFullStackMerge(
            slots, 0xA001u, 0xB001u);

        Assert.Equal(new[]
        {
            ShortcutMutation.Remove(2),
            ShortcutMutation.Remove(9),
            ShortcutMutation.Add(new ShortcutEntry(2, 0xB001u, 0x11223344u)),
        }, plan);
    }

    [Fact]
    public void InvalidDrop_ReturnsNoMutation()
    {
        Assert.Empty(ShortcutDropPlanner.PlanDrop(
            Empty(), ShortcutDropSource.FreshItem, 0, -1,
            new ShortcutEntry(0, 0xA001u, 0u)));
        Assert.Empty(ShortcutDropPlanner.PlanDrop(
            Empty(), ShortcutDropSource.FreshItem, 0, 0,
            new ShortcutEntry(0, 0u, 0u)));
    }

    private static ShortcutEntry?[] Empty() => new ShortcutEntry?[ShortcutStore.SlotCount];

    private static ShortcutEntry?[] Full()
    {
        var slots = Empty();
        for (int i = 0; i < slots.Length; i++)
            slots[i] = new ShortcutEntry(i, (uint)(0xC000 + i), 0u);
        return slots;
    }
}
