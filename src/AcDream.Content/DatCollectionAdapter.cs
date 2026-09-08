using DatReaderWriter;
using DatReaderWriter.Enums;
using DatReaderWriter.Lib.IO;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace AcDream.Content;

public sealed class DatCollectionAdapter : IDatReaderWriter {
    private readonly DatCollection _dats;
    private readonly DatDatabaseWrapper _portal;
    private readonly DatDatabaseWrapper _cell;
    private readonly DatDatabaseWrapper _highRes;
    private readonly DatDatabaseWrapper _language;
    private readonly ReadOnlyDictionary<uint, IDatDatabase> _cellRegions;

    public DatCollectionAdapter(DatCollection dats) {
        ArgumentNullException.ThrowIfNull(dats);
        _dats = dats;
        _portal = new DatDatabaseWrapper(dats.Portal);
        _cell = new DatDatabaseWrapper(dats.Cell);
        _highRes = new DatDatabaseWrapper(dats.HighRes);
        _language = new DatDatabaseWrapper(dats.Local);

        var regions = new Dictionary<uint, IDatDatabase> { [0u] = _cell };
        _cellRegions = new ReadOnlyDictionary<uint, IDatDatabase>(regions);
    }

    /// <summary>Source directory of the underlying DatCollection.</summary>
    public string SourceDirectory => _dats.Options.DatDirectory ?? string.Empty;

    public CacheStats ObjectCacheStats =>
        _portal.ObjectCacheStats + _cell.ObjectCacheStats
        + _highRes.ObjectCacheStats + _language.ObjectCacheStats;

    public IDatDatabase Portal => _portal;
    public IDatDatabase Cell => _cell;
    public ReadOnlyDictionary<uint, IDatDatabase> CellRegions => _cellRegions;
    public IDatDatabase HighRes => _highRes;
    public IDatDatabase Language => _language;
    public IDatDatabase Local => _language;

    [return: MaybeNull]
    public T Get<T>(uint fileId) where T : IDBObj =>
        TryGet<T>(fileId, out var value) ? value : default;

    public bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value) where T : IDBObj {
        if (typeof(T) == typeof(DatReaderWriter.DBObjs.Iteration)) {
            throw new Exception(
                "Iteration is not a valid type to get from a dat file collection since it is used in all dat files. Use a specific dat like datCollection.Portal.Get<Iteration>()");
        }

        switch (_dats.TypeToDatFileType<T>()) {
            case DatFileType.Cell:
                return _cell.TryGet(fileId, out value);
            case DatFileType.Portal:
                return _portal.TryGet(fileId, out value)
                    || _highRes.TryGet(fileId, out value);
            case DatFileType.Local:
                return _language.TryGet(fileId, out value);
            default:
                value = default;
                return false;
        }
    }

    public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj =>
        _dats.TypeToDatFileType<T>() switch {
            DatFileType.Cell => _cell.GetAllIdsOfType<T>(),
            DatFileType.Portal => _portal.GetAllIdsOfType<T>()
                .Concat(_highRes.GetAllIdsOfType<T>()),
            DatFileType.Local => _language.GetAllIdsOfType<T>(),
            _ => Array.Empty<uint>(),
        };

    public ReadOnlyDictionary<uint, uint> RegionFileMap =>
        new ReadOnlyDictionary<uint, uint>(new Dictionary<uint, uint>());

    public int PortalIteration => _portal.Iteration;
    public int CellIteration => _cell.Iteration;
    public int HighResIteration => _highRes.Iteration;
    public int LanguageIteration => _language.Iteration;

    public bool TryGetFileBytes(uint regionId, uint fileId, ref byte[] bytes, out int bytesRead) {
        return _cell.TryGetFileBytes(fileId, ref bytes, out bytesRead);
    }

    public IEnumerable<IDatReaderWriter.IdResolution> ResolveId(uint id) {
        var results = new List<IDatReaderWriter.IdResolution>();

        void CheckDb(DatDatabaseWrapper wrapper) {
            var rawDb = wrapper.RawDatabase;
            if (rawDb.Tree.TryGetFile(id, out _)) {
                var type = rawDb.TypeFromId(id);
                if (type != DBObjType.Unknown)
                    results.Add(new IDatReaderWriter.IdResolution(wrapper, type));
            }
        }

        CheckDb(_highRes);
        CheckDb(_portal);
        CheckDb(_language);
        CheckDb(_cell);

        return results;
    }

    public bool TryResolvePreferred(
        uint id,
        [NotNullWhen(true)] out IDatDatabase? database,
        out DBObjType type) {
        if (TryResolve(_portal, id, out type)) {
            database = _portal;
            return true;
        }
        if (TryResolve(_highRes, id, out type)) {
            database = _highRes;
            return true;
        }
        if (TryResolve(_language, id, out type)) {
            database = _language;
            return true;
        }
        if (TryResolve(_cell, id, out type)) {
            database = _cell;
            return true;
        }

        database = null;
        type = DBObjType.Unknown;
        return false;
    }

    private static bool TryResolve(
        DatDatabaseWrapper database,
        uint id,
        out DBObjType type) {
        DatDatabase raw = database.RawDatabase;
        if (raw.Tree.TryGetFile(id, out _)) {
            type = raw.TypeFromId(id);
            if (type != DBObjType.Unknown)
                return true;
        }

        type = DBObjType.Unknown;
        return false;
    }

    public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj =>
        throw new NotSupportedException("DatCollectionAdapter is read-only.");

    public bool TrySave<T>(uint regionId, T obj, int iteration = 0) where T : IDBObj =>
        throw new NotSupportedException("DatCollectionAdapter is read-only.");

    public void Dispose() {
    }
}

