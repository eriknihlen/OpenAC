namespace AcDream.App.Rendering.Wb;

internal sealed class TextureAtlasSlotAllocator
{
    private readonly bool[] _rented;
    private readonly Stack<int> _returned = new();
    private int _next;

    public TextureAtlasSlotAllocator(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _rented = new bool[capacity];
    }

    public int AvailableCount => _returned.Count + (_rented.Length - _next);
    public int Capacity => _rented.Length;

    public int Rent()
    {
        int slot;
        if (_returned.Count != 0)
        {
            slot = _returned.Pop();
        }
        else
        {
            if (_next == _rented.Length)
                throw new InvalidOperationException(
                    $"Texture atlas has no GPU-safe layer available ({_rented.Length} layers)."
                );
            slot = _next++;
        }

        if (_rented[slot])
            throw new InvalidOperationException($"Texture atlas layer {slot} was rented twice.");
        _rented[slot] = true;
        return slot;
    }

    public void Return(int slot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, _next);
        if (!_rented[slot])
            throw new InvalidOperationException($"Texture atlas layer {slot} was returned twice.");

        _rented[slot] = false;
        _returned.Push(slot);
    }
}
