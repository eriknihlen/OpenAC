using System.Buffers;

namespace AcDream.Core.Physics;

internal ref struct ShadowEntrySnapshot
{
    private ShadowEntry[]? _buffer;
    private readonly int _count;

    private ShadowEntrySnapshot(ShadowEntry[] buffer, int count)
    {
        _buffer = buffer;
        _count = count;
    }

    public readonly ReadOnlySpan<ShadowEntry> Entries
        => _buffer.AsSpan(0, _count);

    public static ShadowEntrySnapshot Capture(IReadOnlyList<ShadowEntry> source)
    {
        int count = source.Count;
        ShadowEntry[] buffer = ArrayPool<ShadowEntry>.Shared.Rent(count);

        try
        {
            for (int i = 0; i < count; i++)
                buffer[i] = source[i];

            return new ShadowEntrySnapshot(buffer, count);
        }
        catch
        {
            ArrayPool<ShadowEntry>.Shared.Return(buffer, clearArray: false);
            throw;
        }
    }

    public void Dispose()
    {
        ShadowEntry[]? buffer = _buffer;
        _buffer = null;

        if (buffer is not null)
        {
            ArrayPool<ShadowEntry>.Shared.Return(buffer, clearArray: false);
        }
    }
}