public sealed class DatDatabaseWrapper : IDatDatabase {
    private readonly DatDatabase _db;
    private readonly BoundedDatObjectCache _cache = new();
    private readonly object _databaseLock = new();
    private int _malformedRecordDiagnosticEmitted;

    public DatDatabaseWrapper(DatDatabase db) {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    internal DatDatabase RawDatabase => _db;

    public DatDatabase Db => _db;
    public int Iteration => _db.Iteration?.CurrentIteration ?? 0;

    public CacheStats ObjectCacheStats => _cache.Stats;

    public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj =>
        _db.GetAllIdsOfType<T>();

    public bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value) where T : IDBObj {
        if (_cache.TryGet(fileId, out value)) {
            return true;
        }

        bool existingRecordFailed;
        lock (_databaseLock) {
            if (_cache.TryGet(fileId, out value)) {
                return true;
            }

            if (_db.TryGet<T>(fileId, out value)) {
                value = _cache.GetOrAdd(fileId, value);
                return true;
            }

            existingRecordFailed = _db.Tree.TryGetFile(fileId, out _);
        }

        // One actionable report per database keeps a repeatedly requested
        // malformed record from becoming another exception/logging-style hot
        // path. Missing keys stay silent.
        if (existingRecordFailed
            && Interlocked.Exchange(
                ref _malformedRecordDiagnosticEmitted,
                1) == 0)
        {
            Console.Error.WriteLine(
                $"[dat-miss] {typeof(T).Name} 0x{fileId:X8} entry EXISTS but TryGet failed " +
                $"(thread={Environment.CurrentManagedThreadId}; further reports suppressed)");
        }

        return false;
    }

    public bool TryGetFileBytes(uint fileId, [MaybeNullWhen(false)] out byte[] value) {
        lock (_databaseLock) {
            return _db.TryGetFileBytes(fileId, out value);
        }
    }

    public bool TryGetFileBytes(uint fileId, ref byte[] bytes, out int bytesRead) {
        lock (_databaseLock) {
            return _db.TryGetFileBytes(fileId, ref bytes, out bytesRead);
        }
    }

    public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj =>
        throw new NotSupportedException("DatDatabaseWrapper is read-only.");

    public void Dispose() {
        // The underlying DatDatabase is owned by DatCollection — do not dispose here.
    }
}
