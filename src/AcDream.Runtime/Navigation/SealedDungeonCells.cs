using AcDream.Content;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;

namespace AcDream.Runtime.Navigation;

/// <summary>Whether a cell belongs to a sealed dungeon, as the game data marks its indoor cells.</summary>
internal static class SealedDungeonCells
{
    /// <summary>
    /// True for an indoor cell that sees nothing outside, as a dungeon's cells do; false for
    /// outdoor cells, building interiors that see outside, and cells the data lacks.
    /// </summary>
    public static bool IsSealedDungeon(IDatReaderWriter dats, object datLock, uint cellId)
    {
        ArgumentNullException.ThrowIfNull(dats);
        ArgumentNullException.ThrowIfNull(datLock);
        uint low = cellId & 0xFFFFu;
        if (low < 0x0100u || low >= 0xFFFEu)
            return false;

        EnvCell? envCell;
        lock (datLock)
            envCell = dats.Get<EnvCell>(cellId);
        return envCell is not null
            && !envCell.Flags.HasFlag(EnvCellFlags.SeenOutside);
    }
}
