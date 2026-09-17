using AcDream.Content;

namespace AcDream.App.Streaming;

internal interface ISealedDungeonCellClassifier
{
    bool IsSealedDungeon(uint cellId);
}

internal sealed class DatSealedDungeonCellClassifier
    : ISealedDungeonCellClassifier
{
    private readonly IDatReaderWriter _dats;
    private readonly object _datLock;

    public DatSealedDungeonCellClassifier(
        IDatReaderWriter dats,
        object datLock)
    {
        _dats = dats ?? throw new ArgumentNullException(nameof(dats));
        _datLock = datLock ?? throw new ArgumentNullException(nameof(datLock));
    }

    public bool IsSealedDungeon(uint cellId) =>
        AcDream.Runtime.Navigation.SealedDungeonCells.IsSealedDungeon(_dats, _datLock, cellId);
}
