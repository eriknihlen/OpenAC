using System;
using System.Collections.Generic;

namespace AcDream.Core.Terrain;

public sealed class TerrainSlotAllocator
{
    private readonly Queue<int> _freeSlots = new();
    private readonly HashSet<int> _liveSlots = new();
    private int _nextFreeSlot;
    private int _capacity;

    public TerrainSlotAllocator(int initialCapacity = 64)
    {
        if (initialCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialCapacity), "must be > 0");
        _capacity = initialCapacity;
    }

    public int Capacity => _capacity;

    public int LoadedCount => _liveSlots.Count;

    public int Allocate(out bool needsGrow)
    {
        int slot;
        if (_freeSlots.TryDequeue(out var freed))
        {
            slot = freed;
        }
        else
        {
            slot = _nextFreeSlot++;
        }
        _liveSlots.Add(slot);
        needsGrow = slot >= _capacity;
        return slot;
    }

    public void Free(int slot)
    {
        if (!_liveSlots.Remove(slot))
            throw new InvalidOperationException(
                $"Slot {slot} was not allocated (double-free or unknown slot).");
        _freeSlots.Enqueue(slot);
    }

    public void GrowTo(int newCapacity)
    {
        if (newCapacity < _capacity)
            throw new ArgumentException("Capacity can only grow", nameof(newCapacity));
        _capacity = newCapacity;
    }
}
