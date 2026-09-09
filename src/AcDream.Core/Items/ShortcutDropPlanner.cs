using System;
using System.Collections.Generic;

namespace AcDream.Core.Items;

public enum ShortcutDropSource
{
    FreshItem,
    ShortcutAlias,
}

public enum ShortcutMutationKind
{
    Remove,
    Add,
}

public readonly record struct ShortcutMutation(
    ShortcutMutationKind Kind,
    int Slot,
    ShortcutEntry? Entry)
{
    public static ShortcutMutation Remove(int slot) => new(ShortcutMutationKind.Remove, slot, null);

    public static ShortcutMutation Add(ShortcutEntry entry)
        => new(ShortcutMutationKind.Add, entry.Index, entry);
}

public static class ShortcutDropPlanner
{
    public static ShortcutMutation[] PlanDrop(
        IReadOnlyList<ShortcutEntry?> slots,
        ShortcutDropSource source,
        int sourceSlot,
        int targetSlot,
        ShortcutEntry dragged)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (slots.Count != ShortcutStore.SlotCount
            || (uint)targetSlot >= ShortcutStore.SlotCount
            || dragged.ObjectId == 0)
            return Array.Empty<ShortcutMutation>();

        var state = new ShortcutEntry?[ShortcutStore.SlotCount];
        for (int i = 0; i < state.Length; i++)
            state[i] = slots[i];

        var mutations = new List<ShortcutMutation>(4);
        ShortcutEntry? displaced = VisibleEntry(state[targetSlot]);
        RemoveVisibleAt(state, targetSlot, mutations);

        if (source == ShortcutDropSource.FreshItem)
        {
            RemoveFirstVisibleObject(state, dragged.ObjectId, mutations);
            Add(state, dragged.WithIndex(targetSlot), mutations);

            if (displaced is { } entry && entry.ObjectId != dragged.ObjectId)
            {
                int empty = FirstEmptyToRight(state, targetSlot);
                if (empty >= 0)
                    Add(state, entry.WithIndex(empty), mutations);
            }
        }
        else
        {
            Add(state, dragged.WithIndex(targetSlot), mutations);
            if (displaced is { } entry
                && entry.ObjectId != dragged.ObjectId
                && (uint)sourceSlot < ShortcutStore.SlotCount
                && IsVisuallyEmpty(state[sourceSlot]))
            {
                Add(state, entry.WithIndex(sourceSlot), mutations);
            }
        }

        return mutations.ToArray();
    }

    public static ShortcutMutation[] PlanFullStackMerge(
        IReadOnlyList<ShortcutEntry?> slots,
        uint oldObjectId,
        uint newObjectId)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (slots.Count != ShortcutStore.SlotCount || oldObjectId == 0 || newObjectId == 0)
            return Array.Empty<ShortcutMutation>();

        var state = new ShortcutEntry?[ShortcutStore.SlotCount];
        for (int slot = 0; slot < slots.Count; slot++)
        {
            state[slot] = slots[slot];
        }

        var mutations = new List<ShortcutMutation>(3);
        int sourceSlot = FindFirstVisibleObject(state, oldObjectId);
        if (sourceSlot < 0)
            return Array.Empty<ShortcutMutation>();

        ShortcutEntry sourceEntry = state[sourceSlot]!.Value;
        RemoveVisibleAt(state, sourceSlot, mutations);
        RemoveFirstVisibleObject(state, newObjectId, mutations);
        Add(state, sourceEntry with { ObjectId = newObjectId }, mutations);
        return mutations.ToArray();
    }

    private static ShortcutEntry? VisibleEntry(ShortcutEntry? entry)
        => entry is { ObjectId: not 0 } ? entry : null;

    private static bool IsVisuallyEmpty(ShortcutEntry? entry) => VisibleEntry(entry) is null;

    private static void RemoveVisibleAt(
        ShortcutEntry?[] state,
        int slot,
        ICollection<ShortcutMutation> mutations)
    {
        if (!IsVisuallyEmpty(state[slot]))
        {
            state[slot] = null;
            mutations.Add(ShortcutMutation.Remove(slot));
        }
    }

    private static void RemoveFirstVisibleObject(
        ShortcutEntry?[] state,
        uint objectId,
        ICollection<ShortcutMutation> mutations)
    {
        for (int slot = 0; slot < state.Length; slot++)
        {
            if (VisibleEntry(state[slot]) is not { } entry || entry.ObjectId != objectId)
                continue;

            state[slot] = null;
            mutations.Add(ShortcutMutation.Remove(slot));
            return;
        }
    }

    private static int FindFirstVisibleObject(ShortcutEntry?[] state, uint objectId)
    {
        for (int slot = 0; slot < state.Length; slot++)
            if (VisibleEntry(state[slot]) is { } entry && entry.ObjectId == objectId)
                return slot;
        return -1;
    }

    private static void Add(
        ShortcutEntry?[] state,
        ShortcutEntry entry,
        ICollection<ShortcutMutation> mutations)
    {
        state[entry.Index] = entry;
        mutations.Add(ShortcutMutation.Add(entry));
    }

    private static int FirstEmptyToRight(ShortcutEntry?[] state, int targetSlot)
    {
        for (int slot = targetSlot + 1; slot < state.Length; slot++)
            if (IsVisuallyEmpty(state[slot]))
                return slot;
        for (int slot = 0; slot <= targetSlot; slot++)
            if (IsVisuallyEmpty(state[slot]))
                return slot;
        return -1;
    }
}
