using DatReaderWriter;
using DatReaderWriter.Enums;
using DatReaderWriter.Lib.IO;
using AcDream.Core.Content;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;


namespace AcDream.Content;

/// <summary>
/// Interface for the dat reader/writer
/// </summary>
public interface IDatReaderWriter : IDisposable, IDatObjectSource {
    /// <summary>
    /// Gets the source directory of the DAT files.
    /// </summary>
    string SourceDirectory { get; }

    /// <summary>
    /// Tries to get the raw bytes of a file from a specific region database.
    /// </summary>
    bool TryGetFileBytes(uint regionId, uint fileId, ref byte[] bytes, out int bytesRead);

    /// <summary>
    /// The portal database
    /// </summary>
    IDatDatabase Portal { get; }

    IDatDatabase Cell { get; }

    ReadOnlyDictionary<uint, IDatDatabase> CellRegions { get; }

    /// <summary>
    /// The high res database
    /// </summary>
    IDatDatabase HighRes { get; }

    /// <summary>
    /// The language database
    /// </summary>
    IDatDatabase Language { get; }

    /// <summary>Alias matching DatCollection's Local database name.</summary>
    IDatDatabase Local { get; }

    /// <summary>Enumerates typed ids across the database selected for the type.</summary>
    IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj;

    /// <summary>
    /// A mapping of region ids to region dat file entry ids. key: region id, value: region dat file entry
    /// </summary>
    ReadOnlyDictionary<uint, uint> RegionFileMap { get; }

    int PortalIteration { get; }

    int CellIteration { get; }

    int HighResIteration { get; }

    int LanguageIteration { get; }

    bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj;

    /// <summary>Attempts to save a database object to the appropriate DAT for a specific region.</summary>
    bool TrySave<T>(uint regionId, T obj, int iteration = 0) where T : IDBObj;

    /// <summary>
    /// Resolution of a data ID to a database and type
    /// </summary>
    public record IdResolution(IDatDatabase Database, DBObjType Type);

    /// <summary>
    /// Resolves a data ID to all possible databases and types.
    /// </summary>
    public IEnumerable<IdResolution> ResolveId(uint id);

    bool TryResolvePreferred(
        uint id,
        [NotNullWhen(true)] out IDatDatabase? database,
        out DBObjType type)
    {
        IDatDatabase? highRes = null;
        DBObjType highResType = DBObjType.Unknown;
        IDatDatabase? language = null;
        DBObjType languageType = DBObjType.Unknown;
        IDatDatabase? cell = null;
        DBObjType cellType = DBObjType.Unknown;

        foreach (IdResolution resolution in ResolveId(id))
        {
            if (ReferenceEquals(resolution.Database, Portal))
            {
                database = resolution.Database;
                type = resolution.Type;
                return true;
            }
            if (ReferenceEquals(resolution.Database, HighRes))
            {
                highRes = resolution.Database;
                highResType = resolution.Type;
            }
            else if (ReferenceEquals(resolution.Database, Language))
            {
                language = resolution.Database;
                languageType = resolution.Type;
            }
            else if (ReferenceEquals(resolution.Database, Cell))
            {
                cell = resolution.Database;
                cellType = resolution.Type;
            }
        }

        database = highRes ?? language ?? cell;
        type = highRes is not null
            ? highResType
            : language is not null
                ? languageType
                : cellType;
        return database is not null;
    }
}

/// <summary>
/// Interface for a dat database, providing methods to retrieve files and objects.
/// </summary>
public interface IDatDatabase : IDisposable {
    DatDatabase Db { get; }

    int Iteration { get; }

    /// <summary>Retrieves all file IDs of a specific type.</summary>
    public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj;

    /// <summary>Attempts to retrieve a database object by its file ID.</summary>
    public bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value) where T : IDBObj;

    /// <summary>Attempts to retrieve the raw bytes of a file by its ID.</summary>
    bool TryGetFileBytes(uint fileId, [MaybeNullWhen(false)] out byte[] value);

    /// <summary>Attempts to retrieve the raw bytes of a file by its ID into a provided buffer.</summary>
    bool TryGetFileBytes(uint fileId, ref byte[] bytes, out int bytesRead);

    /// <summary>Attempts to save a database object.</summary>
    bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj;
}
