namespace AcDream.App.Rendering.Wb;

internal readonly record struct MeshBufferRange(int Offset, int Length)
{
    public int End => checked(Offset + Length);
}

internal sealed class ContiguousRangeAllocator
{
    private readonly List<MeshBufferRange> _free = new();

    public ContiguousRangeAllocator(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
        _free.Add(new MeshBufferRange(0, capacity));
    }

    public int Capacity { get; private set; }
    public int Used { get; private set; }
    public int HighWaterMark { get; private set; }
    public int Free => Capacity - Used;
    public int LargestFreeRange => _free.Count == 0 ? 0 : _free.Max(range => range.Length);
    public int TrailingFreeLength =>
        _free.Count != 0 && _free[^1].End == Capacity
            ? _free[^1].Length
            : 0;

    public bool TryAllocate(int length, out MeshBufferRange allocation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);

        int bestIndex = -1;
        int bestLength = int.MaxValue;
        for (int i = 0; i < _free.Count; i++)
        {
            int candidateLength = _free[i].Length;
            if (candidateLength >= length && candidateLength < bestLength)
            {
                bestIndex = i;
                bestLength = candidateLength;
                if (candidateLength == length)
                    break;
            }
        }

        if (bestIndex < 0)
        {
            allocation = default;
            return false;
        }

        MeshBufferRange free = _free[bestIndex];
        allocation = new MeshBufferRange(free.Offset, length);
        if (free.Length == length)
            _free.RemoveAt(bestIndex);
        else
            _free[bestIndex] = new MeshBufferRange(free.Offset + length, free.Length - length);

        Used = checked(Used + length);
        HighWaterMark = Math.Max(HighWaterMark, allocation.End);
        return true;
    }

    public void Grow(int newCapacity)
    {
        if (newCapacity <= Capacity)
            throw new ArgumentOutOfRangeException(nameof(newCapacity));

        int oldCapacity = Capacity;
        Capacity = newCapacity;
        InsertAndCoalesce(new MeshBufferRange(oldCapacity, newCapacity - oldCapacity));
    }

    /// <summary>
    /// Removes an unused tail after the matching physical GPU buffer has been
    /// replaced. Live offsets are unchanged, so no render-data rewrite is
    /// required.
    /// </summary>
    public void Shrink(int newCapacity)
    {
        if (newCapacity <= 0 || newCapacity >= Capacity)
            throw new ArgumentOutOfRangeException(nameof(newCapacity));
        if (newCapacity < HighWaterMark)
            throw new InvalidOperationException("Cannot trim a GPU arena through a live allocation.");

        MeshBufferRange tail = _free.Count == 0 ? default : _free[^1];
        if (tail.End != Capacity || tail.Offset > newCapacity)
            throw new InvalidOperationException("The requested GPU arena tail is not wholly free.");

        if (tail.Offset == newCapacity)
            _free.RemoveAt(_free.Count - 1);
        else
            _free[^1] = new MeshBufferRange(tail.Offset, newCapacity - tail.Offset);
        Capacity = newCapacity;
        RecalculateHighWaterMark();
    }

    public void Release(MeshBufferRange allocation)
    {
        if (allocation.Length <= 0
            || allocation.Offset < 0
            || allocation.End > Capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(allocation));
        }

        InsertAndCoalesce(allocation);
        Used = checked(Used - allocation.Length);
        RecalculateHighWaterMark();
    }

    private void InsertAndCoalesce(MeshBufferRange released)
    {
        int index = _free.BinarySearch(
            released,
            Comparer<MeshBufferRange>.Create((left, right) => left.Offset.CompareTo(right.Offset)));
        if (index < 0)
            index = ~index;

        if (index > 0 && _free[index - 1].End > released.Offset)
            throw new InvalidOperationException("GPU buffer range was released more than once.");
        if (index < _free.Count && released.End > _free[index].Offset)
            throw new InvalidOperationException("GPU buffer range overlaps an existing free range.");

        int start = released.Offset;
        int end = released.End;
        if (index > 0 && _free[index - 1].End == start)
        {
            start = _free[index - 1].Offset;
            _free.RemoveAt(--index);
        }
        if (index < _free.Count && end == _free[index].Offset)
        {
            end = _free[index].End;
            _free.RemoveAt(index);
        }

        _free.Insert(index, new MeshBufferRange(start, end - start));
    }

    private void RecalculateHighWaterMark()
    {
        HighWaterMark = _free.Count != 0 && _free[^1].End == Capacity
            ? _free[^1].Offset
            : Capacity;
    }
}
