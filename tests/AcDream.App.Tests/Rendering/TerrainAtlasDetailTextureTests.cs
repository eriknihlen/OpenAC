using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Wb;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.Content;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Lib.IO;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public sealed class TerrainAtlasDetailTextureTests
{
    private const uint SurfaceTextureId = 0x05001787u;
    private const uint RenderSurfaceId = 0x06006D58u;

    [Fact]
    public void DetailTexture_UploadsFullMipChainAndUsesRepeatLinearSampler()
    {
        const int width = 4;
        const int height = 4;

        using var device = new RecordingGpuDevice();
        device.Clear();

        var dats = new FakeDetailTextureDats();
        dats.Register(new SurfaceTexture
        {
            Textures = new List<QualifiedDataId<RenderSurface>> { RenderSurfaceId },
        }, SurfaceTextureId);
        dats.Register(new RenderSurface
        {
            Width = width,
            Height = height,
            Format = PixelFormat.PFID_A8R8G8B8,
            SourceData = new byte[width * height * 4],
        }, RenderSurfaceId);

        var terrain = new TMTerrainDesc
        {
            TerrainTex = new TerrainTex
            {
                DetailTextureId = SurfaceTextureId,
                DetailTexTiling = 4u,
            },
        };

        IGpuSampler sampler = device.CreateSampler(TerrainAtlas.DetailSamplerDescription);

        MethodInfo method = typeof(TerrainAtlas).GetMethod(
            "TryCreateDetailTexture",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        object? result = method.Invoke(
            null,
            new object[] { device, dats, sampler, terrain, "building" });

        Assert.NotNull(result);

        RecordingGpuTexture texture = Assert.Single(device.CreatedTextures);
        Assert.Equal(width, texture.Width);
        Assert.Equal(height, texture.Height);
        int expectedMipLevels = RhiWorldTextureArray.MipLevelsFor(width, height);
        Assert.True(expectedMipLevels > 1, "the test fixture must exercise a real mip chain, not a 1x1 edge case");
        Assert.Equal(expectedMipLevels, texture.MipLevelCount);
        Assert.True(texture.MipChainGenerated);

        // Repeat/linear sampler: the exact sampler GpuBindingModel world
        // draws use, not WorldClamp (the alpha atlas' sampler) or any
        // point-filtered UI sampler.
        GpuRecordedTextureRegistration registration = Assert.Single(
            device.Calls.OfType<GpuRecordedTextureRegistration>());
        Assert.Equal(TerrainAtlas.DetailSamplerDescription, registration.Sampler);
        Assert.Equal(GpuFilter.Linear, registration.Sampler.MinFilter);
        Assert.Equal(GpuFilter.Linear, registration.Sampler.MagFilter);
        Assert.Equal(GpuMipFilter.Linear, registration.Sampler.MipFilter);
        Assert.Equal(GpuAddressMode.Repeat, registration.Sampler.AddressU);
        Assert.Equal(GpuAddressMode.Repeat, registration.Sampler.AddressV);
    }

    private sealed class FakeDetailTextureDats : IDatReaderWriter
    {
        private readonly Dictionary<uint, IDBObj> _objects = new();

        public void Register<T>(T obj, uint id) where T : IDBObj => _objects[id] = obj;

        public string SourceDirectory => string.Empty;
        public IDatDatabase Portal => throw new NotSupportedException();
        public IDatDatabase Cell => throw new NotSupportedException();
        public ReadOnlyDictionary<uint, IDatDatabase> CellRegions { get; } =
            new(new Dictionary<uint, IDatDatabase>());
        public IDatDatabase HighRes => throw new NotSupportedException();
        public IDatDatabase Language => throw new NotSupportedException();
        public IDatDatabase Local => throw new NotSupportedException();
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

        public bool TrySave<T>(uint regionId, T obj, int iteration = 0) where T : IDBObj =>
            throw new NotSupportedException();

        [return: MaybeNull]
        public T Get<T>(uint fileId) where T : IDBObj =>
            _objects.TryGetValue(fileId, out IDBObj? obj) && obj is T typed ? typed : default;

        public bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value) where T : IDBObj
        {
            if (_objects.TryGetValue(fileId, out IDBObj? obj) && obj is T typed)
            {
                value = typed;
                return true;
            }
            value = default;
            return false;
        }

        public void Dispose()
        {
        }
    }
}
