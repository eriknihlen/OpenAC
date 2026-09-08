using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
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

    public bool IsSealedDungeon(uint cellId)
    {
        uint low = cellId & 0xFFFFu;
        if (low < 0x0100u || low >= 0xFFFEu)
            return false;

        EnvCell? envCell;
        lock (_datLock)
            envCell = _dats.Get<EnvCell>(cellId);
        return envCell is not null
            && !envCell.Flags.HasFlag(EnvCellFlags.SeenOutside);
    }
}
