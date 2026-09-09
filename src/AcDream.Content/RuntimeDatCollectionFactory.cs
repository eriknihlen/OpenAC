using DatReaderWriter;
using DatReaderWriter.Options;
using DatReaderWriter.Lib.IO;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace AcDream.Content;

public static class RuntimeDatCollectionFactory
{
    public static IDatReaderWriter OpenReadOnly(string datDirectory) =>
        new RuntimeDatCollection(new DatCollection(CreateReadOnlyOptions(datDirectory)));

    public static DatCollectionOptions CreateReadOnlyOptions(string datDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datDirectory);

        return new DatCollectionOptions
        {
            DatDirectory = datDirectory,
            AccessType = DatAccessType.Read,
            IndexCachingStrategy = IndexCachingStrategy.OnDemand,
            FileCachingStrategy = FileCachingStrategy.Never,
        };
    }
}

public sealed class RuntimeDatCollection : IDatReaderWriter
{
    private readonly DatCollection _raw;
    private readonly DatCollectionAdapter _bounded;
    private int _disposeStage;

    internal RuntimeDatCollection(DatCollection raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        _raw = raw;
        _bounded = new DatCollectionAdapter(raw);
    }

    public string SourceDirectory => _bounded.SourceDirectory;

    public CacheStats ObjectCacheStats => _bounded.ObjectCacheStats;

    public IDatDatabase Portal => _bounded.Portal;
    public IDatDatabase Cell => _bounded.Cell;
    public ReadOnlyDictionary<uint, IDatDatabase> CellRegions => _bounded.CellRegions;
    public IDatDatabase HighRes => _bounded.HighRes;
    public IDatDatabase Language => _bounded.Language;
    public IDatDatabase Local => _bounded.Local;
    public ReadOnlyDictionary<uint, uint> RegionFileMap => _bounded.RegionFileMap;
    public int PortalIteration => _bounded.PortalIteration;
    public int CellIteration => _bounded.CellIteration;
    public int HighResIteration => _bounded.HighResIteration;
    public int LanguageIteration => _bounded.LanguageIteration;

    [return: MaybeNull]
    public T Get<T>(uint fileId) where T : IDBObj => _bounded.Get<T>(fileId);

    public bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value)
        where T : IDBObj =>
        _bounded.TryGet(fileId, out value);

    public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj =>
        _bounded.GetAllIdsOfType<T>();

    public bool TryGetFileBytes(uint regionId, uint fileId, ref byte[] bytes, out int bytesRead) =>
        _bounded.TryGetFileBytes(regionId, fileId, ref bytes, out bytesRead);

    public IEnumerable<IDatReaderWriter.IdResolution> ResolveId(uint id) =>
        _bounded.ResolveId(id);

    public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj =>
        _bounded.TrySave(obj, iteration);

    public bool TrySave<T>(uint regionId, T obj, int iteration = 0) where T : IDBObj =>
        _bounded.TrySave(regionId, obj, iteration);

    public void Dispose()
    {
        while (_disposeStage < 2)
        {
            switch (_disposeStage)
            {
                case 0:
                    _bounded.Dispose();
                    _disposeStage++;
                    break;
                case 1:
                    _raw.Dispose();
                    _disposeStage++;
                    break;
                default:
                    throw new InvalidOperationException(
                        "Unknown DAT owner teardown stage.");
            }
        }
    }
}
