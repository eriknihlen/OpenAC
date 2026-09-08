using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using AcDream.Content;
using AcDream.Core.Content;
using DatReaderWriter;
using DatReaderWriter.Enums;
using DatReaderWriter.Lib.IO;

namespace AcDream.Content.Tests;

public sealed class DatResolutionPrecedenceTests
{
    [Fact]
    public void PortalWinsEvenWhenLegacyEnumerationStartsWithHighRes()
    {
        var source = new ResolutionSource();
        source.Resolutions =
        [
            new(source.HighRes, DBObjType.GfxObj),
            new(source.Portal, DBObjType.Setup),
            new(source.Language, DBObjType.StringTable),
            new(source.Cell, DBObjType.EnvCell),
        ];

        Assert.True(((IDatReaderWriter)source).TryResolvePreferred(
            1,
            out IDatDatabase? database,
            out DBObjType type));
        Assert.Same(source.Portal, database);
        Assert.Equal(DBObjType.Setup, type);
    }

    [Fact]
    public void RemainingPrecedenceIsHighResThenLanguageThenCell()
    {
        var source = new ResolutionSource();
        source.Resolutions =
        [
            new(source.Cell, DBObjType.EnvCell),
            new(source.Language, DBObjType.StringTable),
            new(source.HighRes, DBObjType.GfxObj),
        ];

        Assert.True(((IDatReaderWriter)source).TryResolvePreferred(
            1,
            out IDatDatabase? database,
            out DBObjType type));
        Assert.Same(source.HighRes, database);
        Assert.Equal(DBObjType.GfxObj, type);

        source.Resolutions =
        [
            new(source.Cell, DBObjType.EnvCell),
            new(source.Language, DBObjType.StringTable),
        ];
        Assert.True(((IDatReaderWriter)source).TryResolvePreferred(
            1,
            out database,
            out type));
        Assert.Same(source.Language, database);
        Assert.Equal(DBObjType.StringTable, type);
    }

    [Fact]
    public void MissingIdReturnsUnknownWithoutAPlaceholderResolution()
    {
        var source = new ResolutionSource();

        Assert.False(((IDatReaderWriter)source).TryResolvePreferred(
            1,
            out IDatDatabase? database,
            out DBObjType type));
        Assert.Null(database);
        Assert.Equal(DBObjType.Unknown, type);
    }

    private sealed class ResolutionSource : IDatReaderWriter
    {
        private readonly StubDatabase _portal = new();
        private readonly StubDatabase _highRes = new();
        private readonly StubDatabase _language = new();
        private readonly StubDatabase _cell = new();

        public string SourceDirectory => string.Empty;
        public IDatDatabase Portal => _portal;
        public IDatDatabase Cell => _cell;
        public ReadOnlyDictionary<uint, IDatDatabase> CellRegions { get; } =
            new(new Dictionary<uint, IDatDatabase>());
        public IDatDatabase HighRes => _highRes;
        public IDatDatabase Language => _language;
        public IDatDatabase Local => _language;
        public ReadOnlyDictionary<uint, uint> RegionFileMap { get; } =
            new(new Dictionary<uint, uint>());
        public int PortalIteration => 0;
        public int CellIteration => 0;
        public int HighResIteration => 0;
        public int LanguageIteration => 0;
        public IReadOnlyList<IDatReaderWriter.IdResolution> Resolutions { get; set; } =
            Array.Empty<IDatReaderWriter.IdResolution>();

        public bool TryGetFileBytes(
            uint regionId,
            uint fileId,
            ref byte[] bytes,
            out int bytesRead)
        {
            bytesRead = 0;
            return false;
        }

        public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj =>
            Array.Empty<uint>();

        public IEnumerable<IDatReaderWriter.IdResolution> ResolveId(uint id) =>
            Resolutions;

        public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj =>
            throw new NotSupportedException();

        public bool TrySave<T>(
            uint regionId,
            T obj,
            int iteration = 0) where T : IDBObj =>
            throw new NotSupportedException();

        [return: MaybeNull]
        public T Get<T>(uint fileId) where T : IDBObj => default;

        public bool TryGet<T>(
            uint fileId,
            [MaybeNullWhen(false)] out T value) where T : IDBObj
        {
            value = default;
            return false;
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubDatabase : IDatDatabase
    {
        public DatDatabase Db => throw new NotSupportedException();
        public int Iteration => 0;

        public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj =>
            Array.Empty<uint>();

        public bool TryGet<T>(
            uint fileId,
            [MaybeNullWhen(false)] out T value) where T : IDBObj
        {
            value = default;
            return false;
        }

        public bool TryGetFileBytes(
            uint fileId,
            [MaybeNullWhen(false)] out byte[] value)
        {
            value = null;
            return false;
        }

        public bool TryGetFileBytes(
            uint fileId,
            ref byte[] bytes,
            out int bytesRead)
        {
            bytesRead = 0;
            return false;
        }

        public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
