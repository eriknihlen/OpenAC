using AcDream.Content;
using AcDream.Core.Content;
using DatReaderWriter.Lib.IO;
using DatReaderWriter.Options;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace AcDream.App.Tests;

public sealed class BoundedTestDatCollection : DatReaderWriter.DatCollection, IDatReaderWriter
{
    private readonly DatCollectionAdapter _bounded;

    public BoundedTestDatCollection(
        string datDirectory,
        DatAccessType datAccessType = DatAccessType.Read)
        : base(new DatCollectionOptions
        {
            DatDirectory = datDirectory,
            AccessType = datAccessType,
            IndexCachingStrategy = IndexCachingStrategy.OnDemand,
            FileCachingStrategy = FileCachingStrategy.Never,
        })
    {
        _bounded = new DatCollectionAdapter(this);
    }

    public BoundedTestDatCollection(DatCollectionOptions options)
        : base(options)
    {
        _bounded = new DatCollectionAdapter(this);
    }

    string IDatReaderWriter.SourceDirectory => _bounded.SourceDirectory;
    IDatDatabase IDatReaderWriter.Portal => _bounded.Portal;
    IDatDatabase IDatReaderWriter.Cell => _bounded.Cell;
    ReadOnlyDictionary<uint, IDatDatabase> IDatReaderWriter.CellRegions => _bounded.CellRegions;
    IDatDatabase IDatReaderWriter.HighRes => _bounded.HighRes;
    IDatDatabase IDatReaderWriter.Language => _bounded.Language;
    IDatDatabase IDatReaderWriter.Local => _bounded.Local;
    ReadOnlyDictionary<uint, uint> IDatReaderWriter.RegionFileMap => _bounded.RegionFileMap;
    int IDatReaderWriter.PortalIteration => _bounded.PortalIteration;
    int IDatReaderWriter.CellIteration => _bounded.CellIteration;
    int IDatReaderWriter.HighResIteration => _bounded.HighResIteration;
    int IDatReaderWriter.LanguageIteration => _bounded.LanguageIteration;

    [return: MaybeNull]
    T IDatObjectSource.Get<T>(uint fileId) =>
        _bounded.Get<T>(fileId);

    bool IDatObjectSource.TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value)
        => _bounded.TryGet(fileId, out value);

    IEnumerable<uint> IDatReaderWriter.GetAllIdsOfType<T>() =>
        _bounded.GetAllIdsOfType<T>();

    bool IDatReaderWriter.TryGetFileBytes(
        uint regionId,
        uint fileId,
        ref byte[] bytes,
        out int bytesRead) =>
        _bounded.TryGetFileBytes(regionId, fileId, ref bytes, out bytesRead);

    IEnumerable<IDatReaderWriter.IdResolution> IDatReaderWriter.ResolveId(uint id) =>
        _bounded.ResolveId(id);

    bool IDatReaderWriter.TryResolvePreferred(
        uint id,
        [NotNullWhen(true)] out IDatDatabase? database,
        out DatReaderWriter.Enums.DBObjType type) =>
        _bounded.TryResolvePreferred(id, out database, out type);

    bool IDatReaderWriter.TrySave<T>(T obj, int iteration) =>
        _bounded.TrySave(obj, iteration);

    bool IDatReaderWriter.TrySave<T>(uint regionId, T obj, int iteration) =>
        _bounded.TrySave(regionId, obj, iteration);
}
