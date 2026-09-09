using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using AcDream.Content;
using AcDream.Core.Items;
using DatReaderWriter;
using DatReaderWriter.Lib.IO;
using DatReaderWriter.Options;
using Xunit;

namespace AcDream.App.Tests.UI;

public sealed class RetailMarkupIconResolverMemoizationTests
{
    private const uint DecalHabitDoubleNormalizedId = 0x0C000165u;

    [Fact]
    public void ResolveDid_RepeatedUnresolvableId_ProbesTheDatExactlyOnce()
    {
        var dats = new CountingDatReaderWriter();
        var device = new RecordingGpuDevice();
        using var cache = new TextureCache(device, dats);
        var icons = new IconComposer(dats, cache);
        var objects = new ClientObjectTable();
        var resolver = new RetailMarkupIconResolver(dats, icons, objects);

        (uint tex1, int w1, int h1) = resolver.ResolveDid(DecalHabitDoubleNormalizedId);
        (uint tex2, int w2, int h2) = resolver.ResolveDid(DecalHabitDoubleNormalizedId);
        (uint tex3, int w3, int h3) = resolver.ResolveDid(DecalHabitDoubleNormalizedId);

        Assert.Equal((0u, 0, 0), (tex1, w1, h1));
        Assert.Equal((0u, 0, 0), (tex2, w2, h2));
        Assert.Equal((0u, 0, 0), (tex3, w3, h3));

        Assert.Equal(1, dats.Portal.TryGetCallCount);
        Assert.Equal(1, dats.HighRes.TryGetCallCount);
    }

    [Fact]
    public void ResolveDid_DifferentIds_ProbeIndependently()
    {
        var dats = new CountingDatReaderWriter();
        var device = new RecordingGpuDevice();
        using var cache = new TextureCache(device, dats);
        var icons = new IconComposer(dats, cache);
        var objects = new ClientObjectTable();
        var resolver = new RetailMarkupIconResolver(dats, icons, objects);

        resolver.ResolveDid(0x06000001u);
        resolver.ResolveDid(0x06000002u);
        resolver.ResolveDid(0x06000001u);

        Assert.Equal(2, dats.Portal.TryGetCallCount);
        Assert.Equal(2, dats.HighRes.TryGetCallCount);
    }

    [Fact]
    public void ResolveDid_TwoHundredFiftySeventhDistinctMiss_EvictsTheFirst()
    {
        var dats = new CountingDatReaderWriter();
        var device = new RecordingGpuDevice();
        using var cache = new TextureCache(device, dats);
        var icons = new IconComposer(dats, cache);
        var objects = new ClientObjectTable();
        var resolver = new RetailMarkupIconResolver(dats, icons, objects);

        for (uint id = 1; id <= 256; id++)
            resolver.ResolveDid(id);

        Assert.Equal(256, dats.Portal.TryGetCallCount);
        Assert.Equal(256, dats.HighRes.TryGetCallCount);

        resolver.ResolveDid(1u);
        Assert.Equal(256, dats.Portal.TryGetCallCount);
        Assert.Equal(256, dats.HighRes.TryGetCallCount);

        resolver.ResolveDid(257u);
        Assert.Equal(257, dats.Portal.TryGetCallCount);
        Assert.Equal(257, dats.HighRes.TryGetCallCount);

        resolver.ResolveDid(1u);
        Assert.Equal(258, dats.Portal.TryGetCallCount);
        Assert.Equal(258, dats.HighRes.TryGetCallCount);
    }

    private sealed class CountingDatDatabase : IDatDatabase
    {
        public int TryGetCallCount { get; private set; }

        public DatDatabase Db => throw new NotImplementedException();
        public int Iteration => 0;

        public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj =>
            throw new NotImplementedException();

        public bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value)
            where T : IDBObj
        {
            TryGetCallCount++;
            value = default;
            return false;
        }

        public bool TryGetFileBytes(uint fileId, [MaybeNullWhen(false)] out byte[] value) =>
            throw new NotImplementedException();

        public bool TryGetFileBytes(uint fileId, ref byte[] bytes, out int bytesRead) =>
            throw new NotImplementedException();

        public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj =>
            throw new NotImplementedException();

        public void Dispose() { }
    }

    private sealed class CountingDatReaderWriter : IDatReaderWriter
    {
        public CountingDatDatabase Portal { get; } = new();
        public CountingDatDatabase HighRes { get; } = new();

        IDatDatabase IDatReaderWriter.Portal => Portal;
        IDatDatabase IDatReaderWriter.HighRes => HighRes;

        public string SourceDirectory => string.Empty;
        public IDatDatabase Cell => throw new NotImplementedException();
        public ReadOnlyDictionary<uint, IDatDatabase> CellRegions => throw new NotImplementedException();
        public IDatDatabase Language => throw new NotImplementedException();
        public IDatDatabase Local => throw new NotImplementedException();
        public ReadOnlyDictionary<uint, uint> RegionFileMap => throw new NotImplementedException();
        public int PortalIteration => 0;
        public int CellIteration => 0;
        public int HighResIteration => 0;
        public int LanguageIteration => 0;

        public bool TryGetFileBytes(uint regionId, uint fileId, ref byte[] bytes, out int bytesRead) =>
            throw new NotImplementedException();

        public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj =>
            throw new NotImplementedException();

        public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj =>
            throw new NotImplementedException();

        public bool TrySave<T>(uint regionId, T obj, int iteration = 0) where T : IDBObj =>
            throw new NotImplementedException();

        public IEnumerable<IDatReaderWriter.IdResolution> ResolveId(uint id) =>
            throw new NotImplementedException();

        [return: MaybeNull]
        public T Get<T>(uint fileId) where T : IDBObj =>
            throw new NotImplementedException();

        public bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value)
            where T : IDBObj =>
            throw new NotImplementedException();

        public void Dispose() { }
    }
}
