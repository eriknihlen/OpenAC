using AcDream.Content;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Streaming;

internal sealed class DatSpawnClaimHydrationClassifier
{
    private readonly Func<uint, LandBlockInfo?> _lookup;
    private (uint Claim, bool Unhydratable)? _memo;

    public DatSpawnClaimHydrationClassifier(
        IDatReaderWriter dats,
        object datLock)
        : this(CreateLookup(dats, datLock))
    {
    }

    internal DatSpawnClaimHydrationClassifier(
        Func<uint, LandBlockInfo?> lookup) =>
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));

    public bool IsUnhydratable(uint claim)
    {
        uint low = claim & 0xFFFFu;
        if (low < 0x0100u)
            return false;
        if (_memo is { } memo && memo.Claim == claim)
            return memo.Unhydratable;

        LandBlockInfo? info = _lookup((claim & 0xFFFF0000u) | 0xFFFEu);
        bool unhydratable = info is null
            || info.NumCells == 0
            || low >= 0x0100u + info.NumCells;
        _memo = (claim, unhydratable);
        return unhydratable;
    }

    public void Reset() => _memo = null;

    private static Func<uint, LandBlockInfo?> CreateLookup(
        IDatReaderWriter dats,
        object datLock)
    {
        ArgumentNullException.ThrowIfNull(dats);
        ArgumentNullException.ThrowIfNull(datLock);
        return id =>
        {
            lock (datLock)
                return dats.Get<LandBlockInfo>(id);
        };
    }
}
