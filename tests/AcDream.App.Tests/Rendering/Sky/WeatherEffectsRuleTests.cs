using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Sky;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.Content;
using AcDream.Core.World;
using DatReaderWriter;
using DatReaderWriter.Lib.IO;
using Xunit;

namespace AcDream.App.Tests.Rendering.Sky;

public sealed class WeatherEffectsRuleTests
{
    private const uint PostSceneGfxObjId = 0x01001234u;

    private static readonly SkyKeyframe Keyframe = new(
        0f, 0f, 0f, Vector3.Zero, 1f, Vector3.Zero, 1f, Vector3.Zero, 0f);

    private sealed class IdentityCamera : ICamera
    {
        public Matrix4x4 View => Matrix4x4.Identity;

        public Matrix4x4 Projection => Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 4f, 1f, 0.1f, 1000f);
        public float Aspect { get; set; } = 1f;
    }

    private sealed class FixedGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }

    private sealed class NullWorldPassScope : IWorldPassScope
    {
        public int SampleCount => 1;
        public IGpuPassEncoder? CurrentEncoder => null;
        public int AttachmentWidth => 1;
        public int AttachmentHeight => 1;
        public WorldFrameSections Sections { get; } = new();

        public IGpuPassEncoder RequireEncoder() =>
            throw new InvalidOperationException("No recording world pass is open.");

        public void ClearInteriorDepth() { }

        public IDisposable Publish(IGpuPassEncoder encoder) =>
            throw new NotSupportedException();
    }

    private sealed class StubDatabase : IDatDatabase
    {
        public DatDatabase Db => throw new NotSupportedException();
        public int Iteration => 0;

        public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj =>
            Array.Empty<uint>();

        public bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value) where T : IDBObj
        {
            value = default;
            return false;
        }

        public bool TryGetFileBytes(uint fileId, [MaybeNullWhen(false)] out byte[] value)
        {
            value = null;
            return false;
        }

        public bool TryGetFileBytes(uint fileId, ref byte[] bytes, out int bytesRead)
        {
            bytesRead = 0;
            return false;
        }

        public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj =>
            throw new NotSupportedException();

        public void Dispose() { }
    }

    private sealed class NoopDatReaderWriter : IDatReaderWriter
    {
        private readonly StubDatabase _db = new();

        public string SourceDirectory => string.Empty;
        public IDatDatabase Portal => _db;
        public IDatDatabase Cell => _db;

        public ReadOnlyDictionary<uint, IDatDatabase> CellRegions { get; } =
            new(new Dictionary<uint, IDatDatabase>());

        public IDatDatabase HighRes => _db;
        public IDatDatabase Language => _db;
        public IDatDatabase Local => _db;

        public ReadOnlyDictionary<uint, uint> RegionFileMap { get; } =
            new(new Dictionary<uint, uint>());

        public int PortalIteration => 0;
        public int CellIteration => 0;
        public int HighResIteration => 0;
        public int LanguageIteration => 0;

        public bool TryGetFileBytes(uint regionId, uint fileId, ref byte[] bytes, out int bytesRead)
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
        public T Get<T>(uint fileId) where T : IDBObj => default;

        public bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value) where T : IDBObj
        {
            value = default;
            return false;
        }

        public void Dispose() { }
    }

    private static SkyRenderer CreateRenderer()
    {
        var device = new RecordingGpuDevice();
        var dats = new NoopDatReaderWriter();
        var textures = new TextureCache(device, dats);
        return new SkyRenderer(device, new FixedGpuFrameSource(), new NullWorldPassScope(), dats, textures);
    }

    // Post-scene, not weather-flagged (0x01 = post-scene, 0x04 = weather):
    // retail's own after-scene sky draw is unconditional on the object being
    // weather, only on the option (r12 GameSky::Draw / LScape::draw).
    private static DayGroupData PostSceneNonWeatherGroup() => new()
    {
        Name = "Test",
        SkyObjects =
        [
            new SkyObjectData { GfxObjId = PostSceneGfxObjId, Properties = 0x01u },
        ],
    };

    [Fact]
    public void RenderWeather_SkipsTheEntirePostScenePassWhenTheOptionIsOn()
    {
        using SkyRenderer renderer = CreateRenderer();
        renderer.DisableMostWeatherEffects = () => true;

        // Never uploaded via PrepareDayGroup: if the pass touched the
        // object at all, it would throw looking up its GPU mesh.
        renderer.RenderWeather(
            new IdentityCamera(), Vector3.Zero, 0f, PostSceneNonWeatherGroup(), Keyframe);
    }

    [Fact]
    public void RenderWeather_StillDrawsNonWeatherPostSceneObjectsWhenTheOptionIsOff()
    {
        using SkyRenderer renderer = CreateRenderer();
        renderer.DisableMostWeatherEffects = () => false;

        Assert.Throws<InvalidOperationException>(() =>
            renderer.RenderWeather(
                new IdentityCamera(), Vector3.Zero, 0f, PostSceneNonWeatherGroup(), Keyframe));
    }
}
