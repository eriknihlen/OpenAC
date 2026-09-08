using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Gpu.Vk;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.Content;
using AcDream.Core.Terrain;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Lib.IO;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public sealed class TerrainWalkSlotKeyNormalizationTests : IDisposable
{
    private readonly RecordingGpuDevice _device = new();
    private readonly GpuDeviceFrameLifetime _frameLifetime;
    private readonly VulkanWorldPassScope _scope = new(sampleCount: 1);
    private readonly TerrainAtlas _atlas;
    private readonly TerrainModernRenderer _terrain;

    public TerrainWalkSlotKeyNormalizationTests()
    {
        _frameLifetime = new GpuDeviceFrameLifetime(_device);
        // A Region with no TerrainInfo takes BuildBackendNeutral's own
        // documented single-white-fallback-layer branch — no installed DAT
        // needed, and this suite stays hermetic.
        _atlas = TerrainAtlas.BuildBackendNeutral(_device, new EmptyRegionDats());
        _terrain = new TerrainModernRenderer(_device, _frameLifetime, _scope, _atlas, _device.Retirement);
    }

    private DrawScope BeginDraw()
    {
        _frameLifetime.BeginFrame();
        IGpuFrame frame = _frameLifetime.CurrentFrame!;
        IGpuPassEncoder pass = frame.BeginPass(
            GpuPassDescription.BackbufferClear(
                "s3-chunk3-fix-round-1-f1", Vector4.Zero, sampleCount: 1));
        IDisposable publication = _scope.Publish(pass);
        _device.Clear();
        return new DrawScope(frame, pass, publication);
    }

    private readonly struct DrawScope(
        IGpuFrame frame, IGpuPassEncoder pass, IDisposable publication) : IDisposable
    {
        public IGpuFrame Frame { get; } = frame;
        public IGpuPassEncoder Pass { get; } = pass;

        public void Dispose() => publication.Dispose();
    }

    private static LandblockMeshData MakeFullSizeMesh()
    {
        var vertices = new TerrainVertex[LandblockMesh.VerticesPerLandblock];
        var indices = new uint[LandblockMesh.VerticesPerLandblock];
        for (int i = 0; i < indices.Length; i++)
            indices[i] = (uint)i;
        return new LandblockMeshData(vertices, indices);
    }

    [Fact]
    public void DrawLandCells_ResolvesTheDatSlotFromTheWalksLowWordZeroLandblockId()
    {
        _terrain.AddLandblock(0xA9B4FFFFu, MakeFullSizeMesh(), Vector3.Zero);

        using DrawScope draw = BeginDraw();
        _terrain.BeginFrame(frameSlot: 0);
        _terrain.DrawLandCells(
            Matrix4x4.Identity,
            new (uint LandblockId, int SideCellCount, int CellIndex)[] { (0xA9B40000u, 8, 0) });

        GpuRecordedMultiDrawIndirect call = Assert.Single(
            _device.Calls.OfType<GpuRecordedMultiDrawIndirect>());
        Assert.Equal(1u, call.DrawCount);
    }

    [Fact]
    public void DrawLandCells_UnknownLandblockIsASilentNoOp()
    {
        using DrawScope draw = BeginDraw();
        _terrain.BeginFrame(frameSlot: 0);

        _terrain.DrawLandCells(
            Matrix4x4.Identity,
            new (uint LandblockId, int SideCellCount, int CellIndex)[] { (0xDEAD0000u, 8, 0) });

        Assert.Empty(_device.Calls.OfType<GpuRecordedMultiDrawIndirect>());
        Assert.Equal(0, _terrain.WalkDrawCount);
        Assert.Equal(0, _terrain.WalkVisibleSlotCount);
    }

    [Fact]
    public void DrawLandCells_KnownAndUnknownLandblocksInOneBatch_SubmitsOnlyTheKnownEntry()
    {
        _terrain.AddLandblock(0xA9B4FFFFu, MakeFullSizeMesh(), Vector3.Zero);

        using DrawScope draw = BeginDraw();
        _terrain.BeginFrame(frameSlot: 0);
        _terrain.DrawLandCells(
            Matrix4x4.Identity,
            new (uint LandblockId, int SideCellCount, int CellIndex)[]
            {
                (0xDEAD0000u, 8, 0), (0xA9B40000u, 8, 1),
            });

        GpuRecordedMultiDrawIndirect call = Assert.Single(
            _device.Calls.OfType<GpuRecordedMultiDrawIndirect>());
        Assert.Equal(1u, call.DrawCount);
        Assert.Equal(1, _terrain.WalkDrawCount);
        Assert.Equal(1, _terrain.WalkVisibleSlotCount);
    }

    public void Dispose()
    {
        _terrain.Dispose();
        _atlas.Dispose();
        _device.Dispose();
    }

    private sealed class EmptyRegionDats : IDatReaderWriter
    {
        private readonly Region _region = new();
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
            Array.Empty<IDatReaderWriter.IdResolution>();

        public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj =>
            throw new NotSupportedException();

        public bool TrySave<T>(
            uint regionId,
            T obj,
            int iteration = 0) where T : IDBObj =>
            throw new NotSupportedException();

        [return: MaybeNull]
        public T Get<T>(uint fileId) where T : IDBObj
        {
            if (typeof(T) == typeof(Region) && fileId == 0x13000000u)
                return (T)(object)_region;
            return default;
        }

        public bool TryGet<T>(
            uint fileId,
            [MaybeNullWhen(false)] out T value) where T : IDBObj
        {
            value = Get<T>(fileId);
            return value is not null;
        }

        public void Dispose()
        {
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
}
